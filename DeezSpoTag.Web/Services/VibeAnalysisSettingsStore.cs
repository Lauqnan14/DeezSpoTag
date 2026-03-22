using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace DeezSpoTag.Web.Services;

public sealed class VibeAnalysisSettingsStore
{
    private readonly string _settingsPath;
    private readonly ILogger<VibeAnalysisSettingsStore> _logger;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private VibeAnalysisSettingsDto? _cached;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
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
        await _sync.WaitAsync();
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            if (!File.Exists(_settingsPath))
            {
                _cached = VibeAnalysisSettingsDto.Defaults();
                return _cached;
            }

            var json = await File.ReadAllTextAsync(_settingsPath);
            _cached = JsonSerializer.Deserialize<VibeAnalysisSettingsDto>(json, _jsonOptions)
                ?? VibeAnalysisSettingsDto.Defaults();
            return _cached;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load vibe analysis settings from {Path}.", _settingsPath);
            _cached ??= VibeAnalysisSettingsDto.Defaults();
            return _cached;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<VibeAnalysisSettingsDto> SaveAsync(VibeAnalysisSettingsDto settings)
    {
        await _sync.WaitAsync();
        try
        {
            _cached = settings;
            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            await File.WriteAllTextAsync(_settingsPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save vibe analysis settings to {Path}. Using in-memory settings for this runtime.", _settingsPath);
        }
        finally
        {
            _sync.Release();
        }

        return _cached ?? settings;
    }
}

public sealed record VibeAnalysisSettingsDto(bool Enabled, int BatchSize, int IntervalMinutes)
{
    public static VibeAnalysisSettingsDto Defaults() => new(true, 50, 30);
}
