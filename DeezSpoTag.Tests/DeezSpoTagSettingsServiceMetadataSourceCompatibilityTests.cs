using System;
using System.IO;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class DeezSpoTagSettingsServiceMetadataSourceCompatibilityTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly TestConfigRootScope _configScope;
    private readonly DeezSpoTagSettingsService _settingsService;

    public DeezSpoTagSettingsServiceMetadataSourceCompatibilityTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-settings-metadata-source-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _configScope = new TestConfigRootScope(_tempRoot);
        _settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
    }

    [Fact]
    public void LoadAndSaveSettings_IgnoresLegacyMetadataSource_AndCleansItFromPersistedConfig()
    {
        var configPath = Path.Join(_tempRoot, "deezspotag", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """
{
  "service": "auto",
  "metadataSource": "spotify"
}
""");

        var loaded = _settingsService.LoadSettings();
        _settingsService.SaveSettings(loaded);

        var persisted = File.ReadAllText(configPath);
        Assert.False(persisted.Contains("\"metadataSource\"", StringComparison.OrdinalIgnoreCase));
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
