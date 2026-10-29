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
    [Theory]
    [InlineData(20)]
    [InlineData(60)]
    [InlineData(100)]
    public void ShazamOnlyAllocationsRemainEnabledAndRespectTheTotal(int total)
    {
        var settings = new DailyRecommendationSettings { TrackCount = total, ShazamEnabled = true, ShazamTrackCount = total,
            DeezerEnabled = false, DeezerTrackCount = 0, SpotifyEnabled = false, SpotifyTrackCount = 0, TidalEnabled = false, TidalTrackCount = 0 };
        Assert.True(settings.TryValidate(out _));
        Assert.False(settings.IsDisabled);
        Assert.Equal(total, settings.Allocations()["shazam"]);
    }
    [Fact]
    public void TooltipInitializationPreservesRecommendationStatusDuringSettingsLoad()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Join(root.FullName, "DeezSpoTag.Web", "Views"))) root = root.Parent;
        Assert.NotNull(root);
        var script = """
            const fs = require('node:fs');
            const vm = require('node:vm');
            const assert = require('node:assert/strict');
            const view = fs.readFileSync(`${process.argv[1]}/DeezSpoTag.Web/Views/Settings/Index.cshtml`, 'utf8');
            const elements = new Map();
            const label = {};
            const warnings = ['dailyRecommendationPaused', 'multiQualitySecondaryFolderWarning', 'staticHelp'].map(id => {
                const warning = {
                    id, hidden: id === 'dailyRecommendationPaused', textContent: 'Help text',
                    getAttribute: () => null,
                    closest: selector => selector === '.form-group' ? { querySelector: () => label } : null,
                    remove() { elements.delete(id); }
                };
                elements.set(id, warning);
                return warning;
            });
            elements.set('dailyRecommendationTrackCount', { value: '60' });
            elements.set('dailyRecommendationAiFilterMode', { value: '1' });
            for (const source of ['Shazam', 'Deezer', 'Spotify', 'Tidal']) {
                elements.set(`daily${source}Enabled`, { checked: source !== 'Shazam' });
                elements.set(`daily${source}TrackCount`, { value: source === 'Shazam' ? '0' : '20' });
            }
            const context = vm.createContext({
                document: {
                    getElementById: id => elements.get(id) || null,
                    querySelectorAll: () => warnings.filter(warning => elements.has(warning.id))
                },
                upsertSettingsTooltip: () => ({ dataset: {} })
            });
            for (const name of ['resolveTooltipTarget', 'initializeSettingsTooltips', 'collectDailyRecommendations', 'updateDailyRecommendationAllocation']) {
                const match = view.match(new RegExp(`^function ${name}\\([^\\n]*\\) \\{[\\s\\S]*?^\\}`, 'm'));
                assert.ok(match, `Missing function ${name}`);
                vm.runInContext(match[0], context);
            }
            // Tooltips initialize while the settings fetch is pending, then again after it finishes.
            context.initializeSettingsTooltips();
            assert.equal(context.updateDailyRecommendationAllocation(), true);
            assert.equal(elements.get('dailyRecommendationPaused').hidden, true);
            assert.ok(elements.has('multiQualitySecondaryFolderWarning'));
            assert.equal(elements.has('staticHelp'), false);
            context.initializeSettingsTooltips();
            for (const source of ['Deezer', 'Spotify', 'Tidal']) {
                elements.get(`daily${source}Enabled`).checked = false;
                elements.get(`daily${source}TrackCount`).value = '0';
            }
            assert.equal(context.updateDailyRecommendationAllocation(), true);
            assert.equal(elements.get('dailyRecommendationPaused').hidden, false);
            elements.get('dailyDeezerEnabled').checked = true;
            elements.get('dailyDeezerTrackCount').value = '60';
            assert.equal(context.updateDailyRecommendationAllocation(), true);
            assert.equal(elements.get('dailyRecommendationPaused').hidden, true);
            elements.get('dailyShazamEnabled').checked = true;
            elements.get('dailyShazamTrackCount').value = '30';
            elements.get('dailyDeezerTrackCount').value = '30';
            assert.equal(context.updateDailyRecommendationAllocation(), true);
            assert.equal(context.collectDailyRecommendations().shazamEnabled, true);
            assert.equal(context.collectDailyRecommendations().shazamTrackCount, 30);
            """;
        var start = new System.Diagnostics.ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(root!.FullName);
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

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
    public void SettingsAllowShazamAndPreserveSixtyTrackDefaults()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Join(root.FullName, "DeezSpoTag.Web", "Views"))) root = root.Parent;
        Assert.NotNull(root);
        var view = File.ReadAllText(Path.Join(root!.FullName, "DeezSpoTag.Web", "Views", "Settings", "Index.cshtml"));
        Assert.Contains("id=\"dailyShazamEnabled\"", view);
        Assert.Contains("id=\"dailyShazamTrackCount\" min=\"0\" max=\"100\" step=\"1\" value=\"15\"", view);
        Assert.DoesNotContain("Shazam's similar-song service is not functional.", view);
        Assert.Contains("id=\"dailyRecommendationTrackCount\" min=\"20\" max=\"100\" step=\"1\" value=\"60\"", view);
        foreach (var source in new[] { "Shazam", "Deezer", "Spotify", "Tidal" })
        {
            Assert.Contains($"id=\"daily{source}Enabled\" checked", view);
            Assert.Contains($"id=\"daily{source}TrackCount\" min=\"0\" max=\"100\" step=\"1\" value=\"15\"", view);
        }
        Assert.Contains("setCheckboxValue(`daily${source}Enabled`, dailyRecommendations[`${key}Enabled`] === true)", view);
        Assert.Contains("setFieldValue(`daily${source}TrackCount`, dailyRecommendations[`${key}TrackCount`] ?? 0)", view);
        Assert.DoesNotContain("daily.shazamEnabled = false", view);
        Assert.DoesNotContain("daily.shazamTrackCount = 0", view);
    }
    [Fact]
    public void MissingSettingsAndDefaultsUseSixtyTracksAcrossFourSources()
    {
        foreach (var settings in new[] { new DeezSpoTagSettings(), JsonSerializer.Deserialize<DeezSpoTagSettings>("{}")!, DeezSpoTagSettingsService.GetStaticDefaultSettings() })
        {
            var daily = settings.DailyRecommendations;
            Assert.Equal(60, daily.TrackCount);
            Assert.True(daily.ShazamEnabled);
            Assert.True(daily.DeezerEnabled);
            Assert.Equal(15, daily.ShazamTrackCount);
            Assert.Equal(15, daily.DeezerTrackCount);
            Assert.True(daily.SpotifyEnabled);
            Assert.Equal(15, daily.SpotifyTrackCount);
            Assert.True(daily.TidalEnabled);
            Assert.Equal(15, daily.TidalTrackCount);
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
        var daily = new DailyRecommendationSettings { ShazamEnabled = false, ShazamTrackCount = 0 };
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
    public void ShazamUsesNormalAllocationValidation(bool enabled, int count)
    {
        var daily = new DailyRecommendationSettings { TrackCount = 60 + Math.Max(0, count),
            ShazamEnabled = enabled, ShazamTrackCount = count, DeezerEnabled = true, DeezerTrackCount = 20,
            SpotifyEnabled = true, SpotifyTrackCount = 20, TidalEnabled = true, TidalTrackCount = 20 };
        Assert.Equal(enabled && count > 0, daily.TryValidate(out _));
    }

    [Theory]
    [InlineData(true, 25, 25, 0, 0)]
    [InlineData(true, 10, 30, 20, 20)]
    public void SavedShazamAllocationsArePreservedWithoutChangingAiMode(
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
            File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var migrated = service.LoadSettings().DailyRecommendations;
            Assert.Equal(shazamCount + deezer + spotify + tidal, migrated.TrackCount);
            Assert.Equal(shazam, migrated.ShazamEnabled);
            Assert.Equal(shazamCount, migrated.ShazamTrackCount);
            Assert.Equal(deezer, migrated.DeezerTrackCount);
            Assert.Equal(spotify, migrated.SpotifyTrackCount);
            Assert.Equal(tidal, migrated.TidalTrackCount);
            Assert.Equal(DailyRecommendationAiFilterMode.Strict, migrated.AiFilterMode);
            Assert.True(migrated.TryValidate(out _));
            using var persisted = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(migrated.TrackCount, persisted.RootElement.GetProperty("dailyRecommendations").GetProperty("trackCount").GetInt32());
            Assert.Equal(shazam, persisted.RootElement.GetProperty("dailyRecommendations").GetProperty("shazamEnabled").GetBoolean());
            Assert.Equal(JsonSerializer.Serialize(migrated), JsonSerializer.Serialize(service.LoadSettings().DailyRecommendations));
        });
    }

    [Theory]
    [InlineData(60, 20, 20, 20)]
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
            Assert.Equal(15, service.LoadSettings().DailyRecommendations.ShazamTrackCount);
        }
        finally { Directory.Delete(root, true); }
    }
}
