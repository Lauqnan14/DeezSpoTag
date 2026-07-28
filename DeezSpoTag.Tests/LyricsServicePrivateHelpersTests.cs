using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LyricsServicePrivateHelpersTests
{
    private static MethodInfo GetStaticMethod(string name)
    {
        return typeof(LyricsService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"LyricsService.{name} not found.");
    }

    private static T InvokeStatic<T>(string methodName, params object?[] args)
    {
        return (T)(GetStaticMethod(methodName).Invoke(null, args)
            ?? throw new InvalidOperationException($"LyricsService.{methodName} returned null."));
    }

    [Theory]
    [InlineData("itunes", "apple")]
    [InlineData("apple music", "apple")]
    [InlineData("apple_music", "apple")]
    [InlineData("lrc-get", "lrclib")]
    [InlineData("UNSUPPORTED_PROVIDER", "unsupported_provider")]
    public void NormalizeLyricsProviderToken_NormalizesAliases(string input, string expected)
    {
        var normalized = InvokeStatic<string>("NormalizeLyricsProviderToken", input);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void ResolveLyricsProviders_UsesConfiguredOrder_WhenFallbackEnabled()
    {
        var settings = new DeezSpoTagSettings
        {
            LyricsFallbackEnabled = true,
            LyricsFallbackOrder = "apple music, lrc-get, musixmatch, apple music"
        };

        var providers = InvokeStatic<List<string>>("ResolveLyricsProviders", settings);

        Assert.Equal(["apple", "lrclib", "musixmatch"], providers);
    }

    [Fact]
    public void ResolveLyricsProviders_ReducesToPrimaryProvider_WhenFallbackDisabled()
    {
        var settings = new DeezSpoTagSettings
        {
            LyricsFallbackEnabled = false,
            LyricsFallbackOrder = "spotify,deezer,apple"
        };

        var providers = InvokeStatic<List<string>>("ResolveLyricsProviders", settings);

        Assert.Single(providers);
        Assert.Equal("spotify", providers[0]);
    }

    [Fact]
    public void ReadSpotifyBlobPaths_PrefersWebPlayerBlobPathBeforeGenericBlobPath()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "activeAccount": "Edloaqx",
              "accounts": [
                {
                  "name": "Other",
                  "webPlayerBlobPath": "/data/spotify/other-web.json",
                  "blobPath": "/data/spotify/other-librespot.json"
                },
                {
                  "name": "Edloaqx",
                  "webPlayerBlobPath": "/data/spotify/web-player.web.json",
                  "blobPath": "/data/spotify/Edloaqx.json"
                }
              ]
            }
            """);

        var paths = InvokeStatic<List<string>>(
            "ReadSpotifyBlobPaths",
            doc.RootElement,
            "Edloaqx");

        Assert.Equal(
            [
                "/data/spotify/web-player.web.json",
                "/data/spotify/Edloaqx.json",
                "/data/spotify/other-web.json",
                "/data/spotify/other-librespot.json"
            ],
            paths);
    }

    [Fact]
    public void ResolveSpotifyLyricsTrackId_UsesTrackUrlMetadataWithoutSongLink()
    {
        var track = new Track
        {
            Id = "local-track",
            Source = "deezer",
            SourceId = "908604612",
            Urls =
            {
                ["spotify_track_id"] = "0VjIjW4GlUZAMYd2vXMi3b"
            }
        };

        var resolved = InvokeStatic<string?>("ResolveSpotifyLyricsTrackId", track);

        Assert.Equal("0VjIjW4GlUZAMYd2vXMi3b", resolved);
    }

    [Fact]
    public void TryResolveDeezerTrackIdFromTrack_UsesTrackUrlMetadataWithoutSongLink()
    {
        var track = new Track
        {
            Id = "local-track",
            Source = "spotify",
            SourceId = "0VjIjW4GlUZAMYd2vXMi3b",
            Urls =
            {
                ["deezer_track_id"] = "908604612"
            }
        };

        object?[] args = [track, null];
        var resolved = (bool)(GetStaticMethod("TryResolveDeezerTrackIdFromTrack").Invoke(null, args)
            ?? throw new InvalidOperationException("TryResolveDeezerTrackIdFromTrack returned null."));

        Assert.True(resolved);
        Assert.Equal("908604612", args[1]);
    }

    [Fact]
    public void LyricsService_DoesNotDependOnSongLinkResolver()
    {
        var sourcePath = Path.GetFullPath(Path.Join(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "DeezSpoTag.Services",
            "Download",
            "Utils",
            "LyricsService.cs"));
        var source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("SongLinkResolver", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveSongLink", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LyricsResolution_ConsumesCompleteCentralIdentityMatrix()
    {
        var repoRoot = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var lyricsSource = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Services",
            "Download",
            "Utils",
            "LyricsService.cs"));
        var sharedSource = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Services",
            "Download",
            "Shared",
            "EngineAudioPostDownloadHelper.cs"));
        var appleSource = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Services",
            "Apple",
            "AppleLyricsService.cs"));

        foreach (var key in new[]
                 {
                     "spotify_track_id",
                     "deezer_track_id",
                     "apple_track_id",
                     "qobuz_track_id",
                     "tidal_track_id",
                     "amazon_track_id"
                 })
        {
            Assert.Contains(key, sharedSource, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("ITrackIdentityResolver", lyricsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_trackIdentityResolver.ResolveAsync", lyricsSource, StringComparison.Ordinal);
        Assert.Contains("TryResolveSpotifyTrackIdFromTrack", lyricsSource, StringComparison.Ordinal);
        Assert.Contains("TryResolveDeezerTrackIdFromTrack", lyricsSource, StringComparison.Ordinal);
        Assert.Contains("ResolveAppleLyricsTrackId", lyricsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveLyricsForTrackAsync", appleSource, StringComparison.Ordinal);
        Assert.DoesNotContain("TryResolveAppleIdByIsrcAsync", appleSource, StringComparison.Ordinal);
        Assert.DoesNotContain("TryResolveAppleIdBySearchTermsAsync", appleSource, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildLrclibRequestOptions_UsesDefaultsWhenPropertiesMissing()
    {
        var providerOptions = new LrclibLyricsProviderOptions();

        var requestOptions = InvokeStatic<LrclibLyricsService.LrclibRequestOptions>(
            "BuildLrclibRequestOptions",
            providerOptions);

        Assert.Equal(10, requestOptions.DurationToleranceSeconds);
        Assert.True(requestOptions.UseDurationHint);
        Assert.True(requestOptions.SearchFallback);
        Assert.True(requestOptions.PreferSynced);
    }

    [Fact]
    public void BuildLrclibRequestOptions_UsesConfiguredValues()
    {
        var providerOptions = new LrclibLyricsProviderOptions
        {
            DurationToleranceSeconds = 3,
            UseDurationHint = false,
            SearchFallback = false,
            PreferSynced = false
        };

        var requestOptions = InvokeStatic<LrclibLyricsService.LrclibRequestOptions>(
            "BuildLrclibRequestOptions",
            providerOptions);

        Assert.Equal(3, requestOptions.DurationToleranceSeconds);
        Assert.False(requestOptions.UseDurationHint);
        Assert.False(requestOptions.SearchFallback);
        Assert.False(requestOptions.PreferSynced);
    }

    [Fact]
    public void ParseSelectedLyricsTypes_DefaultsWhenEmpty()
    {
        var settings = new DeezSpoTagSettings { LrcType = string.Empty };
        var selected = InvokeStatic<HashSet<string>>("ParseSelectedLyricsTypes", settings);

        Assert.Contains("lyrics", selected);
        Assert.Contains("syllable-lyrics", selected);
        Assert.Contains("ttml-lyrics", selected);
        Assert.Contains("unsynced-lyrics", selected);
    }

    [Fact]
    public void ParseSelectedLyricsTypes_NormalizesAliasesAndDeduplicates()
    {
        var settings = new DeezSpoTagSettings
        {
            LrcType = "synced-lyrics,time_synced_lyrics,ttmllyrics,unsynchronized-lyrics,lyrics,UNSYNCED"
        };

        var selected = InvokeStatic<HashSet<string>>("ParseSelectedLyricsTypes", settings);

        Assert.Equal(4, selected.Count);
        Assert.Contains("lyrics", selected);
        Assert.Contains("syllable-lyrics", selected);
        Assert.Contains("ttml-lyrics", selected);
        Assert.Contains("unsynced-lyrics", selected);
    }

    [Theory]
    [InlineData("lyrics", "richlyrics")]
    [InlineData("lrc", "lrc")]
    [InlineData("elrc", "elrc")]
    [InlineData("ttml", "ttml")]
    [InlineData("lrc+ttml", "both")]
    [InlineData("both", "richlyrics")]
    [InlineData("richlyrics", "richlyrics")]
    [InlineData("unknown-format", "richlyrics")]
    public void NormalizeLyricsOutputFormat_NormalizesExpectedValues(string value, string expected)
    {
        var actual = InvokeStatic<string>("NormalizeLyricsOutputFormat", value);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ShouldSaveSyncedLrc_RequiresGateAndSupportedType()
    {
        var disabled = new DeezSpoTagSettings
        {
            SyncedLyrics = false,
            SaveLyrics = false,
            Tags = new TagSettings { Lyrics = false, SyncedLyrics = false },
            LrcType = "lyrics",
            LrcFormat = "lrc"
        };

        var disabledResult = InvokeStatic<bool>("ShouldSaveSyncedLrc", disabled);
        Assert.False(disabledResult);

        var enabled = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            LrcType = "lyrics",
            LrcFormat = "lrc"
        };

        var enabledResult = InvokeStatic<bool>("ShouldSaveSyncedLrc", enabled);
        Assert.True(enabledResult);
    }

    [Fact]
    public void ShouldSavePlainLyrics_RequiresGateAndUnsyncedSelection()
    {
        var noUnsynced = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            SaveLyrics = true,
            LrcType = "lyrics,syllable-lyrics"
        };

        var noUnsyncedResult = InvokeStatic<bool>("ShouldSavePlainLyrics", noUnsynced);
        Assert.False(noUnsyncedResult);

        var withUnsynced = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            SaveLyrics = true,
            LrcType = "lyrics,unsynced-lyrics"
        };

        var withUnsyncedResult = InvokeStatic<bool>("ShouldSavePlainLyrics", withUnsynced);
        Assert.True(withUnsyncedResult);
    }

    [Fact]
    public void ShouldSavePlainLyrics_DoesNotUseTagFlagsWhenSaveLyricsIsDisabled()
    {
        var settings = new DeezSpoTagSettings
        {
            SyncedLyrics = false,
            SaveLyrics = false,
            LrcType = "unsynced-lyrics",
            Tags = new TagSettings
            {
                Lyrics = true
            }
        };

        var result = InvokeStatic<bool>("ShouldSavePlainLyrics", settings);

        Assert.False(result);
    }

    [Fact]
    public void ResolveOutputRequirements_ObeysSyncedOnlyTechnicalPreference()
    {
        var settings = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            SaveLyrics = false,
            LrcType = "lyrics,syllable-lyrics",
            LrcFormat = "lrc"
        };

        var requirements = InvokeStatic<object>("ResolveOutputRequirements", settings);

        AssertRequirement(requirements, "WantsLrcLyrics", expected: true);
        AssertRequirement(requirements, "WantsTtmlLyrics", expected: false);
        AssertRequirement(requirements, "WantsPlainLyrics", expected: false);
    }

    [Fact]
    public void ResolveOutputRequirements_ObeysUnsyncedOnlyTechnicalPreference()
    {
        var settings = new DeezSpoTagSettings
        {
            SyncedLyrics = false,
            SaveLyrics = true,
            LrcType = "unsynced-lyrics",
            LrcFormat = "both"
        };

        var requirements = InvokeStatic<object>("ResolveOutputRequirements", settings);

        AssertRequirement(requirements, "WantsLrcLyrics", expected: false);
        AssertRequirement(requirements, "WantsTtmlLyrics", expected: false);
        AssertRequirement(requirements, "WantsPlainLyrics", expected: true);
    }

    [Fact]
    public void ResolveOutputRequirements_RequiresRichLyrics_WhenRichFormatsAreEnabled()
    {
        var settings = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            SaveLyrics = true,
            LrcType = "lyrics,syllable-lyrics,ttml-lyrics,unsynced-lyrics",
            LrcFormat = "both"
        };

        var requirements = InvokeStatic<object>("ResolveOutputRequirements", settings);

        AssertRequirement(requirements, "WantsLrcLyrics", expected: true);
        AssertRequirement(requirements, "WantsEnhancedSynchronizedLyrics", expected: true);
        AssertRequirement(requirements, "WantsTtmlLyrics", expected: true);
        AssertRequirement(requirements, "WantsPlainLyrics", expected: true);
    }

    [Fact]
    public void MergeLyricsData_UpgradesLineSyncedLyricsWithEnhancedWordTiming()
    {
        var target = new LyricsSource
        {
            SyncedLyrics =
            [
                new SynchronizedLyric("AJE line", "[00:01.00]", 1000, 2000)
            ],
            SyncedLyricsSourceFormat = LyricsSourceFormat.DownloadedLrc
        };
        var candidate = new LyricsSource
        {
            SyncedLyrics =
            [
                new SynchronizedLyric("AJE line", "[00:01.00]", 1000, 2000)
                {
                    Words =
                    [
                        new SynchronizedLyricWord("AJE", 1000, 1400),
                        new SynchronizedLyricWord(" line", 1400, 2000)
                    ]
                }
            ],
            SyncedLyricsSourceFormat = LyricsSourceFormat.ProviderSyncedJson
        };

        GetStaticMethod("MergeLyricsData").Invoke(null, [target, candidate]);

        Assert.True(target.HasEnhancedSynchronizedLyrics());
        Assert.Equal(LyricsSourceFormat.ProviderSyncedJson, target.SyncedLyricsSourceFormat);
    }

    [Fact]
    public void ShouldReturnResolvedLyrics_AllowsLrcOnlyAtFinalProvider_WhenBothFormatsAreRequested()
    {
        var settings = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            SaveLyrics = false,
            LrcType = "lyrics,syllable-lyrics,ttml-lyrics,unsynced-lyrics",
            LrcFormat = "both"
        };
        var requirements = InvokeStatic<object>("ResolveOutputRequirements", settings);
        var state = CreateLyricsResolutionState(new LyricsSource
        {
            SyncedLyrics =
            [
                new SynchronizedLyric { Text = "Line one", Milliseconds = 1000 },
                new SynchronizedLyric { Text = "Line two", Milliseconds = 2000 }
            ],
            SyncedLyricsSourceFormat = LyricsSourceFormat.DownloadedLrc
        });

        var early = InvokeShouldReturnResolvedLyrics(state, requirements, requireAllRequestedRichLyrics: true);
        var final = InvokeShouldReturnResolvedLyrics(state, requirements, requireAllRequestedRichLyrics: false);

        Assert.False(early);
        Assert.True(final);
    }

    [Fact]
    public void ShouldReturnResolvedLyrics_StillAcceptsPlainOnly_WhenNoRichLyricsExist()
    {
        var settings = new DeezSpoTagSettings
        {
            SyncedLyrics = false,
            SaveLyrics = true,
            LrcType = "unsynced-lyrics",
            LrcFormat = "both"
        };
        var requirements = InvokeStatic<object>("ResolveOutputRequirements", settings);
        var state = CreateLyricsResolutionState(new LyricsSource
        {
            UnsyncedLyrics = "Plain fallback",
            UnsyncedLyricsSourceFormat = LyricsSourceFormat.DownloadedPlainText
        });

        Assert.True(InvokeShouldReturnResolvedLyrics(state, requirements, requireAllRequestedRichLyrics: false));
    }

    [Theory]
    [InlineData("ttml", true)]
    [InlineData("both", true)]
    [InlineData("lrc", false)]
    public void ShouldSaveTtml_UsesRawAppleTtml_WhenTtmlOutputIsRequested(string format, bool expected)
    {
        var settings = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            LrcType = "lyrics,ttml-lyrics",
            LrcFormat = format
        };
        var appleLyrics = new LyricsSource
        {
            TtmlLyrics = "<?xml version=\"1.0\" encoding=\"utf-8\"?><tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"Word\"><body><div><p begin=\"1.0\" end=\"2.0\"><span begin=\"1.0\" end=\"2.0\">Word</span></p></div></body></tt>"
        };

        var result = InvokeStatic<bool>("ShouldSaveTtml", settings, appleLyrics);

        Assert.Equal(expected, result);
    }

    private static object CreateLyricsResolutionState(LyricsBase lyrics)
    {
        var stateType = typeof(LyricsService).GetNestedType("LyricsResolutionState", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LyricsService.LyricsResolutionState not found.");
        var state = Activator.CreateInstance(stateType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create LyricsResolutionState.");
        stateType.GetProperty("ResolvedLyrics")?.SetValue(state, lyrics);
        return state;
    }

    private static bool InvokeShouldReturnResolvedLyrics(
        object state,
        object requirements,
        bool requireAllRequestedRichLyrics)
    {
        return (bool)(GetStaticMethod("ShouldReturnResolvedLyrics")
            .Invoke(null, [state, requirements, requireAllRequestedRichLyrics])
            ?? throw new InvalidOperationException("LyricsService.ShouldReturnResolvedLyrics returned null."));
    }

    [Fact]
    public void AppleTtmlTiming_RejectsReferenceTimingNonePayload()
    {
        const string ttml = "<tt xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"None\"><body><div><p>Plain text</p></div></body></tt>";

        Assert.False(DeezSpoTag.Services.Apple.AppleLyricsService.IsTimedTtml(ttml));
        Assert.True(DeezSpoTag.Services.Apple.AppleLyricsService.TryExtractPlainLyrics(ttml, out var plainLyrics));
        Assert.Equal("Plain text", plainLyrics);
    }

    [Fact]
    public void ParsePaxsenixLyricsPayload_TreatsTimingNoneAsPlainTextOnly()
    {
        using var payload = JsonDocument.Parse(
            """
            {
              "type": "TTML",
              "content": "<tt xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"None\"><body><div><p>First line</p><p>Second line</p></div></body></tt>"
            }
            """);

        var lyrics = InvokeStatic<LyricsBase>(
            "ParsePaxsenixLyricsPayload",
            payload.RootElement,
            new DeezSpoTagSettings());

        Assert.Equal("First line\nSecond line", lyrics.UnsyncedLyrics);
        Assert.Equal(LyricsSourceFormat.DownloadedPlainText, lyrics.UnsyncedLyricsSourceFormat);
        Assert.Null(lyrics.TtmlLyrics);
        Assert.False(lyrics.CanSaveLrcSidecar());
    }

    [Theory]
    [InlineData("<tt xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"Line\"><body><div><p begin=\"17.235\" end=\"18.958\">Line</p></div></body></tt>", AppleTtmlTimingKind.Line)]
    [InlineData("<tt xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"Word\"><body><div><p><span begin=\"00:00:01.250\" end=\"00:00:02.000\">Word</span></p></div></body></tt>", AppleTtmlTimingKind.Word)]
    public void AppleTtmlTiming_ClassifiesReferenceLineAndWordPayloads(
        string ttml,
        AppleTtmlTimingKind expected)
    {
        Assert.Equal(expected, DeezSpoTag.Services.Apple.AppleLyricsService.ClassifyTtml(ttml));
    }

    [Fact]
    public void AppleTtmlTiming_RejectsWordLabelWithoutTimedWordSpans()
    {
        const string ttml = "<tt xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"Word\"><body><div><p begin=\"1.0\" end=\"2.0\">Line only</p></div></body></tt>";

        Assert.Equal(AppleTtmlTimingKind.Invalid, AppleLyricsService.ClassifyTtml(ttml));
        Assert.False(AppleLyricsService.IsWordSyncedTtml(ttml));
    }

    [Fact]
    public void AppleLyricsEndpointOrder_RequestsWordBeforeLine_WhenBothOutputsAreNeeded()
    {
        var method = typeof(AppleLyricsService).GetMethod(
            "BuildLyricsTypeCandidates",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AppleLyricsService.BuildLyricsTypeCandidates not found.");
        var candidates = ((IEnumerable<string>)method.Invoke(
                null,
                ["lyrics,ttml-lyrics", true])!)
            .ToArray();

        Assert.Equal("syllable-lyrics", candidates[0]);
        Assert.Contains("lyrics", candidates);
    }

    [Theory]
    [InlineData("<tt xmlns='http://www.w3.org/ns/ttml'><body><script /><p begin='1.0'>Line</p></body></tt>")]
    [InlineData("<tt><body><script /><p begin='1.0'>Line</p></body></tt>")]
    [InlineData("<!DOCTYPE tt [<!ENTITY forged 'Line'>]><tt><body><div><p begin='1.0'>&forged;</p></div></body></tt>")]
    public void AppleTtmlTiming_RejectsPayloadsOutsideBuiltInSchema(string ttml)
    {
        Assert.False(DeezSpoTag.Services.Apple.AppleLyricsService.IsTimedTtml(ttml));
        Assert.False(DeezSpoTag.Services.Apple.AppleLyricsService.TryExtractPlainLyrics(ttml, out _));
    }

    [Fact]
    public void AppleTtmlTiming_AcceptsStandardHeadAndExtensionMetadata()
    {
        const string ttml = """
            <tt xmlns="http://www.w3.org/ns/ttml"
                xmlns:itunes="http://music.apple.com/lyric-ttml-internal"
                itunes:timing="Line">
              <head>
                <metadata><itunes:songWriter>Artist</itunes:songWriter></metadata>
                <styling><style xml:id="defaultStyle" /></styling>
                <layout><region xml:id="lyrics" /></layout>
              </head>
              <body><div><p begin="00:00:01.000" end="00:00:02.000">Line</p></div></body>
            </tt>
            """;

        Assert.True(DeezSpoTag.Services.Apple.AppleLyricsService.IsTimedTtml(ttml));
    }

    [Fact]
    public void ShouldSaveTtml_RejectsUntimedApplePayload()
    {
        var settings = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            LrcType = "lyrics",
            LrcFormat = "ttml"
        };
        var lyrics = new LyricsSource
        {
            TtmlLyrics = "<tt xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"None\"><body><div><p>Plain text</p></div></body></tt>",
            TtmlLyricsSourceFormat = LyricsSourceFormat.DownloadedTtml
        };

        Assert.False(InvokeStatic<bool>("ShouldSaveTtml", settings, lyrics));
    }

    [Fact]
    public void ShouldSaveTtml_RejectsLineSynchronizedApplePayload()
    {
        var settings = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            LrcType = "ttml-lyrics",
            LrcFormat = "ttml"
        };
        var lyrics = new LyricsSource
        {
            TtmlLyrics = "<tt xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"Line\"><body><div><p begin=\"1.0\" end=\"2.0\">Line</p></div></body></tt>",
            TtmlLyricsSourceFormat = LyricsSourceFormat.DownloadedTtml
        };

        Assert.False(InvokeStatic<bool>("ShouldSaveTtml", settings, lyrics));
    }

    [Fact]
    public async Task SaveLyricsAsync_WritesTtmlSidecar_FromRawAppleTtml()
    {
        var service = (LyricsService)RuntimeHelpers.GetUninitializedObject(typeof(LyricsService));
        typeof(LyricsService)
            .GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, NullLogger<LyricsService>.Instance);
        var directory = Path.Combine(Path.GetTempPath(), $"deezspotag-ttml-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new DeezSpoTagSettings
            {
                SyncedLyrics = true,
                LrcType = "ttml-lyrics",
                LrcFormat = "ttml"
            };
            var lyrics = new LyricsSource
            {
                TtmlLyrics = "<?xml version=\"1.0\" encoding=\"utf-8\"?><tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"Word\"><body><div><p begin=\"1.0\" end=\"2.0\"><span begin=\"1.0\" end=\"2.0\">Apple word</span></p></div></body></tt>"
            };
            var track = new Track
            {
                Id = "apple-ttml-test",
                Title = "Apple TTML Test",
                ArtistString = "Apple Artist"
            };
            var paths = (directory, "apple-ttml-test", directory, directory, directory);

            await service.SaveLyricsAsync(lyrics, track, paths, settings);

            var ttmlPath = Path.Combine(directory, "apple-ttml-test.ttml");
            Assert.True(File.Exists(ttmlPath));
            Assert.Contains("Apple word", await File.ReadAllTextAsync(ttmlPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveLyricsAsync_TtmlOnly_DoesNotCreateLrcOrTxt()
    {
        var service = CreateUninitializedLyricsService();
        var directory = CreateLyricsTestDirectory();
        try
        {
            var settings = new DeezSpoTagSettings
            {
                SyncedLyrics = true,
                SaveLyrics = true,
                LrcType = "lyrics,ttml-lyrics,unsynced-lyrics",
                LrcFormat = "ttml"
            };
            var lyrics = new LyricsSource
            {
                TtmlLyrics = "<tt xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"Word\"><body><div><p begin=\"1.0\" end=\"2.0\"><span begin=\"1.0\" end=\"2.0\">Timed</span></p></div></body></tt>",
                UnsyncedLyrics = "Plain"
            };

            await service.SaveLyricsAsync(lyrics, CreateLyricsTestTrack(), BuildLyricsPaths(directory), settings);

            Assert.True(File.Exists(Path.Join(directory, "track.ttml")));
            Assert.False(File.Exists(Path.Join(directory, "track.lrc")));
            Assert.False(File.Exists(Path.Join(directory, "track.txt")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveLyricsAsync_LrcOnly_DoesNotCreateTtmlOrTxt()
    {
        var service = CreateUninitializedLyricsService();
        var directory = CreateLyricsTestDirectory();
        try
        {
            var settings = new DeezSpoTagSettings
            {
                SyncedLyrics = true,
                SaveLyrics = true,
                LrcType = "lyrics,ttml-lyrics,unsynced-lyrics",
                LrcFormat = "lrc"
            };
            var lyrics = new LyricsSource
            {
                SyncedLyrics = [new SynchronizedLyric("Timed", "[00:01.00]", 1000)],
                SyncedLyricsSourceFormat = LyricsSourceFormat.DownloadedLrc,
                UnsyncedLyrics = "Plain"
            };

            await service.SaveLyricsAsync(lyrics, CreateLyricsTestTrack(), BuildLyricsPaths(directory), settings);

            Assert.True(File.Exists(Path.Join(directory, "track.lrc")));
            Assert.False(File.Exists(Path.Join(directory, "track.ttml")));
            Assert.False(File.Exists(Path.Join(directory, "track.txt")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveLyricsAsync_LrcOnly_CreatesLrcFromProviderSyncedJson()
    {
        var service = CreateUninitializedLyricsService();
        var directory = CreateLyricsTestDirectory();
        try
        {
            var settings = new DeezSpoTagSettings
            {
                SyncedLyrics = true,
                SaveLyrics = true,
                LrcType = "lyrics",
                LrcFormat = "lrc"
            };
            var lyrics = new LyricsSource
            {
                SyncedLyrics = [new SynchronizedLyric("Timed", "[00:01.00]", 1000)],
                SyncedLyricsSourceFormat = LyricsSourceFormat.ProviderSyncedJson
            };

            await service.SaveLyricsAsync(lyrics, CreateLyricsTestTrack(), BuildLyricsPaths(directory), settings);

            Assert.True(File.Exists(Path.Join(directory, "track.lrc")));
            Assert.False(File.Exists(Path.Join(directory, "track.ttml")));
            Assert.False(File.Exists(Path.Join(directory, "track.txt")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveLyricsAsync_EnhancedSynchronizedLyrics_WritesElrcOnly()
    {
        var service = CreateUninitializedLyricsService();
        var directory = CreateLyricsTestDirectory();
        try
        {
            var settings = new DeezSpoTagSettings
            {
                SyncedLyrics = true,
                SaveLyrics = true,
                LrcType = "lyrics,unsynced-lyrics",
                LrcFormat = "elrc"
            };
            var lyrics = new LyricsSource
            {
                SyncedLyrics =
                [
                    new SynchronizedLyric("Oh yeah", "[00:01.00]", 1000, 2000)
                    {
                        Words =
                        [
                            new SynchronizedLyricWord("Oh", 1000, 1300),
                            new SynchronizedLyricWord(" ", 1300, 1301),
                            new SynchronizedLyricWord("yeah", 1400, 2500)
                        ]
                    }
                ],
                SyncedLyricsSourceFormat = LyricsSourceFormat.ProviderSyncedJson,
                UnsyncedLyrics = "Plain"
            };

            await service.SaveLyricsAsync(lyrics, CreateLyricsTestTrack(), BuildLyricsPaths(directory), settings);

            Assert.True(File.Exists(Path.Join(directory, "track.elrc")));
            Assert.False(File.Exists(Path.Join(directory, "track.lrc")));
            Assert.False(File.Exists(Path.Join(directory, "track.ttml")));
            Assert.False(File.Exists(Path.Join(directory, "track.txt")));
            var elrc = await File.ReadAllTextAsync(Path.Join(directory, "track.elrc"));
            Assert.Contains("[00:01.00]<00:01.000>Oh <00:01.400>yeah", elrc);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveLyricsAsync_AllRichFormats_WritesLrcElrcAndTtml()
    {
        var service = CreateUninitializedLyricsService();
        var directory = CreateLyricsTestDirectory();
        try
        {
            var settings = new DeezSpoTagSettings
            {
                SyncedLyrics = true,
                SaveLyrics = true,
                LrcType = "lyrics,ttml-lyrics,unsynced-lyrics",
                LrcFormat = "lrc,elrc,ttml",
                SynthesizeLrcFromTtml = true
            };
            var lyrics = new LyricsSource
            {
                SyncedLyrics =
                [
                    new SynchronizedLyric("Oh yeah", "[00:01.00]", 1000, 2000)
                    {
                        Words =
                        [
                            new SynchronizedLyricWord("Oh", 1000, 1300),
                            new SynchronizedLyricWord(" ", 1300, 1301),
                            new SynchronizedLyricWord("yeah", 1400, 2500)
                        ]
                    }
                ],
                SyncedLyricsSourceFormat = LyricsSourceFormat.ProviderSyncedJson,
                TtmlLyrics = "<tt xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"Word\"><body><div><p begin=\"1.0\" end=\"3.0\"><span begin=\"1.0\" end=\"1.3\">Oh</span><span begin=\"1.4\" end=\"2.5\">yeah</span></p></div></body></tt>",
                TtmlLyricsSourceFormat = LyricsSourceFormat.DownloadedTtml,
                UnsyncedLyrics = "Plain"
            };

            await service.SaveLyricsAsync(lyrics, CreateLyricsTestTrack(), BuildLyricsPaths(directory), settings);

            Assert.True(File.Exists(Path.Join(directory, "track.lrc")));
            Assert.True(File.Exists(Path.Join(directory, "track.elrc")));
            Assert.True(File.Exists(Path.Join(directory, "track.ttml")));
            Assert.False(File.Exists(Path.Join(directory, "track.txt")));
            var lrc = await File.ReadAllTextAsync(Path.Join(directory, "track.lrc"));
            var elrc = await File.ReadAllTextAsync(Path.Join(directory, "track.elrc"));
            var ttml = await File.ReadAllTextAsync(Path.Join(directory, "track.ttml"));
            Assert.Contains("[00:01.00]Oh yeah", lrc);
            Assert.DoesNotContain("<00:01.000>", lrc);
            Assert.Contains("[00:01.00]<00:01.000>Oh <00:01.400>yeah", elrc);
            Assert.Contains("<span", ttml);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveLyricsAsync_LrcOnly_CreatesLrcFromConvertedLineTtml()
    {
        var service = CreateUninitializedLyricsService();
        var directory = CreateLyricsTestDirectory();
        try
        {
            var settings = new DeezSpoTagSettings
            {
                SyncedLyrics = true,
                LrcType = "ttml-lyrics",
                LrcFormat = "lrc",
                SynthesizeLrcFromTtml = true
            };
            var lyrics = new LyricsSource
            {
                SyncedLyrics = [new SynchronizedLyric("Apple line", "[00:01.00]", 1000)],
                SyncedLyricsSourceFormat = LyricsSourceFormat.ConvertedFromTtml
            };

            await service.SaveLyricsAsync(lyrics, CreateLyricsTestTrack(), BuildLyricsPaths(directory), settings);

            Assert.True(File.Exists(Path.Join(directory, "track.lrc")));
            Assert.False(File.Exists(Path.Join(directory, "track.ttml")));
            Assert.False(File.Exists(Path.Join(directory, "track.txt")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveLyricsAsync_WritesTxtOnlyWhenEnabledRichLyricsAreUnavailable()
    {
        var service = CreateUninitializedLyricsService();
        var directory = CreateLyricsTestDirectory();
        try
        {
            var settings = new DeezSpoTagSettings
            {
                SyncedLyrics = true,
                SaveLyrics = true,
                LrcType = "lyrics,unsynced-lyrics",
                LrcFormat = "both"
            };
            var lyrics = new LyricsSource { UnsyncedLyrics = "Plain fallback" };

            await service.SaveLyricsAsync(lyrics, CreateLyricsTestTrack(), BuildLyricsPaths(directory), settings);

            Assert.True(File.Exists(Path.Join(directory, "track.txt")));
            Assert.False(File.Exists(Path.Join(directory, "track.lrc")));
            Assert.False(File.Exists(Path.Join(directory, "track.ttml")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ParsePaxsenixLyricsPayload_ExtractsTtmlFromAppleContentEnvelope()
    {
        using var doc = JsonDocument.Parse("""
            {
              "type": "TTML",
              "content": "<?xml version='1.0' encoding='utf-8'?><tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"Word\"><body><div><p begin=\"1.0\" end=\"2.0\"><span begin=\"1.0\" end=\"2.0\">Apple public word</span></p></div></body></tt>"
            }
            """);

        var lyrics = InvokeStatic<LyricsBase>(
            "ParsePaxsenixLyricsPayload",
            doc.RootElement,
            new DeezSpoTagSettings());

        Assert.NotNull(lyrics.TtmlLyrics);
        Assert.Equal(LyricsSourceFormat.DownloadedTtml, lyrics.TtmlLyricsSourceFormat);
        Assert.StartsWith("<?xml", lyrics.TtmlLyrics);
        Assert.Contains("Apple public word", lyrics.TtmlLyrics);
        Assert.DoesNotContain("\"type\"", lyrics.TtmlLyrics);
    }

    [Fact]
    public void ParsePaxsenixLyricsPayload_ConvertsLineTtmlToLrcOnlyWhenEnabled()
    {
        using var doc = JsonDocument.Parse("""
            {
              "type": "TTML",
              "content": "<tt xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"Line\"><body><div><p begin=\"1.0\" end=\"2.0\">Apple line</p></div></body></tt>"
            }
            """);

        var disabled = InvokeStatic<LyricsBase>(
            "ParsePaxsenixLyricsPayload",
            doc.RootElement,
            new DeezSpoTagSettings { SynthesizeLrcFromTtml = false });
        var enabled = InvokeStatic<LyricsBase>(
            "ParsePaxsenixLyricsPayload",
            doc.RootElement,
            new DeezSpoTagSettings { SynthesizeLrcFromTtml = true });

        Assert.False(disabled.CanSaveLrcSidecar());
        Assert.Null(disabled.TtmlLyrics);
        Assert.True(enabled.CanSaveLrcSidecar());
        Assert.Equal(LyricsSourceFormat.ConvertedFromTtml, enabled.SyncedLyricsSourceFormat);
        Assert.Equal("Apple line", Assert.Single(enabled.SyncedLyrics!).Text);
        Assert.Null(enabled.TtmlLyrics);
    }

    [Fact]
    public void ConvertLineTtmlToSynchronizedLyrics_BuildsOrderedLrcLines()
    {
        const string ttml = "<tt xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"Line\"><body><div><p begin=\"5.0\" end=\"6.0\">Second line</p><p begin=\"1.0\" end=\"2.0\">First line</p></div></body></tt>";

        Assert.True(AppleLyricsService.TryConvertTtmlToSynchronizedLyrics(ttml, out var lines));
        Assert.Collection(
            lines,
            first =>
            {
                Assert.Equal(1000, first.Milliseconds);
                Assert.Equal("First line", first.Text);
            },
            second =>
            {
                Assert.Equal(5000, second.Milliseconds);
                Assert.Equal("Second line", second.Text);
            });
    }

    [Fact]
    public void ConvertWordTtmlToSynchronizedLyrics_UsesWordsWhenLineTtmlIsUnavailable()
    {
        const string ttml = "<tt xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"Word\"><body><div><p begin=\"1.0\" end=\"2.0\"><span begin=\"1.0\" end=\"1.3\">Hello</span><span begin=\"1.4\" end=\"2.0\">world!</span></p></div></body></tt>";

        Assert.True(AppleLyricsService.TryConvertTtmlToSynchronizedLyrics(ttml, out var lines));
        var line = Assert.Single(lines);
        Assert.Equal(1000, line.Milliseconds);
        Assert.Equal("Hello world!", line.Text);
    }

    [Fact]
    public void MergeLyricsData_PrefersDirectLrcOverTtmlConversion()
    {
        var converted = new LyricsSource
        {
            SyncedLyrics = [new SynchronizedLyric("Converted", "[00:01.00]", 1000)],
            SyncedLyricsSourceFormat = LyricsSourceFormat.ConvertedFromTtml
        };
        var direct = new LyricsSource
        {
            SyncedLyrics = [new SynchronizedLyric("Direct", "[00:02.00]", 2000)],
            SyncedLyricsSourceFormat = LyricsSourceFormat.DownloadedLrc
        };

        GetStaticMethod("MergeLyricsData").Invoke(null, [converted, direct]);

        Assert.Equal(LyricsSourceFormat.DownloadedLrc, converted.SyncedLyricsSourceFormat);
        Assert.Equal("Direct", Assert.Single(converted.SyncedLyrics!).Text);
    }

    [Fact]
    public void GenerateLrcContent_RemainsLineOnly_WhenEnhancedWordsAreAvailable()
    {
        var lyrics = new LyricsSource
        {
            SyncedLyrics =
            [
                new SynchronizedLyric("Oh yeah,", "[00:01.00]", 1000, 2000)
                {
                    Words =
                    [
                        new SynchronizedLyricWord("Oh", 1000, 1300),
                        new SynchronizedLyricWord(" ", 1300, 1301),
                        new SynchronizedLyricWord("yeah,", 1400, 2000)
                    ]
                }
            ]
        };

        var lrc = lyrics.GenerateLrcContent("AJE", "Alikiba", "So Hot");

        Assert.Contains("[ti:AJE]", lrc);
        Assert.Contains("[00:01.00]Oh yeah,", lrc);
        Assert.DoesNotContain("<00:01.000>", lrc);
    }

    [Fact]
    public void GenerateEnhancedLrcContent_UsesEnhancedWordTimestamps_WhenAvailable()
    {
        var lyrics = new LyricsSource
        {
            SyncedLyrics =
            [
                new SynchronizedLyric("Oh yeah,", "[00:01.00]", 1000, 2000)
                {
                    Words =
                    [
                        new SynchronizedLyricWord("Oh", 1000, 1300),
                        new SynchronizedLyricWord(" ", 1300, 1301),
                        new SynchronizedLyricWord("yeah,", 1400, 2000)
                    ]
                }
            ]
        };

        var elrc = lyrics.GenerateEnhancedLrcContent("AJE", "Alikiba", "So Hot");

        Assert.Contains("[ti:AJE]", elrc);
        Assert.Contains("[00:01.00]<00:01.000>Oh <00:01.400>yeah,", elrc);
    }

    [Fact]
    public void GenerateLrcContent_KeepsLineOnlyOutput_WhenWordsAreUnavailable()
    {
        var lyrics = new LyricsSource
        {
            SyncedLyrics =
            [
                new SynchronizedLyric("Line only", "[00:01.00]", 1000, 2000)
            ]
        };

        var lrc = lyrics.GenerateLrcContent();

        Assert.Contains("[00:01.00]Line only", lrc);
        Assert.DoesNotContain("<00:01.000>", lrc);
    }

    [Fact]
    public void BuildMusixmatchSignedUrl_DoesNotAppendTimestampParameter()
    {
        var signedUrl = InvokeStatic<string>(
            "BuildMusixmatchSignedUrl",
            "track.search",
            new List<KeyValuePair<string, string>>
            {
                new("q_track", "Blinding Lights"),
                new("q_artist", "The Weeknd"),
                new("usertoken", "token")
            },
            "b3dc8788299f5806a70a6a20a0cb0ffc");

        Assert.StartsWith("https://apic.musixmatch.com/ws/1.1/track.search?", signedUrl);
        Assert.Contains("app_id=web-desktop-app-v1.0", signedUrl);
        Assert.Contains("format=json", signedUrl);
        Assert.Contains("q_track=Blinding+Lights", signedUrl);
        Assert.Contains("q_artist=The+Weeknd", signedUrl);
        Assert.Contains("signature=", signedUrl);
        Assert.Contains("signature_protocol=sha256", signedUrl);
        Assert.DoesNotContain("&t=", signedUrl);
        Assert.DoesNotContain("user_language=", signedUrl);
        Assert.DoesNotContain("apic-desktop.musixmatch.com", signedUrl);
    }

    [Fact]
    public void ParseMusixmatchSearchTracks_SelectsIdentityMatchedCandidate()
    {
        using var doc = JsonDocument.Parse("""
            {
              "message": {
                "header": { "status_code": 200 },
                "body": {
                  "track_list": [
                    {
                      "track": {
                        "track_id": 1,
                        "track_name": "Wrong Song",
                        "artist_name": "The Weeknd",
                        "album_name": "Wrong Album",
                        "track_length": 200
                      }
                    },
                    {
                      "track": {
                        "track_id": 2,
                        "track_name": "Blinding Lights",
                        "artist_name": "The Weeknd",
                        "album_name": "After Hours",
                        "track_length": 200,
                        "track_isrc": "USUG11904206"
                      }
                    }
                  ]
                }
              }
            }
            """);

        var tracks = GetStaticMethod("ParseMusixmatchSearchTracks").Invoke(null, [doc.RootElement])
            ?? throw new InvalidOperationException("ParseMusixmatchSearchTracks returned null.");
        var selected = GetStaticMethod("SelectMusixmatchTrack").Invoke(
            null,
            [
                new Track
                {
                    Title = "Blinding Lights",
                    ArtistString = "The Weeknd",
                    Duration = 200,
                    ISRC = "USUG11904206"
                },
                tracks
            ]);

        Assert.NotNull(selected);
        var trackId = selected.GetType().GetProperty("TrackId")?.GetValue(selected);
        Assert.Equal(2L, trackId);
    }

    [Fact]
    public void TryParseLrcTimestampMilliseconds_AcceptsHundredthsAndMilliseconds()
    {
        var method = GetStaticMethod("TryParseLrcTimestampMilliseconds");
        object?[] hundredthsArgs = ["01:02.34", 0];
        object?[] millisecondsArgs = ["01:02.345", 0];

        Assert.True((bool)method.Invoke(null, hundredthsArgs)!);
        Assert.True((bool)method.Invoke(null, millisecondsArgs)!);
        Assert.Equal(62340, hundredthsArgs[1]);
        Assert.Equal(62345, millisecondsArgs[1]);
    }

    [Fact]
    public async Task ResolveLoadedLyricsOrNullAsync_ReturnsNull_WhenResolverReturnsNull()
    {
        var resolver = (Func<Task<LyricsBase>>)(() => Task.FromResult<LyricsBase>(null!));

        var task = (Task<LyricsBase?>)(GetStaticMethod("ResolveLoadedLyricsOrNullAsync")
            .Invoke(null, [resolver])
            ?? throw new InvalidOperationException("LyricsService.ResolveLoadedLyricsOrNullAsync returned null task."));

        var resolved = await task;

        Assert.Null(resolved);
    }

    private static void AssertRequirement(object requirements, string propertyName, bool expected)
    {
        var property = requirements.GetType().GetProperty(propertyName)
            ?? throw new InvalidOperationException($"Requirement property {propertyName} not found.");
        var actual = (bool)(property.GetValue(requirements)
            ?? throw new InvalidOperationException($"Requirement property {propertyName} returned null."));
        Assert.Equal(expected, actual);
    }

    private static LyricsService CreateUninitializedLyricsService()
    {
        var service = (LyricsService)RuntimeHelpers.GetUninitializedObject(typeof(LyricsService));
        typeof(LyricsService)
            .GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, NullLogger<LyricsService>.Instance);
        return service;
    }

    private static string CreateLyricsTestDirectory()
    {
        var directory = Path.Join(Path.GetTempPath(), $"deezspotag-lyrics-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static Track CreateLyricsTestTrack()
    {
        return new Track
        {
            Id = "lyrics-policy-track",
            Title = "Track",
            ArtistString = "Artist"
        };
    }

    private static (string FilePath, string Filename, string ExtrasPath, string CoverPath, string ArtistPath)
        BuildLyricsPaths(string directory)
    {
        return (directory, "track", directory, directory, directory);
    }
}
