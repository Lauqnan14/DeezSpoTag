using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class MonitoredPlaylistPresentationGuardrailTests
{
    [Fact]
    public void Incomplete_candidate_cache_is_refreshed_even_when_snapshot_is_unchanged()
    {
        var source = File.ReadAllText(FindSourceFile(
            "DeezSpoTag.Web",
            "Services",
            "WatchlistEngine.cs"));

        Assert.Contains("var cachedCandidatesComplete = cachedCandidates is not null", source, StringComparison.Ordinal);
        Assert.Contains("if (cachedCandidatesComplete)", source, StringComparison.Ordinal);
        Assert.Contains("cached candidates are incomplete. Refreshing candidates.", source, StringComparison.Ordinal);
        Assert.Contains("FetchPlaylistTrackPageAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitored_playlist_metadata_persists_owner_without_a_cached_tracklist_author_path()
    {
        var watchServiceSource = File.ReadAllText(FindSourceFile(
            "DeezSpoTag.Web",
            "Services",
            "WatchlistEngine.cs"));
        var controllerSource = File.ReadAllText(FindSourceFile(
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "LibraryPlaylistWatchlistApiController.cs"));

        Assert.Contains("OwnerName", watchServiceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("creator = new { name = playlist.Source },", controllerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitored_tracklist_does_not_use_a_separate_cached_tracklist_renderer()
    {
        var viewSource = File.ReadAllText(FindSourceFile(
            "DeezSpoTag.Web",
            "Views",
            "Tracklist",
            "Index.cshtml"));

        Assert.DoesNotContain("loadMonitoredPlaylistGlobalTracklist", viewSource, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/library/playlists/tracklist/", viewSource, StringComparison.Ordinal);
        Assert.Contains("preloadMonitoredPlaylistTrackStatuses", viewSource, StringComparison.Ordinal);
        Assert.Contains("shouldShowMonitoredStateColumn", viewSource, StringComparison.Ordinal);

        var controllerSource = File.ReadAllText(FindSourceFile(
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "LibraryPlaylistWatchlistApiController.cs"));

        Assert.DoesNotContain("[HttpGet(\"tracklist/{source}/{sourceId}\")]", controllerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitored_playlist_state_column_accepts_every_backend_location_status()
    {
        var viewSource = File.ReadAllText(FindSourceFile(
            "DeezSpoTag.Web",
            "Views",
            "Tracklist",
            "Index.cshtml"));
        var controllerSource = File.ReadAllText(FindSourceFile(
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "LibraryPlaylistWatchlistApiController.cs"));

        string[] statuses =
        [
            "blocked",
            "queued",
            "downloading",
            "paused",
            "retrying",
            "failed",
            "cancelled",
            "review",
            "redirected",
            "synced",
            "waiting_for_target",
            "downloaded",
            "library",
            "unavailable",
            "missing"
        ];

        foreach (var status in statuses)
        {
            Assert.Contains($"\"{status}\"", controllerSource, StringComparison.Ordinal);
            Assert.Contains($"'{status}'", viewSource, StringComparison.Ordinal);
        }

        string[] styledStatuses =
        [
            "synced",
            "redirected",
            "waiting_for_target",
            "downloaded",
            "review"
        ];

        foreach (var status in styledStatuses)
        {
            Assert.Contains($"track-location-status-pill--{status}", viewSource, StringComparison.Ordinal);
        }
    }

    private static string FindSourceFile(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate {Path.Combine(pathParts)}.");
    }
}
