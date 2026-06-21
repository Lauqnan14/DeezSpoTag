using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
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

        AssertRequirement(requirements, "WantsTimedLyrics", expected: true);
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

        AssertRequirement(requirements, "WantsTimedLyrics", expected: false);
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

        AssertRequirement(requirements, "WantsTimedLyrics", expected: true);
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
}
