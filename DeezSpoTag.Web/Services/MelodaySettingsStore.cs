using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace DeezSpoTag.Web.Services;

public sealed class MelodaySettingsStore
{
    private readonly string _settingsPath;
    private readonly ILogger<MelodaySettingsStore> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public MelodaySettingsStore(IWebHostEnvironment env, ILogger<MelodaySettingsStore> logger)
    {
        _logger = logger;
        var dataDir = Path.Join(AppDataPaths.GetDataRoot(env), "meloday");
        Directory.CreateDirectory(dataDir);
        _settingsPath = Path.Join(dataDir, "settings.json");
    }

    public async Task<MelodayOptions> LoadAsync(MelodayOptions defaults)
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return defaults;
            }

            var json = await File.ReadAllTextAsync(_settingsPath);
            var stored = JsonSerializer.Deserialize<MelodayOptions>(json, _jsonOptions);
            if (stored == null)
            {
                return defaults;
            }

            return Merge(defaults, stored);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load Meloday settings.");
            return defaults;
        }
    }

    public async Task<MelodayOptions> SaveAsync(MelodayOptions settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            await File.WriteAllTextAsync(_settingsPath, json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to save Meloday settings.");
        }
        return settings;
    }

    private static MelodayOptions Merge(MelodayOptions defaults, MelodayOptions stored) => new()
    {
        Enabled = stored.Enabled,
        LibraryName = string.IsNullOrWhiteSpace(stored.LibraryName) ? defaults.LibraryName : stored.LibraryName,
        PlaylistPrefix = string.IsNullOrWhiteSpace(stored.PlaylistPrefix) ? defaults.PlaylistPrefix : stored.PlaylistPrefix,
        BaseUrl = string.IsNullOrWhiteSpace(stored.BaseUrl) ? defaults.BaseUrl : stored.BaseUrl,
        ExcludePlayedDays = stored.ExcludePlayedDays <= 0 ? defaults.ExcludePlayedDays : stored.ExcludePlayedDays,
        HistoryLookbackDays = stored.HistoryLookbackDays <= 0 ? defaults.HistoryLookbackDays : stored.HistoryLookbackDays,
        MaxTracks = stored.MaxTracks <= 0 ? defaults.MaxTracks : stored.MaxTracks,
        HistoricalRatio = stored.HistoricalRatio <= 0 ? defaults.HistoricalRatio : stored.HistoricalRatio,
        SonicSimilarLimit = stored.SonicSimilarLimit <= 0 ? defaults.SonicSimilarLimit : stored.SonicSimilarLimit,
        SonicSimilarityDistance = stored.SonicSimilarityDistance <= 0 ? defaults.SonicSimilarityDistance : stored.SonicSimilarityDistance,
        UpdateIntervalMinutes = stored.UpdateIntervalMinutes <= 0 ? defaults.UpdateIntervalMinutes : stored.UpdateIntervalMinutes,
        MoodMapPath = string.IsNullOrWhiteSpace(stored.MoodMapPath) ? defaults.MoodMapPath : stored.MoodMapPath,
        CoversPath = string.IsNullOrWhiteSpace(stored.CoversPath) ? defaults.CoversPath : stored.CoversPath,
        FontsPath = string.IsNullOrWhiteSpace(stored.FontsPath) ? defaults.FontsPath : stored.FontsPath,
        MainFontFile = string.IsNullOrWhiteSpace(stored.MainFontFile) ? defaults.MainFontFile : stored.MainFontFile,
        BrandFontFile = string.IsNullOrWhiteSpace(stored.BrandFontFile) ? defaults.BrandFontFile : stored.BrandFontFile
    };
}
