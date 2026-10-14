using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the repository surface that verification-driven retry selects on: the enqueue stamp,
/// the failed-item query, and the release clear.
/// </summary>
[Collection("Settings Config Isolation")]
public sealed class VerificationRetryServiceTest : IDisposable
{
    private readonly string _tempRoot;

    public VerificationRetryServiceTest()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-verify-retry-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("engine")]
    [InlineData("combined")]
    [InlineData("conditional-payload")]
    [InlineData("conditional-identity")]
    [InlineData("identity")]
    [InlineData("destinations")]
    public async Task PublicPlanAddedAfterEnqueueBecomesEligibleWithoutRestart(string update)
    {
        using var scope = new TestConfigRootScope(Path.Join(_tempRoot, "plan-update"));
        var settings = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var configuration = settings.LoadSettings();
        configuration.VerificationRetryInitialSweepCompleted = true;
        settings.SaveSettings(configuration);
        var repository = CreateRepository();
        var item = CreateItem("changed-plan") with { Engine = "deezer" };
        await repository.EnqueueAsync(item);
        var before = await ReadStampAsync(repository, item.QueueUuid);
        Assert.False(before.Flag);
        const string plan = "{\"fallbackPlan\":[{\"engine\":\"tidal\",\"quality\":\"LOSSLESS\"}]}";
        var changed = item with { Engine = "tidal", PayloadJson = "{}" };
        switch (update)
        {
            case "payload": await repository.UpdatePayloadAsync(item.QueueUuid, plan); break;
            case "engine": await repository.UpdateEngineAsync(item.QueueUuid, "tidal"); break;
            case "combined": await repository.UpdatePayloadAndEngineAsync(item.QueueUuid, "deezer", plan); break;
            case "conditional-payload": Assert.True(await repository.TryUpdateQueuedPayloadIfCurrentAsync(item.QueueUuid, "{}", plan)); break;
            case "conditional-identity": Assert.True(await repository.TryUpdateQueuedIdentityIfCurrentAsync(changed, "{}")); break;
            case "identity": await repository.UpdateQueueIdentityAsync(changed); break;
            case "destinations": await repository.UpdateFinalDestinationsAsync(item.QueueUuid, "{}", plan); break;
        }
        var after = await ReadStampAsync(repository, item.QueueUuid);
        Assert.True(after.Flag);
        Assert.Equal(before.QueuedAt, after.QueuedAt);
        await repository.UpdateStatusAsync(item.QueueUuid, "failed");
        var service = new VerificationRetryService(repository, new PublicApiSessionVerificationStore(settings), settings,
            new NullActivityLogWriter(), NullLogger<VerificationRetryService>.Instance);
        Assert.Equal(1, await service.OnSessionVerifiedAsync("tidal", async (uuid, token) =>
        {
            await repository.UpdateStatusAsync(uuid, "queued", cancellationToken: token);
            return true;
        }));
        await repository.UpdateStatusAsync(item.QueueUuid, "failed");
        await repository.UpdateEngineAsync(item.QueueUuid, "qobuz");
        Assert.Equal(0, await service.RecoverPendingAsync((_, _) => throw new Exception("Verification already consumed")));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("canceled")]
    public async Task ChangingTerminalItemEngineDoesNotRestoreRecoveryEligibility(string status)
    {
        var repository = CreateRepository();
        await repository.EnqueueAsync(CreateItem("terminal") with { Engine = "deezer" });
        await repository.UpdateStatusAsync("terminal", status);
        await repository.UpdateEngineAsync("terminal", "tidal");
        Assert.False((await ReadStampAsync(repository, "terminal")).Flag);
        Assert.Empty(await repository.GetFailedForVerificationRetryAsync(true));
    }

    [Fact]
    public void VerifyAllDefersRecoveryUntilAllPlatformsHaveBeenAttempted()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "DeezSpoTag.Web", "Views", "Login", "Index.cshtml"))) root = root.Parent;
        Assert.NotNull(root);
        var script = """
        const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
        const source=fs.readFileSync(process.argv[1]+'/DeezSpoTag.Web/Views/Login/Index.cshtml','utf8');
        let calls=[],failTidal=false,reloads=0,challenge=false,completeBody;
        let listener;
        const window={location:{origin:'http://localhost:8668'},setTimeout,clearTimeout,setInterval,clearInterval,addEventListener:(event,handler)=>listener=handler,removeEventListener:()=>{},open:()=>{const popup={closed:false,close(){this.closed=true}};setTimeout(()=>listener({origin:'http://localhost:8668',data:{type:'zarz_grant',grant:'fixture'}}),0);return popup}};
        const ctx=vm.createContext({URLSearchParams,console,encodeURIComponent,document:{querySelectorAll:()=>[]},
          window,
          fetch:async (url,options)=>{calls.push(url);if(url.endsWith('/complete'))completeBody=JSON.parse(options.body);return {ok:!(failTidal&&url.includes('/tidal/')),json:async()=>({authenticated:!challenge||url.endsWith('/complete')||!url.includes('deferRetry='),verificationUrl:'https://api.zarz.moe/verify',releasedFailedItems:2})}},
          buildLoginApiHeaders:()=>({}),reloadPublicDownloadSession:async()=>reloads++,getApiErrorMessage:async()=> 'unavailable',showToast:()=>{}});
        const runStart=source.indexOf('async function runPublicDownloadVerification('),runEnd=source.indexOf('async function startPublicDownloadVerification(',runStart);
        const allStart=source.indexOf('async function startAllPublicDownloadVerifications()'),allEnd=source.indexOf('function extractPublicDownloadGrant(',allStart);
        const configStart=source.indexOf('const PUBLIC_API_PROVIDER_CONFIG ='),configEnd=source.indexOf('const publicApiProviderStates',configStart);
        const grantStart=source.indexOf('function extractPublicDownloadGrant('),grantEnd=source.indexOf('function renderTidalInfo(',grantStart);
        vm.runInContext(source.slice(configStart,configEnd)+source.slice(runStart,runEnd)+source.slice(allStart,allEnd)+source.slice(grantStart,grantEnd)+'\nglobalThis.testConfig=PUBLIC_API_PROVIDER_CONFIG;',ctx);
        (async()=>{
          await ctx.startAllPublicDownloadVerifications();
          assert.equal(calls.length,4);assert(calls.slice(0,3).every(url=>url.endsWith('?deferRetry=true')));
          assert.equal(new URLSearchParams(calls[3].split('?')[1]).get('verifiedProviders'),'qobuz,tidal,amazon');assert.equal(reloads,3);
          calls=[];failTidal=true;
          await ctx.startAllPublicDownloadVerifications();
          assert.equal(calls.length,4);assert.equal(new URLSearchParams(calls[3].split('?')[1]).get('verifiedProviders'),'qobuz,amazon');
          calls=[];failTidal=false;
          await ctx.runPublicDownloadVerification(ctx.testConfig.qobuz);
          assert(calls[0].endsWith('?deferRetry=false'),'Already-authenticated single verification must invoke recovery');
          calls=[];challenge=true;
          await ctx.runPublicDownloadVerification(ctx.testConfig.qobuz,{deferRetry:true});
          assert.equal(completeBody.deferRetry,true);assert.equal(completeBody.grant,'fixture');
          assert.equal(calls.length,2);assert(calls[1].endsWith('/complete'));
        })().catch(e=>{console.error(e);process.exitCode=1});
        """;
        var start = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardError = true };
        start.ArgumentList.Add("-e"); start.ArgumentList.Add(script); start.ArgumentList.Add(root!.FullName);
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public async Task CentralEnqueueTracksPublicApiFilesAfterEarlierVerifications()
    {
        using var scope = new TestConfigRootScope(Path.Join(_tempRoot, "central"));
        var settings = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var store = new PublicApiSessionVerificationStore(settings);
        foreach (var slug in PublicApiSessionVerificationStore.PublicApiSlugs) store.RecordVerified(slug);
        var repository = CreateRepository();
        await repository.EnqueueAsync(CreateItem("public-path"), CancellationToken.None);
        Assert.True((await ReadStampAsync(repository, "public-path")).Flag);
        await repository.EnqueueAsync(CreateItem("public-plan") with { Engine = "deezer", PayloadJson = "{\"FallbackPlan\":[{\"StepId\":\"step-0\",\"Engine\":\"tidal\",\"Quality\":\"flac\",\"RequiredInputs\":[],\"ResolutionStrategy\":\"direct_url\"}]}" }, CancellationToken.None);
        Assert.True((await ReadStampAsync(repository, "public-plan")).Flag);
    }

    [Fact]
    public async Task EnqueueAsync_DefaultsToNotStampedAndRecordsQueuedAt()
    {
        var repository = CreateRepository();
        await repository.EnqueueAsync(CreateItem("unstamped") with { Engine = "deezer" }, CancellationToken.None);

        var (flag, queuedAt) = await ReadStampAsync(repository, "unstamped");

        // Existing callers that never set the flag must behave exactly as before this column existed.
        Assert.False(flag);
        Assert.NotNull(queuedAt);
    }

    [Fact]
    public async Task EnqueueAsync_PersistsTheStampWhenTheCallerSetsIt()
    {
        var repository = CreateRepository();
        var queuedAt = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);
        await repository.EnqueueAsync(
            CreateItem("stamped") with
            {
                PublicApiUnverifiedWhenQueued = true,
                LastQueuedAtUtc = queuedAt
            },
            CancellationToken.None);

        var (flag, persistedAt) = await ReadStampAsync(repository, "stamped");

        Assert.True(flag);
        Assert.Equal(queuedAt, persistedAt);
    }

    [Fact]
    public async Task MarkPublicApiUnverifiedWhenQueuedAsync_StampsAnExistingRow()
    {
        var repository = CreateRepository();
        await repository.EnqueueAsync(CreateItem("late-stamp"), CancellationToken.None);

        var stampedAt = new DateTimeOffset(2026, 6, 7, 8, 9, 10, TimeSpan.Zero);
        await repository.MarkPublicApiUnverifiedWhenQueuedAsync("late-stamp", true, stampedAt, CancellationToken.None);

        var (flag, persistedAt) = await ReadStampAsync(repository, "late-stamp");
        Assert.True(flag);
        Assert.Equal(stampedAt, persistedAt);
    }

    [Fact]
    public async Task GetFailedForVerificationRetryAsync_ReturnsOnlyStampedFailedItems()
    {
        var repository = CreateRepository();
        await repository.EnqueueAsync(
            CreateItem("stamped-failed") with { Status = "failed", PublicApiUnverifiedWhenQueued = true },
            CancellationToken.None);
        await repository.EnqueueAsync(
            CreateItem("unstamped-failed") with { Engine = "deezer", Status = "failed" },
            CancellationToken.None);
        await repository.EnqueueAsync(
            CreateItem("stamped-queued") with { Status = "queued", PublicApiUnverifiedWhenQueued = true },
            CancellationToken.None);

        var candidates = await repository.GetFailedForVerificationRetryAsync(
            requireUnverifiedFlag: true,
            CancellationToken.None);

        Assert.Equal(new[] { "stamped-failed" }, candidates.ToArray());
    }

    [Fact]
    public async Task GetFailedForVerificationRetryAsync_InitialSweepReturnsEveryFailedItem()
    {
        var repository = CreateRepository();
        await repository.EnqueueAsync(
            CreateItem("stamped-failed") with { Status = "failed", PublicApiUnverifiedWhenQueued = true },
            CancellationToken.None);
        await repository.EnqueueAsync(
            CreateItem("unstamped-failed") with { Engine = "deezer", Status = "failed" },
            CancellationToken.None);

        var candidates = await repository.GetFailedForVerificationRetryAsync(
            requireUnverifiedFlag: false,
            CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        Assert.Contains("stamped-failed", candidates);
        Assert.Contains("unstamped-failed", candidates);
    }

    [Theory]
    [InlineData("canceled")]
    [InlineData("cancelled")]
    public async Task GetFailedForVerificationRetryAsync_NeverReturnsUserCancelledItems(string status)
    {
        var repository = CreateRepository();
        await repository.EnqueueAsync(
            CreateItem("cancelled-item") with
            {
                Status = status,
                PublicApiUnverifiedWhenQueued = true
            },
            CancellationToken.None);

        // A user cancellation is final in both modes: the initial sweep included.
        Assert.Empty(await repository.GetFailedForVerificationRetryAsync(true, CancellationToken.None));
        Assert.Empty(await repository.GetFailedForVerificationRetryAsync(false, CancellationToken.None));
    }

    [Fact]
    public async Task ClearPublicApiRetryFlagAsync_StopsFurtherReleases()
    {
        var repository = CreateRepository();
        await repository.EnqueueAsync(
            CreateItem("released") with { Status = "failed", PublicApiUnverifiedWhenQueued = true },
            CancellationToken.None);

        await repository.ClearPublicApiRetryFlagAsync("released", CancellationToken.None);

        var (flag, _) = await ReadStampAsync(repository, "released");
        Assert.False(flag);
        Assert.Empty(await repository.GetFailedForVerificationRetryAsync(true, CancellationToken.None));
    }

    [Fact]
    public async Task OnSessionVerifiedAsync_ReleasesRegardlessOfEnabledEngines()
    {
        // Soulseek enabled alongside the public APIs must not disable this mechanism. There is no
        // configuration gate: verification always releases the items that were waiting on it.
        using var scope = new TestConfigRootScope(Path.Join(_tempRoot, "always"));
        var settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = settingsService.LoadSettings();
        settings.Service = "qobuz";
        settings.Soulseek.AutomationEnabled = true;
        settingsService.SaveSettings(settings);

        var repository = CreateRepository();
        await repository.EnqueueAsync(
            CreateItem("mixed-engine-item") with { Status = "failed", PublicApiUnverifiedWhenQueued = true },
            CancellationToken.None);

        var releasedUuids = new List<string>();
        var service = new VerificationRetryService(
            repository,
            new PublicApiSessionVerificationStore(settingsService),
            settingsService,
            new NullActivityLogWriter(),
            NullLogger<VerificationRetryService>.Instance);

        var released = await service.OnSessionVerifiedAsync(
            "qobuz",
            (uuid, _) =>
            {
                releasedUuids.Add(uuid);
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.Equal(1, released);
        Assert.Equal(new[] { "mixed-engine-item" }, releasedUuids.ToArray());
    }

    [Fact]
    public async Task CentralEnqueueTracksPublicFilesRegardlessOfOtherEnabledEngines()
    {
        using var scope = new TestConfigRootScope(Path.Join(_tempRoot, "stamp"));
        var settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = settingsService.LoadSettings();
        settings.Service = "qobuz";
        settings.Soulseek.AutomationEnabled = true;
        settingsService.SaveSettings(settings);

        var repository = CreateRepository();
        await repository.EnqueueAsync(CreateItem("to-stamp"), CancellationToken.None);
        var (flag, _) = await ReadStampAsync(repository, "to-stamp");
        Assert.True(flag);
    }

    [Fact]
    public async Task OnSessionVerifiedAsync_InitialSweepReleasesEveryFailedItemThenOnlyStampedOnes()
    {
        using var scope = new TestConfigRootScope(Path.Join(_tempRoot, "sweep"));
        var settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = settingsService.LoadSettings();
        settings.Service = "qobuz";
        settingsService.SaveSettings(settings);

        var repository = CreateRepository();
        await repository.EnqueueAsync(
            CreateItem("first-sweep-stamped") with { Status = "failed", PublicApiUnverifiedWhenQueued = true },
            CancellationToken.None);
        await repository.EnqueueAsync(
            CreateItem("first-sweep-unstamped") with { Engine = "deezer", Status = "failed" },
            CancellationToken.None);

        var service = new VerificationRetryService(
            repository,
            new PublicApiSessionVerificationStore(settingsService),
            settingsService,
            new NullActivityLogWriter(),
            NullLogger<VerificationRetryService>.Instance);

        // Recovery keeps the flag until success; the verification epoch prevents duplicate release.
        async Task<bool> ReleaseAsync(string uuid, CancellationToken token)
        {
            await repository.UpdateStatusAsync(uuid, "queued", cancellationToken: token);
            return true;
        }

        var firstSweep = new List<string>();
        Assert.Equal(2, await service.OnSessionVerifiedAsync(
            "qobuz",
            (uuid, token) =>
            {
                firstSweep.Add(uuid);
                return ReleaseAsync(uuid, token);
            },
            CancellationToken.None));
        Assert.Equal(2, firstSweep.Count);

        // A second verification must not repeat the unfiltered sweep.
        await repository.EnqueueAsync(
            CreateItem("second-unstamped") with { Engine = "deezer", Status = "failed" },
            CancellationToken.None);
        var secondSweep = new List<string>();
        Assert.Equal(0, await service.OnSessionVerifiedAsync(
            "tidal",
            (uuid, token) =>
            {
                secondSweep.Add(uuid);
                return ReleaseAsync(uuid, token);
            },
            CancellationToken.None));
        Assert.Empty(secondSweep);
    }

    [Fact]
    public async Task OnSessionVerifiedAsync_IgnoresNonPublicSlugs()
    {
        using var scope = new TestConfigRootScope(Path.Join(_tempRoot, "slug"));
        var settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = settingsService.LoadSettings();
        settings.Service = "qobuz";
        settingsService.SaveSettings(settings);

        var store = new PublicApiSessionVerificationStore(settingsService);
        var service = new VerificationRetryService(
            CreateRepository(),
            store,
            settingsService,
            new NullActivityLogWriter(),
            NullLogger<VerificationRetryService>.Instance);

        var released = await service.OnSessionVerifiedAsync(
            "deezer",
            (_, _) => Task.FromResult(true),
            CancellationToken.None);

        Assert.Equal(0, released);
        Assert.Null(store.GetLastVerifiedAtUtc("deezer"));
    }

    [Fact]
    public async Task RecoveryCatchesLateFailureWithoutLoopingAndRetriesAfterNewVerification()
    {
        using var scope = new TestConfigRootScope(Path.Join(_tempRoot, "late"));
        var settings = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var repository = CreateRepository();
        await repository.EnqueueAsync(CreateItem("late") with { Status = "running" }, CancellationToken.None);
        var service = new VerificationRetryService(repository, new PublicApiSessionVerificationStore(settings), settings,
            new NullActivityLogWriter(), NullLogger<VerificationRetryService>.Instance);
        async Task<bool> Release(string uuid, CancellationToken token)
        {
            await repository.UpdateStatusAsync(uuid, "queued", cancellationToken: token);
            return true;
        }
        Assert.Equal(0, await service.OnSessionVerifiedAsync("qobuz", Release));
        await repository.UpdateStatusAsync("late", "failed");
        Assert.Equal(1, await service.RecoverPendingAsync(Release));
        Assert.True((await ReadStampAsync(repository, "late")).Flag);
        await repository.UpdateStatusAsync("late", "failed");
        Assert.Equal(0, await service.RecoverPendingAsync(Release));
        await Task.Delay(5);
        Assert.Equal(1, await service.OnSessionVerifiedAsync("qobuz", Release));
        await repository.UpdateStatusAsync("late", "completed");
        Assert.False((await ReadStampAsync(repository, "late")).Flag);
    }

    [Fact]
    public async Task RecoveryDoesNotUseAVerificationOlderThanTheQueuedFile()
    {
        using var scope = new TestConfigRootScope(Path.Join(_tempRoot, "old"));
        var settings = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var store = new PublicApiSessionVerificationStore(settings);
        store.RecordVerified("qobuz", DateTimeOffset.UtcNow.AddHours(-1));
        var repository = CreateRepository();
        await repository.EnqueueAsync(CreateItem("new-failure") with { Status = "failed" }, CancellationToken.None);
        var service = new VerificationRetryService(repository, store, settings, new NullActivityLogWriter(), NullLogger<VerificationRetryService>.Instance);
        Assert.Equal(0, await service.RecoverPendingAsync((_, _) => throw new Exception("Must not release against an old verification")));
    }

    [Fact]
    public async Task RecoveryDoesNotConsumeInitialSweepWhenReleaseIsUnavailable()
    {
        using var scope = new TestConfigRootScope(Path.Join(_tempRoot, "unavailable"));
        var settings = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var repository = CreateRepository();
        await repository.EnqueueAsync(CreateItem("unavailable") with { Status = "failed" }, CancellationToken.None);
        var service = new VerificationRetryService(repository, new PublicApiSessionVerificationStore(settings), settings,
            new NullActivityLogWriter(), NullLogger<VerificationRetryService>.Instance);
        Assert.Equal(0, await service.OnSessionVerifiedAsync("qobuz", (_, _) => Task.FromResult(false)));
        Assert.False(settings.LoadSettings().VerificationRetryInitialSweepCompleted);
        Assert.True((await ReadStampAsync(repository, "unavailable")).Flag);
    }

    [Fact]
    public async Task SchemaRecoveryBackfillsOldPublicFailuresAndKeepsTerminalRowsExcluded()
    {
        var repository = CreateRepository("legacy.db");
        await repository.EnqueueAsync(CreateItem("old-public") with { Status = "failed" });
        await repository.EnqueueAsync(CreateItem("old-plan") with { Engine = "deezer", Status = "failed",
            PayloadJson = "{\"FallbackPlan\":[{\"Engine\":\"tidal\"}]}" });
        await repository.EnqueueAsync(CreateItem("old-cancelled") with { Status = "cancelled" });
        await repository.EnqueueAsync(CreateItem("old-completed") with { Status = "completed" });
        await using (var connection = new SqliteConnection("Data Source=" + Path.Join(_tempRoot, "legacy.db")))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE download_task SET public_api_unverified_when_queued=0; ALTER TABLE download_task DROP COLUMN last_public_api_retry_verification_at_utc;";
            await command.ExecuteNonQueryAsync();
        }
        var restarted = CreateRepository("legacy.db");
        var eligible = await restarted.GetFailedForVerificationRetryAsync(true);
        Assert.Contains("old-public", eligible);
        Assert.Contains("old-plan", eligible);
        Assert.Equal(2, eligible.Count);
        Assert.False((await ReadStampAsync(restarted, "old-cancelled")).Flag);
        Assert.False((await ReadStampAsync(restarted, "old-completed")).Flag);
    }

    private DownloadQueueRepository CreateRepository(string? databaseName = null)
    {
        var queueDbPath = Path.Join(_tempRoot, databaseName ?? Guid.NewGuid().ToString("N") + ".db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Queue"] = $"Data Source={queueDbPath}",
                ["DataDirectory"] = _tempRoot
            })
            .Build();
        return new DownloadQueueRepository(config, NullLogger<DownloadQueueRepository>.Instance);
    }

    private static async Task<(bool Flag, DateTimeOffset? QueuedAt)> ReadStampAsync(
        DownloadQueueRepository repository,
        string queueUuid)
    {
        var stamp = await repository.GetPublicApiRetryStampAsync(queueUuid, CancellationToken.None);
        Assert.NotNull(stamp);
        return stamp!.Value;
    }

    private static DownloadQueueItem CreateItem(string queueUuid)
        => new(
            Id: 0,
            QueueUuid: queueUuid,
            Engine: "qobuz",
            // Distinct per uuid: the repository treats identical artist/title/duration as a
            // duplicate and silently drops the second insert, which would hide these rows.
            ArtistName: "Artist-" + queueUuid,
            TrackTitle: "Track-" + queueUuid,
            Isrc: "ISRC" + queueUuid.ToUpperInvariant(),
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
            DestinationFolderId: null,
            QualityRank: null,
            QueueOrder: null,
            ContentType: null,
            Status: "queued",
            PayloadJson: "{}",
            Progress: 0,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    private sealed class NullActivityLogWriter : IActivityLogWriter
    {
        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message)
        {
        }
    }
}
