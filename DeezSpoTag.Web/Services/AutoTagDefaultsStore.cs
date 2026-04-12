using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;

namespace DeezSpoTag.Web.Services;

public sealed class AutoTagDefaultsStore
{
    private const string AutoTagFolderName = "autotag";
    private const string DefaultsFileName = "defaults.json";
    private readonly string _defaultsPath;
    private readonly string _dataRoot;
    private readonly string _contentRoot;
    private readonly ILogger<AutoTagDefaultsStore> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public AutoTagDefaultsStore(IWebHostEnvironment env, ILogger<AutoTagDefaultsStore> logger)
    {
        _logger = logger;
        _contentRoot = env.ContentRootPath;
        _dataRoot = AppDataPaths.GetDataRoot(env);
        var dataDir = Path.Join(_dataRoot, AutoTagFolderName);
        Directory.CreateDirectory(dataDir);
        _defaultsPath = Path.Join(dataDir, DefaultsFileName);
    }

    public async Task<AutoTagDefaultsDto> LoadAsync()
    {
        try
        {
            await TryMigrateLegacyDefaultsAsync();
            if (!File.Exists(_defaultsPath))
            {
                return new AutoTagDefaultsDto(
                    null,
                    new Dictionary<string, string>(),
                    AutoTagDefaultsDto.DefaultRecentDownloadWindowHours);
            }

            var json = await File.ReadAllTextAsync(_defaultsPath);
            var defaults = JsonSerializer.Deserialize<AutoTagDefaultsDto>(json, _jsonOptions)
                ?? new AutoTagDefaultsDto(
                    null,
                    new Dictionary<string, string>(),
                    AutoTagDefaultsDto.DefaultRecentDownloadWindowHours);
            var normalized = NormalizeDefaults(defaults, out var normalizedChanged);
            var hadLegacyLibraryProfiles = ContainsLegacyLibraryProfiles(json);
            if (normalizedChanged || hadLegacyLibraryProfiles)
            {
                await SaveAsync(normalized);
            }

            return normalized;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load AutoTag defaults.");
            return new AutoTagDefaultsDto(
                null,
                new Dictionary<string, string>(),
                AutoTagDefaultsDto.DefaultRecentDownloadWindowHours);
        }
    }

    public async Task<AutoTagDefaultsDto> SaveAsync(AutoTagDefaultsDto defaults)
    {
        try
        {
            var json = JsonSerializer.Serialize(defaults, _jsonOptions);
            await File.WriteAllTextAsync(_defaultsPath, json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to save AutoTag defaults.");
        }

        return defaults;
    }

    public async Task<bool> RemoveProfileReferencesAsync(string? profileId, string? profileName)
    {
        var references = AutoTagProfileReferenceSet.Build(profileId, profileName);
        if (references.Count == 0)
        {
            return false;
        }

        var defaults = await LoadAsync();
        var defaultFileProfile = defaults.DefaultFileProfile;
        var librarySchedules = defaults.LibrarySchedules is { Count: > 0 }
            ? new Dictionary<string, string>(defaults.LibrarySchedules, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;

        if (!string.IsNullOrWhiteSpace(defaultFileProfile) && references.Contains(defaultFileProfile.Trim()))
        {
            defaultFileProfile = null;
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        await SaveAsync(new AutoTagDefaultsDto(
            defaultFileProfile,
            librarySchedules,
            defaults.RecentDownloadWindowHours));
        return true;
    }

    private static AutoTagDefaultsDto NormalizeDefaults(AutoTagDefaultsDto defaults, out bool changed)
    {
        changed = false;
        var defaultFileProfile = string.IsNullOrWhiteSpace(defaults.DefaultFileProfile)
            ? null
            : defaults.DefaultFileProfile.Trim();
        if (!string.Equals(defaultFileProfile, defaults.DefaultFileProfile, StringComparison.Ordinal))
        {
            changed = true;
        }

        var schedules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawFolderId, rawSchedule) in defaults.LibrarySchedules ?? new Dictionary<string, string>())
        {
            var folderId = rawFolderId?.Trim();
            var schedule = rawSchedule?.Trim();
            if (string.IsNullOrWhiteSpace(folderId) || string.IsNullOrWhiteSpace(schedule))
            {
                changed = true;
                continue;
            }

            if (!long.TryParse(folderId, out var parsedFolderId) || parsedFolderId <= 0)
            {
                changed = true;
                continue;
            }

            if (!string.Equals(folderId, rawFolderId, StringComparison.Ordinal)
                || !string.Equals(schedule, rawSchedule, StringComparison.Ordinal))
            {
                changed = true;
            }

            schedules[folderId] = schedule;
        }

        if (schedules.Count != (defaults.LibrarySchedules?.Count ?? 0))
        {
            changed = true;
        }

        var recentDownloadWindowHours = defaults.RecentDownloadWindowHours;
        if (recentDownloadWindowHours is null || recentDownloadWindowHours < 0)
        {
            recentDownloadWindowHours = AutoTagDefaultsDto.DefaultRecentDownloadWindowHours;
            changed = true;
        }

        return new AutoTagDefaultsDto(defaultFileProfile, schedules, recentDownloadWindowHours);
    }

    private static bool ContainsLegacyLibraryProfiles(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            return JsonNode.Parse(json) is JsonObject root && root["libraryProfiles"] is not null;
        }
        catch
        {
            return false;
        }
    }

    private async Task TryMigrateLegacyDefaultsAsync()
    {
        if (File.Exists(_defaultsPath))
        {
            return;
        }

        foreach (var legacyPath in GetLegacyDefaultsCandidates())
        {
            if (!File.Exists(legacyPath))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_defaultsPath)!);
                await using var source = File.OpenRead(legacyPath);
                await using var target = File.Create(_defaultsPath);
                await source.CopyToAsync(target);
                _logger.LogInformation("Migrated AutoTag defaults from legacy path {LegacyPath} to {DefaultsPath}.", legacyPath, _defaultsPath);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed migrating AutoTag defaults from legacy path {LegacyPath}.", legacyPath);
            }
        }
    }

    private IEnumerable<string> GetLegacyDefaultsCandidates()
        => AutoTagLegacyPathCandidates.Enumerate(_contentRoot, _dataRoot, _defaultsPath, DefaultsFileName, AutoTagFolderName);
}

public sealed record AutoTagDefaultsDto(
    string? DefaultFileProfile,
    Dictionary<string, string> LibrarySchedules,
    int? RecentDownloadWindowHours)
{
    public const int DefaultRecentDownloadWindowHours = 24;
}
