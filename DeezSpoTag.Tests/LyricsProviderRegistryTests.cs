using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LyricsProviderRegistryTests
{
    [Fact]
    public void Registry_ContainsOnlyApprovedProviders_InDefaultOrder()
    {
        Assert.Equal(
            [
                "apple",
                "deezer",
                "spotify",
                "lrclib",
                "musixmatch",
                "youlyplus",
                "betterlyrics"
            ],
            LyricsProviderRegistry.DefaultOrder);
    }

    [Theory]
    [InlineData("YouLy+", "youlyplus")]
    [InlineData("lyricsplus", "youlyplus")]
    [InlineData("Better Lyrics", "betterlyrics")]
    [InlineData("lrcget", "lrclib")]
    [InlineData("itunes", "apple")]
    public void Registry_NormalizesApprovedAliases(string input, string expected)
    {
        Assert.True(LyricsProviderRegistry.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("simpmusic")]
    [InlineData("kugou")]
    [InlineData("youtube")]
    [InlineData("subsonic")]
    [InlineData("navidrome")]
    public void Registry_DoesNotIncludeOutOfScopeProviders(string provider)
    {
        Assert.False(LyricsProviderRegistry.IsRegistered(provider));
    }

    [Fact]
    public void NewProviders_DeclareWordSynchronizedCapabilities()
    {
        Assert.True(LyricsProviderRegistry.TryGet("youlyplus", out var youLyPlus));
        Assert.True(youLyPlus.SupportsWordSynchronized);
        Assert.False(youLyPlus.SupportsNativeTtml);

        Assert.True(LyricsProviderRegistry.TryGet("betterlyrics", out var betterLyrics));
        Assert.True(betterLyrics.SupportsWordSynchronized);
        Assert.True(betterLyrics.SupportsNativeTtml);
    }

    [Theory]
    [InlineData("apple", false)]
    [InlineData("deezer", false)]
    [InlineData("spotify", false)]
    [InlineData("lrclib", true)]
    [InlineData("musixmatch", true)]
    [InlineData("youlyplus", true)]
    [InlineData("betterlyrics", true)]
    public void Registry_DeclaresWhetherProviderIsLyricsOnly(string provider, bool expected)
    {
        Assert.True(LyricsProviderRegistry.TryGet(provider, out var descriptor));
        Assert.Equal(expected, descriptor.IsLyricsOnly);
    }

    [Fact]
    public void AutoTagUi_OffersBothApprovedNewProviders()
    {
        var root = ResolveRepoRoot();
        var view = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml"));
        var script = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));

        Assert.Contains("LyricsProviderRegistry.All", view, StringComparison.Ordinal);
        Assert.Contains("@foreach (var provider in lyricsProviders)", view, StringComparison.Ordinal);
        Assert.Contains("lyricsProviderRegistry", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DEFAULT_LYRICS_SOURCE_ORDER = Object.freeze([\"apple\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("autotag-write-lrc", view, StringComparison.Ordinal);
        Assert.DoesNotContain("writeLrc", script, StringComparison.Ordinal);
        Assert.Contains("technical.saveLyrics === true || technical.syncedLyrics === true", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("apple", true, true, true)]
    [InlineData("deezer", true, false, false)]
    [InlineData("spotify", true, false, false)]
    [InlineData("lrclib", true, false, false)]
    [InlineData("musixmatch", true, true, false)]
    [InlineData("youlyplus", true, true, false)]
    [InlineData("betterlyrics", true, true, true)]
    public void ProviderOutputMatrix_IsExplicit(
        string provider,
        bool line,
        bool word,
        bool nativeTtml)
    {
        Assert.True(LyricsProviderRegistry.TryGet(provider, out var descriptor));
        Assert.Equal(line, descriptor.SupportsLineSynchronized);
        Assert.Equal(word, descriptor.SupportsWordSynchronized);
        Assert.Equal(nativeTtml, descriptor.SupportsNativeTtml);
    }

    [Fact]
    public void StageMatrix_UsesUnifiedEngineAndExcludesAutomaticDownloadEnrichment()
    {
        var root = ResolveRepoRoot();
        var runner = File.ReadAllText(Path.Join(
            root,
            "DeezSpoTag.Web",
            "Services",
            "AutoTag",
            "LocalAutoTagRunner.cs"));
        var stages = File.ReadAllText(Path.Join(
            root,
            "DeezSpoTag.Web",
            "Services",
            "AutoTagService.EnrichmentStages.cs"));
        var refresh = File.ReadAllText(Path.Join(
            root,
            "DeezSpoTag.Web",
            "Services",
            "LyricsRefreshQueueService.cs"));
        var prefetch = File.ReadAllText(Path.Join(
            root,
            "DeezSpoTag.Services",
            "Download",
            "Shared",
            "EngineAudioPostDownloadHelper.cs"));

        Assert.Contains("_downloadLyricsService.ResolveLyricsAsync", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("_appleLyricsService.ResolveLyricsAsync", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("PopulateAppleLyricsAsync", runner, StringComparison.Ordinal);
        Assert.Contains("lyricsProvider.IsLyricsOnly", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("case \"youlyplus\"", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("case \"betterlyrics\"", runner, StringComparison.Ordinal);
        Assert.Contains("ResolveAutomaticDownloadEnrichmentRequestedTags", stages, StringComparison.Ordinal);
        Assert.Contains("provider.IsLyricsOnly", stages, StringComparison.Ordinal);
        Assert.Contains("LyricsService", refresh, StringComparison.Ordinal);
        Assert.Contains("ResolveLyricsWithDetailsAsync", prefetch, StringComparison.Ordinal);
    }

    [Fact]
    public void YouLyPlusPayload_PreservesWordTimingForElrcAndTtml()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "trackName": "AJE",
              "artistName": "Alikiba",
              "duration": 180,
              "lyrics": [
                {
                  "text": "Aje",
                  "time": 1000,
                  "duration": 900,
                  "syllabus": [
                    { "text": "A", "time": 1000, "duration": 300 },
                    { "text": "je", "time": 1300, "duration": 600 }
                  ]
                }
              ]
            }
            """);
        var settings = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            LrcType = "lyrics,syllable-lyrics,ttml-lyrics",
            LrcFormat = "lrc,elrc,ttml"
        };

        var result = InvokeYouLyPlusParser(document.RootElement, settings);

        Assert.NotNull(result);
        Assert.True(result!.HasEnhancedSynchronizedLyrics());
        Assert.Equal(2, result.SyncedLyrics![0].Words!.Count);
        Assert.Contains("<span begin=\"00:00:01.000\"", result.TtmlLyrics, StringComparison.Ordinal);
        Assert.True(AppleLyricsService.IsWordSyncedTtml(result.TtmlLyrics));
    }

    [Fact]
    public void YouLyPlusPayload_DoesNotPromoteLineTimingToWordTtml()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "trackName": "AJE",
              "artistName": "Alikiba",
              "duration": 180,
              "syncedLyrics": "[00:01.00]Aje"
            }
            """);
        var settings = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            LrcType = "lyrics,syllable-lyrics,ttml-lyrics",
            LrcFormat = "lrc,elrc,ttml"
        };

        var result = InvokeYouLyPlusParser(document.RootElement, settings);

        Assert.NotNull(result);
        Assert.True(result!.IsSynced());
        Assert.False(result.HasEnhancedSynchronizedLyrics());
        Assert.Null(result.TtmlLyrics);
    }

    [Fact]
    public void YouLyPlusPayload_AcceptsLiveShapeWithoutRepeatedIdentity()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "lyrics": [
                {
                  "text": "Aje",
                  "time": 1000,
                  "duration": 900,
                  "syllabus": [
                    { "text": "A", "time": 1000, "duration": 300 },
                    { "text": "je", "time": 1300, "duration": 600 }
                  ]
                }
              ]
            }
            """);
        var settings = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            LrcType = "lyrics,syllable-lyrics,ttml-lyrics",
            LrcFormat = "lrc,elrc,ttml"
        };

        var result = InvokeYouLyPlusParser(document.RootElement, settings);

        Assert.NotNull(result);
        Assert.True(result!.HasEnhancedSynchronizedLyrics());
        Assert.NotNull(result.TtmlLyrics);
        Assert.True(AppleLyricsService.IsWordSyncedTtml(result.TtmlLyrics));
    }

    [Fact]
    public void YouLyPlusPayload_RejectsTimelineBeyondTrackDuration()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "lyrics": [
                {
                  "text": "Wrong timeline",
                  "time": 250000,
                  "duration": 1000,
                  "syllabus": [
                    { "text": "Wrong", "time": 250000, "duration": 500 },
                    { "text": "timeline", "time": 250500, "duration": 500 }
                  ]
                }
              ]
            }
            """);
        var settings = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            LrcType = "lyrics,syllable-lyrics,ttml-lyrics",
            LrcFormat = "lrc,elrc,ttml"
        };

        Assert.Null(InvokeYouLyPlusParser(document.RootElement, settings));
    }

    [Fact]
    public void CanonicalWordLyrics_PreserveAgentBackgroundAndAuxiliaryText()
    {
        const string ttml =
            """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" timing="Word">
              <body>
                <div>
                  <p begin="00:00:01.000" end="00:00:03.000" ttm:agent="v1">
                    <span begin="00:00:01.000" end="00:00:02.000">Main</span>
                    <span ttm:role="x-translation">Translation</span>
                    <span ttm:role="x-roman">Romanization</span>
                    <span ttm:role="x-bg">Background</span>
                  </p>
                </div>
              </body>
            </tt>
            """;

        Assert.True(AppleLyricsService.TryConvertTtmlToSynchronizedLyrics(ttml, out var lines));
        var line = Assert.Single(lines);
        Assert.Equal("v1", line.Agent);
        Assert.Equal("Translation", line.Translation);
        Assert.Equal("Romanization", line.Romanization);
        Assert.Equal("Background", line.BackgroundVocals);
        Assert.Equal("Main", Assert.Single(line.Words!).Text);
    }

    [Fact]
    public void CachedLyricsClone_PreservesCanonicalRichLyricsData()
    {
        var source = new LyricsSource
        {
            ProviderId = "betterlyrics",
            NativeSourceFormat = "ttml",
            SourcePayloadHash = new string('A', 64),
            SyncedLyrics =
            [
                new SynchronizedLyric("Main", "[00:01.00]", 1000, 1000)
                {
                    Agent = "v1",
                    IsBackground = true,
                    Translation = "Translation",
                    Romanization = "Romanization",
                    BackgroundVocals = "Background",
                    Words =
                    [
                        new SynchronizedLyricWord("Main", 1000, 2000)
                        {
                            IsBackground = true
                        }
                    ]
                }
            ]
        };
        var method = typeof(LyricsService).GetMethod(
            "CloneLyrics",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(LyricsService), "CloneLyrics");

        var clone = Assert.IsAssignableFrom<LyricsBase>(method.Invoke(null, [source]));
        Assert.Equal(source.ProviderId, clone.ProviderId);
        Assert.Equal(source.NativeSourceFormat, clone.NativeSourceFormat);
        Assert.Equal(source.SourcePayloadHash, clone.SourcePayloadHash);
        var line = Assert.Single(clone.SyncedLyrics!);
        Assert.Equal("v1", line.Agent);
        Assert.True(line.IsBackground);
        Assert.Equal("Translation", line.Translation);
        Assert.Equal("Romanization", line.Romanization);
        Assert.Equal("Background", line.BackgroundVocals);
        Assert.True(Assert.Single(line.Words!).IsBackground);
    }

    private static LyricsBase? InvokeYouLyPlusParser(
        JsonElement payload,
        DeezSpoTagSettings settings)
    {
        var method = typeof(LyricsService).GetMethod(
            "ParseYouLyPlusPayload",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(LyricsService), "ParseYouLyPlusPayload");
        var track = new Track
        {
            Title = "AJE",
            MainArtist = new Artist("Alikiba"),
            Artists = ["Alikiba"],
            Duration = 180
        };
        return (LyricsBase?)method.Invoke(null, [payload, track, "Alikiba", settings]);
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Services")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
