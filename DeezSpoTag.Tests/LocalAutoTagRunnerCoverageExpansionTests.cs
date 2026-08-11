using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LocalAutoTagRunnerCoverageExpansionTests
{
    private static readonly string[] DeezerSpotifyPlatforms = ["deezer", "spotify"];

    private static readonly Type AutoTagRunnerConfigType =
        typeof(LocalAutoTagRunner).GetNestedType("AutoTagRunnerConfig", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("LocalAutoTagRunner.AutoTagRunnerConfig not found.");

    private static readonly Type LyricsRequestFlagsType =
        typeof(LocalAutoTagRunner).GetNestedType("LyricsRequestFlags", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("LocalAutoTagRunner.LyricsRequestFlags not found.");

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
        List<string>? platforms = null,
        string? manualReleasePreference = null,
        long? manualDestinationFolderId = null)
    {
        var config = Activator.CreateInstance(AutoTagRunnerConfigType)
            ?? throw new InvalidOperationException("Failed to instantiate AutoTagRunnerConfig.");

        AutoTagRunnerConfigType.GetProperty("Tags")!.SetValue(config, tags ?? new List<string>());
        AutoTagRunnerConfigType.GetProperty("TargetFiles")!.SetValue(config, targetFiles);
        AutoTagRunnerConfigType.GetProperty("IncludeSubfolders")!.SetValue(config, includeSubfolders);
        AutoTagRunnerConfigType.GetProperty("Platforms")!.SetValue(config, platforms ?? new List<string>());
        AutoTagRunnerConfigType.GetProperty("ManualReleasePreference")!.SetValue(config, manualReleasePreference);
        AutoTagRunnerConfigType.GetProperty("ManualDestinationFolderId")!.SetValue(config, manualDestinationFolderId);
        return config;
    }

    private static (bool WantsSynced, bool WantsUnsynced, bool WantsTtml) ApplyLyricsPreferenceGate(
        DeezSpoTagSettings settings,
        bool wantsSynced,
        bool wantsUnsynced,
        bool wantsTtml)
    {
        var flags = Activator.CreateInstance(
            LyricsRequestFlagsType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [wantsSynced, wantsUnsynced, wantsTtml],
            culture: null)
            ?? throw new InvalidOperationException("Failed to instantiate LyricsRequestFlags.");
        var result = RunnerMethod("ApplyLyricsPreferenceGate").Invoke(null, [settings, flags])
            ?? throw new InvalidOperationException("ApplyLyricsPreferenceGate returned null.");

        return (
            (bool)LyricsRequestFlagsType.GetProperty("WantsSynced")!.GetValue(result)!,
            (bool)LyricsRequestFlagsType.GetProperty("WantsUnsynced")!.GetValue(result)!,
            (bool)LyricsRequestFlagsType.GetProperty("WantsTtml")!.GetValue(result)!);
    }

    [Fact]
    public void ParseLyricsTypeSelection_NormalizesAliases()
    {
        var selected = InvokeStatic<HashSet<string>>(
            "ParseLyricsTypeSelection",
            "synced-lyrics, time_synced_lyrics, ttmllyrics, unsynced");

        Assert.Contains("lyrics", selected);
        Assert.Contains("syllable-lyrics", selected);
        Assert.Contains("ttml-lyrics", selected);
        Assert.Contains("unsynced-lyrics", selected);
    }

    [Fact]
    public void ParseLyricsTypeSelection_UsesDefaultSet_WhenRawIsOnlySeparators()
    {
        var selected = InvokeStatic<HashSet<string>>("ParseLyricsTypeSelection", ", , ,");

        Assert.Contains("lyrics", selected);
        Assert.Contains("syllable-lyrics", selected);
        Assert.Contains("ttml-lyrics", selected);
        Assert.Contains("unsynced-lyrics", selected);
    }

    [Fact]
    public void NormalizeLyricsFormat_MapsKnownValuesAndDefaultsToBoth()
    {
        Assert.Equal("lrc", InvokeStatic<string>("NormalizeLyricsFormat", "LRC"));
        Assert.Equal("lrc", InvokeStatic<string>("NormalizeLyricsFormat", "elrc"));
        Assert.Equal("ttml", InvokeStatic<string>("NormalizeLyricsFormat", " ttml "));
        Assert.Equal("both", InvokeStatic<string>("NormalizeLyricsFormat", "both"));
        Assert.Equal("both", InvokeStatic<string>("NormalizeLyricsFormat", "richlyrics"));
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

        var result = ApplyLyricsPreferenceGate(settings, true, true, true);

        Assert.False(result.WantsSynced);
        Assert.False(result.WantsUnsynced);
        Assert.False(result.WantsTtml);
    }

    [Fact]
    public void ApplyLyricsPreferenceGate_DoesNotUseTagFlagsAsBypassWhenLyricsTogglesOff()
    {
        var settings = new DeezSpoTagSettings
        {
            SaveLyrics = false,
            SyncedLyrics = false,
            Tags = new TagSettings
            {
                Lyrics = true,
                SyncedLyrics = true
            }
        };

        var result = ApplyLyricsPreferenceGate(settings, true, true, true);

        Assert.False(result.WantsSynced);
        Assert.False(result.WantsUnsynced);
        Assert.False(result.WantsTtml);
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

        var result = ApplyLyricsPreferenceGate(settings, true, true, true);

        Assert.True(result.WantsSynced);
        Assert.False(result.WantsUnsynced);
        Assert.False(result.WantsTtml);
    }

    [Fact]
    public void ApplyLyricsPreferenceGate_AllowsTtmlOnlyWhenTtmlTypeAndOutputAreSelected()
    {
        var settings = new DeezSpoTagSettings
        {
            SaveLyrics = true,
            SyncedLyrics = true,
            LrcType = "ttml-lyrics",
            LrcFormat = "ttml"
        };

        var result = ApplyLyricsPreferenceGate(settings, true, true, true);

        Assert.False(result.WantsSynced);
        Assert.False(result.WantsUnsynced);
        Assert.True(result.WantsTtml);
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
    public void ShouldLookupLyricsInManualEnrichment_RejectsAutomaticAndDownloadEnrichment()
    {
        var config = CreateRunnerConfig(tags: new List<string> { "syncedLyrics" });
        var settings = new DeezSpoTagSettings
        {
            SaveLyrics = true,
            SyncedLyrics = true,
            LrcType = "synced-lyrics",
            LrcFormat = "lrc"
        };

        var shouldLookup = InvokeStatic<bool>("ShouldLookupLyricsInManualEnrichment", config, settings);

        Assert.False(shouldLookup);
    }

    [Fact]
    public void ShouldLookupLyricsInManualEnrichment_AllowsExplicitManualEnrichment()
    {
        var config = CreateRunnerConfig(
            tags: new List<string> { "syncedLyrics" },
            manualReleasePreference: "album",
            manualDestinationFolderId: 1);
        var settings = new DeezSpoTagSettings
        {
            SaveLyrics = true,
            SyncedLyrics = true,
            LrcType = "synced-lyrics",
            LrcFormat = "lrc"
        };

        var shouldLookup = InvokeStatic<bool>("ShouldLookupLyricsInManualEnrichment", config, settings);

        Assert.True(shouldLookup);
    }

    [Fact]
    public void LyricsLookupEntryPoints_AreRestrictedToManualEnrichment()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");

        Assert.Contains("if (ShouldLookupLyricsInManualEnrichment(context.Plan.Config, context.Plan.Settings))", source, StringComparison.Ordinal);
        Assert.Contains("var wantsAppleLyrics = ShouldLookupLyricsInManualEnrichment(config, settings);", source, StringComparison.Ordinal);
        Assert.Contains("var enableLyrics = context.IsManualEnrichment", source, StringComparison.Ordinal);
        Assert.Contains("=> IsManualEnrichment(config) && ShouldRequestAnyLyrics(config, settings);", source, StringComparison.Ordinal);
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
    public void BuildEffectivePlatforms_ExcludesLyricsOnlyPlatformsOutsideManualEnrichment()
    {
        var config = CreateRunnerConfig(platforms: new List<string> { "deezer", "musixmatch", "lrclib" });

        var platforms = InvokeStatic<List<string>>("BuildEffectivePlatforms", config);

        Assert.Equal(new[] { "deezer" }, platforms);
    }

    [Fact]
    public void BuildEffectivePlatforms_CollapsesLyricsProvidersIntoOnePassForManualEnrichment()
    {
        var config = CreateRunnerConfig(
            platforms: new List<string> { "deezer", "musixmatch", "lrclib" },
            manualReleasePreference: "album",
            manualDestinationFolderId: 1);

        var platforms = InvokeStatic<List<string>>("BuildEffectivePlatforms", config);

        Assert.Equal(new[] { "deezer", "lyrics" }, platforms);
    }

    [Fact]
    public void ResolveLyricsProviderOrder_PreservesConfiguredFallbackChain()
    {
        var config = CreateRunnerConfig(
            platforms: new List<string> { "deezer", "musixmatch", "lrclib" },
            manualReleasePreference: "album",
            manualDestinationFolderId: 1);

        var providers = InvokeStatic<List<string>>("ResolveLyricsProviderOrder", config);

        Assert.Equal(new[] { "musixmatch", "lrclib" }, providers);
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
    [InlineData("cover.webp", true)]
    [InlineData("cover_tall.gif", true)]
    [InlineData("Artist - Album.webp", true)]
    [InlineData("track.mp4", false)]
    [InlineData("square_animated_artwork.flac", false)]
    public void IsAnimatedArtworkFile_DetectsKnownAnimatedArtworkPatterns(string fileName, bool expected)
    {
        var result = AnimatedArtworkFileNaming.IsAnimatedArtworkSidecar(fileName);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Runner_AllowsOtherPlatformsAfterForcedShazamNoMatch()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");

        Assert.Contains("if (config.ForceShazam || (hasShazamConfig && shazamConfig.ForceMatch))", source, StringComparison.Ordinal);
        Assert.Contains("ShazamFailureKind.Infrastructure", source, StringComparison.Ordinal);
        Assert.Contains("ShazamFailureKind.NoMatch", source, StringComparison.Ordinal);
        Assert.Contains("new ShazamEnrichmentResult(false, \"shazam could not identify track\", false, ShazamFailureKind.NoMatch)", source, StringComparison.Ordinal);
        Assert.Contains("continuing with {context.Platform}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShouldShortCircuitOnShazamIdentifyFailure", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EmitSkippedStatus(context, shazamResult.Error ?? \"shazam identify failed\", shazamResult.UsedShazam)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EmitReviewStatus(\n                    context,\n                    shazamResult.Error ?? \"shazam identify failed\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShazamFingerprintMatching_UsesOriginalFileIdentityForValidation()
    {
        var runnerSource = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        var matcherSource = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "ShazamMatcher.cs");

        Assert.Contains("var validationInfo = firstManualPass", runnerSource, StringComparison.Ordinal);
        Assert.Contains("? BuildAudioInfo(", runnerSource, StringComparison.Ordinal);
        Assert.Contains(": CloneAudioInfo(context.Plan.OriginalManualInfo[context.FileIndex]);", runnerSource, StringComparison.Ordinal);
        Assert.Contains("var info = firstManualPass", runnerSource, StringComparison.Ordinal);
        Assert.Contains("? CloneAudioInfo(validationInfo)", runnerSource, StringComparison.Ordinal);
        Assert.Contains("var matchInfo = string.Equals(context.Platform, ShazamPlatform, StringComparison.OrdinalIgnoreCase)", runnerSource, StringComparison.Ordinal);
        Assert.Contains("? validationInfo", runnerSource, StringComparison.Ordinal);
        Assert.Contains("var match = await ResolvePlatformMatchAsync(context, matchInfo);", runnerSource, StringComparison.Ordinal);
        Assert.Contains("usedShazamForStatus", runnerSource, StringComparison.Ordinal);
        Assert.Contains("!string.Equals(context.Platform, ShazamPlatform", runnerSource, StringComparison.Ordinal);
        Assert.Contains("var validationBasis", runnerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("var match = await ResolvePlatformMatchAsync(context, info, usedShazamForStatus);", runnerSource, StringComparison.Ordinal);
        Assert.Contains("Isrc = recognized.Isrc,", matcherSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Isrc = FirstNonEmpty(recognized.Isrc, info.Isrc)", matcherSource, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalEnrichmentFailures_DoNotFailResolvedProviderMetadata()
    {
        var runnerSource = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");

        Assert.Contains("catch (Exception ex) when (ex is not OperationCanceledException)", runnerSource, StringComparison.Ordinal);
        Assert.Contains("optional {stepName} failed; continuing with provider metadata", runnerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtworkPersistenceFailure_DoesNotDiscardPersistedProviderMetadata()
    {
        var runnerSource = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");

        Assert.Contains("persistenceFailures.Remove(SupportedTag.AlbumArt)", runnerSource, StringComparison.Ordinal);
        Assert.Contains("returnedTags.Remove(SupportedTag.AlbumArt)", runnerSource, StringComparison.Ordinal);
        Assert.Contains("retaining provider metadata and reporting artwork as missing", runnerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ShazamProviderMatch_UsesAsyncRecognizerWithoutTaskRunWrapper()
    {
        var matcherSource = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "ShazamMatcher.cs");

        Assert.Contains("await _recognitionService.RecognizeAsync(filePath, cancellationToken)", matcherSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Task" + ".Run(", matcherSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ShazamRecognition_RetriesAreBoundedInsideProviderDeadline()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "ShazamRecognitionService.cs");

        Assert.Contains("AudioOnlySignatureRetryWindowsSeconds = [10, 18]", source, StringComparison.Ordinal);
        Assert.Contains("RecognizerProcessTimeout = TimeSpan.FromSeconds(15)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UseSearchAssistedFallbackAfterAudioOnlyMiss", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Mp4NonCoreFields_FallThroughToRawTagWriter()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");

        Assert.Contains("if (Mp4TagHelper.TrySetMp4Field(", source, StringComparison.Ordinal);
        Assert.Contains("SetRaw(context, binding.Mp4Field, binding.Tag, values);", source, StringComparison.Ordinal);
        Assert.Contains("default:\n                    return false;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomaticEnrichment_DoesNotApplyManualReleasePreference()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");

        Assert.Contains("PreferredReleaseType = IsManualEnrichment(config)", source, StringComparison.Ordinal);
        Assert.Contains("? config.ManualReleasePreference\n                : null", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PreferredReleaseType = config.ManualReleasePreference", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StyleMerge_SplitsConfiguredCompositeValuesBeforeDeduplication()
    {
        var normalized = InvokeStatic<List<string>>(
            "NormalizeStyleValues",
            new[] { "Synthwave, New Wave", "Synthwave", "New Wave" },
            ", ");

        Assert.Equal(new[] { "Synthwave", "New Wave" }, normalized);

        var normalizedLegacyVorbis = InvokeStatic<List<string>>(
            "NormalizeStyleValues",
            new[] { "Synthwave, New Wave", "Synthwave" },
            string.Empty);

        Assert.Equal(new[] { "Synthwave", "New Wave" }, normalizedLegacyVorbis);
    }

    [Fact]
    public void GenreWrite_PerformsFinalCanonicalDedupeAfterFormatting()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");

        Assert.Contains(
            "genres = GenreTagAliasNormalizer.DedupeValues(genres, context.GenreBlockList);",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EnhancementArtistSeparator_UsesVorbisSeparatorForNonMp3NonMp4Files()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        var body = ExtractMethodBody(source, "private static string ResolveArtistSeparator");

        Assert.Contains("return config.Separators.Vorbis ?? \"\";", body, StringComparison.Ordinal);
        Assert.Contains("return config.Separators.Id3 ?? \"\";", body, StringComparison.Ordinal);
        Assert.True(
            body.LastIndexOf("return config.Separators.Vorbis ?? \"\";", StringComparison.Ordinal)
            > body.LastIndexOf("return config.Separators.Id3 ?? \"\";", StringComparison.Ordinal),
            "Non-MP3/non-MP4 formats must fall through to Vorbis separators after the MP3 branch.");
    }

    [Fact]
    public void AutomaticEnrichment_AttemptsAppleExtrasOnlyOncePerFile()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");

        Assert.Contains("if (context.Plan.AttemptedAppleExtras.Add(context.FileIndex))", source, StringComparison.Ordinal);
        Assert.DoesNotContain("if (!isManualEnrichment || context.Plan.AttemptedAppleExtras.Add(context.FileIndex))", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryWideEnhancement_UsesFortyFileBatchesWithoutTargetFilePath()
    {
        var runnerSource = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        var autoTagSource = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");
        var workflowSource = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.EnhancementWorkflows.cs");
        var organizerSource = ReadSource("DeezSpoTag.Web", "Services", "AutoTagLibraryOrganizer.cs");
        var executeBody = ExtractMethodBody(runnerSource, "private async Task ExecutePlatformPassesAsync");
        var batchBody = ExtractMethodBody(runnerSource, "private async Task ExecuteLibraryWideEnhancementBatchesAsync");
        var enhancementBody = ExtractMethodBody(autoTagSource, "private bool TryBuildEnhancementStage");

        Assert.Contains("DefaultLibraryWideEnhancementBatchSize = 40", runnerSource, StringComparison.Ordinal);
        Assert.Contains("LibraryWideEnhancementBatchSize", runnerSource, StringComparison.Ordinal);
        Assert.Contains("ExecuteLibraryWideEnhancementBatchesAsync", executeBody, StringComparison.Ordinal);
        Assert.Contains("config.TargetFiles == null", runnerSource, StringComparison.Ordinal);
        Assert.Contains("plan.Files.Sort(CompareLibraryWideEnhancementFiles);", runnerSource, StringComparison.Ordinal);
        Assert.Contains("batchStart += batchSize", batchBody, StringComparison.Ordinal);
        Assert.Contains("for (var platformIndex = firstPlatformIndex; platformIndex < plan.PlatformCount; platformIndex++)", batchBody, StringComparison.Ordinal);
        Assert.Contains("if (await batchCompletedCallback(plan.Files.GetRange(batchStart, batchEnd - batchStart), token))", batchBody, StringComparison.Ordinal);
        Assert.Contains("ApplyEnhancementBatchTemplatesAsync", autoTagSource, StringComparison.Ordinal);
        Assert.Contains("OrganizePathInBatchesAsync", workflowSource, StringComparison.Ordinal);
        Assert.Contains("options.BatchScopedFilesOnly = true;", workflowSource, StringComparison.Ordinal);
        Assert.Contains("if (options.BatchScopedFilesOnly)", organizerSource, StringComparison.Ordinal);
        Assert.Contains("stageRoot[AutoTagLiterals.LibraryWideEnhancementBatchSizeKey] = 40;", enhancementBody, StringComparison.Ordinal);
        Assert.Contains("WriteStringList(stageRoot, AutoTagLiterals.TargetFilesKey, targetFiles);", enhancementBody, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] pathParts)
        => File.ReadAllText(Path.Join([ResolveRepoRoot(), .. pathParts]));

    private static string ExtractMethodBody(string source, string methodSignature)
    {
        var start = source.IndexOf(methodSignature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find method: {methodSignature}");
        var brace = source.IndexOf('{', start);
        Assert.True(brace > start, $"Could not find method body: {methodSignature}");
        var depth = 0;
        for (var index = brace; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(brace, index - brace + 1);
                }
            }
        }

        throw new InvalidOperationException($"Could not extract method body: {methodSignature}");
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Join(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root could not be resolved.");
    }
}
