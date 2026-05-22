using System;
using System.Collections.Generic;
using System.IO;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class WatchlistSettingsBehaviorTests : IDisposable
{
    private const string WatchMaxItemsPerRunName = "WatchMaxItemsPerRun";
    private const string WatchMaxReleasesPerArtistName = "WatchMaxReleasesPerArtist";
    private const string WatchMaxReleasesPerArtistJsonName = "watchMaxReleasesPerArtist";
    private const string WatchMaxTracksPerPlaylistCheckJsonName = "watchMaxTracksPerPlaylistCheck";
    private const string WatchArtistTopSongsEnabledJsonName = "watchArtistTopSongsEnabled";
    private const string WatchArtistLatestReleasesOnlyJsonName = "watchArtistLatestReleasesOnly";
    private const string AlbumGroup = "album";
    private const string SingleGroup = "single";
    private const string AppearsOnGroup = "appears_on";
    private const string CompilationGroup = "compilation";

    private readonly string _tempRoot;
    private readonly TestConfigRootScope _configScope;
    private readonly DeezSpoTagSettingsService _settingsService;

    public WatchlistSettingsBehaviorTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-watchlist-settings-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _configScope = new TestConfigRootScope(_tempRoot);
        _settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
    }

    [Fact]
    public void SaveSettings_EmptyWatchlistAlbumGroups_RestoresDefaultGroups()
    {
        var settings = _settingsService.LoadSettings();
        settings.WatchedArtistAlbumGroup = new List<string>();

        _settingsService.SaveSettings(settings);

        var persisted = _settingsService.LoadSettings();
        Assert.Equal(new[] { AlbumGroup, SingleGroup }, persisted.WatchedArtistAlbumGroup);
    }

    [Fact]
    public void SaveSettings_WatchlistAlbumGroups_AreCanonicalAndDropUnknownValues()
    {
        var settings = _settingsService.LoadSettings();
        settings.WatchedArtistAlbumGroup = new List<string>
        {
            " Single ",
            "appearson",
            "compilations",
            "unsupported",
            SingleGroup
        };

        _settingsService.SaveSettings(settings);

        var persisted = _settingsService.LoadSettings();
        Assert.Equal(new[] { AppearsOnGroup, CompilationGroup, SingleGroup }, persisted.WatchedArtistAlbumGroup);
    }

    [Fact]
    public void SaveSettings_WatchMaxItemsPerRunAboveFifty_RestoresDefault()
    {
        var settings = _settingsService.LoadSettings();
        settings.WatchMaxItemsPerRun = 100;
        settings.WatchMaxReleasesPerArtist = 100;
        settings.WatchMaxTracksPerPlaylistCheck = 500;

        _settingsService.SaveSettings(settings);

        var persisted = _settingsService.LoadSettings();
        Assert.Equal(50, persisted.WatchMaxItemsPerRun);
        Assert.Equal(100, persisted.WatchMaxReleasesPerArtist);
        Assert.Equal(500, persisted.WatchMaxTracksPerPlaylistCheck);
    }

    [Fact]
    public void SaveSettings_InvalidNestedWatchLimits_NormalizeIndependently()
    {
        var settings = _settingsService.LoadSettings();
        settings.WatchMaxItemsPerRun = 25;
        settings.WatchMaxReleasesPerArtist = 101;
        settings.WatchMaxTracksPerPlaylistCheck = 501;

        _settingsService.SaveSettings(settings);

        var persisted = _settingsService.LoadSettings();
        Assert.Equal(25, persisted.WatchMaxItemsPerRun);
        Assert.Equal(50, persisted.WatchMaxReleasesPerArtist);
        Assert.Equal(50, persisted.WatchMaxTracksPerPlaylistCheck);
    }

    [Fact]
    public void ArtistWatch_NormalizesProviderAlbumGroups_ConsistentlyForSpotifyAppleAndDeezer()
    {
        var normalized = ArtistWatchService.NormalizeAlbumGroups(new[]
        {
            " Single ",
            "appears-on",
            "compile",
            "unsupported",
            SingleGroup
        });

        Assert.Equal(new[] { AppearsOnGroup, CompilationGroup, SingleGroup }, normalized);
    }

    [Fact]
    public void SettingsView_BlocksSavingWhenNoWatchlistAlbumGroupIsSelected()
    {
        var repoRoot = ResolveRepoRoot();
        var viewPath = Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "Settings", "Index.cshtml");
        Assert.True(File.Exists(viewPath), $"Missing settings view: {viewPath}");

        var source = File.ReadAllText(viewPath);
        Assert.Contains("Select at least one release group for artist watchlist monitoring.", source, StringComparison.Ordinal);
        Assert.Contains("Select at least one artist album group.", source, StringComparison.Ordinal);
        Assert.Contains("settings.watchedArtistAlbumGroup.length === 0", source, StringComparison.Ordinal);
        Assert.Contains("Max Monitored Items Per Run", source, StringComparison.Ordinal);
        Assert.Contains(WatchMaxReleasesPerArtistJsonName, source, StringComparison.Ordinal);
        Assert.Contains(WatchMaxTracksPerPlaylistCheckJsonName, source, StringComparison.Ordinal);
        Assert.Contains(WatchArtistTopSongsEnabledJsonName, source, StringComparison.Ordinal);
        Assert.Contains(WatchArtistLatestReleasesOnlyJsonName, source, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchMaxItemsPerRun_IsOnlyUsedBySchedulerAndSettingsContract()
    {
        var repoRoot = ResolveRepoRoot();
        var serviceRoot = Path.Join(repoRoot, "DeezSpoTag.Web", "Services");

        var artistWatchSource = File.ReadAllText(Path.Join(serviceRoot, "ArtistWatchService.cs"));
        var playlistWatchSource = File.ReadAllText(Path.Join(serviceRoot, "PlaylistWatchService.cs"));
        var hostedSource = File.ReadAllText(Path.Join(serviceRoot, "PlaylistWatchHostedService.cs"));
        var repoSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));
        var artistControllerSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "LibraryWatchlistApiController.cs"));
        var watchlistScriptSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "library-watchlists.js"));

        Assert.DoesNotContain(WatchMaxItemsPerRunName, artistWatchSource, StringComparison.Ordinal);
        Assert.DoesNotContain(WatchMaxItemsPerRunName, playlistWatchSource, StringComparison.Ordinal);
        Assert.Contains(WatchMaxItemsPerRunName, hostedSource, StringComparison.Ordinal);
        Assert.Contains(WatchMaxReleasesPerArtistName, artistWatchSource, StringComparison.Ordinal);
        Assert.Contains("WatchArtistTopSongsEnabled", artistWatchSource, StringComparison.Ordinal);
        Assert.Contains("WatchArtistLatestReleasesOnly", artistWatchSource, StringComparison.Ordinal);
        Assert.Contains("top-track:", artistWatchSource, StringComparison.Ordinal);
        Assert.Contains("artist-top:", artistWatchSource, StringComparison.Ordinal);
        Assert.Contains("UpdateWatchlistPreferencesAsync", repoSource, StringComparison.Ordinal);
        Assert.Contains("{artistId:long}/preferences", artistControllerSource, StringComparison.Ordinal);
        Assert.Contains("/api/library/watchlist/${encodeURIComponent(artistId)}/preferences", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("watchedArtistAlbumGroup", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("watchArtistTopSongsEnabled", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("watchArtistLatestReleasesOnly", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("preferredEngine", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("routingRules", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("data-artist-engine", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("data-artist-routing-rules", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("PreferredEngine", artistWatchSource, StringComparison.Ordinal);
        Assert.Contains("RoutingRules", artistWatchSource, StringComparison.Ordinal);
        Assert.Contains("preferred_engine", repoSource, StringComparison.Ordinal);
        Assert.Contains("routing_rules_json", repoSource, StringComparison.Ordinal);
        Assert.Contains("NormalizePreferredEngine", artistControllerSource, StringComparison.Ordinal);
        Assert.Contains("NormalizeRoutingRules", artistControllerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatch_RefreshesFullSnapshotAndUsesSnapshotCache()
    {
        var repoRoot = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "PlaylistWatchService.cs"));

        Assert.Contains("WatchUseSnapshotIdChecking", source, StringComparison.Ordinal);
        Assert.Contains("var maxCandidates = MaxPlaylistCandidateFetchCount;", source, StringComparison.Ordinal);
        Assert.Contains("FetchLivePlaylistSnapshotAsync(source, sourceId, maxCandidates, cancellationToken)", source, StringComparison.Ordinal);
        Assert.Contains("UpdatePlaylistWatchlistMetadataAsync", source, StringComparison.Ordinal);
        Assert.Contains("RemovePlaylistWatchTracksNotInAsync", source, StringComparison.Ordinal);
        Assert.Contains("FetchPlaylistPageAsync(", source, StringComparison.Ordinal);
        Assert.Contains("Math.Min(100, maxCandidates)", source, StringComparison.Ordinal);
        Assert.Contains("while (candidates.Count < maxCandidates)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistPreferenceApi_NormalizesAndValidatesIncomingSettings()
    {
        var repoRoot = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "LibraryPlaylistWatchlistApiController.cs"));

        Assert.Contains("GetValidFolderIdsAsync", source, StringComparison.Ordinal);
        Assert.Contains("ValidatePlaylistPreferenceRequest", source, StringComparison.Ordinal);
        Assert.Contains("NormalizePreferredEngine", source, StringComparison.Ordinal);
        Assert.Contains("NormalizeDownloadVariantMode", source, StringComparison.Ordinal);
        Assert.Contains("NormalizeSyncMode", source, StringComparison.Ordinal);
        Assert.Contains("NormalizeRoutingRules", source, StringComparison.Ordinal);
        Assert.Contains("NormalizeBlockRules", source, StringComparison.Ordinal);
        Assert.Contains("Routing destination folder was not found or is disabled.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtistPreferenceApi_RejectsNullRequestBody()
    {
        var repoRoot = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "LibraryWatchlistApiController.cs"));

        Assert.Contains("if (request is null)", source, StringComparison.Ordinal);
        Assert.Contains("Artist watchlist preference request is required.", source, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _configScope.Dispose();
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Join(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test output path.");
    }
}
