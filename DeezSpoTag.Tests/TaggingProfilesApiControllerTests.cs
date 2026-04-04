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
                    ["downloadTags"] = JsonSerializer.SerializeToElement(new[] { "title", "artist" }),
                    ["tags"] = JsonSerializer.SerializeToElement(new[] { "genre" })
                }
            },
            Technical: null,
            FolderStructure: null,
            Verification: null);

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
