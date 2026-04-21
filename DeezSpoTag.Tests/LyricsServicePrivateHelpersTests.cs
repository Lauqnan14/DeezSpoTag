using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Utils;
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
    public async Task ResolveLoadedLyricsOrNullAsync_ReturnsNull_WhenResolverReturnsNull()
    {
        var resolver = (Func<Task<LyricsBase>>)(() => Task.FromResult<LyricsBase>(null!));

        var task = (Task<LyricsBase?>)(GetStaticMethod("ResolveLoadedLyricsOrNullAsync")
            .Invoke(null, [resolver])
            ?? throw new InvalidOperationException("LyricsService.ResolveLoadedLyricsOrNullAsync returned null task."));

        var resolved = await task;

        Assert.Null(resolved);
    }
}
