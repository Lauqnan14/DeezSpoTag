using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
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
        Assert.Contains("unsynced-lyrics", selected);
    }

    [Fact]
    public void ParseSelectedLyricsTypes_NormalizesAliasesAndDeduplicates()
    {
        var settings = new DeezSpoTagSettings
        {
            LrcType = "synced-lyrics,time_synced_lyrics,unsynchronized-lyrics,lyrics,UNSYNCED"
        };

        var selected = InvokeStatic<HashSet<string>>("ParseSelectedLyricsTypes", settings);

        Assert.Equal(3, selected.Count);
        Assert.Contains("lyrics", selected);
        Assert.Contains("syllable-lyrics", selected);
        Assert.Contains("unsynced-lyrics", selected);
    }

    [Theory]
    [InlineData("lyrics", "both")]
    [InlineData("lrc", "lrc")]
    [InlineData("ttml", "ttml")]
    [InlineData("lrc+ttml", "both")]
    [InlineData("unknown-format", "both")]
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
    public void ResolveOutputRequirements_RequiresBoth_WhenBothTechnicalTypesAreEnabled()
    {
        var settings = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            SaveLyrics = true,
            LrcType = "lyrics,unsynced-lyrics",
            LrcFormat = "both"
        };

        var requirements = InvokeStatic<object>("ResolveOutputRequirements", settings);

        AssertRequirement(requirements, "WantsLrcLyrics", expected: true);
        AssertRequirement(requirements, "WantsTtmlLyrics", expected: true);
        AssertRequirement(requirements, "WantsPlainLyrics", expected: true);
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
            LrcType = "lyrics",
            LrcFormat = format
        };
        var appleLyrics = new LyricsSource
        {
            TtmlLyrics = "<?xml version=\"1.0\" encoding=\"utf-8\"?><tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=\"00:00:01.000\">Line</p></div></body></tt>"
        };

        var result = InvokeStatic<bool>("ShouldSaveTtml", settings, appleLyrics);

        Assert.Equal(expected, result);
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
                LrcType = "lyrics",
                LrcFormat = "ttml"
            };
            var lyrics = new LyricsSource
            {
                TtmlLyrics = "<?xml version=\"1.0\" encoding=\"utf-8\"?><tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=\"00:00:01.000\">Apple line</p></div></body></tt>"
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
            Assert.Contains("Apple line", await File.ReadAllTextAsync(ttmlPath));
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
                LrcType = "lyrics,unsynced-lyrics",
                LrcFormat = "ttml"
            };
            var lyrics = new LyricsSource
            {
                TtmlLyrics = "<tt><body><div><p begin=\"00:00:01.000\">Timed</p></div></body></tt>",
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
                LrcType = "lyrics,unsynced-lyrics",
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
    public async Task SaveLyricsAsync_LrcOnly_DoesNotCreateLrcFromProviderSyncedJson()
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

            Assert.False(File.Exists(Path.Join(directory, "track.lrc")));
            Assert.False(File.Exists(Path.Join(directory, "track.ttml")));
            Assert.False(File.Exists(Path.Join(directory, "track.txt")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveLyricsAsync_LrcOnly_DoesNotCreateLrcFromTtml()
    {
        var service = CreateUninitializedLyricsService();
        var directory = CreateLyricsTestDirectory();
        try
        {
            var settings = new DeezSpoTagSettings
            {
                SyncedLyrics = true,
                LrcType = "lyrics",
                LrcFormat = "lrc"
            };
            var lyrics = new LyricsSource
            {
                TtmlLyrics = "<tt><body><div><p begin=\"00:00:01.000\">Apple line</p></div></body></tt>",
                TtmlLyricsSourceFormat = LyricsSourceFormat.DownloadedTtml
            };

            await service.SaveLyricsAsync(lyrics, CreateLyricsTestTrack(), BuildLyricsPaths(directory), settings);

            Assert.False(File.Exists(Path.Join(directory, "track.lrc")));
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
              "content": "<?xml version='1.0' encoding='utf-8'?><tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=\"00:00:01.000\">Apple public line</p></div></body></tt>"
            }
            """);

        var lyrics = InvokeStatic<LyricsBase>("ParsePaxsenixLyricsPayload", doc.RootElement);

        Assert.NotNull(lyrics.TtmlLyrics);
        Assert.Equal(LyricsSourceFormat.DownloadedTtml, lyrics.TtmlLyricsSourceFormat);
        Assert.StartsWith("<?xml", lyrics.TtmlLyrics);
        Assert.Contains("Apple public line", lyrics.TtmlLyrics);
        Assert.DoesNotContain("\"type\"", lyrics.TtmlLyrics);
    }

    [Fact]
    public void TryBuildTtmlFromSyncedLyrics_BuildsOrderedEncodedParagraphs()
    {
        var lyrics = new LyricsSource
        {
            SyncedLyrics =
            [
                new SynchronizedLyric("Second line", "[00:05.00]", 5000),
                new SynchronizedLyric("First <line>", "[00:01.00]", 1000),
                new SynchronizedLyric(" ", "[00:09.00]", 9000)
            ]
        };

        var ttml = InvokeStatic<string?>("TryBuildTtmlFromSyncedLyrics", lyrics);

        Assert.NotNull(ttml);
        Assert.Contains("<?xml version=\"1.0\" encoding=\"utf-8\"?>", ttml);
        Assert.Contains("&lt;line&gt;", ttml);
        Assert.Contains("begin=\"00:00:01.000\"", ttml);
        Assert.DoesNotContain("> </p>", ttml);
        Assert.True(ttml.IndexOf("First &lt;line&gt;", StringComparison.Ordinal)
            < ttml.IndexOf("Second line", StringComparison.Ordinal));
    }

    [Fact]
    public void ShouldSynthesizeTtmlBySettings_IsDisabledByDefault()
    {
        var settings = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            LrcFormat = "ttml"
        };

        var result = InvokeStatic<bool>("ShouldSynthesizeTtmlBySettings", settings);

        Assert.False(result);
    }

    [Fact]
    public void ShouldSynthesizeTtmlBySettings_RequiresExplicitPreferenceAndTtmlOutput()
    {
        var lrcOnly = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            LrcFormat = "lrc",
            SynthesizeTtmlLyrics = true
        };
        var ttml = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            LrcFormat = "ttml",
            SynthesizeTtmlLyrics = true
        };

        Assert.False(InvokeStatic<bool>("ShouldSynthesizeTtmlBySettings", lrcOnly));
        Assert.True(InvokeStatic<bool>("ShouldSynthesizeTtmlBySettings", ttml));
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
