using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TaggingProfileCanonicalizerTests
{
    [Fact]
    public void BuildTagConfig_UsesAutoTagArraysAsCanonicalSource()
    {
        var fallback = CreateEmptyTagConfig();
        fallback.Album = TagSource.DownloadSource;

        var data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["downloadTags"] = JsonSerializer.SerializeToElement(new[] { "title", "artist", "lyrics" }),
            ["tags"] = JsonSerializer.SerializeToElement(new[] { "style", "genre" })
        };

        var config = TaggingProfileCanonicalizer.BuildTagConfig(fallback, data);

        Assert.Equal(TagSource.DownloadSource, config.Title);
        Assert.Equal(TagSource.DownloadSource, config.Artist);
        Assert.Equal(TagSource.DownloadSource, config.UnsyncedLyrics);
        Assert.Equal(TagSource.AutoTagPlatform, config.Style);
        Assert.Equal(TagSource.AutoTagPlatform, config.Genre);
        Assert.Equal(TagSource.None, config.Album);
    }

    [Fact]
    public void Canonicalize_SeedsMissingTagArraysFromTagConfig()
    {
        var profile = new TaggingProfile
        {
            TagConfig = CreateEmptyTagConfig(),
            AutoTag = new AutoTagSettings
            {
                Data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            }
        };

        profile.TagConfig.Title = TagSource.DownloadSource;
        profile.TagConfig.Genre = TagSource.Both;
        profile.TagConfig.Style = TagSource.AutoTagPlatform;

        var changed = TaggingProfileCanonicalizer.Canonicalize(profile, seedFromTagConfigWhenMissing: true);

        Assert.True(changed);
        Assert.Equal(
            new[] { "title", "genre" },
            ReadStringArray(profile.AutoTag.Data["downloadTags"]));
        Assert.Equal(
            new[] { "genre", "style" },
            ReadStringArray(profile.AutoTag.Data["tags"]));

        Assert.Equal(TagSource.DownloadSource, profile.TagConfig.Title);
        Assert.Equal(TagSource.Both, profile.TagConfig.Genre);
        Assert.Equal(TagSource.AutoTagPlatform, profile.TagConfig.Style);
        Assert.Equal(TagSource.None, profile.TagConfig.Album);
    }

    [Fact]
    public void Canonicalize_NormalizesAliasesAndSyncsTagConfig()
    {
        var profile = new TaggingProfile
        {
            TagConfig = CreateEmptyTagConfig(),
            AutoTag = new AutoTagSettings
            {
                Data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["downloadTags"] = JsonSerializer.SerializeToElement(new[] { "duration", "albumArt", "upc", "unsyncedLyrics" }),
                    ["tags"] = JsonSerializer.SerializeToElement(new[] { "duration", "unsyncedLyrics" })
                }
            }
        };

        var changed = TaggingProfileCanonicalizer.Canonicalize(profile, seedFromTagConfigWhenMissing: true);

        Assert.True(changed);
        Assert.Equal(
            new[] { "length", "cover", "barcode", "lyrics" },
            ReadStringArray(profile.AutoTag.Data["downloadTags"]));
        Assert.Equal(
            new[] { "length", "lyrics" },
            ReadStringArray(profile.AutoTag.Data["tags"]));

        Assert.Equal(TagSource.Both, profile.TagConfig.Duration);
        Assert.Equal(TagSource.DownloadSource, profile.TagConfig.Cover);
        Assert.Equal(TagSource.DownloadSource, profile.TagConfig.Barcode);
        Assert.Equal(TagSource.Both, profile.TagConfig.UnsyncedLyrics);
    }

    [Fact]
    public void SyncTagArraysFromConfig_OverwritesStaleArrays()
    {
        var profile = new TaggingProfile
        {
            TagConfig = CreateEmptyTagConfig(),
            AutoTag = new AutoTagSettings
            {
                Data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["downloadTags"] = JsonSerializer.SerializeToElement(new[] { "artist", "album" }),
                    ["tags"] = JsonSerializer.SerializeToElement(new[] { "genre" })
                }
            }
        };

        profile.TagConfig.Title = TagSource.DownloadSource;
        profile.TagConfig.ReleaseDate = TagSource.AutoTagPlatform;

        var changed = TaggingProfileCanonicalizer.SyncTagArraysFromConfig(profile);

        Assert.True(changed);
        Assert.Equal(new[] { "title" }, ReadStringArray(profile.AutoTag.Data["downloadTags"]));
        Assert.Equal(new[] { "releaseDate" }, ReadStringArray(profile.AutoTag.Data["tags"]));
    }

    private static UnifiedTagConfig CreateEmptyTagConfig()
    {
        var config = new UnifiedTagConfig();
        var properties = typeof(UnifiedTagConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(TagSource) && property.CanWrite);
        foreach (var property in properties)
        {
            property.SetValue(config, TagSource.None);
        }

        return config;
    }

    private static string[] ReadStringArray(JsonElement element)
    {
        return element.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!)
            .ToArray();
    }
}
