using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace DeezSpoTag.Web.Services.Notifications;

public sealed class NotificationStore
{
    private const string NotificationsFolderName = "notifications";
    private const string EntriesFileName = "entries.json";
    private const string PreferencesFileName = "preferences.json";
    private const int MaxEntries = 500;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _entriesPath;
    private readonly string _preferencesPath;
    private readonly ILogger<NotificationStore> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public NotificationStore(IWebHostEnvironment env, ILogger<NotificationStore> logger)
    {
        _logger = logger;
        var dataDir = Path.Join(AppDataPaths.GetDataRoot(env), NotificationsFolderName);
        Directory.CreateDirectory(dataDir);
        _entriesPath = Path.Join(dataDir, EntriesFileName);
        _preferencesPath = Path.Join(dataDir, PreferencesFileName);
    }

    public async Task<NotificationPreferences> LoadPreferencesAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var preferences = ReadPreferences();
            preferences.EnsureDefaults();
            return preferences;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NotificationPreferences> SavePreferencesAsync(NotificationPreferences preferences)
    {
        await _gate.WaitAsync();
        try
        {
            preferences.EnsureDefaults();
            await File.WriteAllTextAsync(_preferencesPath, JsonSerializer.Serialize(preferences, _jsonOptions));
            return preferences;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<NotificationEntry>> GetAsync(bool unreadOnly = false, int limit = 100)
    {
        await _gate.WaitAsync();
        try
        {
            var entries = ReadEntries();
            return entries
                .Where(entry => !unreadOnly || !entry.IsRead)
                .OrderByDescending(entry => entry.LastSeenUtc)
                .Take(Math.Clamp(limit, 1, MaxEntries))
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> GetUnreadCountAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return ReadEntries().Count(entry => !entry.IsRead);
        }
        finally
        {
            _gate.Release();
        }
    }

    public sealed record AddResult(NotificationEntry Entry, bool IsNewIncident);

    public sealed record ResolveResult(bool HadOpenIncident, bool ManuallyResolved);

    /// <summary>
    /// Closes any open incident for the key. Manual resolution records that the user acted, so a
    /// later recovery is not announced back to them.
    /// </summary>
    public async Task<ResolveResult> ResolveIncidentAsync(string dedupeKey, bool manuallyResolved)
    {
        await _gate.WaitAsync();
        try
        {
            var entries = ReadEntries();
            var now = DateTimeOffset.UtcNow;
            var closed = 0;
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (!entry.IsOpen || !string.Equals(entry.DedupeKey, dedupeKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                entries[index] = entry with { ResolvedUtc = now, ManuallyResolved = manuallyResolved };
                closed++;
            }

            if (closed > 0)
            {
                await WriteEntriesAsync(entries);
            }

            return new ResolveResult(closed > 0, manuallyResolved);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AddResult> AddOrCoalesceAsync(NotificationRequest request, int retentionDays)
    {
        await _gate.WaitAsync();
        try
        {
            var entries = ReadEntries();
            var dedupeKey = string.IsNullOrWhiteSpace(request.DedupeKey)
                ? $"{request.Kind}:{request.EntityType}:{request.EntityId}"
                : request.DedupeKey.Trim();
            var now = DateTimeOffset.UtcNow;

            // Coalesce against the open incident, not the unread flag: a condition that keeps
            // failing must not re-announce itself just because the user cleared the last alert.
            var existing = entries.FirstOrDefault(entry =>
                string.Equals(entry.DedupeKey, dedupeKey, StringComparison.OrdinalIgnoreCase)
                && entry.IsOpen);

            NotificationEntry result;
            var isNewIncident = existing is null;
            if (existing is not null)
            {
                result = existing with
                {
                    Title = request.Title,
                    Body = request.Body,
                    Severity = request.Severity,
                    OccurrenceCount = existing.OccurrenceCount + 1,
                    LastSeenUtc = now
                };
                entries[entries.IndexOf(existing)] = result;
            }
            else
            {
                result = new NotificationEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Kind = request.Kind,
                    DedupeKey = dedupeKey,
                    Severity = request.Severity,
                    Title = request.Title,
                    Body = request.Body,
                    EntityType = request.EntityType,
                    EntityId = request.EntityId,
                    Link = request.Link,
                    CreatedUtc = now,
                    LastSeenUtc = now
                };
                entries.Add(result);
            }

            await WriteEntriesAsync(Prune(entries, retentionDays));
            return new AddResult(result, isNewIncident);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> MarkReadAsync(IReadOnlyCollection<string> ids)
    {
        await _gate.WaitAsync();
        try
        {
            var entries = ReadEntries();
            var now = DateTimeOffset.UtcNow;
            var changed = 0;
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (entry.IsRead || !ids.Contains(entry.Id, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                entries[index] = entry with
                {
                    ReadUtc = now,
                    ResolvedUtc = entry.ResolvedUtc ?? now,
                    ManuallyResolved = entry.ManuallyResolved
                };
                changed++;
            }

            if (changed > 0)
            {
                await WriteEntriesAsync(entries);
            }

            return changed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> RemoveAsync(IReadOnlyCollection<string> ids)
    {
        await _gate.WaitAsync();
        try
        {
            var entries = ReadEntries();
            var removed = entries.RemoveAll(entry => ids.Contains(entry.Id, StringComparer.OrdinalIgnoreCase));
            if (removed > 0)
            {
                await WriteEntriesAsync(entries);
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> ClearAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var removed = ReadEntries().Count;
            if (removed > 0)
            {
                await WriteEntriesAsync([]);
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> MarkAllReadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var entries = ReadEntries();
            var now = DateTimeOffset.UtcNow;
            var changed = 0;
            for (var index = 0; index < entries.Count; index++)
            {
                if (entries[index].IsRead)
                {
                    continue;
                }

                entries[index] = entries[index] with
                {
                    ReadUtc = now,
                    ResolvedUtc = entries[index].ResolvedUtc ?? now
                };
                changed++;
            }

            if (changed > 0)
            {
                await WriteEntriesAsync(entries);
            }

            return changed;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static List<NotificationEntry> Prune(List<NotificationEntry> entries, int retentionDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, retentionDays));
        return entries
            .Where(entry => !entry.IsRead || entry.LastSeenUtc >= cutoff)
            .OrderByDescending(entry => entry.LastSeenUtc)
            .Take(MaxEntries)
            .ToList();
    }

    private NotificationPreferences ReadPreferences()
    {
        try
        {
            if (!File.Exists(_preferencesPath))
            {
                return NotificationPreferences.CreateDefault();
            }

            var json = File.ReadAllText(_preferencesPath);
            return string.IsNullOrWhiteSpace(json)
                ? NotificationPreferences.CreateDefault()
                : JsonSerializer.Deserialize<NotificationPreferences>(json, _jsonOptions) ?? NotificationPreferences.CreateDefault();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Failed reading notification preferences; using defaults.");
            return NotificationPreferences.CreateDefault();
        }
    }

    private List<NotificationEntry> ReadEntries()
    {
        try
        {
            if (!File.Exists(_entriesPath))
            {
                return [];
            }

            var json = File.ReadAllText(_entriesPath);
            return string.IsNullOrWhiteSpace(json)
                ? []
                : JsonSerializer.Deserialize<List<NotificationEntry>>(json, _jsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Failed reading notifications; starting from an empty list.");
            return [];
        }
    }

    private async Task WriteEntriesAsync(List<NotificationEntry> entries)
        => await File.WriteAllTextAsync(_entriesPath, JsonSerializer.Serialize(entries, _jsonOptions));
}
