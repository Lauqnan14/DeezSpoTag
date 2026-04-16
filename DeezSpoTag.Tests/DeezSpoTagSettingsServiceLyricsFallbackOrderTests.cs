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
    }

    [Fact]
    public void SaveSettings_UsesExpandedDefaultOrder_WhenConfiguredOrderHasNoKnownProviders()
    {
        var settings = _settingsService.LoadSettings();
        settings.LyricsFallbackOrder = "invalid-provider,still-invalid";

        _settingsService.SaveSettings(settings);

        var persisted = _settingsService.LoadSettings();
        Assert.Equal("apple,deezer,spotify,lrclib,musixmatch", persisted.LyricsFallbackOrder);
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
