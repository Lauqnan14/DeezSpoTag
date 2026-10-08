using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Guards the TV card identities in the soundtracks UI. Episodes previously sent the
/// show's id and title, so an episode action silently overwrote the show-level
/// soundtrack, and season cards offered no soundtrack action at all.
/// </summary>
public sealed class MediaServerSoundtrackEpisodeIdentityTest
{
    [Fact]
    public void EpisodeCards_ResolveAgainstTheEpisodeNotTheShow()
    {
        var source = ReadSoundtrackScript();

        Assert.Contains("const episodeId = String(item?.episodeId || '').trim();", source, StringComparison.Ordinal);
        Assert.Contains("data-soundtrack-item-id=\"${escapeHtml(episodeId)}\"", source, StringComparison.Ordinal);
        Assert.Contains("data-soundtrack-title=\"${escapeHtml(episodeTitle)}\"", source, StringComparison.Ordinal);

        // The show is still carried as a query qualifier, just never as the identity.
        Assert.Contains("data-soundtrack-show-title=\"${escapeHtml(showTitle)}\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("selectedShow.showId || '').trim();\n    const resolveTitle", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EpisodeCards_SendTheEpisodeCategorySoTheBackendKeepsEpisodeIdentity()
    {
        var source = ReadSoundtrackScript();

        Assert.Contains("data-soundtrack-category=\"tv_episode\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SeasonCards_OfferSoundtrackActionsAndSendTheSeasonCategory()
    {
        var source = ReadSoundtrackScript();

        Assert.Contains("data-soundtrack-category=\"tv_season\"", source, StringComparison.Ordinal);
        Assert.Contains("buildSoundtrackActionMenu(seasonResolveAttributes", source, StringComparison.Ordinal);
        Assert.Contains("data-soundtrack-season-number=", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SeasonCards_KeepBrowsingEpisodesAsTheArtAction()
    {
        var source = ReadSoundtrackScript();

        // The season soundtrack is reached from the action menu so navigating into
        // the season is never hijacked by a resolved tracklist.
        Assert.Contains("data-soundtrack-open-season=\"${escapeHtml(seasonId)}\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvePayload_CarriesShowAndSeasonContext()
    {
        var source = ReadSoundtrackScript();

        Assert.Contains("button?.dataset?.soundtrackShowTitle", source, StringComparison.Ordinal);
        Assert.Contains("button?.dataset?.soundtrackSeasonTitle", source, StringComparison.Ordinal);
        Assert.Contains("button?.dataset?.soundtrackSeasonNumber", source, StringComparison.Ordinal);
        Assert.Contains("seasonNumber: seasonNumber", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionMenu_ExposesAnOpenTracklistItemWhenAMatchExists()
    {
        var source = ReadSoundtrackScript();

        Assert.Contains("const tracklistItem = options.tracklistTarget", source, StringComparison.Ordinal);
        Assert.Contains(">Open Tracklist</button>", source, StringComparison.Ordinal);
        Assert.Contains(
            ".soundtrack-card-open-tracklist-btn, [data-soundtrack-open-tracklist=\"true\"]",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SeasonCards_ReceiveTheSelectedShowContext()
    {
        var source = ReadSoundtrackScript();

        Assert.Contains(
            "buildSoundtrackTvSeasonCardMarkup(season, soundtrackState.selectedTvShow)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TracklistManualMatch_KeepsSeasonAndEpisodeCategoriesDistinct()
    {
        var source = ReadSource("DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml");

        // Season and episode contexts must not be folded into tv_show/movie, or the
        // manual match round trip would write over the show or the movie identity.
        Assert.Contains("return 'tv_season';", source, StringComparison.Ordinal);
        Assert.Contains("return 'tv_episode';", source, StringComparison.Ordinal);
        Assert.Contains("contextType === 'tv_season' || contextType === 'season'", source, StringComparison.Ordinal);
        Assert.Contains("contextType === 'tv_episode' || contextType === 'episode'", source, StringComparison.Ordinal);
    }

    private static string ReadSoundtrackScript() => ReadSource("DeezSpoTag.Web", "wwwroot", "js", "library-soundtracks.js");

    private static string ReadSource(params string[] relativePath)
    {
        var path = Path.Join(new[] { ResolveRepoRoot() }.Concat(relativePath).ToArray());
        Assert.True(File.Exists(path), $"Missing source file: {path}");
        return File.ReadAllText(path);
    }

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not resolve repository root.");
    }
}
