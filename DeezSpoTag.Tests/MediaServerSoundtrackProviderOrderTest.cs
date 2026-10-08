using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Guards the requested soundtrack provider behaviour: Deezer resolves first with
/// Spotify as the fallback, and no third-party scraping bridge participates in the
/// automatic path. Source-level assertions match the style of the existing
/// soundtrack behavior guardrails.
/// </summary>
public sealed class MediaServerSoundtrackProviderOrderTest
{
    [Fact]
    public void ResolveDirect_QueriesDeezerBeforeSpotifyAndMusicBrainz()
    {
        var source = ReadServiceSource();

        var deezerIndex = source.IndexOf("TryResolveDeezerSoundtrackMatchAsync(item, queries", StringComparison.Ordinal);
        var spotifyIndex = source.IndexOf("TryResolveSpotifySoundtrackMatchAsync(item, queries", StringComparison.Ordinal);
        var musicBrainzIndex = source.IndexOf("TryResolveMusicBrainzCuratedMatchAsync(item, queries", StringComparison.Ordinal);

        Assert.True(deezerIndex > 0, "Deezer resolution step is missing from ResolveSoundtrackDirectAsync.");
        Assert.True(spotifyIndex > 0, "Spotify fallback step is missing from ResolveSoundtrackDirectAsync.");
        Assert.True(musicBrainzIndex > 0, "MusicBrainz curated step is missing from ResolveSoundtrackDirectAsync.");
        Assert.True(deezerIndex < spotifyIndex, "Deezer must be queried before Spotify.");
        Assert.True(spotifyIndex < musicBrainzIndex, "Spotify must be queried before the MusicBrainz curated step.");
    }

    [Fact]
    public void AutomaticResolution_HasNoThirdPartyScrapingBridge()
    {
        var source = ReadServiceSource();

        Assert.DoesNotContain("r.jina.ai", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FetchSpotifyWebSearchMarkdownAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryResolveSpotifyWebSearchMatchAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapSpotifyWebCandidates", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ParseSpotifyWebSearchCandidates", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SpotifyMarkdownLinkPattern", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShouldTrySpotifyWebFallback", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DeezerSearch_UsesAnonymousPerEntityMethodsInsteadOfTheLoginGuardedOverload()
    {
        var source = ReadServiceSource();

        // DeezerClient.SearchAsync(query, type, options) calls EnsureLoggedIn and
        // throws without a session, so only the per-entity methods may be used.
        Assert.Contains("_deezerClient.SearchAlbumAsync(query, options)", source, StringComparison.Ordinal);
        Assert.Contains("_deezerClient.SearchPlaylistAsync(query, options)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_deezerClient.SearchAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DeezerMatches_UseUnprefixedKindAndPopulateDeezerIdSoProviderResolvesToDeezer()
    {
        var source = ReadServiceSource();

        Assert.Contains("const string MatchProviderDeezer = \"deezer\";", source, StringComparison.Ordinal);
        Assert.Contains("DeezerId = deezerId,", source, StringComparison.Ordinal);
        // Kind stays \"album\"/\"playlist\" unprefixed so ResolveMatchProvider reports
        // deezer without any extra provider plumbing.
        Assert.Contains("kind is MatchKindAlbum or MatchKindPlaylist or MatchKindTrack", source, StringComparison.Ordinal);
        Assert.Contains("return MatchProviderDeezer;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DeezerMatches_ReuseTheExistingScoringAndCompatibilityRules()
    {
        var source = ReadServiceSource();

        Assert.Contains("IsSoundtrackCandidateCompatible(item.Title, title, item.Year)", source, StringComparison.Ordinal);
        Assert.Contains("ComputeMatchScore(item.Title, title, item.Year)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SeasonDiscriminator_IsFixedAtTheRootRatherThanWorkedAroundPerCategory()
    {
        var source = ReadServiceSource();

        // The trailing-number check resolves a season as a season instead of running
        // sequel heuristics over it, and titles with no season keep the sequel path.
        Assert.Contains("IsSequenceDiscriminatorCompatible", source, StringComparison.Ordinal);
        Assert.Contains("var mediaSeason = ExtractSeasonNumber(mediaTitle);", source, StringComparison.Ordinal);
        Assert.Contains("return IsSequelCompatible(mediaTitle, candidateTitle);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsSoundtrackCandidateCompatibleForItem", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsSoundtrackCandidateCompatibleForUnit", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DeezerClient_IsInjectedIntoTheSoundtrackServiceDependencies()
    {
        var serviceSource = ReadServiceSource();
        var programSource = ReadSource("DeezSpoTag.Web", "Program.cs");

        Assert.Contains("public required SoundtrackDeezerClient DeezerClient { get; init; }", serviceSource, StringComparison.Ordinal);
        Assert.Contains("_deezerClient = dependencies.DeezerClient;", serviceSource, StringComparison.Ordinal);
        Assert.Contains("DeezerClient = sp.GetRequiredService<DeezSpoTag.Integrations.Deezer.DeezerClient>()", programSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualSpotifyLinkParsing_IsRetainedEvenThoughTheScraperIsGone()
    {
        var source = ReadServiceSource();

        Assert.Contains("SpotifyWebLinkPattern", source, StringComparison.Ordinal);
        Assert.Contains("TryParseSpotifyManualTarget", source, StringComparison.Ordinal);
        Assert.Contains("TryParseDeezerManualTarget", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistSearch_OnlyRunsWhenAlbumSearchDidNotReachTheFallbackThreshold()
    {
        var source = ReadServiceSource();

        Assert.Contains("ShouldTryDeezerPlaylistFallback(best)", source, StringComparison.Ordinal);
        Assert.Contains("best == null || best.Score < ProviderFallbackScoreThreshold", source, StringComparison.Ordinal);
    }

    private static string ReadServiceSource() => ReadSource("DeezSpoTag.Web", "Services", "MediaServerSoundtrackService.cs");

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
