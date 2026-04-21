using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace DeezSpoTag.Web.Services;

public sealed class MelodaySettingsStore
{
    private readonly string _settingsPath;
    private readonly ILogger<MelodaySettingsStore> _logger;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private MelodayOptions? _cached;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
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
        await _sync.WaitAsync();
        try
        {
            if (_cached is not null)
            {
                return Clone(_cached);
            }

            if (!File.Exists(_settingsPath))
            {
                _cached = Clone(defaults);
                return Clone(_cached);
            }

            var json = await File.ReadAllTextAsync(_settingsPath);
            var stored = JsonSerializer.Deserialize<MelodayOptions>(json, _jsonOptions);
            if (stored == null)
            {
                _cached = Clone(defaults);
                return Clone(_cached);
            }

            _cached = Merge(defaults, stored);
            return Clone(_cached);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load Meloday settings from {Path}.", _settingsPath);
            _cached ??= Clone(defaults);
            return Clone(_cached);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<MelodayOptions> SaveAsync(MelodayOptions settings)
    {
        await _sync.WaitAsync();
        try
        {
            _cached = Clone(settings);
            var json = JsonSerializer.Serialize(_cached, _jsonOptions);
            await File.WriteAllTextAsync(_settingsPath, json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to save Meloday settings to {Path}. Using in-memory settings for this runtime.", _settingsPath);
        }
        finally
        {
            _sync.Release();
        }

        return Clone(_cached ?? settings);
    }

    private static MelodayOptions Merge(MelodayOptions defaults, MelodayOptions stored) => new()
    {
        Enabled = stored.Enabled,
        LibraryName = string.IsNullOrWhiteSpace(stored.LibraryName) ? defaults.LibraryName : stored.LibraryName,
        PlaylistPrefix = string.IsNullOrWhiteSpace(stored.PlaylistPrefix) ? defaults.PlaylistPrefix : stored.PlaylistPrefix,
        BaseUrl = string.IsNullOrWhiteSpace(stored.BaseUrl) ? defaults.BaseUrl : stored.BaseUrl,
        ExcludePlayedDays = MelodayClamp.AllowZeroOrDefault(stored.ExcludePlayedDays, defaults.ExcludePlayedDays, 0, 365),
        HistoryLookbackDays = MelodayClamp.PositiveOrDefault(stored.HistoryLookbackDays, defaults.HistoryLookbackDays, 1, 365),
        MaxTracks = MelodayClamp.PositiveOrDefault(stored.MaxTracks, defaults.MaxTracks, 10, 500),
        HistoricalRatio = MelodayClamp.AllowZeroOrDefault(stored.HistoricalRatio, defaults.HistoricalRatio, 0d, 1d),
        SonicSimilarLimit = MelodayClamp.PositiveOrDefault(stored.SonicSimilarLimit, defaults.SonicSimilarLimit, 1, 50),
        SonicSimilarityDistance = MelodayClamp.PositiveOrDefault(stored.SonicSimilarityDistance, defaults.SonicSimilarityDistance, 0.05d, 1d),
        UpdateIntervalMinutes = MelodayClamp.PositiveOrDefault(stored.UpdateIntervalMinutes, defaults.UpdateIntervalMinutes, 5, 1440),
        MoodMapPath = string.IsNullOrWhiteSpace(stored.MoodMapPath) ? defaults.MoodMapPath : stored.MoodMapPath,
        CoversPath = string.IsNullOrWhiteSpace(stored.CoversPath) ? defaults.CoversPath : stored.CoversPath,
        FontsPath = string.IsNullOrWhiteSpace(stored.FontsPath) ? defaults.FontsPath : stored.FontsPath,
        MainFontFile = string.IsNullOrWhiteSpace(stored.MainFontFile) ? defaults.MainFontFile : stored.MainFontFile,
        BrandFontFile = string.IsNullOrWhiteSpace(stored.BrandFontFile) ? defaults.BrandFontFile : stored.BrandFontFile
    };

    private static MelodayOptions Clone(MelodayOptions source) => new()
    {
        Enabled = source.Enabled,
        LibraryName = source.LibraryName,
        PlaylistPrefix = source.PlaylistPrefix,
        BaseUrl = source.BaseUrl,
        ExcludePlayedDays = source.ExcludePlayedDays,
        HistoryLookbackDays = source.HistoryLookbackDays,
        MaxTracks = source.MaxTracks,
        HistoricalRatio = source.HistoricalRatio,
        SonicSimilarLimit = source.SonicSimilarLimit,
        SonicSimilarityDistance = source.SonicSimilarityDistance,
        UpdateIntervalMinutes = source.UpdateIntervalMinutes,
        MoodMapPath = source.MoodMapPath,
        CoversPath = source.CoversPath,
        FontsPath = source.FontsPath,
        MainFontFile = source.MainFontFile,
        BrandFontFile = source.BrandFontFile
    };
}
