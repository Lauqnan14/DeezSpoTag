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
            "PlaylistWatchService.cs"));

        Assert.Contains("var cachedCandidatesComplete = cachedCandidates is not null", source, StringComparison.Ordinal);
        Assert.Contains("if (cachedCandidatesComplete)", source, StringComparison.Ordinal);
        Assert.Contains("cached candidates are incomplete. Refreshing candidates.", source, StringComparison.Ordinal);
        Assert.Contains("FetchPlaylistTrackPageAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitored_tracklist_uses_persisted_owner_instead_of_source_as_author()
    {
        var source = File.ReadAllText(FindSourceFile(
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "LibraryPlaylistWatchlistApiController.cs"));

        Assert.Contains("playlist.OwnerName", source, StringComparison.Ordinal);
        Assert.DoesNotContain("creator = new { name = playlist.Source },", source, StringComparison.Ordinal);
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
