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
    private static readonly string[] CanonicalDownloadTags = { "title", "artist", "lyrics" };
    private static readonly string[] CanonicalAutoTags = { "style", "genre" };
    private static readonly string[] ExpectedSeededDownloadTags = { "title", "genre" };
    private static readonly string[] ExpectedSeededAutoTags = { "genre", "style" };
    private static readonly string[] LegacyAliasDownloadTags = { "duration", "albumArt", "upc", "unsyncedLyrics" };
    private static readonly string[] LegacyAliasAutoTags = { "duration", "unsyncedLyrics" };
    private static readonly string[] ExpectedNormalizedDownloadTags = { "length", "cover", "barcode", "lyrics" };
    private static readonly string[] ExpectedNormalizedAutoTags = { "length", "lyrics" };
    private static readonly string[] StaleDownloadTags = { "artist", "album" };
    private static readonly string[] StaleAutoTags = { "genre" };
    private static readonly string[] StaleEnhancementTags = { "label" };
    private static readonly string[] ExpectedSyncedDownloadTags = { "title" };
    private static readonly string[] ExpectedSyncedAutoTags = { "releaseDate" };
    private static readonly string[] ExpectedSyncedEnhancementTags = { "label" };

    [Fact]
    public void BuildTagConfig_UsesAutoTagArraysAsCanonicalSource()
    {
        var fallback = CreateEmptyTagConfig();
        fallback.Album = TagSource.DownloadSource;

        var data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["downloadTags"] = JsonSerializer.SerializeToElement(CanonicalDownloadTags),
            ["tags"] = JsonSerializer.SerializeToElement(CanonicalAutoTags)
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
            ExpectedSeededDownloadTags,
            ReadStringArray(profile.AutoTag.Data["downloadTags"]));
        Assert.Equal(
            ExpectedSeededAutoTags,
            ReadStringArray(profile.AutoTag.Data["tags"]));
        Assert.Equal(
            ExpectedSeededAutoTags,
            ReadStringArray(profile.AutoTag.Data["gapFillTags"]));

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
                    ["downloadTags"] = JsonSerializer.SerializeToElement(LegacyAliasDownloadTags),
                    ["tags"] = JsonSerializer.SerializeToElement(LegacyAliasAutoTags)
                }
            }
        };

        var changed = TaggingProfileCanonicalizer.Canonicalize(profile, seedFromTagConfigWhenMissing: true);

        Assert.True(changed);
        Assert.Equal(
            ExpectedNormalizedDownloadTags,
            ReadStringArray(profile.AutoTag.Data["downloadTags"]));
        Assert.Equal(
            ExpectedNormalizedAutoTags,
            ReadStringArray(profile.AutoTag.Data["tags"]));
        Assert.Equal(
            ExpectedNormalizedAutoTags,
            ReadStringArray(profile.AutoTag.Data["gapFillTags"]));

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
                    ["downloadTags"] = JsonSerializer.SerializeToElement(StaleDownloadTags),
                    ["tags"] = JsonSerializer.SerializeToElement(StaleAutoTags),
                    ["gapFillTags"] = JsonSerializer.SerializeToElement(StaleEnhancementTags)
                }
            }
        };

        profile.TagConfig.Title = TagSource.DownloadSource;
        profile.TagConfig.ReleaseDate = TagSource.AutoTagPlatform;

        var changed = TaggingProfileCanonicalizer.SyncTagArraysFromConfig(profile);

        Assert.True(changed);
        Assert.Equal(ExpectedSyncedDownloadTags, ReadStringArray(profile.AutoTag.Data["downloadTags"]));
        Assert.Equal(ExpectedSyncedAutoTags, ReadStringArray(profile.AutoTag.Data["tags"]));
        Assert.Equal(ExpectedSyncedEnhancementTags, ReadStringArray(profile.AutoTag.Data["gapFillTags"]));
    }

    [Fact]
    public void Canonicalize_RemovesLegacyTemplateKeys()
    {
        var profile = new TaggingProfile
        {
            TagConfig = CreateEmptyTagConfig(),
            AutoTag = new AutoTagSettings
            {
                Data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["filenameTemplate"] = JsonSerializer.SerializeToElement("%artists% - %title%"),
                    ["albumTracknameTemplate"] = JsonSerializer.SerializeToElement("%tracknumber% - %title%"),
                    ["playlistTracknameTemplate"] = JsonSerializer.SerializeToElement("%artist% - %title%")
                }
            }
        };
        profile.TagConfig.Title = TagSource.DownloadSource;

        var changed = TaggingProfileCanonicalizer.Canonicalize(profile, seedFromTagConfigWhenMissing: true);

        Assert.True(changed);
        Assert.Equal("%artists% - %title%", profile.AutoTag.Data["tracknameTemplate"].GetString());
        Assert.False(profile.AutoTag.Data.ContainsKey("filenameTemplate"));
        Assert.False(profile.AutoTag.Data.ContainsKey("albumTracknameTemplate"));
        Assert.False(profile.AutoTag.Data.ContainsKey("playlistTracknameTemplate"));
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
