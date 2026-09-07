using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;

namespace DeezSpoTag.Web.Services;

public sealed class MelodaySettingsStore
{
    private readonly string _settingsPath;
    private readonly ILogger<MelodaySettingsStore> _logger;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly SemaphoreSlim _changed = new(0, 1);
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
            var stored = DeserializeWithShapeConversion(json);
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

        if (_changed.CurrentCount == 0)
        {
            _changed.Release();
        }
        return Clone(_cached ?? settings);
    }

    public Task WaitForChangeAsync(CancellationToken cancellationToken)
        => _changed.WaitAsync(cancellationToken);

    public Task<bool> WaitForChangeAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => _changed.WaitAsync(timeout, cancellationToken);

    /// <summary>
    /// Settings files written by earlier Meloday models store libraries in older shapes
    /// (per-slot assignments, or none at all). Convert them to the current per-library
    /// mode + slot-id shape before model binding.
    /// </summary>
    private MelodayOptions? DeserializeWithShapeConversion(string json)
    {
        var node = JsonNode.Parse(json);
        if (node is not JsonObject root)
        {
            return JsonSerializer.Deserialize<MelodayOptions>(json, _jsonOptions);
        }

        if (root["libraries"] is JsonArray libraries)
        {
            var converted = new JsonArray();
            foreach (var library in libraries.OfType<JsonObject>())
            {
                converted.Add(ConvertLibraryShape(library));
            }

            root["libraries"] = converted;
        }

        return root.Deserialize<MelodayOptions>(_jsonOptions);
    }

    private static JsonObject ConvertLibraryShape(JsonObject library)
    {
        long libraryId = library.TryGetPropertyValue("libraryId", out var idNode)
            && idNode is JsonValue idValue
            && idValue.TryGetValue<long>(out var parsedId)
            ? parsedId
            : 0;
        int maxActivePlaylists = library.TryGetPropertyValue("maxActivePlaylists", out var maxNode)
            && maxNode is JsonValue maxValue
            && maxValue.TryGetValue<int>(out var parsedMax)
            ? parsedMax
            : MelodayScheduleSlots.DefaultMaxActivePlaylists;
        var mode = library.TryGetPropertyValue("mode", out var modeNode)
            && modeNode is JsonValue modeValue
            && modeValue.TryGetValue<string>(out var parsedMode)
            ? parsedMode
            : string.Empty;

        var slotIds = new List<string>();
        if (library.TryGetPropertyValue("slotIds", out var slotIdsNode) && slotIdsNode is JsonArray slotIdArray)
        {
            slotIds.AddRange(slotIdArray
                .OfType<JsonValue>()
                .Where(static value => value.TryGetValue<string>(out _))
                .Select(static value => value.GetValue<string>() ?? string.Empty));
        }
        else if (library.TryGetPropertyValue("slots", out var slotsNode) && slotsNode is JsonArray slotsArray)
        {
            // Per-slot-era shape: [{ slotId, mode }]. The first assignment's mode becomes
            // the library mode.
            foreach (var slot in slotsArray.OfType<JsonObject>())
            {
                var slotId = slot.TryGetPropertyValue("slotId", out var slotIdNode)
                    && slotIdNode is JsonValue slotIdValue
                    && slotIdValue.TryGetValue<string>(out var parsedSlotId)
                    ? parsedSlotId
                    : null;
                if (string.IsNullOrWhiteSpace(slotId))
                {
                    continue;
                }

                slotIds.Add(slotId);
                if (string.IsNullOrWhiteSpace(mode)
                    && slot.TryGetPropertyValue("mode", out var slotModeNode)
                    && slotModeNode is JsonValue slotModeValue
                    && slotModeValue.TryGetValue<string>(out var parsedSlotMode))
                {
                    mode = parsedSlotMode;
                }
            }
        }

        return new JsonObject
        {
            ["libraryId"] = libraryId,
            ["maxActivePlaylists"] = maxActivePlaylists,
            ["mode"] = mode,
            ["slotIds"] = new JsonArray(slotIds.Select(slotId => JsonValue.Create(slotId)).ToArray())
        };
    }

    private static MelodayOptions Merge(MelodayOptions defaults, MelodayOptions stored)
    {
        var merged = new MelodayOptions
        {
            Enabled = stored.Enabled,
            BaseUrl = string.IsNullOrWhiteSpace(stored.BaseUrl) ? defaults.BaseUrl : stored.BaseUrl,
            ExcludePlayedDays = MelodayClamp.AllowZeroOrDefault(stored.ExcludePlayedDays, defaults.ExcludePlayedDays, 0, 365),
            HistoryLookbackDays = MelodayClamp.PositiveOrDefault(stored.HistoryLookbackDays, defaults.HistoryLookbackDays, 1, 365),
            MaxTracks = MelodayClamp.PositiveOrDefault(stored.MaxTracks, defaults.MaxTracks, 10, 500),
            HistoricalRatio = MelodayClamp.AllowZeroOrDefault(stored.HistoricalRatio, defaults.HistoricalRatio, 0d, 1d),
            SonicSimilarLimit = MelodayClamp.PositiveOrDefault(stored.SonicSimilarLimit, defaults.SonicSimilarLimit, 1, 50),
            SonicSimilarityDistance = MelodayClamp.PositiveOrDefault(stored.SonicSimilarityDistance, defaults.SonicSimilarityDistance, 0.05d, 1d),
            UpdateIntervalMinutes = MelodayClamp.PositiveOrDefault(stored.UpdateIntervalMinutes, defaults.UpdateIntervalMinutes, 5, 1440),
            Mode = MelodayModes.Normalize(string.IsNullOrWhiteSpace(stored.Mode) ? defaults.Mode : stored.Mode),
            MoodMapPath = string.IsNullOrWhiteSpace(stored.MoodMapPath) ? defaults.MoodMapPath : stored.MoodMapPath,
            Slots = MelodayScheduleSlots.Normalize(stored.Slots),
            Libraries = MelodayScheduleSlots.NormalizeLibraries(stored.Libraries),
            MissedRunGraceMinutes = MelodayClamp.PositiveOrDefault(stored.MissedRunGraceMinutes, defaults.MissedRunGraceMinutes, 0, 720),
            TargetServers = MelodayTargetServers.Normalize(stored.TargetServers, defaultToAll: true),
            TargetLibraryIds = MelodayService.NormalizeTargetLibraryIds(stored.TargetLibraryIds)
        };

        MigrateLegacyLibraryTargets(merged);
        return merged;
    }

    /// <summary>
    /// One-time upgrade for the very first scheduled-slot generation: files saved before
    /// scheduled slots carried only TargetLibraryIds + a global mode. Those become
    /// per-library schedules with every slot at the previous global mode.
    /// </summary>
    private static void MigrateLegacyLibraryTargets(MelodayOptions merged)
    {
        if (merged.Libraries.Count > 0 || merged.TargetLibraryIds.Count == 0)
        {
            return;
        }

        merged.Libraries = merged.TargetLibraryIds
            .Select(libraryId => new MelodayLibrarySchedule(
                libraryId,
                MelodayScheduleSlots.DefaultMaxActivePlaylists,
                merged.Mode,
                MelodayScheduleSlots.Defaults.Select(static slot => slot.Id).ToList()))
            .ToList();
    }

    private static MelodayOptions Clone(MelodayOptions source) => new()
    {
        Enabled = source.Enabled,
        BaseUrl = source.BaseUrl,
        ExcludePlayedDays = source.ExcludePlayedDays,
        HistoryLookbackDays = source.HistoryLookbackDays,
        MaxTracks = source.MaxTracks,
        HistoricalRatio = source.HistoricalRatio,
        SonicSimilarLimit = source.SonicSimilarLimit,
        SonicSimilarityDistance = source.SonicSimilarityDistance,
        UpdateIntervalMinutes = source.UpdateIntervalMinutes,
        Mode = MelodayModes.Normalize(source.Mode),
        Slots = MelodayScheduleSlots.Normalize(source.Slots),
        Libraries = MelodayScheduleSlots.NormalizeLibraries(source.Libraries),
        MissedRunGraceMinutes = source.MissedRunGraceMinutes,
        MoodMapPath = source.MoodMapPath,
        TargetServers = MelodayTargetServers.Normalize(source.TargetServers, defaultToAll: true),
        TargetLibraryIds = MelodayService.NormalizeTargetLibraryIds(source.TargetLibraryIds)
    };
}
