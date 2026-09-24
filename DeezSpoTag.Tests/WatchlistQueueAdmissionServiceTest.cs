using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class WatchlistQueueAdmissionServiceTest
{
    [Fact]
    public void DownloadAdmission_IgnoresPlaylistWithoutPrimaryDestinationEvenWhenRoutingRulesExist()
    {
        var preference = new PlaylistWatchPreferenceDto(
            "spotify",
            "playlist-with-routes-only",
            DestinationFolderId: null,
            Service: null,
            SyncTargets: null,
            PreferredEngine: null,
            DownloadEngineOrder: null,
            DownloadVariantMode: "standard",
            SyncMode: null,
            UpdateArtwork: true,
            ReuseSavedArtwork: false,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            RoutingRules: [new PlaylistTrackRoutingRule("genre", "contains", "Gospel", 3, 0)]);

        Assert.False(WatchlistEngine.HasDownloadDestination(preference));
    }

    [Theory]
    [InlineData(19, 20, false)]
    [InlineData(20, 20, true)]
    [InlineData(9, 10, false)]
    [InlineData(10, 10, true)]
    [InlineData(20, 0, false)]
    public void QuotaAwareAdmission_TriggersOnlyWhenEligibleRowsFillRemainingQuota(
        int eligibleRows,
        int remainingQuota,
        bool expected)
    {
        Assert.Equal(
            expected,
            WatchlistQueueAdmissionService.ShouldAdmitBeforeRunEnd(eligibleRows, remainingQuota));
    }

    [Fact]
    public void SelectAdmissionBatch_WaitsUntilQuotaIsFilled()
    {
        var rows = CreateMissingRows(
            ("playlist-a", "a-1", 1, "One"),
            ("playlist-a", "a-2", 2, "Two"),
            ("playlist-a", "a-3", 3, "Three"));

        Assert.Empty(WatchlistEngine.SelectAdmissionBatch(rows, remainingQuota: 5, allowBelowQuota: false));
        Assert.Equal(
            ["a-1", "a-2", "a-3"],
            WatchlistEngine.SelectAdmissionBatch(rows, remainingQuota: 5, allowBelowQuota: true)
                .Select(static row => row.TrackSourceId));
    }

    [Fact]
    public void SelectAdmissionBatch_CompletesArtistAlbumPastQuota()
    {
        var rows = CreateMissingRows(
            ("artist:7", "r1-t1", ArtistWatchQueueOptions.ReleasePositionStride + 1, "Release One"),
            ("artist:7", "r1-t2", ArtistWatchQueueOptions.ReleasePositionStride + 2, "Release One"),
            ("artist:7", "r1-t3", ArtistWatchQueueOptions.ReleasePositionStride + 3, "Release One"),
            ("artist:7", "r2-t1", (2 * ArtistWatchQueueOptions.ReleasePositionStride) + 1, "Release Two"));

        var selected = WatchlistEngine.SelectAdmissionBatch(rows, remainingQuota: 2, allowBelowQuota: false);

        Assert.Equal(["r1-t1", "r1-t2", "r1-t3"], selected.Select(static row => row.TrackSourceId));
        Assert.DoesNotContain(selected, static row => row.TrackSourceId == "r2-t1");
    }

    [Fact]
    public void SelectAdmissionBatch_CompletesPlaylistAlbumPastQuota()
    {
        var rows = CreateMissingRows(
            ("playlist-a", "t1", 1, "Same Album"),
            ("playlist-a", "t2", 2, "Same Album"),
            ("playlist-a", "t3", 3, "Same Album"),
            ("playlist-a", "other", 4, "Other Album"));

        var selected = WatchlistEngine.SelectAdmissionBatch(rows, remainingQuota: 2, allowBelowQuota: false);

        Assert.Equal(["t1", "t2", "t3"], selected.Select(static row => row.TrackSourceId));
        Assert.DoesNotContain(selected, static row => row.TrackSourceId == "other");
    }

    [Fact]
    public void SelectAdmissionBatch_DoesNotStartNextAlbumAfterQuotaIsFilled()
    {
        var rows = CreateMissingRows(
            ("playlist-a", "first-1", 1, "First"),
            ("playlist-a", "first-2", 2, "First"),
            ("playlist-a", "second-1", 3, "Second"));

        var selected = WatchlistEngine.SelectAdmissionBatch(rows, remainingQuota: 2, allowBelowQuota: false);

        Assert.Equal(["first-1", "first-2"], selected.Select(static row => row.TrackSourceId));
    }

    [Fact]
    public void SelectAdmissionBatch_DistinctPlaylistAlbumsDoNotExceedLimit()
    {
        var rows = CreateMissingRows(
            ("playlist-a", "one", 1, "Album One"),
            ("playlist-a", "two", 2, "Album Two"),
            ("playlist-a", "three", 3, "Album Three"));

        var selected = WatchlistEngine.SelectAdmissionBatch(rows, remainingQuota: 2, allowBelowQuota: false);

        Assert.Equal(["one", "two"], selected.Select(static row => row.TrackSourceId));
    }

    [Fact]
    public void AllowQuotaOverflow_LetsAlbumRemainderReserveBeyondTheOriginalBudget()
    {
        var service = new WatchlistQueueAdmissionService();
        _ = service.BeginRun(2);

        Assert.True(service.TryReserve(2));
        Assert.Equal(0, service.GetRemaining());
        Assert.False(service.TryReserve(1));

        service.AllowQuotaOverflow(3);
        Assert.Equal(3, service.GetRemaining());
        Assert.True(service.TryReserve(3));
        Assert.Equal(0, service.GetRemaining());
    }

    private static List<PlaylistWatchMissingTrackDto> CreateMissingRows(
        params (string SourceId, string TrackId, int Position, string Album)[] rows)
    {
        var result = new List<PlaylistWatchMissingTrackDto>(rows.Length);
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            result.Add(new PlaylistWatchMissingTrackDto(
                Id: index + 1,
                Source: "spotify",
                SourceId: row.SourceId,
                TrackSourceId: row.TrackId,
                Isrc: null,
                SourcePosition: row.Position,
                Title: row.TrackId,
                Artist: "Artist",
                Album: row.Album,
                DurationMs: 180000,
                CoverUrl: null,
                DeezerId: null,
                MappingStatus: null,
                Status: "missing",
                SnapshotId: null,
                CandidateRevision: null,
                ProviderReadinessRevision: null,
                QueueUuid: null,
                UpdatedAt: DateTimeOffset.UtcNow));
        }

        return result;
    }

    [Fact]
    public void IncompletePlaylistState_RoundTripsThroughPersistedStatus()
    {
        var state = WatchlistStateService.Parse("incomplete");

        Assert.Equal("Incomplete", state.ToString());
        Assert.Equal("incomplete", WatchlistStateService.ToPersistedStatus(state));
    }

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
    public void OrderedLedgerAdmission_FillsExactlyTheConfiguredQuotaWithoutSkippingAhead()
    {
        var service = new WatchlistQueueAdmissionService();
        _ = service.BeginRun(3);
        var orderedRows = new[] { "priority-1-track-1", "priority-1-track-2", "priority-2-track-1", "priority-2-track-2" };
        var admitted = new List<string>();

        foreach (var row in orderedRows)
        {
            if (!service.TryAdmitTrack().Allowed)
            {
                break;
            }
            admitted.Add(row);
        }

        Assert.Equal(orderedRows[..3], admitted);
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
    public void AttemptedIdentities_SkipDuplicatesForTheWholeRun()
    {
        var service = new WatchlistQueueAdmissionService();
        var token = service.BeginRun(2);
        var firstKeys = new[] { "isrc:usrc17600001", "deezer:111" };
        var sameFileDifferentPlaylist = new[] { "isrc:usrc17600001", "spotify:abc" };
        var uniqueFile = new[] { "isrc:usrc17600002" };

        Assert.False(service.HasAnyAttemptedIdentity(firstKeys));
        Assert.True(service.TryReserve(1));
        service.RememberAttemptedIdentities(firstKeys);

        Assert.True(service.HasAnyAttemptedIdentity(sameFileDifferentPlaylist));
        Assert.Equal(1, service.GetRemaining());
        Assert.False(service.HasAnyAttemptedIdentity(uniqueFile));

        Assert.True(service.TryReserve(1));
        service.RememberAttemptedIdentities(uniqueFile);
        Assert.Equal(0, service.GetRemaining());
        Assert.False(service.TryReserve(1));

        service.EndRun(token);
        Assert.False(service.HasAnyAttemptedIdentity(firstKeys));
        Assert.Equal(0, service.GetRemaining());

        var nextRun = service.BeginRun(2);
        Assert.False(service.HasAnyAttemptedIdentity(firstKeys));
        service.EndRun(nextRun);
    }

    [Fact]
    public void BuildWatchIdentityKeys_MatchTheSameFileAcrossPlaylists()
    {
        var deezerIntent = new DownloadIntent
        {
            Isrc = "USRC17600001",
            DeezerId = "111",
            Title = "Same Song",
            Artist = "Same Artist",
            DurationMs = 180000
        };
        var spotifyIntent = new DownloadIntent
        {
            Isrc = "usrc17600001",
            SpotifyId = "spotify-track",
            Title = "Same Song",
            Artist = "Same Artist",
            DurationMs = 180000
        };

        var deezerKeys = WatchlistEngine.BuildWatchIdentityKeys("dz-1", "USRC17600001", deezerIntent);
        var spotifyKeys = WatchlistEngine.BuildWatchIdentityKeys("sp-1", "USRC17600001", spotifyIntent);

        var service = new WatchlistQueueAdmissionService();
        _ = service.BeginRun(50);
        service.RememberAttemptedIdentities(deezerKeys);

        Assert.Contains("isrc:USRC17600001", deezerKeys, StringComparer.OrdinalIgnoreCase);
        Assert.True(service.HasAnyAttemptedIdentity(spotifyKeys));
        Assert.Contains(deezerKeys, key => key.StartsWith("meta:", StringComparison.Ordinal));
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
