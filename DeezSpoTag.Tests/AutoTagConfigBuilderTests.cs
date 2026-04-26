using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagConfigBuilderTests
{
    private static readonly string[] ExpectedDownloadTagsFromConfig = { "title", "genre" };
    private static readonly string[] ExpectedAutoTagsFromConfig = { "genre", "style" };
    private static readonly string[] ExpectedEnhancementTagsFromConfig = { "genre", "style" };
    private static readonly string[] LegacyDownloadTags = { "artist" };
    private static readonly string[] LegacyAutoTags = { "label" };
    private static readonly string[] LegacyEnhancementTags = { "label" };
    private static readonly string[] ExpectedTitleOnlyDownloadTags = { "title" };
    private static readonly string[] ExpectedReleaseDateOnlyTags = { "releaseDate" };
    private static readonly string[] ExpectedReleaseDateEnhancementTags = { "releaseDate", "label" };

    [Fact]
    public void BuildConfigJson_DerivesTagArraysFromTagConfig_WhenAutoTagDataIsEmpty()
    {
        var profile = new TaggingProfile
        {
            TagConfig = CreateEmptyTagConfig(),
            AutoTag = new AutoTagSettings
            {
                Data = new Dictionary<string, JsonElement>()
            }
        };

        profile.TagConfig.Title = TagSource.DownloadSource;
        profile.TagConfig.Genre = TagSource.Both;
        profile.TagConfig.Style = TagSource.AutoTagPlatform;

        var builder = new AutoTagConfigBuilder();
        var json = builder.BuildConfigJson(profile);

        Assert.False(string.IsNullOrWhiteSpace(json));

        using var document = JsonDocument.Parse(json!);
        var root = document.RootElement;

        Assert.Equal(
            ExpectedDownloadTagsFromConfig,
            ReadStringArray(root.GetProperty("downloadTags")));
        Assert.Equal(
            ExpectedAutoTagsFromConfig,
            ReadStringArray(root.GetProperty("tags")));
        Assert.Equal(
            ExpectedEnhancementTagsFromConfig,
            ReadStringArray(root.GetProperty("gapFillTags")));
    }

    [Fact]
    public void BuildConfigJson_OverwritesLegacyTagArrays_WithTagConfigDerivedValues()
    {
        var profile = new TaggingProfile
        {
            TagConfig = CreateEmptyTagConfig(),
            AutoTag = new AutoTagSettings
            {
                Data = new Dictionary<string, JsonElement>
                {
                    ["downloadTags"] = JsonSerializer.SerializeToElement(LegacyDownloadTags),
                    ["tags"] = JsonSerializer.SerializeToElement(LegacyAutoTags),
                    ["gapFillTags"] = JsonSerializer.SerializeToElement(LegacyEnhancementTags),
                    ["downloadTagSource"] = JsonSerializer.SerializeToElement("spotify")
                }
            }
        };

        profile.TagConfig.Title = TagSource.DownloadSource;
        profile.TagConfig.ReleaseDate = TagSource.AutoTagPlatform;

        var builder = new AutoTagConfigBuilder();
        var json = builder.BuildConfigJson(profile);

        Assert.False(string.IsNullOrWhiteSpace(json));

        using var document = JsonDocument.Parse(json!);
        var root = document.RootElement;

        Assert.Equal(ExpectedTitleOnlyDownloadTags, ReadStringArray(root.GetProperty("downloadTags")));
        Assert.Equal(ExpectedReleaseDateOnlyTags, ReadStringArray(root.GetProperty("tags")));
        Assert.Equal(ExpectedReleaseDateEnhancementTags, ReadStringArray(root.GetProperty("gapFillTags")));
        Assert.Equal("spotify", root.GetProperty("downloadTagSource").GetString());
    }

    [Fact]
    public void BuildConfigJson_PreservesFollowEngineDownloadTagSource()
    {
        var profile = new TaggingProfile
        {
            TagConfig = CreateEmptyTagConfig(),
            AutoTag = new AutoTagSettings
            {
                Data = new Dictionary<string, JsonElement>
                {
                    ["downloadTagSource"] = JsonSerializer.SerializeToElement("engine")
                }
            }
        };

        profile.TagConfig.Title = TagSource.DownloadSource;

        var builder = new AutoTagConfigBuilder();
        var json = builder.BuildConfigJson(profile);

        Assert.False(string.IsNullOrWhiteSpace(json));

        using var document = JsonDocument.Parse(json!);
        var root = document.RootElement;

        Assert.Equal("engine", root.GetProperty("downloadTagSource").GetString());
    }

    [Fact]
    public void BuildConfigJson_MigratesLegacyFilenameTemplateToTracknameTemplate()
    {
        var profile = new TaggingProfile
        {
            TagConfig = CreateEmptyTagConfig(),
            AutoTag = new AutoTagSettings
            {
                Data = new Dictionary<string, JsonElement>
                {
                    ["filenameTemplate"] = JsonSerializer.SerializeToElement("%artists% - %title%"),
                    ["albumTracknameTemplate"] = JsonSerializer.SerializeToElement("%tracknumber% - %title%"),
                    ["playlistTracknameTemplate"] = JsonSerializer.SerializeToElement("%artist% - %title%")
                }
            }
        };
        profile.TagConfig.Title = TagSource.DownloadSource;

        var builder = new AutoTagConfigBuilder();
        var json = builder.BuildConfigJson(profile);

        Assert.False(string.IsNullOrWhiteSpace(json));
        using var document = JsonDocument.Parse(json!);
        var root = document.RootElement;

        Assert.Equal("%artists% - %title%", root.GetProperty("tracknameTemplate").GetString());
        Assert.False(root.TryGetProperty("filenameTemplate", out _));
        Assert.False(root.TryGetProperty("albumTracknameTemplate", out _));
        Assert.False(root.TryGetProperty("playlistTracknameTemplate", out _));
    }

    [Fact]
    public void BuildConfigJson_IncludesProfileTechnicalSettings()
    {
        var profile = new TaggingProfile
        {
            TagConfig = CreateEmptyTagConfig(),
            Technical = new TechnicalTagSettings
            {
                FeaturedToTitle = "2",
                MultiArtistSeparator = "default",
                SingleAlbumArtist = true
            },
            AutoTag = new AutoTagSettings
            {
                Data = new Dictionary<string, JsonElement>()
            }
        };

        profile.TagConfig.Title = TagSource.DownloadSource;
        profile.TagConfig.Artist = TagSource.AutoTagPlatform;

        var builder = new AutoTagConfigBuilder();
        var json = builder.BuildConfigJson(profile);

        Assert.False(string.IsNullOrWhiteSpace(json));

        using var document = JsonDocument.Parse(json!);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("technical", out var technical));
        Assert.Equal("2", technical.GetProperty("featuredToTitle").GetString());
        Assert.Equal("default", technical.GetProperty("multiArtistSeparator").GetString());
        Assert.True(technical.GetProperty("singleAlbumArtist").GetBoolean());
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
