using System;
using System.IO;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class DeezSpoTagSettingsServiceLyricsFallbackOrderTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly TestConfigRootScope _configScope;
    private readonly DeezSpoTagSettingsService _settingsService;

    public DeezSpoTagSettingsServiceLyricsFallbackOrderTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-settings-lyrics-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _configScope = new TestConfigRootScope(_tempRoot);
        _settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
    }

    [Fact]
    public void SaveSettings_NormalizesLyricsProviderAliases_AndPreservesMusixmatch()
    {
        var settings = _settingsService.LoadSettings();
        settings.LyricsFallbackOrder = "apple,lrcget,musixmatch,deezer,unknown,lrc-get";

        _settingsService.SaveSettings(settings);

        var persisted = _settingsService.LoadSettings();
        Assert.Equal("apple,lrclib,musixmatch,deezer", persisted.LyricsFallbackOrder);
        Assert.Equal(1, persisted.LyricsProviderRegistryVersion);
    }

    [Theory]
    [InlineData("richlyrics", true)]
    [InlineData("both", true)]
    [InlineData("elrc", true)]
    [InlineData("lrc,elrc,ttml", true)]
    [InlineData("lrc,ttml", false)]
    [InlineData("lrc", false)]
    [InlineData("ttml", false)]
    public void SaveSettings_DerivesPreferEnhancedLrcFromLegacyFormatOnce(string legacyFormat, bool expected)
    {
        var settings = _settingsService.LoadSettings();
        settings.LyricsFormatSchemaVersion = 0;
        settings.LrcFormat = legacyFormat;
        settings.PreferEnhancedLrc = !expected;

        _settingsService.SaveSettings(settings);

        var persisted = _settingsService.LoadSettings();
        Assert.Equal(expected, persisted.PreferEnhancedLrc);
        Assert.Equal(1, persisted.LyricsFormatSchemaVersion);
        Assert.DoesNotContain("elrc", persisted.LrcFormat, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SaveSettings_DoesNotRederivePreferEnhancedLrcAfterMigration()
    {
        var settings = _settingsService.LoadSettings();
        settings.LyricsFormatSchemaVersion = 1;
        settings.LrcFormat = "lrc,ttml";
        settings.PreferEnhancedLrc = true;

        _settingsService.SaveSettings(settings);

        Assert.True(_settingsService.LoadSettings().PreferEnhancedLrc);
    }

    [Fact]
    public void SaveSettings_DoesNotReenableNewProviderAfterRegistryMigration()
    {
        var settings = _settingsService.LoadSettings();
        settings.LyricsProviderRegistryVersion = 1;
        settings.LyricsFallbackOrder = "apple,deezer";

        _settingsService.SaveSettings(settings);

        var persisted = _settingsService.LoadSettings();
        Assert.Equal("apple,deezer", persisted.LyricsFallbackOrder);
        Assert.Equal(1, persisted.LyricsProviderRegistryVersion);
    }

    [Fact]
    public void SaveSettings_AppendsNewProvidersOnceForPreRegistrySettings()
    {
        var settings = _settingsService.LoadSettings();
        settings.LyricsProviderRegistryVersion = 0;
        settings.LyricsFallbackOrder = "apple,lrclib";

        _settingsService.SaveSettings(settings);

        var persisted = _settingsService.LoadSettings();
        Assert.Equal("apple,lrclib,youlyplus,betterlyrics", persisted.LyricsFallbackOrder);
        Assert.Equal(1, persisted.LyricsProviderRegistryVersion);
    }

    [Fact]
    public void SaveSettings_UsesExpandedDefaultOrder_WhenConfiguredOrderHasNoKnownProviders()
    {
        var settings = _settingsService.LoadSettings();
        settings.LyricsFallbackOrder = "invalid-provider,still-invalid";

        _settingsService.SaveSettings(settings);

        var persisted = _settingsService.LoadSettings();
        Assert.Equal("apple,deezer,spotify,lrclib,musixmatch,youlyplus,betterlyrics", persisted.LyricsFallbackOrder);
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
