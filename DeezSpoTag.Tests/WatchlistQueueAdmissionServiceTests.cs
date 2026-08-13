using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class WatchlistQueueAdmissionServiceTests
{
    [Fact]
    public void GetRemaining_WhenNoRunActive_DeniesWatchlistQueueing()
    {
        var service = new WatchlistQueueAdmissionService();

        Assert.Equal(0, service.GetRemaining());
        Assert.False(service.TryReserve(1));
    }

    [Fact]
    public void BeginRun_TracksRemainingBudget()
    {
        var service = new WatchlistQueueAdmissionService();
        var token = service.BeginRun(5);

        Assert.Equal(5, service.GetRemaining());
        Assert.True(service.TryReserve(2));
        Assert.Equal(3, service.GetRemaining());

        service.EndRun(token);
        Assert.Equal(0, service.GetRemaining());
    }

    [Fact]
    public void Release_ReturnsUnusedReservationWithoutExceedingRunLimit()
    {
        var service = new WatchlistQueueAdmissionService();
        _ = service.BeginRun(3);

        Assert.True(service.TryReserve(2));
        service.Release(1);
        service.Release(5);

        Assert.Equal(3, service.GetRemaining());
    }

    [Fact]
    public void TryReserve_DoesNotPartiallyReserveBeyondRemainingBudget()
    {
        var service = new WatchlistQueueAdmissionService();
        _ = service.BeginRun(2);

        Assert.False(service.TryReserve(3));
        Assert.Equal(2, service.GetRemaining());
    }

    [Fact]
    public void TryReserve_ConcurrentCallersCannotExceedRunLimit()
    {
        var service = new WatchlistQueueAdmissionService();
        _ = service.BeginRun(10);
        var reserved = 0;

        Parallel.For(
            0,
            50,
            _ =>
            {
                if (service.TryReserve(1))
                {
                    Interlocked.Increment(ref reserved);
                }
            });

        Assert.Equal(10, reserved);
        Assert.Equal(0, service.GetRemaining());
    }

    [Fact]
    public void EndRun_IgnoresStaleToken()
    {
        var service = new WatchlistQueueAdmissionService();
        _ = service.BeginRun(4);
        var activeToken = service.BeginRun(6);

        service.EndRun(activeToken - 1);

        Assert.Equal(6, service.GetRemaining());
    }

    [Fact]
    public void EmptyBudget_ReturnsRunBudgetDecision()
    {
        var service = new WatchlistQueueAdmissionService();
        var token = service.BeginRun(0);

        var decision = service.TryAdmitTrack();
        Assert.False(decision.Allowed);
        Assert.Equal(WatchQueueStopReason.RunBudget, decision.Reason);

        service.EndRun(token);
    }

    [Fact]
    public void BeginRunIfInactive_OpensBudgetForDirectReconciliationOnlyWhenNoRunOwnsContext()
    {
        var service = new WatchlistQueueAdmissionService();

        var directToken = service.BeginRunIfInactive(2);

        Assert.NotEqual(0, directToken);
        Assert.True(service.TryReserve(1));
        Assert.Equal(1, service.GetRemaining());
        Assert.Equal(0, service.BeginRunIfInactive(5));

        service.EndRun(directToken);
        Assert.Equal(0, service.GetRemaining());
    }

    [Fact]
    public async Task EvaluateQueueGate_AllowsAdmitWhenDownloadPipelineIsBusy()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-admit-pipeline-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Queue"] = $"Data Source={Path.Join(tempRoot, "queue.db")}"
                })
                .Build();
            var queueRepository = new DownloadQueueRepository(config, NullLogger<DownloadQueueRepository>.Instance);
            await queueRepository.EnqueueAsync(
                new DownloadQueueItem(
                    Id: 0,
                    QueueUuid: "pipeline-busy",
                    Engine: "qobuz",
                    ArtistName: "Artist",
                    TrackTitle: "Busy",
                    Isrc: null,
                    DeezerTrackId: null,
                    DeezerAlbumId: null,
                    DeezerArtistId: null,
                    SpotifyTrackId: null,
                    SpotifyAlbumId: null,
                    SpotifyArtistId: null,
                    AppleTrackId: null,
                    AppleAlbumId: null,
                    AppleArtistId: null,
                    DurationMs: null,
                    DestinationFolderId: 1,
                    QualityRank: null,
                    QueueOrder: null,
                    ContentType: "stereo",
                    Status: "queued",
                    PayloadJson: "{}",
                    Progress: 0,
                    Downloaded: 0,
                    Failed: 0,
                    Error: null,
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: DateTimeOffset.UtcNow),
                CancellationToken.None);
            Assert.True(await queueRepository.HasActiveDownloadPipelineAsync(CancellationToken.None));

            var service = new WatchlistQueueAdmissionService();
            var decision = await service.EvaluateQueueGateAsync(queueRepository, CancellationToken.None);

            Assert.True(decision.Allowed);
            Assert.Equal(WatchQueueStopReason.None, decision.Reason);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public void EvaluateQueueGate_StillDeniesWhenOrchestrationIsPaused()
    {
        var admission = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "DeezSpoTag.Web", "Services", "WatchlistQueueAdmissionService.cs"));
        var threeArgStart = admission.IndexOf(
            "public async Task<WatchlistQueueAdmissionDecision> EvaluateQueueGateAsync(\n        DownloadQueueRepository queueRepository,\n        DownloadOrchestrationService orchestrationService,",
            StringComparison.Ordinal);
        Assert.True(threeArgStart >= 0);
        var threeArgEnd = admission.IndexOf(
            "public async Task<WatchlistQueueAdmissionDecision> EvaluateQueueGateAsync(\n        DownloadQueueRepository queueRepository,\n        CancellationToken cancellationToken)",
            threeArgStart + 1,
            StringComparison.Ordinal);
        var threeArgBody = admission[threeArgStart..threeArgEnd];
        Assert.Contains("EvaluateDownloadGateAsync(orchestrationService, cancellationToken)", threeArgBody, StringComparison.Ordinal);
        Assert.Contains("WatchQueueStopReason.DownloadGate", admission, StringComparison.Ordinal);
        Assert.DoesNotContain("HasActiveDownloadPipelineAsync", admission, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "online", false, false, true)]
    [InlineData(true, "online", true, true, true)]
    [InlineData(true, "online", true, false, false)]
    [InlineData(true, "offline", false, true, false)]
    [InlineData(true, "degraded", false, true, false)]
    [InlineData(true, "rate_limited", false, true, false)]
    [InlineData(false, "online", false, true, false)]
    public void PublicApiReadiness_UsesOnlyOnlineHealthAndRequiredVerification(
        bool enabled,
        string status,
        bool requiresVerification,
        bool verificationValid,
        bool expected)
    {
        Assert.Equal(
            expected,
            WatchlistPublicApiReadinessService.IsProviderUsable(
                enabled,
                status,
                requiresVerification,
                verificationValid));
    }

}
