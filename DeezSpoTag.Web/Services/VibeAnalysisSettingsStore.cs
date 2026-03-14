using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace DeezSpoTag.Web.Services;

public sealed class VibeAnalysisSettingsStore
{
    private readonly string _settingsPath;
    private readonly ILogger<VibeAnalysisSettingsStore> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public VibeAnalysisSettingsStore(
        IWebHostEnvironment env,
        IConfiguration configuration,
        ILogger<VibeAnalysisSettingsStore> logger)
    {
        _logger = logger;
        var configuredDataDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR");
        if (string.IsNullOrWhiteSpace(configuredDataDir))
        {
            configuredDataDir = configuration["DataDirectory"];
        }

        var baseDataDir = string.IsNullOrWhiteSpace(configuredDataDir)
            ? Path.Combine(env.ContentRootPath, "Data")
            : configuredDataDir;
        var dataDir = Path.Combine(baseDataDir, "analysis");
        Directory.CreateDirectory(dataDir);
        _settingsPath = Path.Combine(dataDir, "settings.json");
    }

    public async Task<VibeAnalysisSettingsDto> LoadAsync()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return VibeAnalysisSettingsDto.Defaults();
            }

            var json = await File.ReadAllTextAsync(_settingsPath);
            return JsonSerializer.Deserialize<VibeAnalysisSettingsDto>(json, _jsonOptions)
                ?? VibeAnalysisSettingsDto.Defaults();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load vibe analysis settings.");
            return VibeAnalysisSettingsDto.Defaults();
        }
    }

    public async Task<VibeAnalysisSettingsDto> SaveAsync(VibeAnalysisSettingsDto settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            await File.WriteAllTextAsync(_settingsPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save vibe analysis settings.");
        }

        return settings;
    }
}

public sealed record VibeAnalysisSettingsDto(bool Enabled, int BatchSize, int IntervalMinutes)
{
    public static VibeAnalysisSettingsDto Defaults() => new(true, 50, 30);
}
