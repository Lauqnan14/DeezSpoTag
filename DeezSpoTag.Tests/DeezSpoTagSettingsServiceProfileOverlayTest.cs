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
public sealed class DeezSpoTagSettingsServiceProfileOverlayTest : IDisposable
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

    public DeezSpoTagSettingsServiceProfileOverlayTest()
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

    [Theory]
    [InlineData("{}", 10, true, 10, 10)]
    [InlineData("{\"lrclib\":{\"duration_tolerance_seconds\":3,\"search_fallback\":false},\"musixmatch\":{\"search_page_size\":25,\"richsync_max_deviation_seconds\":6}}", 3, false, 25, 6)]
    [InlineData("{\"lrclib\":{\"duration_tolerance_seconds\":9999},\"musixmatch\":{\"search_page_size\":0,\"richsync_max_deviation_seconds\":-1}}", 60, true, 1, 0)]
    [InlineData("{\"lrclib\":{\"duration_tolerance_seconds\":\"bad\",\"search_fallback\":{}},\"musixmatch\":{\"search_page_size\":[],\"richsync_max_deviation_seconds\":null}}", 10, true, 10, 10)]
    public void LyricsProfileOptions_ReachSharedRuntimeAndCloneIndependently(string json, int tolerance, bool search, int pageSize, int deviation)
    {
        var profile = BuildDefaultProfile();
        profile.AutoTag.Data["custom"] = JsonDocument.Parse(json).RootElement.Clone();
        var settings = new DeezSpoTagSettings();
        TaggingProfileSettingsOverlay.ApplyProfileToSettings(settings, profile);
        Assert.Equal(tolerance, settings.Lrclib.DurationToleranceSeconds);
        Assert.Equal(search, settings.Lrclib.SearchFallback);
        Assert.Equal(pageSize, settings.Musixmatch.SearchPageSize);
        Assert.Equal(deviation, settings.Musixmatch.RichsyncMaxDeviationSeconds);
        Assert.True(settings.Lrclib.UseDurationHint);
        Assert.True(settings.Lrclib.PreferSynced);
        Assert.Equal(10, settings.Musixmatch.DurationToleranceSeconds);
        Assert.Equal(10, settings.Musixmatch.SubtitleMaxDeviationSeconds);
        var type = typeof(TaggingProfileSettingsOverlay).Assembly.GetType("DeezSpoTag.Services.Download.Shared.LyricsResolveSettingsBuilder")!;
        var copy = (DeezSpoTagSettings)type.GetMethod("Build")!.Invoke(null, new object[] { settings, new TagSettings() })!;
        Assert.Equal(tolerance, copy.Lrclib.DurationToleranceSeconds);
        Assert.Equal(search, copy.Lrclib.SearchFallback);
        Assert.Equal(pageSize, copy.Musixmatch.SearchPageSize);
        Assert.Equal(deviation, copy.Musixmatch.RichsyncMaxDeviationSeconds);
        copy.Lrclib.DurationToleranceSeconds = 42;
        copy.Musixmatch.SearchPageSize = 42;
        Assert.Equal(tolerance, settings.Lrclib.DurationToleranceSeconds);
        Assert.Equal(pageSize, settings.Musixmatch.SearchPageSize);
    }

    [Fact]
    public void LyricsProfile_AllEightValuesReachDownloadConsumer()
    {
        var profile = BuildDefaultProfile();
        profile.AutoTag.Data["custom"] = JsonDocument.Parse("""{"lrclib":{"duration_tolerance_seconds":4,"use_duration_hint":false,"search_fallback":false,"prefer_synced":false},"musixmatch":{"duration_tolerance_seconds":5,"search_page_size":26,"richsync_max_deviation_seconds":7,"subtitle_max_deviation_seconds":8}}""").RootElement.Clone();
        var settings = new DeezSpoTagSettings();
        TaggingProfileSettingsOverlay.ApplyProfileToSettings(settings, profile);
        var type = typeof(TaggingProfileSettingsOverlay).Assembly.GetType("DeezSpoTag.Services.Download.Shared.LyricsResolveSettingsBuilder")!;
        var copy = (DeezSpoTagSettings)type.GetMethod("Build")!.Invoke(null, new object[] { settings, new TagSettings() })!;
        Assert.Equal(4, copy.Lrclib.DurationToleranceSeconds);
        Assert.False(copy.Lrclib.UseDurationHint);
        Assert.False(copy.Lrclib.SearchFallback);
        Assert.False(copy.Lrclib.PreferSynced);
        Assert.Equal(5, copy.Musixmatch.DurationToleranceSeconds);
        Assert.Equal(26, copy.Musixmatch.SearchPageSize);
        Assert.Equal(7, copy.Musixmatch.RichsyncMaxDeviationSeconds);
        Assert.Equal(8, copy.Musixmatch.SubtitleMaxDeviationSeconds);
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"musixmatch\":{\"require_lyrics\":true}}", true)]
    [InlineData("{\"musixmatch\":{\"require_lyrics\":false}}", false)]
    [InlineData("{\"musixmatch\":{\"require_lyrics\":\"false\"}}", false)]
    [InlineData("{\"musixmatch\":{\"require_lyrics\":\"bad\"}}", true)]
    [InlineData("{\"musixmatch\":{\"require_lyrics\":{}}}", true)]
    [InlineData("{\"musixmatch\":null}", true)]
    public void MusixmatchRequireLyrics_MapsThroughOverlayAndRetainsProfileDefault(string customJson, bool expected)
    {
        var profile = BuildDefaultProfile();
        profile.AutoTag.Data["custom"] = JsonDocument.Parse(customJson).RootElement.Clone();
        var settings = new DeezSpoTagSettings();

        TaggingProfileSettingsOverlay.ApplyProfileToSettings(settings, profile);

        Assert.Equal(expected, settings.Musixmatch.RequireLyrics);
        var type = typeof(TaggingProfileSettingsOverlay).Assembly.GetType("DeezSpoTag.Services.Download.Shared.LyricsResolveSettingsBuilder")!;
        var copy = (DeezSpoTagSettings)type.GetMethod("Build")!.Invoke(null, new object[] { settings, new TagSettings() })!;
        Assert.Equal(expected, copy.Musixmatch.RequireLyrics);
        copy.Musixmatch.RequireLyrics = !expected;
        Assert.Equal(expected, settings.Musixmatch.RequireLyrics);
    }

    [Fact]
    public void MusixmatchOptions_ClonePreservesRequireLyricsIndependently()
    {
        var options = new MusixmatchOptions { RequireLyrics = false };

        var copy = options.Clone();

        Assert.False(copy.RequireLyrics);
        copy.RequireLyrics = true;
        Assert.False(options.RequireLyrics);
        Assert.True(new MusixmatchOptions().RequireLyrics);
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
