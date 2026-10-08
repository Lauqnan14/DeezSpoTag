using System;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the TV season/episode soundtrack unit split. The new item categories are
/// what keeps season and episode rows out of the TV Shows grid, and the season
/// compatibility relaxation is what stops "Season N" from being read as a sequel
/// number and rejecting the correct season album.
/// </summary>
public sealed class MediaServerSoundtrackTvUnitTest
{
    [Fact]
    public void NormalizeItemCategory_PreservesSeasonAndEpisodeIdentities()
    {
        Assert.Equal(
            MediaServerSoundtrackConstants.TvSeasonCategory,
            InvokeNormalizeItemCategory("tv_season"));
        Assert.Equal(
            MediaServerSoundtrackConstants.TvSeasonCategory,
            InvokeNormalizeItemCategory("season"));
        Assert.Equal(
            MediaServerSoundtrackConstants.TvEpisodeCategory,
            InvokeNormalizeItemCategory("tv_episode"));
        Assert.Equal(
            MediaServerSoundtrackConstants.TvEpisodeCategory,
            InvokeNormalizeItemCategory("episode"));
    }

    [Fact]
    public void NormalizeItemCategory_StillNormalizesShowsAndMovies()
    {
        Assert.Equal(MediaServerSoundtrackConstants.TvShowCategory, InvokeNormalizeItemCategory("tv_show"));
        Assert.Equal(MediaServerSoundtrackConstants.TvShowCategory, InvokeNormalizeItemCategory("series"));
        Assert.Equal(MediaServerSoundtrackConstants.MovieCategory, InvokeNormalizeItemCategory("movie"));
        Assert.Equal(MediaServerSoundtrackConstants.MovieCategory, InvokeNormalizeItemCategory(null));
    }

    [Fact]
    public void LibraryCategoryNormalization_IsUnchangedByTheNewItemCategories()
    {
        // Library settings must keep collapsing to movie/tv_show. If these ever
        // stopped coercing, a library could be persisted as a season category.
        Assert.Equal(MediaServerSoundtrackConstants.MovieCategory, InvokeNormalizeCategory("tv_season"));
        Assert.Equal(MediaServerSoundtrackConstants.MovieCategory, InvokeNormalizeCategory("tv_episode"));
        Assert.Equal(MediaServerSoundtrackConstants.TvShowCategory, InvokeNormalizeCategory("tv_show"));
        Assert.Equal(MediaServerSoundtrackConstants.MovieCategory, InvokeNormalizeCategory("movie"));
    }

    [Fact]
    public void SeasonAndEpisodeRows_CannotAppearInTheTvShowsGrid()
    {
        // The TV Shows grid is loaded per category, so a distinct category is what
        // keeps season and episode rows out of it.
        Assert.NotEqual(MediaServerSoundtrackConstants.TvShowCategory, MediaServerSoundtrackConstants.TvSeasonCategory);
        Assert.NotEqual(MediaServerSoundtrackConstants.TvShowCategory, MediaServerSoundtrackConstants.TvEpisodeCategory);
        Assert.NotEqual(MediaServerSoundtrackConstants.MovieCategory, MediaServerSoundtrackConstants.TvSeasonCategory);
        Assert.NotEqual(MediaServerSoundtrackConstants.MovieCategory, MediaServerSoundtrackConstants.TvEpisodeCategory);
    }

    [Fact]
    public void SeasonDto_CarriesItsOwnSoundtrack()
    {
        var season = new MediaServerTvShowSeasonDto
        {
            SeasonId = "season-2",
            Title = "Season 2",
            SeasonNumber = 2,
            Soundtrack = new MediaServerSoundtrackMatchDto
            {
                Kind = "album",
                DeezerId = "12345",
                Title = "The Office Season 2 (Original Soundtrack)"
            }
        };

        Assert.NotNull(season.Soundtrack);
        Assert.Equal("12345", season.Soundtrack.DeezerId);
    }

    [Fact]
    public void BuildTvUnitSearchTitle_QualifiesSeasonsAndEpisodesWithTheShow()
    {
        var seasonRequest = new MediaServerSoundtrackResolveRequest
        {
            ShowTitle = "The Office",
            Title = "Season 2",
            SeasonNumber = 2
        };
        Assert.Equal(
            "The Office Season 2",
            InvokeBuildTvUnitSearchTitle(MediaServerSoundtrackConstants.TvSeasonCategory, "Season 2", seasonRequest));

        var episodeRequest = new MediaServerSoundtrackResolveRequest
        {
            ShowTitle = "The Office",
            Title = "Goodbye Michael"
        };
        Assert.Equal(
            "The Office Goodbye Michael",
            InvokeBuildTvUnitSearchTitle(MediaServerSoundtrackConstants.TvEpisodeCategory, "Goodbye Michael", episodeRequest));
    }

    [Fact]
    public void BuildTvUnitSearchTitle_LeavesMoviesAndShowsUntouched()
    {
        var request = new MediaServerSoundtrackResolveRequest
        {
            ShowTitle = "The Office",
            Title = "The Matrix"
        };

        Assert.Equal(
            "The Matrix",
            InvokeBuildTvUnitSearchTitle(MediaServerSoundtrackConstants.MovieCategory, "The Matrix", request));
        Assert.Equal(
            "The Office",
            InvokeBuildTvUnitSearchTitle(MediaServerSoundtrackConstants.TvShowCategory, "The Office", request));
    }

    [Theory]
    // A "Season N" qualifier ends in a number, which the sequel heuristics would
    // read as sequel N and reject the album for. These are common real album names.
    [InlineData("The Office Season 2 (Original Soundtrack)")]
    [InlineData("The Office Season 2 OST")]
    [InlineData("The Office Season 2")]
    [InlineData("The Office (Season 2)")]
    [InlineData("The Office - Season 2")]
    public void SeasonQualifier_IsNotReadAsASequelNumber(string candidate)
    {
        Assert.True(InvokeIsCompatible("The Office Season 2", candidate));
    }

    [Theory]
    [InlineData("The Office Season 3 OST")]
    [InlineData("The Office Season 3 (Original Soundtrack)")]
    [InlineData("The Office Season 3")]
    public void SeasonQualifier_StillRejectsADifferentSeason(string candidate)
    {
        // Correcting the sequel misread must not turn into a blanket relaxation:
        // the season numbers still have to agree.
        Assert.False(InvokeIsCompatible("The Office Season 2", candidate));
    }

    [Theory]
    [InlineData("Rocky II", "Rocky III", false)]
    [InlineData("The Matrix", "The Matrix Reloaded", true)]
    [InlineData("Karate Kid Part II", "Karate Kid Part III", false)]
    [InlineData("The Matrix", "The Matrix Revolutions", true)]
    public void SequelHeuristics_AreUnchangedForTitlesWithoutASeasonQualifier(string media, string candidate, bool expected)
    {
        // These titles take the original IsSequelCompatible branch verbatim, so real
        // sequel handling must be exactly what it was before the fix.
        Assert.Equal(expected, InvokeIsCompatible(media, candidate));
    }

    [Theory]
    // Only a numeric qualifier counts as a season. These must not be diverted.
    [InlineData("Seasons of Love")]
    [InlineData("Season of the Witch")]
    [InlineData("The Office")]
    [InlineData("Rocky II")]
    public void SeasonQualifier_RequiresANumber(string title)
    {
        Assert.Null(InvokeExtractSeasonNumber(title));
    }

    [Theory]
    [InlineData("The Office Season 2", 2)]
    [InlineData("The Office season 10", 10)]
    [InlineData("The Office (Season 3)", 3)]
    public void SeasonQualifier_IsExtractedFromCommonShapes(string title, int expected)
    {
        Assert.Equal(expected, InvokeExtractSeasonNumber(title));
    }

    [Theory]
    // Episode identities are composed as "{Show} {EpisodeTitle}" and carry no
    // "Season N", so they keep the shared behaviour and fall back to the show match.
    [InlineData("The Office Goodbye Michael OST", true)]
    [InlineData("The Office Season 2 OST", false)]
    [InlineData("The Office Season 2 (Original Soundtrack)", false)]
    public void EpisodeMatching_UsesTheSharedRulesAndFallsBackToTheShowMatch(string candidate, bool expected)
    {
        Assert.Equal(expected, InvokeIsCompatible("The Office Goodbye Michael", candidate));
    }

    [Fact]
    public void ResolveRequest_CarriesTvUnitContextForServerSideComposition()
    {
        var request = new MediaServerSoundtrackResolveRequest
        {
            ServerType = "plex",
            LibraryId = "1",
            ItemId = "season-2",
            Title = "Season 2",
            ShowTitle = "The Office",
            SeasonNumber = 2
        };

        Assert.Equal("The Office", request.ShowTitle);
        Assert.Equal(2, request.SeasonNumber);
    }

    private static string InvokeNormalizeItemCategory(string? category)
        => InvokeStatic<string>("NormalizeItemCategory", category);

    private static string InvokeNormalizeCategory(string? category)
        => InvokeStatic<string>("NormalizeCategory", category);

    private static string InvokeBuildTvUnitSearchTitle(
        string category,
        string title,
        MediaServerSoundtrackResolveRequest request)
        => InvokeStatic<string>("BuildTvUnitSearchTitle", category, title, request);

    private static bool InvokeIsCompatible(string mediaTitle, string candidateTitle)
        => InvokeStatic<bool>("IsSoundtrackCandidateCompatible", mediaTitle, candidateTitle, (int?)null);

    private static int? InvokeExtractSeasonNumber(string title)
        => InvokeStatic<int?>("ExtractSeasonNumber", title);

    private static TResult InvokeStatic<TResult>(string methodName, params object?[] arguments)
    {
        var method = typeof(MediaServerSoundtrackService)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(candidate => candidate.Name == methodName)
            .FirstOrDefault(candidate => MatchesArguments(candidate, arguments))
            ?? throw new InvalidOperationException($"Missing private static method '{methodName}' on MediaServerSoundtrackService.");

        var result = method.Invoke(null, arguments);
        return result is null
            ? default(TResult)!
            : (TResult)result;
    }

    private static bool MatchesArguments(MethodInfo candidate, object?[] arguments)
    {
        var parameters = candidate.GetParameters();
        if (parameters.Length != arguments.Length)
        {
            return false;
        }

        for (var index = 0; index < parameters.Length; index++)
        {
            var argument = arguments[index];
            if (argument is null)
            {
                continue;
            }

            if (!parameters[index].ParameterType.IsInstanceOfType(argument))
            {
                return false;
            }
        }

        return true;
    }
}
