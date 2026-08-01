using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtistMetadataTargetSelectionGuardrailTests
{
    [Theory]
    [InlineData("spotify", "Spotify")]
    [InlineData("apple", "Apple")]
    [InlineData("tidal", "Tidal")]
    [InlineData("qobuz", "Qobuz")]
    [InlineData("lastfm", "LastFm")]
    public void Artist_metadata_cache_refresh_accepts_only_closed_biography_providers(
        string source,
        string expectedProvider)
    {
        var parseProvider = typeof(ArtistMetadataCacheRefreshService).GetMethod(
            "ParseProvider",
            BindingFlags.NonPublic | BindingFlags.Static);
        var resolveBiography = typeof(ArtistMetadataCacheRefreshService).GetMethod(
            "ResolveBiographyAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(parseProvider);
        Assert.NotNull(resolveBiography);
        Assert.Equal(expectedProvider, parseProvider!.Invoke(null, new object?[] { source })?.ToString());
        Assert.True(resolveBiography!.GetParameters()[0].ParameterType.IsEnum);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("spotify\r\nforged")]
    [InlineData("unsupported")]
    public void Artist_metadata_cache_refresh_rejects_arbitrary_biography_provider_text(string source)
    {
        var parseProvider = typeof(ArtistMetadataCacheRefreshService).GetMethod(
            "ParseProvider",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(parseProvider);
        Assert.Null(parseProvider!.Invoke(null, new object?[] { source }));
    }

    [Fact]
    public void Metadata_updater_controls_use_checkboxes_for_all_target_servers()
    {
        var controls = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "Views",
            "Shared",
            "_ArtistMetadataUpdaterControls.cshtml"));
        var activities = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "Views",
            "Activities",
            "Index.cshtml"));

        Assert.Contains("data-metadata-target=\"plex\"", controls);
        Assert.Contains("data-metadata-target=\"jellyfin\"", controls);
        Assert.Contains("data-metadata-target=\"navidrome\"", controls);
        Assert.Contains("id=\"metadata-library-folder\"", controls);
        Assert.Contains("id=\"metadata-missing-artist-art-only\"", controls);
        Assert.Contains("metadata-updater-option-group", controls);
        Assert.Contains("metadata-updater-checkbox-grid", controls);
        Assert.Contains("Missing artist art targets", controls);
        Assert.Contains("metadata-updater-tooltip-icon", controls);
        Assert.Contains("The selected update fields still apply.", controls);
        Assert.Contains("id=\"metadata-include-avatar\" checked", controls);
        Assert.Contains("id=\"metadata-include-background\" checked", controls);
        Assert.Contains("id=\"metadata-include-bio\"", controls);
        Assert.Contains("id=\"metadata-ocr-text-art-blocking\" checked", controls);
        Assert.Contains("id=\"metadata-save-settings-button\"", activities);
        Assert.Contains("Save Settings", activities);
        Assert.DoesNotContain("<option value=\"both\">", controls, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Artist_page_uses_target_checkboxes_and_artist_sync_block_control()
    {
        var artistPage = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "Views",
            "Library",
            "Artist.cshtml"));

        Assert.Contains("data-artist-sync-target=\"plex\"", artistPage);
        Assert.Contains("data-artist-sync-target=\"jellyfin\"", artistPage);
        Assert.Contains("data-artist-sync-target=\"navidrome\"", artistPage);
        Assert.Contains("id=\"artist-sync-blocked\"", artistPage);
        Assert.DoesNotContain("<option value=\"both\">", artistPage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JavaScript_sends_target_arrays_for_artist_and_activity_metadata_sync()
    {
        var libraryJs = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "wwwroot",
            "js",
            "library.js"));
        var activities = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "Views",
            "Activities",
            "Index.cshtml"));

        Assert.Contains("targets: normalizedTargets", libraryJs);
        Assert.Contains("body: JSON.stringify({ targets: getArtistSyncTargets() })", libraryJs);
        Assert.Contains("const targets = getMetadataUpdaterTargets();", activities);
        Assert.Contains("targets,", activities);
        Assert.Contains("const missingArtistArtworkOnly = missingArtistArtOnlyCheckbox?.checked === true;", activities);
        Assert.DoesNotContain("const targets = missingArtistArtworkOnly ? ['plex'] : getMetadataUpdaterTargets();", activities);
        Assert.Contains("folderId: folderId > 0 ? folderId : null", activities);
        Assert.Contains("missingArtistArtworkOnly,", activities);
        Assert.Contains("includeAvatar: includeAvatarCheckbox?.checked !== false", activities);
        Assert.Contains("includeBackground: includeBackgroundCheckbox?.checked !== false", activities);
        Assert.Contains("includeBio: includeBioCheckbox?.checked === true", activities);
        Assert.Contains("includePopularSongs: popularSongsCheckbox?.checked === true", activities);
        Assert.Contains("loadMetadataUpdaterFolders", activities);
        Assert.Contains("/api/library/folders?contentType=music", activities);
        Assert.Contains("await persistMetadataUpdaterSettings();", activities);
        Assert.Contains("await runMetadataUpdater(true);", activities);
        Assert.Contains("force: forceManualRun === true || missingArtistArtworkOnly || intervalDays === 0", activities);
    }

    [Fact]
    public void Artist_metadata_updater_preferences_are_persisted_and_hydrated()
    {
        var preferences = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "Services",
            "UserPreferencesStore.cs"));
        var layout = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "Views",
            "Shared",
            "_Layout.cshtml"));
        var userPreferencesJs = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "wwwroot",
            "js",
            "user-preferences.js"));

        Assert.Contains("public List<string> MetadataUpdaterTargets", preferences);
        Assert.Contains("public string MetadataUpdaterSource", preferences);
        Assert.Contains("public string? MetadataUpdaterFolderId", preferences);
        Assert.Contains("public int MetadataCacheRefreshIntervalDays", preferences);
        Assert.Contains("public int MetadataTargetUpdateIntervalDays", preferences);
        Assert.Contains("public bool MetadataUpdaterIncludeAvatar", preferences);
        Assert.Contains("public bool MetadataUpdaterIncludeBackground", preferences);
        Assert.Contains("public bool MetadataUpdaterIncludeBio", preferences);
        Assert.Contains("public bool MetadataUpdaterIncludePopularSongs", preferences);
        Assert.Contains("public bool MetadataUpdaterMissingArtistArtworkOnly", preferences);
        Assert.Contains("public bool MetadataUpdaterOcrTextArtBlocking", preferences);

        Assert.Contains("'deezspotag-metadata-updater-targets': 'metadataUpdaterTargets'", layout);
        Assert.Contains("'deezspotag-metadata-updater-targets':   'metadataUpdaterTargets'", userPreferencesJs);
        Assert.Contains("'deezspotag-metadata-cache-refresh-interval-days': 'metadataCacheRefreshIntervalDays'", layout);
        Assert.Contains("'deezspotag-metadata-target-update-interval-days': 'metadataTargetUpdateIntervalDays'", layout);
        Assert.Contains("'deezspotag-metadata-cache-refresh-interval-days': 'metadataCacheRefreshIntervalDays'", userPreferencesJs);
        Assert.Contains("'deezspotag-metadata-target-update-interval-days': 'metadataTargetUpdateIntervalDays'", userPreferencesJs);
        Assert.Contains("window.UserPrefs.set('metadataUpdaterTargets', targets);", File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "Views",
            "Activities",
            "Index.cshtml")));
    }

    [Fact]
    public void Artist_metadata_updater_reports_not_due_skips()
    {
        var updater = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "Services",
            "ArtistMetadataUpdaterService.cs"));
        var activities = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "Views",
            "Activities",
            "Index.cshtml"));

        Assert.Contains("SkippedNotDue", updater);
        Assert.Contains("public const string NotDue = \"notDue\";", updater);
        Assert.Contains("SkipReasons", updater);
        Assert.Contains("BuildCompletionMessage(counters)", updater);
        Assert.Contains("formatMetadataSkipReason", activities);
        Assert.Contains("case 'notDue':", activities);
    }

    [Fact]
    public void Backend_accepts_legacy_both_but_declares_navidrome_limited_capability()
    {
        var controller = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "SpotifyCacheApiController.cs"));
        var updater = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "Services",
            "ArtistMetadataUpdaterService.cs"));

        Assert.Matches(new Regex("normalized\\s*==\\s*\"both\"", RegexOptions.Multiline), controller);
        Assert.Contains("LegacyBothTargets = \"both\"", updater);
        Assert.Contains("MissingArtistArtworkOnly", updater);
        Assert.Contains("SeedMissingArtistArtworkCandidatesAsync", updater);
        Assert.Contains("BuildMissingArtistArtworkPlanAsync", updater);
        Assert.Contains("FindPlexMissingArtistArtworkAsync", updater);
        Assert.Contains("FindJellyfinMissingArtistArtworkAsync", updater);
        Assert.Contains("FindNavidromeMissingArtistArtworkAsync", updater);
        Assert.Contains("OrderByDescending(item => missingByTarget.TryGetValue(item.Target, out var ids) ? ids.Count : 0)", updater);
        Assert.Contains("Navidrome biography is read-only and was not updated.", updater);
        Assert.DoesNotContain("navidromeBiographyAvailable", updater);
        Assert.DoesNotContain("updates.BackgroundUpdated = navidromeBackgroundAvailable", updater);
        Assert.DoesNotContain("Targets = new List<string> { PlexTarget }", updater);
        Assert.Contains("GetArtistsAsync(\"all\", request.FolderId", updater);
        Assert.Contains("server = \"navidrome\"", controller);
        Assert.Contains("Navidrome exposes one artist image slot", controller);
        Assert.Contains("large artist image is used as the background-equivalent", controller);
        Assert.Contains("does not expose an HTTP biography write endpoint", controller);
        Assert.Contains("[HttpGet(\"artist-metadata/audit\")]", controller);
    }

    [Fact]
    public void ArtistArtworkBlocking_StoresSelectedSlotAliases()
    {
        var controller = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "SpotifyCacheApiController.cs"));
        var libraryScript = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "wwwroot",
            "js",
            "library.js"));

        Assert.Contains("ResolveArtistArtworkBlockAliases", controller);
        Assert.Contains("slot:{role}:{localPath}", controller);
        Assert.Contains("file:{localPath}", controller);
        Assert.Contains("visualUrl: visualUrl || null", libraryScript);
        Assert.Contains("localPath: selectedPath || null", libraryScript);
    }

    [Fact]
    public void ArtistVisualPicker_CachesProviderImagesInsideApp()
    {
        var controller = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "LibraryArtistArtworkApiController.cs"));
        var cacheService = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "Services",
            "ArtistArtworkCatalogService.cs"));
        var libraryScript = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "wwwroot",
            "js",
            "library.js"));

        Assert.Contains("[HttpGet(\"{id:long}/artwork\")]", controller);
        Assert.Contains("[HttpPost(\"{id:long}/artwork/refresh\")]", controller);
        Assert.Contains("library-artist-images", cacheService);
        Assert.Contains("artist_artwork_cache", File.ReadAllText(Path.Combine(RepositoryRoot(), "DeezSpoTag.Services", "Library", "LibraryRepository.cs")));
        Assert.Contains("/artwork`", libraryScript);
        Assert.Contains("/artwork/refresh?force=true", libraryScript);
        Assert.Contains("cachedPickerImages", libraryScript);
        Assert.Contains("mergeArtistVisualPickerResult", libraryScript);
        Assert.DoesNotContain("visuals.cachedPickerImages = []", libraryScript);
        Assert.DoesNotContain("_cacheRefresh", controller);
        Assert.Contains("_artwork.RefreshAsync(", controller);
        Assert.DoesNotContain("/visuals/cache", libraryScript);
        Assert.DoesNotContain("loadDeezerArtistVisuals", libraryScript);
        Assert.DoesNotContain("loadAppleArtistVisuals", libraryScript);
    }

    [Fact]
    public void ArtistArtworkCatalog_IsTheSingleProviderAndCachePath()
    {
        var root = RepositoryRoot();
        var catalog = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web", "Services", "ArtistArtworkCatalogService.cs"));
        var updater = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web", "Services", "ArtistMetadataUpdaterService.cs"));
        var imageQueue = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web", "Services", "LibraryArtistImageQueueService.cs"));
        var search = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web", "Services", "DeezSpoTagSearchService.cs"));

        foreach (var provider in new[] { "local", "spotify", "deezer", "itunes", "tidal", "qobuz", "lastfm" })
        {
            Assert.Contains($"\"{provider}\"", catalog);
        }
        Assert.DoesNotContain("ResolveAppleAsync", catalog);

        Assert.Contains("ArtistArtworkCatalogService _artistArtworkCatalog", updater);
        Assert.Contains("ArtistArtworkCatalogService _artworkCatalog", imageQueue);
        Assert.Contains("GetArtistArtworkCacheAsync", catalog);
        Assert.Contains("UpsertArtistArtworkCacheAsync", catalog);
        Assert.Contains("Image.LoadAsync", catalog);
        Assert.Contains("File.Move(temp, final, true)", catalog);
        Assert.Contains("item is Newtonsoft.Json.Linq.JToken", search);
        Assert.False(File.Exists(Path.Combine(root, "DeezSpoTag.Web", "Services", "ArtistVisualCacheService.cs")));
        Assert.False(File.Exists(Path.Combine(root, "DeezSpoTag.Web", "Services", "SpotifyArtistImageCacheService.cs")));
    }

    [Fact]
    public void ArtistVisualPicker_IncludesSpotifyProfileHeaderAndGallery()
    {
        var root = RepositoryRoot();
        var catalog = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web", "Services", "ArtistArtworkCatalogService.cs"));

        Assert.Contains("page.Artist.HeaderImageUrl", catalog);
        Assert.Contains("page.Artist.Gallery", catalog);
    }

    [Fact]
    public void ArtistTopTracks_AreLibrespotEnrichedBeforeDeezerLinking()
    {
        var root = RepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web", "Services", "SpotifyArtistService.cs"));
        var librespot = service.IndexOf("TopTracks = await EnrichTopTracksWithIsrcsAsync(result.TopTracks", StringComparison.Ordinal);
        var deezer = service.IndexOf("result = await TryEnrichWithDeezerLinksAsync", StringComparison.Ordinal);

        Assert.True(librespot >= 0);
        Assert.True(deezer > librespot);
    }

    [Fact]
    public void ArtistMetadataAutomation_HasTwoIsolatedOperationsAndOneCoordinator()
    {
        var root = RepositoryRoot();
        var cacheRefresh = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web", "Services", "ArtistMetadataCacheRefreshService.cs"));
        var targetUpdate = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web", "Services", "ArtistMetadataUpdaterService.cs"));
        var popularSongs = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web", "Services", "ArtistPopularSongsSyncService.cs"));
        var spotifyArtist = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web", "Services", "SpotifyArtistService.cs"));
        var coordinator = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web", "Services", "ArtistMetadataAutomationCoordinator.cs"));
        var controller = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web", "Controllers", "Api", "ArtistMetadataAutomationApiController.cs"));
        var spotifyController = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web", "Controllers", "Api", "SpotifyCacheApiController.cs"));
        var program = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web", "Program.cs"));

        Assert.Contains("ArtistArtworkCatalogService _artworkCatalog", cacheRefresh);
        Assert.Contains("forceProviderRefresh: true", cacheRefresh);
        Assert.Contains("UpsertArtistBiographyCacheAsync", cacheRefresh);
        Assert.DoesNotContain("PlexApiClient", cacheRefresh);
        Assert.DoesNotContain("JellyfinApiClient", cacheRefresh);
        Assert.DoesNotContain("NavidromeApiClient", cacheRefresh);

        Assert.Contains("GetArtistBiographyCacheAsync", targetUpdate);
        Assert.Contains("_artistArtworkCatalog.GetAsync(artistId, cancellationToken)", targetUpdate);
        Assert.DoesNotContain("forceRefresh: true", targetUpdate);
        Assert.DoesNotContain("ITidalAccessTokenProvider", targetUpdate);
        Assert.DoesNotContain("QobuzArtistService", targetUpdate);
        Assert.DoesNotContain("LastFmArtistImageService", targetUpdate);
        Assert.Contains("TryGetCachedArtistPageAsync", popularSongs);
        Assert.DoesNotContain("GetArtistPageAsync(", popularSongs);
        var cachedMethodStart = spotifyArtist.IndexOf("public async Task<SpotifyArtistPageResult?> TryGetCachedArtistPageAsync", StringComparison.Ordinal);
        var cachedMethodEnd = spotifyArtist.IndexOf("private async Task<", cachedMethodStart, StringComparison.Ordinal);
        var cachedMethod = spotifyArtist[cachedMethodStart..cachedMethodEnd];
        Assert.DoesNotContain("_pathfinderMetadataClient", cachedMethod);
        Assert.DoesNotContain("TryHydrateCachedBiographyAsync", cachedMethod);

        var cacheRun = coordinator.IndexOf("if (cacheDue)", StringComparison.Ordinal);
        var targetRun = coordinator.IndexOf("if (updateDue)", StringComparison.Ordinal);
        Assert.True(cacheRun >= 0 && targetRun > cacheRun);
        Assert.Contains("public sealed class ArtistMetadataAutomationCoordinator : BackgroundService", coordinator);
        Assert.Contains("api/library/artist-metadata", controller);
        Assert.Contains("cache/refresh", controller);
        Assert.Contains("targets/update", controller);
        Assert.DoesNotContain("metadata-updater/run", spotifyController);
        Assert.DoesNotContain("[HttpPost(\"refresh\")]", spotifyController);
        Assert.DoesNotContain("ArtistExternalMetadataBackfillService", program);
        Assert.False(File.Exists(Path.Combine(root, "DeezSpoTag.Web", "Services", "ArtistExternalMetadataBackfillService.cs")));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeezSpoTag.Web", "DeezSpoTag.Web.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
