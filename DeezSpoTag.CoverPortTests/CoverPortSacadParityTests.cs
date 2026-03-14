using DeezSpoTag.Web.Services.CoverPort;
using Xunit;

namespace DeezSpoTag.CoverPortTests;

public sealed class CoverPortSacadParityTests
{
    private static readonly string[] SacadCoverSources =
    {
        "coverartarchive",
        "deezer",
        "discogs",
        "itunes",
        "last_fm"
    };

    private static readonly CoverSearchOptions SacadMode = new(
        TargetSize: 1000,
        SizeTolerancePercent: 25,
        PreserveSourceFormat: false,
        PreferPng: false,
        CrunchPng: true,
        UsePerceptualHashScoring: true,
        ReferenceImagePath: null,
        ReferenceImageBytes: null,
        MaxCandidatesToTry: 20,
        ScoringMode: CoverScoringMode.SacadCompatibility);

    [Fact]
    public void SacadOptionMapper_MapsCoreFieldsAndSources()
    {
        var mapped = CoverSacadOptionMapper.Map(new SacadSearchOptionInput(
            Size: 600,
            SizeTolerancePercent: 15,
            PreserveFormat: true,
            CoverSources: SacadCoverSources));

        Assert.Equal(600, mapped.TargetSize);
        Assert.Equal(15, mapped.SizeTolerancePercent);
        Assert.True(mapped.PreserveSourceFormat);
        Assert.Equal(CoverScoringMode.SacadCompatibility, mapped.ScoringMode);
        Assert.NotNull(mapped.EnabledSources);
        Assert.Equal(
            new[]
            {
                CoverSourceName.CoverArtArchive,
                CoverSourceName.Deezer,
                CoverSourceName.Discogs,
                CoverSourceName.Itunes,
                CoverSourceName.LastFm
            },
            mapped.EnabledSources);
    }

    [Fact]
    public void SacadComparator_PrefersSimilarToReference()
    {
        var ranking = new CoverRankingService();
        var similar = Fixture("similar", 900, 900) with { IsSimilarToReference = true };
        var notSimilar = Fixture("other", 1000, 1000) with { IsSimilarToReference = false };

        var ranked = ranking.Rank(new[] { notSimilar, similar }, SacadMode);
        Assert.Equal("similar", ranked[0].Candidate.Url);
    }

    [Fact]
    public void SacadComparator_PrefersAboveTargetSizeOverBelowTarget()
    {
        var ranking = new CoverRankingService();
        var below = Fixture("below", 900, 900);
        var above = Fixture("above", 1200, 1200);

        var ranked = ranking.Rank(new[] { below, above }, SacadMode);
        Assert.Equal("above", ranked[0].Candidate.Url);
    }

    [Fact]
    public void SacadComparator_PrefersBetterRankWhenRelevanceEqual()
    {
        var ranking = new CoverRankingService();
        var rank0 = Fixture("rank0", 1000, 1000) with { Rank = 0 };
        var rank5 = Fixture("rank5", 1000, 1000) with { Rank = 5 };

        var ranked = ranking.Rank(new[] { rank5, rank0 }, SacadMode);
        Assert.Equal("rank0", ranked[0].Candidate.Url);
    }

    [Fact]
    public void SacadComparator_PrefersKnownMetadata()
    {
        var ranking = new CoverRankingService();
        var known = Fixture("known", 1000, 1000) with { IsSizeKnown = true, IsFormatKnown = true };
        var uncertain = Fixture("uncertain", 1000, 1000) with { IsSizeKnown = false, IsFormatKnown = false };

        var ranked = ranking.Rank(new[] { uncertain, known }, SacadMode);
        Assert.Equal("known", ranked[0].Candidate.Url);
    }

    [Fact]
    public void SacadComparator_PrefersPngWhenEverythingElseEqual()
    {
        var ranking = new CoverRankingService();
        var jpeg = Fixture("jpeg", 1000, 1000) with { Format = "jpg" };
        var png = Fixture("png", 1000, 1000) with { Format = "png" };

        var ranked = ranking.Rank(new[] { jpeg, png }, SacadMode);
        Assert.Equal("png", ranked[0].Candidate.Url);
    }

    [Fact]
    public void PerceptualHashService_UsesSacadSimilarityThreshold()
    {
        var hash = new CoverPerceptualHashService();
        Assert.True(hash.IsSacadSimilar(0b0UL, 0b1UL));
        Assert.False(hash.IsSacadSimilar(0b0UL, 0b11UL));
    }

    private static CoverCandidate Fixture(string url, int width, int height)
    {
        return new CoverCandidate(
            Source: CoverSourceName.CoverArtArchive,
            Url: url,
            Width: width,
            Height: height,
            Format: "jpg",
            SourceReliability: 1d,
            MatchConfidence: 1d,
            Artist: "artist",
            Album: "album",
            Rank: 0,
            Relevance: new CoverRelevance(Fuzzy: false, OnlyFrontCovers: true, UnrelatedRisk: false),
            IsSizeKnown: true,
            IsFormatKnown: true);
    }
}
