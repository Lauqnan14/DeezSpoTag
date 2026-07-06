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
        Assert.Contains("server = \"navidrome\"", controller);
        Assert.Contains("not direct artist metadata writes", controller);
        Assert.Contains("[HttpGet(\"artist-metadata/audit\")]", controller);
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
