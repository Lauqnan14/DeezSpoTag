using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtistMetadataTargetSelectionGuardrailTests
{
    [Fact]
    public void Metadata_updater_controls_use_checkboxes_for_all_target_servers()
    {
        var controls = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "Views",
            "Shared",
            "_ArtistMetadataUpdaterControls.cshtml"));

        Assert.Contains("data-metadata-target=\"plex\"", controls);
        Assert.Contains("data-metadata-target=\"jellyfin\"", controls);
        Assert.Contains("data-metadata-target=\"navidrome\"", controls);
        Assert.Contains("id=\"metadata-library-folder\"", controls);
        Assert.Contains("id=\"metadata-missing-artist-art-only\"", controls);
        Assert.Contains("Only artists missing artist art (Plex)", controls);
        Assert.Contains("id=\"metadata-include-avatar\" checked", controls);
        Assert.Contains("id=\"metadata-include-background\" checked", controls);
        Assert.Contains("id=\"metadata-include-bio\"", controls);
        Assert.Contains("id=\"metadata-ocr-text-art-blocking\" checked", controls);
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
        Assert.Contains("const targets = missingArtistArtworkOnly ? ['plex'] : getMetadataUpdaterTargets();", activities);
        Assert.Contains("folderId: folderId > 0 ? folderId : null", activities);
        Assert.Contains("missingArtistArtworkOnly,", activities);
        Assert.Contains("includeAvatar: includeAvatarCheckbox?.checked !== false", activities);
        Assert.Contains("includeBackground: includeBackgroundCheckbox?.checked !== false", activities);
        Assert.Contains("includeBio: includeBioCheckbox?.checked === true", activities);
        Assert.Contains("includePopularSongs: popularSongsCheckbox?.checked === true", activities);
        Assert.Contains("loadMetadataUpdaterFolders", activities);
        Assert.Contains("/api/library/folders?contentType=music", activities);
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
        Assert.Contains("Target = PlexTarget", updater);
        Assert.Contains("Targets = new List<string> { PlexTarget }", updater);
        Assert.Contains("IncludeAvatar = request.IncludeAvatar", updater);
        Assert.Contains("IncludeBackground = request.IncludeBackground", updater);
        Assert.Contains("IncludeBio = request.IncludeBio", updater);
        Assert.Contains("IncludePopularSongs = request.IncludePopularSongs", updater);
        Assert.Contains("string.IsNullOrWhiteSpace(artist.PreferredImagePath)", updater);
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
            "LibraryArtistVisualSelectionApiController.cs"));
        var cacheService = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "Services",
            "ArtistVisualCacheService.cs"));
        var libraryScript = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "DeezSpoTag.Web",
            "wwwroot",
            "js",
            "library.js"));

        Assert.Contains("[HttpPost(\"{id:long}/visuals/cache\")]", controller);
        Assert.Contains("library-artist-images", cacheService);
        Assert.Contains("CacheArtistVisualPickerItems", libraryScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cachedPickerImages", libraryScript);
        Assert.Contains("mergeCachedArtistVisuals", libraryScript);
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
