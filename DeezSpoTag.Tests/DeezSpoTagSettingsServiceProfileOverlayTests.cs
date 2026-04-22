using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class DeezSpoTagSettingsServiceProfileOverlayTests : IDisposable
{
    private static readonly JsonSerializerOptions ProfileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _tempRoot;
    private readonly TestConfigRootScope _configScope;
    private readonly DeezSpoTagSettingsService _settingsService;

    public DeezSpoTagSettingsServiceProfileOverlayTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-settings-profile-overlay-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _configScope = new TestConfigRootScope(_tempRoot);
        _settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
    }

    [Fact]
    public void LoadSettings_AppliesDefaultProfileValues_FromTaggingProfilesStore()
    {
        WriteProfilesFile("tagging-profiles.json", BuildDefaultProfile());

        var settings = _settingsService.LoadSettings();

        Assert.Equal("Y-D-M", settings.DateFormat);
        Assert.Equal("upper", settings.TitleCasing);
        Assert.True(settings.CreateArtistFolder);
        Assert.Equal("%albumartist%", settings.ArtistNameTemplate);
        Assert.True(settings.Tags.Lyrics);
        Assert.True(settings.Tags.SyncedLyrics);
    }

    [Fact]
    public void SaveSettings_PreservesDefaultProfileAsSourceOfTruth_ForOverlayedFields()
    {
        WriteProfilesFile("tagging-profiles.json", BuildDefaultProfile());

        var settings = _settingsService.LoadSettings();
        settings.DateFormat = "Y-M-D";
        settings.TitleCasing = "nothing";
        settings.CreateArtistFolder = false;
        settings.Tags.Lyrics = false;

        _settingsService.SaveSettings(settings);

        var reloaded = _settingsService.LoadSettings();
        Assert.Equal("Y-D-M", reloaded.DateFormat);
        Assert.Equal("upper", reloaded.TitleCasing);
        Assert.True(reloaded.CreateArtistFolder);
        Assert.True(reloaded.Tags.Lyrics);
    }

    [Fact]
    public void LoadSettings_FallsBackToLegacyProfilesFile_WhenPrimaryFileMissing()
    {
        WriteProfilesFile("profiles.json", BuildDefaultProfile());

        var settings = _settingsService.LoadSettings();

        Assert.Equal("Y-D-M", settings.DateFormat);
        Assert.True(settings.CreateArtistFolder);
        Assert.True(settings.Tags.SyncedLyrics);
    }

    private void WriteProfilesFile(string fileName, params TaggingProfile[] profiles)
    {
        var autoTagDir = Path.Join(_tempRoot, "autotag");
        Directory.CreateDirectory(autoTagDir);
        var path = Path.Join(autoTagDir, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(profiles, ProfileJsonOptions));
    }

    private static TaggingProfile BuildDefaultProfile()
    {
        return new TaggingProfile
        {
            Id = "default-profile",
            Name = "Default",
            IsDefault = true,
            TagConfig = new UnifiedTagConfig
            {
                UnsyncedLyrics = TagSource.DownloadSource,
                SyncedLyrics = TagSource.DownloadSource
            },
            Technical = new TechnicalTagSettings
            {
                DateFormat = "Y-D-M",
                TitleCasing = "upper",
                EmbedLyrics = true,
                SaveLyrics = true,
                SyncedLyrics = true
            },
            FolderStructure = new FolderStructureSettings
            {
                CreateArtistFolder = true,
                ArtistNameTemplate = "%albumartist%"
            },
            AutoTag = new AutoTagSettings
            {
                Data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            }
        };
    }

    public void Dispose()
    {
        _configScope.Dispose();
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
