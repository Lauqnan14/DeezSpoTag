using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Controllers.Api;
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
