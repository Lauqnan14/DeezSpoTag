using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ShazamMatcherPrivateHelpersTests
{
    private static readonly string[] RecognizedArtistsExpected = ["Recognized A", "Recognized B"];
    private static readonly string[] RecognizedSoloExpected = ["Recognized Solo"];
    private static readonly string[] InputArtistExpected = ["Input Artist"];
    private static readonly string[] RawKeyExpected = ["value"];
    private static readonly string[] ShazamArtistIdsExpected = ["a1", "a2"];
    private static readonly string[] ShazamArtistAdamIdsExpected = ["x1"];

    private static MethodInfo MatcherMethod(string name)
    {
        return typeof(ShazamMatcher).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"ShazamMatcher.{name} not found.");
    }

    private static T InvokeStatic<T>(string methodName, params object?[] args)
    {
        var result = MatcherMethod(methodName).Invoke(null, args);
        if (result == null)
        {
            return default!;
        }

        return (T)result;
    }

    [Fact]
    public void NormalizeThreshold_ConvertsPercentAndClampsBounds()
    {
        Assert.Equal(0.75d, InvokeStatic<double>("NormalizeThreshold", 75d), 3);
        Assert.Equal(0d, InvokeStatic<double>("NormalizeThreshold", -0.5d), 3);
        Assert.Equal(0.02d, InvokeStatic<double>("NormalizeThreshold", 2d), 3);
    }

    [Fact]
    public void ComputeDurationSimilarity_HandlesNullWithinRangeAndOutOfRange()
    {
        var nullValue = MatcherMethod("ComputeDurationSimilarity").Invoke(null, new object?[] { null, 20 });
        var withinRange = MatcherMethod("ComputeDurationSimilarity").Invoke(null, new object?[] { 0, 20 });
        var outOfRange = MatcherMethod("ComputeDurationSimilarity").Invoke(null, new object?[] { 30, 20 });

        Assert.Null(nullValue);
        Assert.IsType<double>(withinRange);
        Assert.IsType<double>(outOfRange);
        Assert.True(Math.Abs((double)withinRange! - 1d) < 0.001d);
        Assert.True(Math.Abs((double)outOfRange! - 0d) < 0.001d);
    }

    [Fact]
    public void ResolveArtists_PrefersRecognizedArtistsThenArtistThenInputArtists()
    {
        var info = new AutoTagAudioInfo { Artists = new List<string> { "Input Artist" } };

        var fromArtists = InvokeStatic<List<string>>(
            "ResolveArtists",
            new ShazamRecognitionInfo { Artists = new List<string> { "Recognized A", "Recognized B" } },
            info);
        Assert.Equal(RecognizedArtistsExpected, fromArtists);

        var fromArtist = InvokeStatic<List<string>>(
            "ResolveArtists",
            new ShazamRecognitionInfo { Artist = "Recognized Solo" },
            info);
        Assert.Equal(RecognizedSoloExpected, fromArtist);

        var fromInput = InvokeStatic<List<string>>(
            "ResolveArtists",
            new ShazamRecognitionInfo(),
            info);
        Assert.Equal(InputArtistExpected, fromInput);
    }

    [Fact]
    public void ResolveAlbum_UsesConfigAndFallsBackToInputAlbumTag()
    {
        var audioInfo = new AutoTagAudioInfo
        {
            Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ALBUM"] = new() { "Input Album" }
            }
        };

        var withAlbum = InvokeStatic<string?>(
            "ResolveAlbum",
            new ShazamRecognitionInfo { Album = "Recognized Album" },
            audioInfo,
            new ShazamMatchConfig { IncludeAlbum = true });
        Assert.Equal("Recognized Album", withAlbum);

        var fallbackAlbum = InvokeStatic<string?>(
            "ResolveAlbum",
            new ShazamRecognitionInfo(),
            audioInfo,
            new ShazamMatchConfig { IncludeAlbum = true });
        Assert.Equal("Input Album", fallbackAlbum);

        var disabled = MatcherMethod("ResolveAlbum").Invoke(
            null,
            new object?[]
            {
                new ShazamRecognitionInfo { Album = "Recognized Album" },
                audioInfo,
                new ShazamMatchConfig { IncludeAlbum = false }
            });
        Assert.Null(disabled);
    }

    [Fact]
    public void ApplyConfiguredMetadata_AppliesGenreLabelDateAndArtworkPreference()
    {
        var track = new AutoTagTrack();
        var recognized = new ShazamRecognitionInfo
        {
            Genre = " House ",
            Label = " LabelX ",
            ReleaseDate = "2024-12",
            ArtworkUrl = "https://img/normal.jpg",
            ArtworkHqUrl = "https://img/hq.jpg"
        };

        MatcherMethod("ApplyConfiguredMetadata").Invoke(
            null,
            new object?[]
            {
                track,
                recognized,
                new ShazamMatchConfig
                {
                    IncludeGenre = true,
                    IncludeLabel = true,
                    IncludeReleaseDate = true,
                    PreferHqArtwork = true
                }
            });

        Assert.Equal("House", Assert.Single(track.Genres));
        Assert.Equal("LabelX", track.Label);
        Assert.Equal(new DateTime(2024, 12, 1), track.ReleaseDate);
        Assert.Equal("https://img/hq.jpg", track.Art);
    }

    [Fact]
    public void MergeAdditionalTags_TrimsDeduplicatesAndSkipsEmptyEntries()
    {
        var track = new AutoTagTrack();
        var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["  RAW_KEY  "] = new() { " value ", "VALUE", " " },
            ["EMPTY"] = new() { " ", "" }
        };

        MatcherMethod("MergeAdditionalTags").Invoke(null, new object?[] { track, tags });

        Assert.True(track.Other.ContainsKey("RAW_KEY"));
        Assert.Equal(RawKeyExpected, track.Other["RAW_KEY"]);
        Assert.False(track.Other.ContainsKey("EMPTY"));
    }

    [Fact]
    public void AddFingerprintAuditFields_WritesExpectedNumericDiagnostics()
    {
        var track = new AutoTagTrack();

        MatcherMethod("AddFingerprintAuditFields").Invoke(
            null,
            new object?[] { track, 0.81234d, 0.45678d, 5, 0.9d });

        Assert.Equal("FINGERPRINT", Assert.Single(track.Other["SHAZAM_MATCH_STRATEGY"]));
        Assert.Equal("0.812", Assert.Single(track.Other["SHAZAM_TITLE_SIMILARITY"]));
        Assert.Equal("0.457", Assert.Single(track.Other["SHAZAM_ARTIST_SIMILARITY"]));
        Assert.Equal("5", Assert.Single(track.Other["SHAZAM_DURATION_DIFF_SECONDS"]));
        Assert.Equal("0.900", Assert.Single(track.Other["SHAZAM_DURATION_SIMILARITY"]));
    }

    [Fact]
    public void PassesQualityGuards_RejectsWhenThresholdsOrDurationFail()
    {
        var method = MatcherMethod("PassesQualityGuards");

        var titleRejected = (bool)method.Invoke(null, new object?[] { 0.2d, 0.9d, 1, 0.7d, 0.4d, 10 })!;
        var artistRejected = (bool)method.Invoke(null, new object?[] { 0.9d, 0.1d, 1, 0.7d, 0.4d, 10 })!;
        var durationRejected = (bool)method.Invoke(null, new object?[] { 0.9d, 0.8d, 30, 0.7d, 0.4d, 10 })!;
        var accepted = (bool)method.Invoke(null, new object?[] { 0.9d, 0.8d, 5, 0.7d, 0.4d, 10 })!;

        Assert.False(titleRejected);
        Assert.False(artistRejected);
        Assert.False(durationRejected);
        Assert.True(accepted);
    }

    [Fact]
    public void ResolveDuration_UsesRecognizedThenFallsBackToAudioInfo()
    {
        var fromRecognized = InvokeStatic<TimeSpan?>(
            "ResolveDuration",
            new ShazamRecognitionInfo { DurationMs = 5500 },
            new AutoTagAudioInfo { DurationSeconds = 12 });
        Assert.Equal(TimeSpan.FromMilliseconds(5500), fromRecognized);

        var fromInfo = InvokeStatic<TimeSpan?>(
            "ResolveDuration",
            new ShazamRecognitionInfo(),
            new AutoTagAudioInfo { DurationSeconds = 12 });
        Assert.Equal(TimeSpan.FromSeconds(12), fromInfo);
    }

    [Fact]
    public void AddShazamOtherFields_WritesBooleanAndNumericExtras()
    {
        var track = new AutoTagTrack();
        var recognized = new ShazamRecognitionInfo
        {
            TrackNumber = 3,
            DiscNumber = 2,
            DurationMs = 180000,
            Explicit = true,
            ArtistIds = new List<string> { "a1", "A1", "a2" },
            ArtistAdamIds = new List<string> { "x1" }
        };

        MatcherMethod("AddShazamOtherFields").Invoke(null, new object?[] { track, recognized });

        Assert.Equal("3", Assert.Single(track.Other["SHAZAM_TRACK_NUMBER"]));
        Assert.Equal("2", Assert.Single(track.Other["SHAZAM_DISC_NUMBER"]));
        Assert.Equal("180000", Assert.Single(track.Other["SHAZAM_DURATION_MS"]));
        Assert.Equal("true", Assert.Single(track.Other["SHAZAM_EXPLICIT"]));
        Assert.Equal(ShazamArtistIdsExpected, track.Other["SHAZAM_ARTIST_IDS"]);
        Assert.Equal(ShazamArtistAdamIdsExpected, track.Other["SHAZAM_ARTIST_ADAM_IDS"]);
    }
}
