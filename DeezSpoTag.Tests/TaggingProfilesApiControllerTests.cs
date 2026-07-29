using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TaggingProfilesApiControllerTests
{
    private static readonly string[] DownloadTagDefaults = ["title", "artist"];
    private static readonly string[] EnrichmentTagDefaults = ["genre"];

    [Fact]
    public void TryBuildTagConfig_PrefersAutoTagArrays_EvenWhenTagConfigPayloadExists()
    {
        var request = new TaggingProfilesApiController.TaggingProfileUpsertRequest(
            Id: "p1",
            Name: "Main",
            IsDefault: false,
            TagConfig: BuildTagConfigJsonElement(
                ("title", (int)TagSource.None),
                ("genre", (int)TagSource.None)),
            AutoTag: new AutoTagSettings
            {
                Data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["downloadTags"] = JsonSerializer.SerializeToElement(DownloadTagDefaults),
                    ["tags"] = JsonSerializer.SerializeToElement(EnrichmentTagDefaults)
                }
            },
            Technical: null,
            FolderStructure: null,
            Verification: null,
            ApplyToRuntime: null);

        var method = typeof(TaggingProfilesApiController)
            .GetMethod("TryBuildTagConfig", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var args = new object?[] { request, null, null };
        var config = method!.Invoke(null, args) as UnifiedTagConfig;

        Assert.NotNull(config);
        Assert.Equal(TagSource.DownloadSource, config!.Title);
        Assert.Equal(TagSource.DownloadSource, config.Artist);
        Assert.Equal(TagSource.AutoTagPlatform, config.Genre);
    }

    [Fact]
    public void EnrichmentSelections_SurviveControllerMappingAndCanonicalRegeneration()
    {
        var expected = new[] { "activity", "language", "lyricist", "publisher", "description" };
        var request = new TaggingProfilesApiController.TaggingProfileUpsertRequest(
            Id: "p2",
            Name: "Persistence",
            IsDefault: false,
            TagConfig: null,
            AutoTag: new AutoTagSettings
            {
                Data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["downloadTags"] = JsonSerializer.SerializeToElement(Array.Empty<string>()),
                    ["tags"] = JsonSerializer.SerializeToElement(expected)
                }
            },
            Technical: null,
            FolderStructure: null,
            Verification: null,
            ApplyToRuntime: null);

        var method = typeof(TaggingProfilesApiController)
            .GetMethod("TryBuildTagConfig", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var config = Assert.IsType<UnifiedTagConfig>(
            method!.Invoke(null, new object?[] { request, null, null }));
        Assert.Equal(TagSource.AutoTagPlatform, config.Activity);
        Assert.Equal(TagSource.AutoTagPlatform, config.Language);
        Assert.Equal(TagSource.AutoTagPlatform, config.Lyricist);
        Assert.Equal(TagSource.AutoTagPlatform, config.Publisher);
        Assert.Equal(TagSource.AutoTagPlatform, config.Description);

        var autoTag = Assert.IsType<AutoTagSettings>(request.AutoTag);
        var profile = new TaggingProfile
        {
            TagConfig = config,
            AutoTag = autoTag
        };
        TaggingProfileCanonicalizer.SyncTagArraysFromConfig(profile);

        var persisted = autoTag.Data["tags"]
            .EnumerateArray()
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToArray();
        Assert.All(expected, tag => Assert.Contains(tag, persisted, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void EveryCanonicalTag_SurvivesControllerSaveAndRegeneration()
    {
        var sourceConfig = new UnifiedTagConfig();
        var tagProperties = typeof(UnifiedTagConfig)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.PropertyType == typeof(TagSource) && property.CanRead && property.CanWrite)
            .ToArray();
        foreach (var property in tagProperties)
        {
            property.SetValue(sourceConfig, TagSource.AutoTagPlatform);
        }

        var sourceProfile = new TaggingProfile
        {
            TagConfig = sourceConfig,
            AutoTag = new AutoTagSettings
            {
                Data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            }
        };
        TaggingProfileCanonicalizer.SyncTagArraysFromConfig(sourceProfile);
        sourceProfile.AutoTag.Data["downloadTags"] = JsonSerializer.SerializeToElement(Array.Empty<string>());

        var request = new TaggingProfilesApiController.TaggingProfileUpsertRequest(
            Id: "all-tags",
            Name: "All tags",
            IsDefault: false,
            TagConfig: null,
            AutoTag: sourceProfile.AutoTag,
            Technical: null,
            FolderStructure: null,
            Verification: null,
            ApplyToRuntime: null);
        var method = typeof(TaggingProfilesApiController)
            .GetMethod("TryBuildTagConfig", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("TryBuildTagConfig was not found.");
        var rebuilt = Assert.IsType<UnifiedTagConfig>(
            method.Invoke(null, new object?[] { request, null, null }));

        foreach (var property in tagProperties)
        {
            Assert.Equal(
                TagSource.AutoTagPlatform,
                Assert.IsType<TagSource>(property.GetValue(rebuilt)));
        }
    }

    private static JsonElement BuildTagConfigJsonElement(params (string Key, int Value)[] values)
    {
        var payload = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            payload[key] = value;
        }

        return JsonSerializer.SerializeToElement(payload);
    }
}
