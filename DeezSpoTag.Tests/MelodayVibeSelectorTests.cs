using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class MelodayVibeSelectorTests
{
    [Fact]
    public void Select_RanksClosestCompletedResultRegardlessOfInputOrder()
    {
        var seed = CreateAnalysis(1, highProfile: true);
        var far = CreateAnalysis(2, highProfile: false);
        var near = CreateAnalysis(3, highProfile: true);

        var selected = MelodayVibeSelector.Select(
            [seed.TrackId],
            [seed, far, near],
            new HashSet<long> { 1, 2, 3 },
            new HashSet<long>(),
            limit: 1,
            maximumDistance: 1);

        var match = Assert.Single(selected);
        Assert.Equal(near.TrackId, match.TrackId);
        Assert.Equal(1d, match.Similarity, precision: 10);
    }

    [Fact]
    public void Select_UsesBestSimilarityAcrossEveryHistorySeed()
    {
        var firstSeed = CreateAnalysis(1, highProfile: true);
        var secondSeed = CreateAnalysis(2, highProfile: false);
        var secondSeedMatch = CreateAnalysis(3, highProfile: false);

        var selected = MelodayVibeSelector.Select(
            [firstSeed.TrackId, secondSeed.TrackId],
            [firstSeed, secondSeed, secondSeedMatch],
            new HashSet<long> { 1, 2, 3 },
            new HashSet<long>(),
            limit: 1,
            maximumDistance: 0.05);

        Assert.Equal(secondSeedMatch.TrackId, Assert.Single(selected).TrackId);
    }

    [Fact]
    public void Select_RejectsOutsideFolderHistoryAndRecentTracks()
    {
        var seed = CreateAnalysis(1, highProfile: true);
        var outsideFolder = CreateAnalysis(2, highProfile: true);
        var recent = CreateAnalysis(3, highProfile: true);
        var eligible = CreateAnalysis(4, highProfile: true);

        var selected = MelodayVibeSelector.Select(
            [seed.TrackId],
            [seed, outsideFolder, recent, eligible],
            new HashSet<long> { 1, 3, 4 },
            new HashSet<long> { 1, 3 },
            limit: 10,
            maximumDistance: 0.05);

        Assert.Equal([eligible.TrackId], selected.Select(static match => match.TrackId));
    }

    [Fact]
    public void Select_EnforcesConfiguredSimilarityDistance()
    {
        var seed = CreateAnalysis(1, highProfile: true);
        var near = CreateAnalysis(2, highProfile: true);
        var far = CreateAnalysis(3, highProfile: false);

        var selected = MelodayVibeSelector.Select(
            [seed.TrackId],
            [seed, near, far],
            new HashSet<long> { 1, 2, 3 },
            new HashSet<long>(),
            limit: 10,
            maximumDistance: 0.1);

        Assert.Equal([near.TrackId], selected.Select(static match => match.TrackId));
    }

    [Fact]
    public void Select_RejectsCompletedRowsWithoutMeaningfulVibeFeatures()
    {
        var seed = CreateAnalysis(1, highProfile: true);
        var sparse = CreateAnalysis(2, highProfile: true) with
        {
            MoodHappy = null,
            MoodSad = null,
            MoodRelaxed = null,
            MoodAggressive = null,
            MoodParty = null,
            MoodAcoustic = null,
            MoodElectronic = null,
            Energy = null,
            Valence = null,
            Arousal = null,
            Danceability = null,
            Instrumentalness = null,
            Acousticness = null,
            Speechiness = null,
            DanceabilityMl = null,
            ValenceMl = null,
            ArousalMl = null,
            Bpm = null
        };

        var selected = MelodayVibeSelector.Select(
            [seed.TrackId],
            [seed, sparse],
            new HashSet<long> { 1, 2 },
            new HashSet<long>(),
            limit: 10,
            maximumDistance: 1);

        Assert.Empty(selected);
    }

    private static TrackAnalysisResultDto CreateAnalysis(long trackId, bool highProfile)
    {
        var high = highProfile ? 1d : 0d;
        var low = highProfile ? 0d : 1d;
        return new TrackAnalysisResultDto(
            TrackId: trackId,
            LibraryId: 1,
            Status: "complete",
            Energy: high,
            Rms: null,
            ZeroCrossing: null,
            SpectralCentroid: null,
            Bpm: highProfile ? 154 : 77,
            AnalyzedAtUtc: DateTimeOffset.UtcNow,
            Error: null,
            AnalysisMode: "enhanced",
            AnalysisVersion: "test",
            MoodTags: null,
            MoodHappy: high,
            MoodSad: low,
            MoodRelaxed: high,
            MoodAggressive: low,
            MoodParty: high,
            MoodAcoustic: low,
            MoodElectronic: high,
            Valence: high,
            Arousal: high,
            BeatsCount: null,
            Key: null,
            KeyScale: null,
            KeyStrength: null,
            Loudness: null,
            DynamicRange: null,
            Danceability: high,
            Instrumentalness: low,
            Acousticness: low,
            Speechiness: low,
            DanceabilityMl: high,
            EssentiaGenres: null,
            LastfmTags: null,
            Approachability: null,
            Engagement: null,
            VoiceInstrumental: null,
            TonalAtonal: null,
            ValenceMl: high,
            ArousalMl: high,
            DynamicComplexity: null,
            LoudnessMl: null);
    }
}
