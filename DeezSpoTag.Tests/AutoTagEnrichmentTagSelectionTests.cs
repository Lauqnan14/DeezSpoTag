using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagEnrichmentTagSelectionTests
{
    private static readonly string[] ExpectedDownloadEnrichmentTags =
    {
        "artist",
        "genre"
    };

    private static readonly string[] ExpectedEnhancementOnlyTags =
    {
        "artist",
        "genre"
    };
    private static readonly string[] ExpectedMergedEnhancementTags =
    {
        "artist",
        "genre"
    };
    private static readonly string[] RequestedYearAndArtistTags = ["year", "artist"];
    private static readonly string[] ItunesPlatformOnly = ["itunes"];
    private static readonly string[] ExpectedReleaseDateOnly = ["releaseDate"];
    private static readonly string[] ExpectedManualLyricsTags = ["lyrics", "syncedLyrics", "ttmlLyrics"];

    [Fact]
    public void ResolveEnrichmentRequestedTags_DownloadEnrichment_UsesOnlyEnrichmentTags()
    {
        var method = typeof(AutoTagService).GetMethod(
            "ResolveEnrichmentRequestedTags",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var root = new JsonObject
        {
            ["tags"] = new JsonArray("artist", "genre"),
            ["downloadTags"] = new JsonArray("title", "trackId", "releaseId", "source", "url")
        };

        var actual = Assert.IsType<List<string>>(method!.Invoke(null, new object?[] { root }));
        Assert.Equal(ExpectedDownloadEnrichmentTags, actual);
    }

    [Fact]
    public void ResolveEnrichmentRequestedTags_NonDownloadEnrichment_UsesOnlyEnrichmentTags()
    {
        var method = typeof(AutoTagService).GetMethod(
            "ResolveEnrichmentRequestedTags",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var root = new JsonObject
        {
            ["tags"] = new JsonArray("artist", "genre"),
            ["downloadTags"] = new JsonArray("trackId", "releaseId", "source", "url")
        };

        var actual = Assert.IsType<List<string>>(method!.Invoke(null, new object?[] { root }));
        Assert.Equal(ExpectedEnhancementOnlyTags, actual);
    }

    [Fact]
    public void ResolveAutomaticDownloadEnrichmentRequestedTags_RemovesEveryLyricsTag()
    {
        var method = typeof(AutoTagService).GetMethod(
            "ResolveAutomaticDownloadEnrichmentRequestedTags",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var root = new JsonObject
        {
            ["tags"] = new JsonArray(
                "artist",
                "lyrics",
                "unsyncedLyrics",
                "syncedLyrics",
                "ttmlLyrics",
                "genre")
        };

        var actual = Assert.IsType<List<string>>(method!.Invoke(null, new object?[] { root }));
        Assert.Equal(ExpectedDownloadEnrichmentTags, actual);
    }

    [Fact]
    public void ResolveAutomaticDownloadEnrichmentRequestedTags_LyricsOnlyProfileReturnsNoTags()
    {
        var method = typeof(AutoTagService).GetMethod(
            "ResolveAutomaticDownloadEnrichmentRequestedTags",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var root = new JsonObject
        {
            ["tags"] = new JsonArray("lyrics", "unsyncedLyrics", "syncedLyrics", "ttmlLyrics")
        };

        var actual = Assert.IsType<List<string>>(method!.Invoke(null, new object?[] { root }));
        Assert.Empty(actual);
    }

    [Fact]
    public void AutomaticDownloadLyricsOnlyProfile_UsesExistingSuccessfulSkipFinalizationPath()
    {
        var mapStatus = typeof(DownloadOrchestrationService).GetMethod(
            "MapEnrichmentResultToQueueStatus",
            BindingFlags.NonPublic | BindingFlags.Static);
        var finalizationAllowed = typeof(DownloadOrchestrationService).GetMethod(
            "IsFinalizationAllowed",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(mapStatus);
        Assert.NotNull(finalizationAllowed);

        var mappedStatus = mapStatus!.Invoke(null, new object?[] { "skipped" }) as string;
        var canFinalize = Assert.IsType<bool>(finalizationAllowed!.Invoke(null, new object?[] { "skipped" }));

        Assert.Equal("not_required", mappedStatus);
        Assert.True(canFinalize);
    }

    [Fact]
    public void ResolveEnrichmentRequestedTags_ManualEnrichmentRetainsLyricsTags()
    {
        var method = typeof(AutoTagService).GetMethod(
            "ResolveEnrichmentRequestedTags",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var root = new JsonObject
        {
            ["tags"] = new JsonArray("lyrics", "syncedLyrics", "ttmlLyrics")
        };

        var actual = Assert.IsType<List<string>>(method!.Invoke(null, new object?[] { root }));
        Assert.Equal(ExpectedManualLyricsTags, actual);
    }

    [Fact]
    public void ResolveEnhancementRequestedTags_UsesOnlyGapFillTags()
    {
        var method = typeof(AutoTagService).GetMethod(
            "ResolveEnhancementRequestedTags",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var root = new JsonObject
        {
            ["gapFillTags"] = new JsonArray("artist", "genre"),
            ["downloadTags"] = new JsonArray("title", "trackId", "releaseId")
        };

        var actual = Assert.IsType<List<string>>(method!.Invoke(null, new object?[] { root }));
        Assert.Equal(ExpectedMergedEnhancementTags, actual);
    }

    [Theory]
    [InlineData("year", "releaseDate")]
    [InlineData("date", "releaseDate")]
    [InlineData("length", "duration")]
    [InlineData("lyrics", "unsyncedLyrics")]
    [InlineData("cover", "albumArt")]
    [InlineData("recordingId", "recordingId")]
    [InlineData("artistId", "artistId")]
    [InlineData("albumArtistId", "albumArtistId")]
    [InlineData("releaseGroupId", "releaseGroupId")]
    [InlineData("albumId", "albumId")]
    [InlineData("releaseStatus", "releaseStatus")]
    [InlineData("releaseCountry", "releaseCountry")]
    [InlineData("media", "media")]
    [InlineData("activity", "activity")]
    [InlineData("discTotal", "discTotal")]
    [InlineData("copyright", "copyright")]
    [InlineData("composer", "composer")]
    [InlineData("lyricist", "lyricist")]
    [InlineData("involvedPeople", "involvedPeople")]
    [InlineData("publisher", "publisher")]
    [InlineData("description", "description")]
    [InlineData("comment", "description")]
    [InlineData("comments", "description")]
    [InlineData("replayGain", "replayGain")]
    [InlineData("language", "language")]
    [InlineData("rating", "rating")]
    public void NormalizeSupportedTagKey_MapsAliasesToCanonicalKeys(string input, string expected)
    {
        var method = typeof(AutoTagService).GetMethod(
            "NormalizeSupportedTagKey",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var actual = method!.Invoke(null, new object?[] { input }) as string;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void LocalRunner_DiscTotalMapsToItsOwnSupportedTag()
    {
        var field = typeof(LocalAutoTagRunner).GetField(
            "SupportedTagMap",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var map = Assert.IsAssignableFrom<IReadOnlyDictionary<string, SupportedTag>>(field!.GetValue(null));

        Assert.True(map.TryGetValue("discTotal", out var tag));
        Assert.Equal(SupportedTag.DiscTotal, tag);
    }

    [Fact]
    public void EnhancementFolderUniformity_DoesNotExposeLyricsOrArtworkBehaviorPolicies()
    {
        var repoRoot = FindRepoRoot();
        var view = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml"));
        var script = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        var service = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs"));

        Assert.DoesNotContain("folderUniformityArtworkPolicy", view, StringComparison.Ordinal);
        Assert.DoesNotContain("folderUniformityLyricsPolicy", view, StringComparison.Ordinal);
        Assert.DoesNotContain("folderUniformityArtworkPolicy", script, StringComparison.Ordinal);
        Assert.DoesNotContain("folderUniformityLyricsPolicy", script, StringComparison.Ordinal);
        Assert.DoesNotContain("folderUniformity.artworkPolicy", script, StringComparison.Ordinal);
        Assert.DoesNotContain("folderUniformity.lyricsPolicy", script, StringComparison.Ordinal);
        Assert.DoesNotContain("folderUniformity[\"artworkPolicy\"]", service, StringComparison.Ordinal);
        Assert.DoesNotContain("folderUniformity[\"lyricsPolicy\"]", service, StringComparison.Ordinal);
    }

    [Fact]
    public void EnhancementTagList_DoesNotExposeUnbackedAudioFeatureTags()
    {
        var repoRoot = FindRepoRoot();
        var script = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        var tagList = ExtractConstArray(script, "const TAGS = [");
        var unbacked = new[]
        {
            "danceability",
            "energy",
            "valence",
            "acousticness",
            "instrumentalness",
            "speechiness",
            "loudness",
            "tempo",
            "timeSignature",
            "liveness"
        };

        foreach (var tag in unbacked)
        {
            Assert.DoesNotContain($"tag: \"{tag}\"", tagList, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FilterSupportedTags_YearAliasIsAcceptedWhenPlatformSupportsReleaseDate()
    {
        var method = typeof(AutoTagService).GetMethod(
            "FilterSupportedTags",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var capabilityType = typeof(AutoTagService).GetNestedType(
            "PlatformTagCapabilities",
            BindingFlags.NonPublic);
        Assert.NotNull(capabilityType);

        var capability = Activator.CreateInstance(
            capabilityType!,
            new object?[] { new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "releaseDate" }, false });
        Assert.NotNull(capability);

        var capsDictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), capabilityType!);
        var capsDictionary = Activator.CreateInstance(capsDictionaryType);
        Assert.NotNull(capsDictionary);

        var addMethod = capsDictionaryType.GetMethod("Add");
        Assert.NotNull(addMethod);
        addMethod!.Invoke(capsDictionary, new[] { "itunes", capability });

        var actual = Assert.IsType<List<string>>(method!.Invoke(
            null,
            new object?[]
            {
                RequestedYearAndArtistTags,
                ItunesPlatformOnly,
                capsDictionary!
            }));

        Assert.Equal(ExpectedReleaseDateOnly, actual);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "DeezSpoTag.Web")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string ExtractConstArray(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"{marker} was not found.");
        }

        var end = source.IndexOf("];", start, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidOperationException($"{marker} terminator was not found.");
        }

        return source[start..(end + 2)];
    }
}
