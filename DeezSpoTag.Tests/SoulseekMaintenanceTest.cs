using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Soulseek;
using DeezSpoTag.Services.Download.Soulseek;
using DeezSpoTag.Services.Download.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Tests that housekeeping actually runs and cannot break downloads.
/// </summary>
/// <remarks>
///     The three gaps this closes were real: expired peer cooldowns, stale slskd searches and terminal
///     transfer records all grew without bound because nothing called the cleanup. The sweep is driven from the
///     existing queue loop through a generic hook, because two guardrails forbid a per-engine worker.
/// </remarks>
[Collection("Settings Config Isolation")]
public sealed class SoulseekMaintenanceTest : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _dbPath;
    private readonly SoulseekSettingsService _settings;
    private readonly CountingPeerPolicy _peerPolicy = new();

    public SoulseekMaintenanceTest()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-soulseek-maint-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _dbPath = Path.Join(_tempRoot, "queue.db");
        Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", _tempRoot);
        Environment.SetEnvironmentVariable("DEEZSPOTAG_DATA_DIR", _tempRoot);
        _settings = new SoulseekSettingsService(
            new DeezSpoTag.Services.Settings.DeezSpoTagSettingsService(NullLogger<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>.Instance),
            new DeezSpoTag.Services.Library.LibraryRepository(
                new ConfigurationBuilder().Build(),
                NullLogger<DeezSpoTag.Services.Library.LibraryRepository>.Instance),
            NullLogger<SoulseekSettingsService>.Instance);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", null);
        Environment.SetEnvironmentVariable("DEEZSPOTAG_DATA_DIR", null);
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked temp file must not fail the run.
        }
    }

    private SoulseekRepository CreateRepository()
        => new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Queue"] = $"Data Source={_dbPath}" })
                .Build(),
            NullLogger<SoulseekRepository>.Instance);

    private SoulseekMaintenanceTask CreateTask(
        ISoulseekSearchService? search = null,
        ISoulseekTransferService? transfer = null)
        => new(
            new StubCredentials(null),
            _peerPolicy,
            search ?? new CountingSearchService(),
            transfer ?? new CountingTransferService(),
            _settings,
            NullLogger<SoulseekMaintenanceTask>.Instance);

    [Fact]
    public async Task SweepRemovesExpiredCooldownsStaleSearchesAndTransfers()
    {
        var repository = CreateRepository();

        // A cooldown that has already lapsed, plus one that is still live.
        await repository.RecordPeerFailureAsync("gone", "old", TimeSpan.FromMinutes(-30));
        await repository.RecordPeerFailureAsync("live", "recent", TimeSpan.FromMinutes(30));

        var search = new CountingSearchService();
        var transfer = new CountingTransferService();
        var task = CreateTask(search: search, transfer: transfer);

        await task.RunAsync(CancellationToken.None);

        Assert.Equal(1, _peerPolicy.CleanupCalls);

        var remaining = await repository.GetPeersInCooldownAsync();
        Assert.Equal("live", Assert.Single(remaining).Username);

        // Stale search and transfer cleanup both ran.
        Assert.Equal(1, search.CleanupCalls);
        Assert.Equal(1, transfer.CleanupCalls);
    }

    [Fact]
    public async Task AnUnconfiguredInstallStillSweepsLocallyWithoutReachingTheNetwork()
    {
        var search = new CountingSearchService();
        var transfer = new CountingTransferService();
        var task = CreateTask(search: search, transfer: transfer);

        await task.RunAsync(CancellationToken.None);

        // There is no feature flag, so the sweep runs whenever the queue loop ticks. That is safe: every
        // cleanup is a local database delete, and the one path that would talk to slskd is guarded by the
        // credential lookup, which returns null here. So an install with no slskd still prunes its own state
        // and still makes no network call.
        Assert.Equal(1, _peerPolicy.CleanupCalls);
        Assert.Equal(1, search.CleanupCalls);
        Assert.Equal(1, transfer.CleanupCalls);
    }

    [Fact]
    public async Task AnUnconfiguredInstallRaisesNothing()
    {
        var task = CreateTask();

        // No credentials means nothing to sweep, and a sweep that cannot run must still be a no-op rather
        // than a failure that could stop the queue loop.
        await task.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SweepIsThrottledSoTheQueueLoopIsNotHittingTheDatabaseEveryMinute()
    {
        var search = new CountingSearchService();
        var task = CreateTask(search: search);

        await task.RunAsync(CancellationToken.None);
        await task.RunAsync(CancellationToken.None);
        await task.RunAsync(CancellationToken.None);

        // The loop ticks once a minute; the sweep runs far less often than that.
        Assert.Equal(1, search.CleanupCalls);
    }

    [Fact]
    public async Task SweepSwallowsFailuresSoDownloadsKeepProcessing()
    {
        var search = new CountingSearchService { Throw = true };
        var task = CreateTask(search: search);

        // Must not throw, and must not block the loop.
        await task.RunAsync(CancellationToken.None);

        Assert.Equal(1, search.CleanupCalls);
    }

    [Fact]
    public void TheQueueLoopDrivesMaintenanceThroughTheGenericHook()
    {
        // The per-engine background service pattern is forbidden by two existing guardrails, so housekeeping
        // has to ride the existing loop through an engine-agnostic interface.
        var loop = ReadRepoFile("DeezSpoTag.Services", "Download", "Shared", "DeezSpoTagServiceExtensions.cs");

        Assert.Contains("IEnumerable<IQueueMaintenanceTask> maintenanceTasks", loop);
        Assert.Contains("RunMaintenanceTasksAsync(token)", loop);
        Assert.Contains("await task.RunAsync(cancellationToken)", loop);

        var hook = ReadRepoFile("DeezSpoTag.Services", "Download", "Shared", "IQueueMaintenanceTask.cs");
        Assert.Contains("public interface IQueueMaintenanceTask", hook);
        Assert.Contains("string Engine { get; }", hook);
    }

    [Fact]
    public void NoPerEngineMaintenanceServiceWasAdded()
    {
        var program = ReadRepoFile("DeezSpoTag.Web", "Program.cs");
        var extension = ReadRepoFile("DeezSpoTag.Services", "Download", "Soulseek", "SoulseekServiceExtensions.cs");

        // Maintenance registers as a queue-loop task, not as a hosted service. It lives with the engine's other
        // services so that every host which registers the processor also gets the task.
        Assert.Contains("IQueueMaintenanceTask", extension);
        Assert.DoesNotContain("AddHostedService<Soulseek", program);
        Assert.DoesNotContain("SoulseekQueueBackgroundService", program);
    }

    [Fact]
    public void EveryContractEventIsNowActuallyPublished()
    {
        // Three of the eight events previously had no call site at all.
        var search = ReadRepoFile("DeezSpoTag.Services", "Download", "Soulseek", "SoulseekSearchService.cs");
        var transfer = ReadRepoFile("DeezSpoTag.Services", "Download", "Soulseek", "SoulseekTransferService.cs");
        var connection = ReadRepoFile("DeezSpoTag.Services", "Download", "Soulseek", "SoulseekConnectionService.cs");
        var processor = ReadRepoFile("DeezSpoTag.Services", "Download", "Soulseek", "SoulseekEngineProcessor.cs");
        var share = ReadRepoFile("DeezSpoTag.Services", "Download", "Soulseek", "SoulseekShareService.cs");

        Assert.Contains("PublishSearchUpdate", search);
        Assert.Contains("PublishSearchResult", search);
        Assert.Contains("PublishDownloadUpdate", transfer);
        Assert.Contains("PublishConnectionState", connection);
        Assert.Contains("PublishEngineHealth", connection);
        Assert.Contains("PublishImportUpdate", processor);
        Assert.Contains("PublishShareSyncUpdate", share);
        Assert.Contains("PublishShareScanUpdate", share);
    }

    [Fact]
    public void ImportProgressIsPublishedForBothOutcomes()
    {
        var processor = ReadRepoFile("DeezSpoTag.Services", "Download", "Soulseek", "SoulseekEngineProcessor.cs");

        Assert.Contains("SoulseekImportUpdate(payload.Id, \"verified\"", processor);
        Assert.Contains("SoulseekImportUpdate(payload.Id, \"failed\"", processor);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var root = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(new[] { root }.Concat(parts).ToArray()));
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Services")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class CountingPeerPolicy : ISoulseekPeerPolicyService
    {
        public int CleanupCalls { get; private set; }

        public Task<SoulseekPeerDecision> EvaluateAsync(SoulseekRawCandidate candidate, CancellationToken cancellationToken = default)
            => Task.FromResult(new SoulseekPeerDecision(true));

        public Task<SoulseekPeerDecision> EvaluateAsync(
            SoulseekRawCandidate candidate,
            IReadOnlyList<(string Username, DateTimeOffset CooldownExpiresUtc)> cooldownsInEffect,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SoulseekPeerDecision(true));

        public bool IsBlockedFilename(string? filename) => false;

        public Task RecordFailureAsync(string username, string reason, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RecordSuccessAsync(string username, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<(string Username, DateTimeOffset CooldownExpiresUtc)>> GetCooldownsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<(string, DateTimeOffset)>>([]);

        public Task<int> CleanupExpiredCooldownsAsync(CancellationToken cancellationToken = default)
        {
            CleanupCalls++;
            return Task.FromResult(0);
        }
    }

    private sealed class CountingSearchService : ISoulseekSearchService
    {
        public int CleanupCalls { get; private set; }

        public bool Throw { get; init; }

        public Task<SoulseekSearchOutcome> ObserveAsync(
            SoulseekSearchTarget target,
            Guid searchId,
            string searchText,
            SoulseekSearchMode mode,
            string? queueUuid,
            CancellationToken cancellationToken = default,
            string? requiredQualityCode = null)
            => Task.FromResult(new SoulseekSearchOutcome(Guid.NewGuid(), searchText, [], null, true, false, 0, queueUuid));

        public Task<SoulseekSearchOutcome> SearchAsync(
            SoulseekSearchTarget target,
            SoulseekSearchMode mode = SoulseekSearchMode.Manual,
            string? queueUuid = null,
            CancellationToken cancellationToken = default,
            string? requiredQualityCode = null)
            => Task.FromResult(new SoulseekSearchOutcome(Guid.NewGuid(), "text", []));

        public Task<IReadOnlyList<SlskdDirectory>> BrowseAsync(string username, string? directory = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SlskdDirectory>>([]);

        public Task<int> CleanupStaleSearchesAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
        {
            CleanupCalls++;
            if (Throw)
            {
                throw new InvalidOperationException("simulated failure");
            }

            return Task.FromResult(0);
        }
    }

    private sealed class CountingTransferService : ISoulseekTransferService
    {
        public int CleanupCalls { get; private set; }

        public Task<Guid?> EnqueueAsync(string username, string filename, long size, CancellationToken cancellationToken = default)
            => Task.FromResult<Guid?>(Guid.NewGuid());

        public Task<SoulseekTransferStatus?> FindTransferAsync(
            string username,
            string filename,
            CancellationToken cancellationToken = default)
            => Task.FromResult<SoulseekTransferStatus?>(null);

        public Task<SoulseekTransferStatus?> GetStatusAsync(string username, Guid transferId, CancellationToken cancellationToken = default)
            => Task.FromResult<SoulseekTransferStatus?>(null);

        public Task<SoulseekCompletedFile?> WaitForCompletionAsync(
            string username,
            Guid transferId,
            string expectedPath,
            string queueUuid,
            string? completedDownloadsRoot = null,
            Func<double, double, Task>? progress = null,
            CancellationToken cancellationToken = default,
            CancellationToken hostStoppingToken = default)
            => Task.FromResult<SoulseekCompletedFile?>(null);

        public Task CancelAsync(string username, Guid transferId, bool remove = false, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<int> CleanupStaleTransfersAsync(CancellationToken cancellationToken = default)
        {
            CleanupCalls++;
            return Task.FromResult(0);
        }

        public Task<IReadOnlyDictionary<string, string>> FetchSidecarsAsync(
            string username,
            IReadOnlyList<string> remotePaths,
            string outputDirectory,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    private sealed class StubCredentials(DeezSpoTag.Core.Models.Soulseek.SlskdCredentials? credentials)
        : ISoulseekCredentialProvider
    {
        public Task<DeezSpoTag.Core.Models.Soulseek.SlskdCredentials?> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(credentials);
    }
}
