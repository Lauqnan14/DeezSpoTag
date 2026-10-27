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
        await _gate.WaitAsync().ConfigureAwait(false);
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
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            preferences.EnsureDefaults();
            await WriteJsonAtomicallyAsync(_preferencesPath, preferences).ConfigureAwait(false);
            return preferences;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<NotificationEntry>> GetAsync(bool unreadOnly = false, int limit = 100)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return ReadInAppVisibleEntries()
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
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return ReadInAppVisibleEntries().Count(entry => !entry.IsRead);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Entries persist regardless of channel preference so webhook delivery keeps its dedupe
    /// history, but a kind with the "in-app" channel turned off must never surface here: the
    /// toggle otherwise only silences the live push, and the stored entry would still show up the
    /// next time the panel is opened.
    /// </summary>
    private IEnumerable<NotificationEntry> ReadInAppVisibleEntries()
    {
        var preferences = ReadPreferences();
        preferences.EnsureDefaults();
        return ReadEntries().Where(entry => !entry.DismissedUtc.HasValue && preferences.Resolve(entry.Kind).InApp);
    }

    public sealed record AddResult(NotificationEntry Entry, bool IsNewIncident);

    public sealed record ResolveResult(bool HadOpenIncident, bool ManuallyResolved, string? IncidentId = null);

    /// <summary>
    /// Closes any open incident for the key. Manual resolution records that the user acted, so a
    /// later recovery is not announced back to them.
    /// </summary>
    public async Task<ResolveResult> ResolveIncidentAsync(string dedupeKey, bool manuallyResolved)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entries = ReadEntries();
            var now = DateTimeOffset.UtcNow;
            var closed = 0;
            string? incidentId = null;
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (!entry.IsOpen || !string.Equals(entry.DedupeKey, dedupeKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                entries[index] = entry with { ResolvedUtc = now, ManuallyResolved = manuallyResolved };
                incidentId = entry.Id;
                closed++;
            }

            if (closed > 0)
            {
                await WriteEntriesAsync(entries).ConfigureAwait(false);
            }

            return new ResolveResult(closed > 0, manuallyResolved, incidentId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AddResult> AddOrCoalesceAsync(NotificationRequest request, int retentionDays, bool webhookRequested = false)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
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
                    OccurrenceCount = request.Kind == NotificationKinds.VerificationRequired ? 1 : existing.OccurrenceCount + 1,
                    LastSeenUtc = now,
                    WebhookPending = existing.WebhookPending || (webhookRequested && !existing.WebhookDeliveredUtc.HasValue),
                    WebhookAttempts = webhookRequested && !existing.WebhookPending && !existing.WebhookDeliveredUtc.HasValue
                        ? 0 : existing.WebhookAttempts,
                    WebhookNextAttemptUtc = webhookRequested && !existing.WebhookPending && !existing.WebhookDeliveredUtc.HasValue
                        ? now : existing.WebhookNextAttemptUtc
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
                    LastSeenUtc = now,
                    WebhookPending = webhookRequested,
                    WebhookNextAttemptUtc = webhookRequested ? now : null
                };
                entries.Add(result);
            }

            await WriteEntriesAsync(Prune(entries, retentionDays)).ConfigureAwait(false);
            return new AddResult(result, isNewIncident);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> MarkReadAsync(IReadOnlyCollection<string> ids)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
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
                    ResolvedUtc = entry.Kind == NotificationKinds.VerificationRequired ? entry.ResolvedUtc : entry.ResolvedUtc ?? now,
                    ManuallyResolved = entry.ManuallyResolved
                };
                changed++;
            }

            if (changed > 0)
            {
                await WriteEntriesAsync(entries).ConfigureAwait(false);
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
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entries = ReadEntries();
            var removed = DismissEntries(entries, entry => ids.Contains(entry.Id, StringComparer.OrdinalIgnoreCase));
            if (removed > 0)
            {
                await WriteEntriesAsync(entries).ConfigureAwait(false);
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
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entries = ReadEntries();
            var removed = DismissEntries(entries, _ => true);
            if (removed > 0)
            {
                await WriteEntriesAsync(entries).ConfigureAwait(false);
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
        await _gate.WaitAsync().ConfigureAwait(false);
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
                    ResolvedUtc = entries[index].Kind == NotificationKinds.VerificationRequired
                        ? entries[index].ResolvedUtc : entries[index].ResolvedUtc ?? now
                };
                changed++;
            }

            if (changed > 0)
            {
                await WriteEntriesAsync(entries).ConfigureAwait(false);
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
        // An unresolved verification remains deduplicated even after dismissal or retention expiry.
        var pendingVerification = entries.Where(entry => IsPendingVerification(entry) || entry.WebhookPending).ToList();
        return pendingVerification.Concat(entries
            .Where(entry => !IsPendingVerification(entry) && !entry.WebhookPending)
            .Where(entry => !entry.IsRead || entry.LastSeenUtc >= cutoff)
            .OrderByDescending(entry => entry.LastSeenUtc)
            .Take(Math.Max(0, MaxEntries - pendingVerification.Count)))
            .ToList();
    }

    private static bool IsPendingVerification(NotificationEntry entry)
        => entry.Kind == NotificationKinds.VerificationRequired && entry.IsOpen;

    private static int DismissEntries(List<NotificationEntry> entries, Func<NotificationEntry, bool> matches)
    {
        var count = entries.Count(entry => !entry.DismissedUtc.HasValue && matches(entry));
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (matches(entry) && IsPendingVerification(entry))
                entries[index] = entry with { DismissedUtc = entry.DismissedUtc ?? now, ReadUtc = entry.ReadUtc ?? now };
        }
        entries.RemoveAll(entry => matches(entry) && !IsPendingVerification(entry));
        return count;
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
            return JsonSerializer.Deserialize<NotificationPreferences>(json, _jsonOptions)
                ?? throw new JsonException("Notification preferences cannot be null.");
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogError(ex, "Cannot read notification preferences; preserving the saved file.");
            throw;
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
            return JsonSerializer.Deserialize<List<NotificationEntry>>(json, _jsonOptions)
                ?? throw new JsonException("Notification entries cannot be null.");
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogError(ex, "Cannot read notifications; preserving the saved file.");
            throw;
        }
    }

    public async Task<IReadOnlyList<NotificationEntry>> GetPendingWebhooksAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return ReadEntries().Where(entry => entry.WebhookPending).ToList(); }
        finally { _gate.Release(); }
    }

    public async Task RecordWebhookAttemptAsync(string id, bool delivered, TimeSpan? retryDelay)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entries = ReadEntries();
            var index = entries.FindIndex(entry => entry.Id == id);
            if (index < 0) return;
            var now = DateTimeOffset.UtcNow;
            entries[index] = entries[index] with
            {
                WebhookAttempts = entries[index].WebhookAttempts + 1,
                WebhookPending = !delivered && retryDelay.HasValue,
                WebhookNextAttemptUtc = !delivered && retryDelay.HasValue ? now + retryDelay.Value : null,
                WebhookDeliveredUtc = delivered ? now : entries[index].WebhookDeliveredUtc
            };
            await WriteEntriesAsync(entries).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private Task WriteEntriesAsync(List<NotificationEntry> entries)
        => WriteJsonAtomicallyAsync(_entriesPath, entries);

    private async Task WriteJsonAtomicallyAsync<T>(string path, T value)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(value, _jsonOptions)).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
