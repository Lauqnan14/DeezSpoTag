using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Web.Services;

public sealed class TaggingProfileService
{
    private const string ProfilesFileName = "tagging-profiles.json";
    private const string AutoTagFolderName = "autotag";
    private readonly ILogger<TaggingProfileService> _logger;
    private readonly string _profilesPath;
    private readonly string _legacyProfilesPath;
    private readonly string _dataRoot;
    private readonly string _contentRoot;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public TaggingProfileService(IWebHostEnvironment env, ILogger<TaggingProfileService> logger)
    {
        _logger = logger;
        _contentRoot = env.ContentRootPath;
        _dataRoot = AppDataPaths.GetDataRoot(env);
        var dataDir = Path.Join(_dataRoot, AutoTagFolderName);
        Directory.CreateDirectory(dataDir);
        _profilesPath = Path.Join(dataDir, ProfilesFileName);
        _legacyProfilesPath = Path.Join(dataDir, "profiles.json");
    }

    public async Task<List<TaggingProfile>> LoadAsync()
    {
        try
        {
            var profiles = await EnsureSingleStoreAsync();
            var sanitized = SanitizeProfiles(profiles);
            if (sanitized)
            {
                await SaveAsync(profiles);
            }

            return profiles;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load tagging profiles.");
            return new List<TaggingProfile>();
        }
    }

    public async Task<List<TaggingProfile>> EnsureSingleStoreAsync()
    {
        await TryMigrateLegacyProfilesAsync();
        var profiles = await LoadPrimaryProfilesAsync();
        var importedLegacy = await ImportLegacyProfilesIntoSingleStoreAsync(profiles);
        if (importedLegacy)
        {
            await SaveAsync(profiles);
        }

        if (ShouldCleanupLegacyPrimaryStores())
        {
            await CleanupLegacyPrimaryStoreCandidatesAsync();
        }
        else
        {
            _logger.LogDebug(
                "Skipping legacy tagging profile cleanup because custom data root is active. DataRoot={DataRoot}",
                _dataRoot);
        }

        return profiles;
    }

    private async Task<List<TaggingProfile>> LoadPrimaryProfilesAsync()
    {
        if (!File.Exists(_profilesPath))
        {
            return new List<TaggingProfile>();
        }

        var json = await File.ReadAllTextAsync(_profilesPath);
        return JsonSerializer.Deserialize<List<TaggingProfile>>(json, _jsonOptions) ?? new List<TaggingProfile>();
    }

    public async Task SaveAsync(List<TaggingProfile> profiles)
    {
        var json = JsonSerializer.Serialize(profiles, _jsonOptions);
        await File.WriteAllTextAsync(_profilesPath, json);
    }

    public async Task<bool> HasAnyProfilesAsync()
    {
        var profiles = await LoadAsync();
        return profiles.Count > 0;
    }

    public async Task<TaggingProfile?> GetByIdAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var profiles = await LoadAsync();
        return profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<TaggingProfile?> GetDefaultAsync()
    {
        var profiles = await LoadAsync();
        if (profiles.Count == 0)
        {
            return null;
        }

        return profiles.FirstOrDefault(p => p.IsDefault) ?? profiles[0];
    }

    public async Task<TaggingProfile?> SetDefaultProfileAsync(string? profileReference)
    {
        if (string.IsNullOrWhiteSpace(profileReference))
        {
            return await GetDefaultAsync();
        }

        var profiles = await LoadAsync();
        if (profiles.Count == 0)
        {
            return null;
        }

        var target = FindByIdOrName(profiles, profileReference);
        if (target == null)
        {
            return null;
        }

        var changed = false;
        foreach (var profile in profiles)
        {
            var shouldBeDefault = string.Equals(profile.Id, target.Id, StringComparison.OrdinalIgnoreCase);
            if (profile.IsDefault != shouldBeDefault)
            {
                profile.IsDefault = shouldBeDefault;
                changed = true;
            }
        }

        if (changed)
        {
            await SaveAsync(profiles);
        }

        return target;
    }

    public async Task<TaggingProfile?> UpsertAsync(TaggingProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            return null;
        }

        TaggingProfileCanonicalizer.SyncTagArraysFromConfig(profile);
        TaggingProfileCanonicalizer.Canonicalize(profile);

        var profiles = await LoadAsync();
        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            profile.Id = Guid.NewGuid().ToString("N");
        }

        if (profile.IsDefault)
        {
            foreach (var other in profiles)
            {
                other.IsDefault = false;
            }
        }

        var existing = profiles.FirstOrDefault(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Name = profile.Name;
            existing.IsDefault = profile.IsDefault;
            existing.TagConfig = profile.TagConfig;
            existing.AutoTag = profile.AutoTag;
            existing.Technical = profile.Technical;
            existing.FolderStructure = profile.FolderStructure;
            existing.Verification = profile.Verification;
            TaggingProfileCanonicalizer.Canonicalize(existing);
        }
        else
        {
            profiles.Add(profile);
        }

        await SaveAsync(profiles);
        return profile;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var profiles = await LoadAsync();
        var removedProfiles = profiles
            .Where(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var removed = removedProfiles.Count > 0;
        if (removed)
        {
            profiles.RemoveAll(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            var removedDefault = removedProfiles.Any(profile => profile.IsDefault);
            if (removedDefault && profiles.Count > 0 && !profiles.Any(profile => profile.IsDefault))
            {
                profiles[0].IsDefault = true;
            }
            await SaveAsync(profiles);
        }

        return removed;
    }

    public static TaggingProfile? FindByIdOrName(IEnumerable<TaggingProfile> profiles, string? reference)
    {
        if (profiles == null || string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var normalized = reference.Trim();
        return profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, normalized, StringComparison.OrdinalIgnoreCase))
            ?? profiles.FirstOrDefault(profile =>
                string.Equals(profile.Name, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static TaggingProfile? ResolveFallback(IEnumerable<TaggingProfile> profiles)
    {
        if (profiles == null)
        {
            return null;
        }

        var list = profiles as IList<TaggingProfile> ?? profiles.ToList();
        if (list.Count == 0)
        {
            return null;
        }

        return list.FirstOrDefault(profile => profile.IsDefault)
            ?? (list.Count == 1 ? list[0] : null);
    }

    private static bool SanitizeProfiles(List<TaggingProfile> profiles)
    {
        if (profiles == null)
        {
            return false;
        }

        var changed = RemoveLegacyMigratedProfiles(profiles);
        if (EnsureSingleDefaultProfile(profiles))
        {
            changed = true;
        }
        foreach (var _ in profiles.Where(SanitizeProfile))
        {
            changed = true;
        }

        return changed;
    }

    private static bool EnsureSingleDefaultProfile(List<TaggingProfile> profiles)
    {
        if (profiles.Count == 0)
        {
            return false;
        }

        var changed = false;
        var defaultProfiles = profiles
            .Where(profile => profile?.IsDefault == true)
            .ToList();

        if (defaultProfiles.Count == 0)
        {
            profiles[0].IsDefault = true;
            return true;
        }

        var keeper = defaultProfiles[0];
        foreach (var profile in profiles)
        {
            if (ReferenceEquals(profile, keeper))
            {
                continue;
            }

            if (profile.IsDefault)
            {
                profile.IsDefault = false;
                changed = true;
            }
        }

        return changed;
    }

    private static bool RemoveLegacyMigratedProfiles(List<TaggingProfile> profiles)
    {
        var removedLegacyMigratedCount = profiles.RemoveAll(profile =>
            profile != null &&
            string.Equals(profile.Name?.Trim(), "Default (Migrated)", StringComparison.OrdinalIgnoreCase));
        if (removedLegacyMigratedCount <= 0)
        {
            return false;
        }

        if (profiles.Count > 0 && !profiles.Any(profile => profile?.IsDefault == true))
        {
            profiles[0].IsDefault = true;
        }

        return true;
    }

    private static bool SanitizeProfile(TaggingProfile? profile)
    {
        if (profile == null)
        {
            return false;
        }

        profile.AutoTag ??= new AutoTagSettings();
        profile.AutoTag.Data ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        var changed = StripAuthSecrets(profile.AutoTag.Data);
        if (TaggingProfileDataHelper.CanonicalizeEnhancementConfig(profile.AutoTag.Data))
        {
            changed = true;
        }

        if (TryNormalizeDownloadTagSourceField(profile.AutoTag.Data))
        {
            changed = true;
        }

        if (TaggingProfileCanonicalizer.SyncTagArraysFromConfig(profile))
        {
            changed = true;
        }

        if (TaggingProfileCanonicalizer.Canonicalize(profile))
        {
            changed = true;
        }

        return changed;
    }

    private static bool TryNormalizeDownloadTagSourceField(Dictionary<string, JsonElement> data)
    {
        var key = data.Keys.FirstOrDefault(entry =>
            string.Equals(entry, "downloadTagSource", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(key))
        {
            data["downloadTagSource"] = JsonSerializer.SerializeToElement("deezer");
            return true;
        }

        var current = data[key];
        var normalized = current.ValueKind == JsonValueKind.String
            ? TaggingProfileDataHelper.NormalizeDownloadTagSource(current.GetString(), defaultSource: "deezer")
            : "deezer";
        if (current.ValueKind == JsonValueKind.String
            && string.Equals(current.GetString(), normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        data[key] = JsonSerializer.SerializeToElement(normalized);
        return true;
    }

    private static bool StripAuthSecrets(Dictionary<string, JsonElement> data)
        => TaggingProfileDataHelper.StripAuthSecrets(data);

    private async Task<bool> ImportLegacyProfilesIntoSingleStoreAsync(List<TaggingProfile> profiles)
    {
        if (!File.Exists(_legacyProfilesPath))
        {
            return false;
        }

        var imported = false;
        if (profiles.Count == 0)
        {
            try
            {
                var json = await File.ReadAllTextAsync(_legacyProfilesPath);
                var legacyProfiles = JsonSerializer.Deserialize<List<LegacyAutoTagProfile>>(json, _jsonOptions)
                    ?? new List<LegacyAutoTagProfile>();
                foreach (var legacy in legacyProfiles)
                {
                    var name = legacy.Name?.Trim();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var exists = profiles.Any(profile =>
                        string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (exists)
                    {
                        continue;
                    }

                    profiles.Add(new TaggingProfile
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Name = name,
                        IsDefault = profiles.Count == 0,
                        AutoTag = ParseLegacyAutoTagSettings(legacy.Config)
                    });
                    imported = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to import legacy AutoTag profiles from {LegacyPath}.", _legacyProfilesPath);
            }
        }

        try
        {
            File.Delete(_legacyProfilesPath);
            _logger.LogInformation(
                "Removed legacy AutoTag profile store {LegacyPath} after single-store migration (imported={Imported}).",
                _legacyProfilesPath,
                imported);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to remove legacy AutoTag profile store {LegacyPath}.", _legacyProfilesPath);
        }

        return imported;
    }

    private static AutoTagSettings ParseLegacyAutoTagSettings(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object)
        {
            return new AutoTagSettings();
        }

        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(config.GetRawText())
                ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            return new AutoTagSettings
            {
                Data = data
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return new AutoTagSettings();
        }
    }

    private async Task TryMigrateLegacyProfilesAsync()
    {
        if (File.Exists(_profilesPath))
        {
            return;
        }

        foreach (var legacyPath in GetLegacyProfileCandidates())
        {
            if (!File.Exists(legacyPath))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_profilesPath)!);
                await using var source = File.OpenRead(legacyPath);
                await using var target = File.Create(_profilesPath);
                await source.CopyToAsync(target);
                _logger.LogInformation("Migrated tagging profiles from legacy path {LegacyPath} to {ProfilesPath}.", legacyPath, _profilesPath);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed migrating tagging profiles from legacy path {LegacyPath}.", legacyPath);
            }
        }
    }

    private Task CleanupLegacyPrimaryStoreCandidatesAsync()
    {
        if (!File.Exists(_profilesPath))
        {
            return Task.CompletedTask;
        }

        foreach (var legacyPath in GetLegacyProfileCandidates()
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(legacyPath))
            {
                continue;
            }

            try
            {
                File.Delete(legacyPath);
                _logger.LogInformation(
                    "Removed legacy tagging profile store {LegacyPath} after single-store migration.",
                    legacyPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to remove legacy tagging profile store {LegacyPath}.", legacyPath);
            }
        }

        return Task.CompletedTask;
    }

    private bool ShouldCleanupLegacyPrimaryStores()
    {
        // Only run destructive legacy cleanup when using one of the built-in data roots.
        // With custom roots (for example Docker /data), legacy candidate paths may be bind-mount
        // aliases to the same physical file and deleting them can remove the active profile store.
        var dataRootFullPath = Path.GetFullPath(_dataRoot);
        var defaultRoots = new[]
        {
            Path.Join(_contentRoot, "Data"),
            Path.Join(AppContext.BaseDirectory, "Data"),
            Path.Join(Directory.GetCurrentDirectory(), "Data"),
            Path.Join(Directory.GetCurrentDirectory(), "DeezSpoTag.Web", "Data")
        };

        return defaultRoots.Any(candidate =>
            string.Equals(Path.GetFullPath(candidate), dataRootFullPath, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<string> GetLegacyProfileCandidates()
        => AutoTagLegacyPathCandidates.Enumerate(_contentRoot, _dataRoot, _profilesPath, ProfilesFileName, AutoTagFolderName);

    private sealed class LegacyAutoTagProfile
    {
        public string Name { get; set; } = string.Empty;
        public JsonElement Config { get; set; } = default;
    }
}
