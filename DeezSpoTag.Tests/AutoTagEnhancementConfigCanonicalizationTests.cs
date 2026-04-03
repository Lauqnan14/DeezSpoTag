using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagEnhancementConfigCanonicalizationTests
{
    private static readonly int[] DuplicateQualityFolderIds = { 9, 9, 10 };

    private static readonly MethodInfo SanitizeConfigJsonMethod =
        typeof(AutoTagService).GetMethod("SanitizeConfigJson", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("AutoTagService.SanitizeConfigJson not found.");

    [Fact]
    public void SanitizeConfigJson_MigratesLegacyFolderIdToFolderIds()
    {
        var json = """
        {
          "enhancement": {
            "folderUniformity": { "folderId": 7 },
            "coverMaintenance": { "folderId": "9" },
            "qualityChecks": { "folderId": 7, "folderIds": [7, 8, 8] }
          }
        }
        """;

        var sanitized = (string)SanitizeConfigJsonMethod.Invoke(null, [json])!;
        using var document = JsonDocument.Parse(sanitized);
        var enhancement = document.RootElement.GetProperty("enhancement");

        var uniformity = enhancement.GetProperty("folderUniformity");
        Assert.False(uniformity.TryGetProperty("folderId", out _));
        Assert.Equal(new long[] { 7 }, ReadLongArray(uniformity.GetProperty("folderIds")));

        var cover = enhancement.GetProperty("coverMaintenance");
        Assert.False(cover.TryGetProperty("folderId", out _));
        Assert.Equal(new long[] { 9 }, ReadLongArray(cover.GetProperty("folderIds")));

        var quality = enhancement.GetProperty("qualityChecks");
        Assert.False(quality.TryGetProperty("folderId", out _));
        Assert.Equal(new long[] { 7, 8 }, ReadLongArray(quality.GetProperty("folderIds")));
    }

    [Fact]
    public void SanitizeConfigJson_RemovesLegacyFolderUniformityStructureMirrorKeys()
    {
        var json = """
        {
          "enhancement": {
            "folderUniformity": {
              "folderIds": [1],
              "createArtistFolder": true,
              "artistNameTemplate": "%artist%",
              "createAlbumFolder": true,
              "albumNameTemplate": "%album%",
              "illegalCharacterReplacer": "_",
              "multiArtistSeparator": "default",
              "usePrimaryArtistFolders": true
            }
          }
        }
        """;

        var sanitized = (string)SanitizeConfigJsonMethod.Invoke(null, [json])!;
        using var document = JsonDocument.Parse(sanitized);
        var folderUniformity = document.RootElement
            .GetProperty("enhancement")
            .GetProperty("folderUniformity");

        Assert.False(folderUniformity.TryGetProperty("createArtistFolder", out _));
        Assert.False(folderUniformity.TryGetProperty("artistNameTemplate", out _));
        Assert.False(folderUniformity.TryGetProperty("createAlbumFolder", out _));
        Assert.False(folderUniformity.TryGetProperty("albumNameTemplate", out _));
        Assert.False(folderUniformity.TryGetProperty("illegalCharacterReplacer", out _));
        Assert.False(folderUniformity.TryGetProperty("multiArtistSeparator", out _));
        Assert.False(folderUniformity.TryGetProperty("usePrimaryArtistFolders", out _));
        Assert.Equal(new long[] { 1 }, ReadLongArray(folderUniformity.GetProperty("folderIds")));
    }

    [Fact]
    public void TaggingProfileDataHelper_CanonicalizeEnhancementConfig_MigratesAndPurgesLegacyKeys()
    {
        var helperType = typeof(TaggingProfileService).Assembly.GetType("DeezSpoTag.Web.Services.TaggingProfileDataHelper")
                         ?? throw new InvalidOperationException("TaggingProfileDataHelper type not found.");
        var method = helperType.GetMethod("CanonicalizeEnhancementConfig", BindingFlags.Public | BindingFlags.Static)
                     ?? throw new InvalidOperationException("CanonicalizeEnhancementConfig method not found.");

        var data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["enhancement"] = JsonSerializer.SerializeToElement(new
            {
                folderUniformity = new
                {
                    folderId = 5,
                    createArtistFolder = true,
                    artistNameTemplate = "%artist%"
                },
                coverMaintenance = new
                {
                    folderId = "6"
                },
                qualityChecks = new
                {
                    folderIds = DuplicateQualityFolderIds,
                    folderId = 11
                }
            })
        };

        var changed = (bool)method.Invoke(null, [data])!;
        Assert.True(changed);

        using var document = JsonDocument.Parse(data["enhancement"].GetRawText());
        var enhancement = document.RootElement;

        var uniformity = enhancement.GetProperty("folderUniformity");
        Assert.False(uniformity.TryGetProperty("folderId", out _));
        Assert.False(uniformity.TryGetProperty("createArtistFolder", out _));
        Assert.False(uniformity.TryGetProperty("artistNameTemplate", out _));
        Assert.Equal(new long[] { 5 }, ReadLongArray(uniformity.GetProperty("folderIds")));

        var cover = enhancement.GetProperty("coverMaintenance");
        Assert.False(cover.TryGetProperty("folderId", out _));
        Assert.Equal(new long[] { 6 }, ReadLongArray(cover.GetProperty("folderIds")));

        var quality = enhancement.GetProperty("qualityChecks");
        Assert.False(quality.TryGetProperty("folderId", out _));
        Assert.Equal(new long[] { 9, 10 }, ReadLongArray(quality.GetProperty("folderIds")));
    }

    [Fact]
    public void EnhancementContracts_UseFolderIdsOnly()
    {
        Assert.Null(typeof(EnhancementFolderUniformityRequest).GetProperty("FolderId"));
        Assert.Null(typeof(EnhancementQualityChecksRequest).GetProperty("FolderId"));

        var endpoint = typeof(AutoTagEnhancementController).GetMethod(nameof(AutoTagEnhancementController.GetEnhancementTechnicalProfiles));
        Assert.NotNull(endpoint);
        Assert.DoesNotContain(endpoint!.GetParameters(), parameter =>
            string.Equals(parameter.Name, "folderId", StringComparison.OrdinalIgnoreCase));
    }

    private static long[] ReadLongArray(JsonElement element)
    {
        return element
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out _))
            .Select(static item => item.GetInt64())
            .ToArray();
    }
}
