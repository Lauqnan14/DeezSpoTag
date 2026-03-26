using System.Text.Json;
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
                return new AutoTagDefaultsDto(null, new Dictionary<string, string>(), new Dictionary<string, string>());
            }

            var json = await File.ReadAllTextAsync(_defaultsPath);
            return JsonSerializer.Deserialize<AutoTagDefaultsDto>(json, _jsonOptions)
                ?? new AutoTagDefaultsDto(null, new Dictionary<string, string>(), new Dictionary<string, string>());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load AutoTag defaults.");
            return new AutoTagDefaultsDto(null, new Dictionary<string, string>(), new Dictionary<string, string>());
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
        var libraryProfiles = defaults.LibraryProfiles is { Count: > 0 }
            ? new Dictionary<string, string>(defaults.LibraryProfiles, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var librarySchedules = defaults.LibrarySchedules is { Count: > 0 }
            ? new Dictionary<string, string>(defaults.LibrarySchedules, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;

        if (!string.IsNullOrWhiteSpace(defaultFileProfile) && references.Contains(defaultFileProfile.Trim()))
        {
            defaultFileProfile = null;
            changed = true;
        }

        var foldersToClearSchedule = new List<string>();
        foreach (var (folderId, profileReference) in libraryProfiles.ToList())
        {
            if (string.IsNullOrWhiteSpace(profileReference) || !references.Contains(profileReference.Trim()))
            {
                continue;
            }

            libraryProfiles.Remove(folderId);
            foldersToClearSchedule.Add(folderId);
            changed = true;
        }

        foreach (var folderId in foldersToClearSchedule)
        {
            changed |= librarySchedules.Remove(folderId);
        }

        if (!changed)
        {
            return false;
        }

        await SaveAsync(new AutoTagDefaultsDto(defaultFileProfile, libraryProfiles, librarySchedules));
        return true;
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
    Dictionary<string, string> LibraryProfiles,
    Dictionary<string, string> LibrarySchedules);
