using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TaggingProfileLyricsMigrationTests
{
    [Fact]
    public async Task UpsertAndReload_PreservesAllEnrichmentSelections()
    {
        using var environment = new TemporaryEnvironment();
        var service = new TaggingProfileService(
            environment,
            NullLogger<TaggingProfileService>.Instance);
        var tagConfig = new UnifiedTagConfig();

        foreach (var property in typeof(UnifiedTagConfig).GetProperties()
                     .Where(property => property.PropertyType == typeof(TagSource) && property.CanWrite))
        {
            property.SetValue(tagConfig, TagSource.None);
        }

        tagConfig.Activity = TagSource.AutoTagPlatform;
        tagConfig.Language = TagSource.AutoTagPlatform;
        tagConfig.Lyricist = TagSource.AutoTagPlatform;
        tagConfig.Publisher = TagSource.AutoTagPlatform;
        tagConfig.Description = TagSource.AutoTagPlatform;

        var profile = new TaggingProfile
        {
            Name = "Enrichment persistence",
            TagConfig = tagConfig,
            AutoTag = new AutoTagSettings
            {
                Data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    """
                    {
                      "downloadTags": [],
                      "tags": ["Activity", "Language", "Lyricist", "Publisher", "Description"],
                      "gapFillTags": []
                    }
                    """)!
            }
        };

        await service.UpsertAsync(profile);

        var reloadedService = new TaggingProfileService(
            environment,
            NullLogger<TaggingProfileService>.Instance);
        var reloadedProfile = Assert.Single(await reloadedService.LoadAsync());
        var persistedTags = reloadedProfile.AutoTag.Data["tags"]
            .EnumerateArray()
            .Select(value => value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();

        foreach (var tag in new[] { "Activity", "Language", "Lyricist", "Publisher", "Description" })
        {
            Assert.Contains(tag, persistedTags, StringComparer.OrdinalIgnoreCase);
        }

        Assert.Equal(TagSource.AutoTagPlatform, reloadedProfile.TagConfig.Activity);
        Assert.Equal(TagSource.AutoTagPlatform, reloadedProfile.TagConfig.Language);
        Assert.Equal(TagSource.AutoTagPlatform, reloadedProfile.TagConfig.Lyricist);
        Assert.Equal(TagSource.AutoTagPlatform, reloadedProfile.TagConfig.Publisher);
        Assert.Equal(TagSource.AutoTagPlatform, reloadedProfile.TagConfig.Description);
    }

    [Fact]
    public async Task LoadAsync_MigratesLyricsConfigurationOnce_WithoutResettingPreferences()
    {
        using var environment = new TemporaryEnvironment();
        var dataDirectory = Path.Join(environment.ContentRootPath, "Data", "autotag");
        Directory.CreateDirectory(dataDirectory);
        var profilePath = Path.Join(dataDirectory, "tagging-profiles.json");
        await File.WriteAllTextAsync(profilePath,
            """
            [
              {
                "id": "default",
                "name": "Default",
                "isDefault": true,
                "tagConfig": {},
                "autoTag": {
                  "writeLrc": true,
                  "unrelatedPreference": true
                },
                "technical": {
                  "saveLyrics": false,
                  "syncedLyrics": true,
                  "lrcType": "lyrics,syllable-lyrics,unsynced-lyrics",
                  "lrcFormat": "both",
                  "lyricsFallbackOrder": "musixmatch,apple,lrclib"
                },
                "folderStructure": {},
                "verification": {}
              }
            ]
            """);
        var service = new TaggingProfileService(
            environment,
            NullLogger<TaggingProfileService>.Instance);

        var firstLoad = Assert.Single(await service.LoadAsync());
        Assert.False(firstLoad.Technical.SaveLyrics);
        Assert.True(firstLoad.Technical.SyncedLyrics);
        Assert.Equal("lrc,ttml", firstLoad.Technical.LrcFormat);
        Assert.Contains("ttml-lyrics", firstLoad.Technical.LrcType, StringComparison.Ordinal);
        Assert.Equal(
            "musixmatch,apple,lrclib,deezer,spotify,youlyplus,betterlyrics",
            firstLoad.Technical.LyricsFallbackOrder);
        Assert.Equal(
            TechnicalTagSettings.CurrentLyricsSchemaVersion,
            firstLoad.Technical.LyricsSchemaVersion);
        Assert.Equal(LrcTimingModes.PreferEnhanced, firstLoad.Technical.LrcTimingPreference);
        Assert.True(firstLoad.Technical.PreferEnhancedLrc);
        Assert.False(firstLoad.AutoTag.Data.ContainsKey("writeLrc"));
        Assert.True(firstLoad.AutoTag.Data["unrelatedPreference"].GetBoolean());

        var persistedAfterFirstLoad = await File.ReadAllTextAsync(profilePath);
        var secondLoad = Assert.Single(await service.LoadAsync());
        var persistedAfterSecondLoad = await File.ReadAllTextAsync(profilePath);

        Assert.Equal(persistedAfterFirstLoad, persistedAfterSecondLoad);
        Assert.Equal(firstLoad.Technical.LyricsFallbackOrder, secondLoad.Technical.LyricsFallbackOrder);
    }

    private sealed class TemporaryEnvironment : IWebHostEnvironment, IAppDataRootOverride, IDisposable
    {
        public TemporaryEnvironment()
        {
            ContentRootPath = Path.Join(Path.GetTempPath(), $"deezspotag-lyrics-{Guid.NewGuid():N}");
            Directory.CreateDirectory(ContentRootPath);
            WebRootPath = Path.Join(ContentRootPath, "wwwroot");
            Directory.CreateDirectory(WebRootPath);
            ContentRootFileProvider = new PhysicalFileProvider(ContentRootPath);
            WebRootFileProvider = new PhysicalFileProvider(WebRootPath);
        }

        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public IFileProvider WebRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
        public string AppDataRoot => Path.Join(ContentRootPath, "Data");

        public void Dispose()
        {
            (ContentRootFileProvider as IDisposable)?.Dispose();
            (WebRootFileProvider as IDisposable)?.Dispose();
            Directory.Delete(ContentRootPath, recursive: true);
        }
    }
}
