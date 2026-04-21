using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LocalAutoTagRunnerCoverageExpansionTests
{
    private static readonly string[] DeezerSpotifyPlatforms = ["deezer", "spotify"];

    private static readonly Type AutoTagRunnerConfigType =
        typeof(LocalAutoTagRunner).GetNestedType("AutoTagRunnerConfig", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("LocalAutoTagRunner.AutoTagRunnerConfig not found.");

    private static MethodInfo RunnerMethod(string name)
    {
        return typeof(LocalAutoTagRunner).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"LocalAutoTagRunner.{name} not found.");
    }

    private static T InvokeStatic<T>(string methodName, params object?[] args)
    {
        return (T)(RunnerMethod(methodName).Invoke(null, args)
            ?? throw new InvalidOperationException($"LocalAutoTagRunner.{methodName} returned null."));
    }

    private static object CreateRunnerConfig(
        List<string>? tags = null,
        List<string>? targetFiles = null,
        bool includeSubfolders = true,
        List<string>? platforms = null)
    {
        var config = Activator.CreateInstance(AutoTagRunnerConfigType)
            ?? throw new InvalidOperationException("Failed to instantiate AutoTagRunnerConfig.");

        AutoTagRunnerConfigType.GetProperty("Tags")!.SetValue(config, tags ?? new List<string>());
        AutoTagRunnerConfigType.GetProperty("TargetFiles")!.SetValue(config, targetFiles);
        AutoTagRunnerConfigType.GetProperty("IncludeSubfolders")!.SetValue(config, includeSubfolders);
        AutoTagRunnerConfigType.GetProperty("Platforms")!.SetValue(config, platforms ?? new List<string>());
        return config;
    }

    [Fact]
    public void ParseLyricsTypeSelection_NormalizesAliases()
    {
        var selected = InvokeStatic<HashSet<string>>(
            "ParseLyricsTypeSelection",
            "synced-lyrics, time_synced_lyrics, unsynced");

        Assert.Contains("lyrics", selected);
        Assert.Contains("syllable-lyrics", selected);
        Assert.Contains("unsynced-lyrics", selected);
    }

    [Fact]
    public void ParseLyricsTypeSelection_UsesDefaultSet_WhenRawIsOnlySeparators()
    {
        var selected = InvokeStatic<HashSet<string>>("ParseLyricsTypeSelection", ", , ,");

        Assert.Contains("lyrics", selected);
        Assert.Contains("syllable-lyrics", selected);
        Assert.Contains("unsynced-lyrics", selected);
    }

    [Fact]
    public void NormalizeLyricsFormat_MapsKnownValuesAndDefaultsToBoth()
    {
        Assert.Equal("lrc", InvokeStatic<string>("NormalizeLyricsFormat", "LRC"));
        Assert.Equal("ttml", InvokeStatic<string>("NormalizeLyricsFormat", " ttml "));
        Assert.Equal("both", InvokeStatic<string>("NormalizeLyricsFormat", "unknown"));
    }

    [Fact]
    public void ApplyLyricsPreferenceGate_DisablesAllRequestsWhenLyricsTogglesOff()
    {
        var settings = new DeezSpoTagSettings
        {
            SaveLyrics = false,
            SyncedLyrics = false,
            Tags = new TagSettings
            {
                Lyrics = false,
                SyncedLyrics = false
            }
        };

        var args = new object?[] { settings, true, true, true };
        RunnerMethod("ApplyLyricsPreferenceGate").Invoke(null, args);

        Assert.False((bool)args[1]!);
        Assert.False((bool)args[2]!);
        Assert.False((bool)args[3]!);
    }

    [Fact]
    public void ApplyLyricsPreferenceGate_HonorsLrcOnlyFormatAndSyncedType()
    {
        var settings = new DeezSpoTagSettings
        {
            SaveLyrics = true,
            SyncedLyrics = true,
            LrcType = "synced-lyrics",
            LrcFormat = "lrc"
        };

        var args = new object?[] { settings, true, true, true };
        RunnerMethod("ApplyLyricsPreferenceGate").Invoke(null, args);

        Assert.True((bool)args[1]!);  // synced
        Assert.False((bool)args[2]!); // unsynced not in type selection
        Assert.False((bool)args[3]!); // ttml disabled by lrc format
    }

    [Fact]
    public void ShouldRequestAnyLyrics_ReturnsFalseWhenRequestedTypesDoNotPermitSyncedOrTtml()
    {
        var config = CreateRunnerConfig(tags: new List<string> { "syncedLyrics", "ttmlLyrics" });
        var settings = new DeezSpoTagSettings
        {
            SaveLyrics = true,
            SyncedLyrics = true,
            LrcType = "unsynced-lyrics",
            LrcFormat = "ttml"
        };

        var shouldRequest = InvokeStatic<bool>("ShouldRequestAnyLyrics", config, settings);

        Assert.False(shouldRequest);
    }

    [Fact]
    public void ResolveTargetFiles_OnlyKeepsInScopeSupportedNonAnimatedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"autotag-runner-{Guid.NewGuid():N}");
        var outside = Path.Combine(Path.GetTempPath(), $"autotag-outside-{Guid.NewGuid():N}.flac");
        Directory.CreateDirectory(root);
        try
        {
            var valid = Path.Combine(root, "track.flac");
            var animated = Path.Combine(root, "square_animated_artwork.mp4");
            var unsupported = Path.Combine(root, "notes.txt");

            File.WriteAllText(valid, "audio");
            File.WriteAllText(animated, "video");
            File.WriteAllText(unsupported, "text");
            File.WriteAllText(outside, "audio");

            var config = CreateRunnerConfig(
                targetFiles: new List<string> { valid, animated, unsupported, outside, "   " });

            var resolved = InvokeStatic<IEnumerable<string>>("ResolveTargetFiles", root, config).ToList();

            Assert.Single(resolved);
            Assert.Equal(Path.GetFullPath(valid), resolved[0]);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            if (File.Exists(outside))
            {
                File.Delete(outside);
            }
        }
    }

    [Fact]
    public void ResolveTargetFiles_EnumeratesDirectory_WhenTargetFilesNotProvided()
    {
        var root = Path.Combine(Path.GetTempPath(), $"autotag-enumerate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var topLevelFlac = Path.Combine(root, "one.flac");
            var topLevelAnimatedMp4 = Path.Combine(root, "square_animated_artwork.mp4");
            var subDir = Path.Combine(root, "sub");
            var subLevelMp3 = Path.Combine(subDir, "two.mp3");
            Directory.CreateDirectory(subDir);
            File.WriteAllText(topLevelFlac, "audio");
            File.WriteAllText(topLevelAnimatedMp4, "video");
            File.WriteAllText(subLevelMp3, "audio");

            var noSubfoldersConfig = CreateRunnerConfig(targetFiles: null, includeSubfolders: false);
            var noSubfolders = InvokeStatic<IEnumerable<string>>("ResolveTargetFiles", root, noSubfoldersConfig).ToList();
            Assert.Single(noSubfolders);
            Assert.Equal(Path.GetFullPath(topLevelFlac), noSubfolders[0]);

            var withSubfoldersConfig = CreateRunnerConfig(targetFiles: null, includeSubfolders: true);
            var withSubfolders = InvokeStatic<IEnumerable<string>>("ResolveTargetFiles", root, withSubfoldersConfig).ToList();
            Assert.Equal(2, withSubfolders.Count);
            Assert.Contains(Path.GetFullPath(topLevelFlac), withSubfolders);
            Assert.Contains(Path.GetFullPath(subLevelMp3), withSubfolders);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void IsPathWithinScope_ReturnsFalseForEqualPath_AndTrueForDescendant()
    {
        var root = Path.Combine(Path.GetTempPath(), $"autotag-scope-{Guid.NewGuid():N}");
        var child = Path.Combine(root, "sub", "file.flac");

        Assert.False(InvokeStatic<bool>("IsPathWithinScope", root, root));
        Assert.True(InvokeStatic<bool>("IsPathWithinScope", child, root));
        Assert.False(InvokeStatic<bool>("IsPathWithinScope", string.Empty, root));
    }

    [Fact]
    public void BuildEffectivePlatforms_TrimsAndDeduplicatesEntries()
    {
        var config = CreateRunnerConfig(platforms: new List<string> { " deezer ", "Deezer", " spotify ", string.Empty });

        var platforms = InvokeStatic<List<string>>("BuildEffectivePlatforms", config);

        Assert.Equal(DeezerSpotifyPlatforms, platforms);
    }

    [Fact]
    public void BuildMatchCacheKey_ChangesWhenEffectiveLyricsPolicyChanges()
    {
        var config = CreateRunnerConfig(tags: new List<string> { "syncedLyrics" }, platforms: new List<string> { "deezer" });
        var info = new AutoTagAudioInfo
        {
            Title = "Title",
            Artist = "Artist",
            Artists = new List<string> { "Artist" },
            Album = "Album",
            DurationSeconds = 180
        };
        var matching = new AutoTagMatchingConfig
        {
            MatchDuration = true,
            MaxDurationDifferenceSeconds = 3,
            Strictness = 0.75
        };

        var disabledByType = new DeezSpoTagSettings
        {
            SaveLyrics = true,
            SyncedLyrics = true,
            LrcType = "unsynced-lyrics",
            LrcFormat = "lrc"
        };

        var enabledByType = new DeezSpoTagSettings
        {
            SaveLyrics = true,
            SyncedLyrics = true,
            LrcType = "lyrics,syllable-lyrics",
            LrcFormat = "lrc"
        };

        var disabledKey = InvokeStatic<string>("BuildMatchCacheKey", "deezer", info, config, disabledByType, matching);
        var enabledKey = InvokeStatic<string>("BuildMatchCacheKey", "deezer", info, config, enabledByType, matching);

        Assert.NotEqual(disabledKey, enabledKey);
    }

    [Theory]
    [InlineData("square_animated_artwork.mp4", true)]
    [InlineData("Artist - tall_animated_artwork.mp4", true)]
    [InlineData("track.mp4", false)]
    [InlineData("square_animated_artwork.flac", false)]
    public void IsAnimatedArtworkFile_DetectsKnownAnimatedArtworkPatterns(string fileName, bool expected)
    {
        var result = InvokeStatic<bool>("IsAnimatedArtworkFile", fileName);
        Assert.Equal(expected, result);
    }
}
