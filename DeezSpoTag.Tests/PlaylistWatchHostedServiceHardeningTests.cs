using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PlaylistWatchHostedServiceHardeningTests : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private LibraryRepository _repository = default!;
    private DeezSpoTagSettingsService _settingsService = default!;
    private ServiceProvider _provider = default!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-watch-hosted-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);

        var dbPath = Path.Join(_tempRoot, "library.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={dbPath}",
                ["DataDirectory"] = _tempRoot
            })
            .Build();

        var dbService = new LibraryDbService(config, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        _repository = new LibraryRepository(config, NullLogger<LibraryRepository>.Instance);
        _settingsService = new DeezSpoTagSettingsService(config, NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = _settingsService.LoadSettings();
        settings.WatchEnabled = true;
        settings.WatchPollIntervalSeconds = 1;
        settings.WatchDelayBetweenArtistsSeconds = 1;
        settings.WatchDelayBetweenPlaylistsSeconds = 1;
        settings.WatchMaxItemsPerRun = 50;
        _settingsService.SaveSettings(settings);

        var playlistWatchService = new PlaylistWatchService(
            _repository,
            new PlaylistWatchService.PlaylistWatchPlatformServices
            {
                SpotifyMetadataService = null!,
                SpotifyPathfinderMetadataClient = null!,
                SpotifyArtistService = null!,
                DeezerClient = null!,
                DeezerGatewayService = null!,
                AppleCatalogService = null!,
                BoomplayMetadataService = null!,
                LibraryRecommendationService = null!
            },
            _settingsService,
            serviceProvider: null!,
            playlistSyncService: null!,
            playlistVisualService: null!,
            NullLogger<PlaylistWatchService>.Instance);

        var artistWatchService = new ArtistWatchService(
            _repository,
            new ArtistWatchPlatformDependencies(
                spotifyArtistService: null!,
                spotifyMetadataService: null!,
                appleCatalogService: null!,
                deezerClient: null!),
            playlistWatchService,
            _settingsService,
            NullLogger<ArtistWatchService>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(_settingsService);
        services.AddSingleton(_repository);
        services.AddSingleton(playlistWatchService);
        services.AddSingleton(artistWatchService);
        _provider = services.BuildServiceProvider();
    }

    public Task DisposeAsync()
    {
        _provider?.Dispose();
        try
        {
            if (!string.IsNullOrWhiteSpace(_tempRoot) && Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task RunOnce_UsesBackoff_ToAvoidImmediateFailureThrash()
    {
        // Failing source path: spotify (service dependencies intentionally null).
        await _repository.AddPlaylistWatchlistAsync("spotify", "pl-fail", "Failing", null, null, null);
        // Successful/no-op source path: unsupported source branch.
        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-ok", "Noop", null, null, null);

        var hosted = new PlaylistWatchHostedService(_provider, NullLogger<PlaylistWatchHostedService>.Instance);
        var failKey = "playlist:spotify:pl-fail";

        await InvokeRunOnceAsync(hosted);
        var failuresAfterFirst = GetFailureMap(hosted);
        Assert.True(failuresAfterFirst.TryGetValue(failKey, out var firstFailures));
        Assert.Equal(1, firstFailures);

        // Immediate rerun should skip fail key due to nextAllowedRun backoff.
        await InvokeRunOnceAsync(hosted);
        var failuresAfterSecond = GetFailureMap(hosted);
        Assert.True(failuresAfterSecond.TryGetValue(failKey, out var secondFailures));
        Assert.Equal(1, secondFailures);

        // Force eligibility and rerun; failures should increment.
        var nextAllowed = GetNextAllowedMap(hosted);
        nextAllowed[failKey] = DateTimeOffset.UtcNow.AddSeconds(-1);
        await InvokeRunOnceAsync(hosted);
        var failuresAfterThird = GetFailureMap(hosted);
        Assert.True(failuresAfterThird.TryGetValue(failKey, out var thirdFailures));
        Assert.Equal(2, thirdFailures);
    }

    [Fact]
    public async Task RunOnce_CleansStaleFailureState_WhenWatchItemIsRemoved()
    {
        await _repository.AddPlaylistWatchlistAsync("spotify", "pl-stale", "StaleFailing", null, null, null);

        var hosted = new PlaylistWatchHostedService(_provider, NullLogger<PlaylistWatchHostedService>.Instance);
        var staleKey = "playlist:spotify:pl-stale";

        await InvokeRunOnceAsync(hosted);
        var failures = GetFailureMap(hosted);
        Assert.True(failures.ContainsKey(staleKey));

        await _repository.RemovePlaylistWatchlistAsync("spotify", "pl-stale");
        await InvokeRunOnceAsync(hosted);

        var failuresAfterCleanup = GetFailureMap(hosted);
        Assert.False(failuresAfterCleanup.ContainsKey(staleKey));
        var nextAllowedAfterCleanup = GetNextAllowedMap(hosted);
        Assert.False(nextAllowedAfterCleanup.ContainsKey(staleKey));
    }

    private static async Task InvokeRunOnceAsync(PlaylistWatchHostedService hosted)
    {
        var method = typeof(PlaylistWatchHostedService).GetMethod("RunOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(hosted, new object[] { CancellationToken.None });
        Assert.NotNull(result);
        await (Task)result!;
    }

    private static ConcurrentDictionary<string, int> GetFailureMap(PlaylistWatchHostedService hosted)
        => (ConcurrentDictionary<string, int>)GetPrivateField(hosted, "_consecutiveFailures");

    private static ConcurrentDictionary<string, DateTimeOffset> GetNextAllowedMap(PlaylistWatchHostedService hosted)
        => (ConcurrentDictionary<string, DateTimeOffset>)GetPrivateField(hosted, "_nextAllowedRun");

    private static object GetPrivateField(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(instance)!;
    }
}
