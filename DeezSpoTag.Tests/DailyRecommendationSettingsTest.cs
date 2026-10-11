using System;
using System.IO;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class DailyRecommendationSettingsTest
{
    [Fact]
    public void SettingsSurfaceFourSourcesAndExistingThreeSections()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Join(root.FullName, "DeezSpoTag.Web", "Views"))) root = root.Parent;
        Assert.NotNull(root);
        var view = File.ReadAllText(Path.Join(root!.FullName, "DeezSpoTag.Web", "Views", "Settings", "Index.cshtml"));
        Assert.Contains("id=\"recommendations-settings\"", view);
        Assert.Contains("Daily Recommendations", view);
        Assert.Contains("Missing Top Songs", view);
        Assert.Contains("Discover Similar Artists", view);
        foreach (var source in new[] { "Shazam", "Deezer", "Spotify", "Tidal" })
        {
            Assert.Contains($"id=\"daily{source}Enabled\"", view);
            Assert.Contains($"id=\"daily{source}TrackCount\"", view);
        }
        Assert.Contains("dailyRecommendations: collectDailyRecommendations()", view);
        Assert.Contains("updateDailyRecommendationAllocation()", view);
    }

    [Fact]
    public void SettingsKeepShazamVisibleButUnavailableAndShowSixtyTrackDefaults()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Join(root.FullName, "DeezSpoTag.Web", "Views"))) root = root.Parent;
        Assert.NotNull(root);
        var view = File.ReadAllText(Path.Join(root!.FullName, "DeezSpoTag.Web", "Views", "Settings", "Index.cshtml"));
        Assert.Contains("id=\"dailyShazamEnabled\" disabled", view);
        Assert.Contains("id=\"dailyShazamTrackCount\" min=\"0\" max=\"100\" step=\"1\" value=\"0\" disabled", view);
        Assert.Contains("Unavailable — Shazam's similar-song service is not functional.", view);
        Assert.Contains("id=\"dailyRecommendationTrackCount\" min=\"20\" max=\"100\" step=\"1\" value=\"60\"", view);
        foreach (var source in new[] { "Deezer", "Spotify", "Tidal" })
        {
            Assert.Contains($"id=\"daily{source}Enabled\" checked", view);
            Assert.Contains($"id=\"daily{source}TrackCount\" min=\"0\" max=\"100\" step=\"1\" value=\"20\"", view);
        }
        Assert.Contains("setCheckboxValue('dailyShazamEnabled', false)", view);
        Assert.Contains("setFieldValue('dailyShazamTrackCount', 0)", view);
        Assert.Contains("daily.shazamEnabled = false", view);
        Assert.Contains("daily.shazamTrackCount = 0", view);
    }
    [Fact]
    public void LegacySettingsAndDefaultsUseSixtyTracksAcrossAvailableSources()
    {
        foreach (var settings in new[] { new DeezSpoTagSettings(), JsonSerializer.Deserialize<DeezSpoTagSettings>("{}")!, DeezSpoTagSettingsService.GetStaticDefaultSettings() })
        {
            var daily = settings.DailyRecommendations;
            Assert.Equal(60, daily.TrackCount);
            Assert.False(daily.ShazamEnabled);
            Assert.True(daily.DeezerEnabled);
            Assert.Equal(0, daily.ShazamTrackCount);
            Assert.Equal(20, daily.DeezerTrackCount);
            Assert.True(daily.SpotifyEnabled);
            Assert.Equal(20, daily.SpotifyTrackCount);
            Assert.True(daily.TidalEnabled);
            Assert.Equal(20, daily.TidalTrackCount);
            Assert.True(daily.TryValidate(out _));
        }
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(100, true)]
    [InlineData(19, false)]
    [InlineData(101, false)]
    public void TotalMustBeWithinBounds(int total, bool valid)
    {
        var daily = new DailyRecommendationSettings { TrackCount = total, ShazamEnabled = false, ShazamTrackCount = 0,
            DeezerTrackCount = total, SpotifyEnabled = false, SpotifyTrackCount = 0, TidalEnabled = false, TidalTrackCount = 0 };
        Assert.Equal(valid, daily.TryValidate(out _));
    }

    [Fact]
    public void InvalidAllocationsAreRejectedWithoutRedistribution()
    {
        var daily = new DailyRecommendationSettings();
        daily.DeezerTrackCount = -1;
        Assert.False(daily.TryValidate(out _));
        daily.DeezerTrackCount = 0;
        Assert.False(daily.TryValidate(out _));
        daily.DeezerEnabled = false;
        daily.DeezerTrackCount = 20;
        Assert.False(daily.TryValidate(out _));
        daily.DeezerTrackCount = 0;
        Assert.False(daily.TryValidate(out _));
        daily.SpotifyEnabled = daily.TidalEnabled = false;
        daily.SpotifyTrackCount = daily.TidalTrackCount = 0;
        Assert.True(daily.TryValidate(out _));
    }

    [Fact]
    public void AiModeDefaultsAndValidationPreserveUnknownCompatibility()
    {
        var settings = JsonSerializer.Deserialize<DeezSpoTagSettings>("{}")!;
        Assert.Equal(DailyRecommendationAiFilterMode.BlockAiArtistsAndFullyGenerated, settings.DailyRecommendations.AiFilterMode);
        foreach (var mode in Enum.GetValues<DailyRecommendationAiFilterMode>())
        {
            settings.DailyRecommendations.AiFilterMode = mode;
            Assert.True(settings.DailyRecommendations.TryValidate(out _));
        }
        settings.DailyRecommendations.AiFilterMode = (DailyRecommendationAiFilterMode)999;
        Assert.False(settings.DailyRecommendations.TryValidate(out _));
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 20)]
    [InlineData(false, 25)]
    [InlineData(false, -1)]
    public void UnavailableShazamCannotBeEnabledOrAllocated(bool enabled, int count)
    {
        var daily = new DailyRecommendationSettings { TrackCount = 60 + Math.Max(0, count),
            ShazamEnabled = enabled, ShazamTrackCount = count, DeezerEnabled = true, DeezerTrackCount = 20,
            SpotifyEnabled = true, SpotifyTrackCount = 20, TidalEnabled = true, TidalTrackCount = 20 };
        Assert.False(daily.TryValidate(out var error));
        Assert.Contains("Shazam", error!);
    }

    [Theory]
    [InlineData(true, 25, 25, 0, 0)]
    [InlineData(true, 10, 30, 20, 20)]
    [InlineData(false, 10, 30, 20, 20)]
    public void SavedShazamAllocationsMigrateAndPersistWithoutChangingAiMode(
        bool shazam, int shazamCount, int deezer, int spotify, int tidal)
    {
        WithSettingsFile((service, path) =>
        {
            var settings = service.LoadSettings();
            settings.DailyRecommendations = new()
            {
                TrackCount = shazamCount + deezer + spotify + tidal,
                ShazamEnabled = shazam, ShazamTrackCount = shazamCount,
                DeezerEnabled = deezer > 0, DeezerTrackCount = deezer,
                SpotifyEnabled = spotify > 0, SpotifyTrackCount = spotify,
                TidalEnabled = tidal > 0, TidalTrackCount = tidal,
                AiFilterMode = DailyRecommendationAiFilterMode.Strict
            };
            File.WriteAllText(path, JsonSerializer.Serialize(settings));
            var migrated = service.LoadSettings().DailyRecommendations;
            Assert.Equal(60, migrated.TrackCount);
            Assert.False(migrated.ShazamEnabled);
            Assert.Equal(0, migrated.ShazamTrackCount);
            Assert.True(migrated.DeezerEnabled && migrated.SpotifyEnabled && migrated.TidalEnabled);
            Assert.Equal(20, migrated.DeezerTrackCount);
            Assert.Equal(20, migrated.SpotifyTrackCount);
            Assert.Equal(20, migrated.TidalTrackCount);
            Assert.Equal(DailyRecommendationAiFilterMode.Strict, migrated.AiFilterMode);
            Assert.True(migrated.TryValidate(out _));
            using var persisted = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(60, persisted.RootElement.GetProperty("dailyRecommendations").GetProperty("trackCount").GetInt32());
            Assert.False(persisted.RootElement.GetProperty("dailyRecommendations").GetProperty("shazamEnabled").GetBoolean());
            Assert.Equal(JsonSerializer.Serialize(migrated), JsonSerializer.Serialize(service.LoadSettings().DailyRecommendations));
        });
    }

    [Theory]
    [InlineData(40, 15, 25, 0)]
    [InlineData(60, 0, 0, 0)]
    public void SavedNonShazamCustomAndAllDisabledAllocationsArePreserved(int total, int deezer, int spotify, int tidal)
    {
        WithSettingsFile((service, path) =>
        {
            var settings = service.LoadSettings();
            settings.DailyRecommendations = new()
            {
                TrackCount = total, ShazamEnabled = false, ShazamTrackCount = 0,
                DeezerEnabled = deezer > 0, DeezerTrackCount = deezer,
                SpotifyEnabled = spotify > 0, SpotifyTrackCount = spotify,
                TidalEnabled = tidal > 0, TidalTrackCount = tidal,
                AiFilterMode = DailyRecommendationAiFilterMode.Allow
            };
            var expected = JsonSerializer.Serialize(settings.DailyRecommendations);
            File.WriteAllText(path, JsonSerializer.Serialize(settings));
            var loaded = service.LoadSettings().DailyRecommendations;
            Assert.Equal(expected, JsonSerializer.Serialize(loaded));
            Assert.True(loaded.TryValidate(out _));
        });
    }

    [Fact]
    public void MissingDailySettingsLoadAvailableDefaults()
    {
        WithSettingsFile((service, path) =>
        {
            File.WriteAllText(path, "{\"dailyRecommendations\":null}");
            var daily = service.LoadSettings().DailyRecommendations;
            Assert.NotNull(daily);
            Assert.Equal(60, daily.TrackCount);
            Assert.True(daily.TryValidate(out _));
        });
    }

    private static void WithSettingsFile(Action<DeezSpoTagSettingsService, string> assertion)
    {
        var root = Path.Join(Path.GetTempPath(), "daily-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var scope = new TestConfigRootScope(root);
            var service = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
            service.LoadSettings();
            var path = Assert.Single(Directory.GetFiles(root, "config.json", SearchOption.AllDirectories));
            assertion(service, path);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(DailyRecommendationAiFilterMode.Allow, RecommendationAiStatus.FullyAiTrack, false)]
    [InlineData(DailyRecommendationAiFilterMode.BlockAiArtistsAndFullyGenerated, RecommendationAiStatus.AiArtist, true)]
    [InlineData(DailyRecommendationAiFilterMode.BlockAiArtistsAndFullyGenerated, RecommendationAiStatus.FullyAiTrack, true)]
    [InlineData(DailyRecommendationAiFilterMode.BlockAiArtistsAndFullyGenerated, RecommendationAiStatus.PartialAiInvolvement, false)]
    [InlineData(DailyRecommendationAiFilterMode.Strict, RecommendationAiStatus.PartialAiInvolvement, true)]
    [InlineData(DailyRecommendationAiFilterMode.Strict, RecommendationAiStatus.Unknown, false)]
    public void AiPolicyUsesOnlyVerifiedDisclosure(DailyRecommendationAiFilterMode mode, RecommendationAiStatus status, bool rejected)
    {
        var evidence = new RecommendationAiEvidenceDto(status, "provider", "explicit_disclosure", "verified_provider_disclosure", DateTimeOffset.UtcNow);
        Assert.Equal(rejected, LibraryRecommendationService.EvaluateDailyAiPolicy(new[] { evidence }, mode) is not null);
        Assert.Null(LibraryRecommendationService.EvaluateDailyAiPolicy(new[] { evidence with { Authority = "unsupported" } }, mode));
        Assert.Null(LibraryRecommendationService.EvaluateDailyAiPolicy(null, mode));
    }

    [Fact]
    public void SaveLoadAndResetPreserveDefaultsAndRejectInvalidConfiguration()
    {
        var root = Path.Join(Path.GetTempPath(), "daily-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var scope = new TestConfigRootScope(root);
            var service = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
            var settings = service.LoadSettings();
            settings.DailyRecommendations = new DailyRecommendationSettings { TrackCount = 100, ShazamEnabled = false,
                ShazamTrackCount = 0, DeezerTrackCount = 40, SpotifyEnabled = true, SpotifyTrackCount = 30, TidalEnabled = true, TidalTrackCount = 30 };
            service.SaveSettings(settings);
            Assert.Equal(100, service.LoadSettings().DailyRecommendations.TrackCount);
            Assert.Equal(30, service.LoadSettings().DailyRecommendations.TidalTrackCount);
            settings.DailyRecommendations.TrackCount = 99;
            Assert.Throws<ArgumentException>(() => service.SaveSettings(settings));
            Assert.Equal(100, service.LoadSettings().DailyRecommendations.TrackCount);
            service.SaveSettings(DeezSpoTagSettingsService.GetStaticDefaultSettings());
            Assert.Equal(60, service.LoadSettings().DailyRecommendations.TrackCount);
            Assert.Equal(0, service.LoadSettings().DailyRecommendations.ShazamTrackCount);
        }
        finally { Directory.Delete(root, true); }
    }
}
