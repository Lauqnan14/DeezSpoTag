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
            new[] { "title", "genre" },
            ReadStringArray(root.GetProperty("downloadTags")));
        Assert.Equal(
            new[] { "genre", "style" },
            ReadStringArray(root.GetProperty("tags")));
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
                    ["downloadTags"] = JsonSerializer.SerializeToElement(new[] { "artist" }),
                    ["tags"] = JsonSerializer.SerializeToElement(new[] { "label" }),
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

        Assert.Equal(new[] { "title" }, ReadStringArray(root.GetProperty("downloadTags")));
        Assert.Equal(new[] { "releaseDate" }, ReadStringArray(root.GetProperty("tags")));
        Assert.Equal("spotify", root.GetProperty("downloadTagSource").GetString());
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
