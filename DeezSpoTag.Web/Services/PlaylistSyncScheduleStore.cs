using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services;

/// <summary>How often one library playlist is synced. Manual means only on request.</summary>
public enum PlaylistSyncCadence
{
    /// <summary>Never on a schedule. The default, so nothing syncs until a user asks for it.</summary>
    Manual = 0,

    Every15Minutes = 15,
    Hourly = 60,
    Every6Hours = 360,
    Daily = 1440,
}

/// <summary>One playlist's schedule and its last outcome, as stored.</summary>
public sealed class PlaylistSyncScheduleEntry
{
    public string SourceService { get; set; } = string.Empty;

    public string SourcePlaylistId { get; set; } = string.Empty;

    public PlaylistSyncCadence Cadence { get; set; } = PlaylistSyncCadence.Manual;

    /// <summary>The platforms this playlist is mirrored to. Empty means the user's choice was never made.</summary>
    public List<string> Targets { get; set; } = new();

    /// <summary>
    /// Tracks that are never copied out of this playlist to any destination.
    /// <para>
    /// Not the watchlist blocklist. That one stops a track being added to a playlist; this stops a
    /// track being copied out of one that already exists. Keeping them in separate stores is what
    /// stops a user excluding a track from one playlist's sync and unknowingly blocking that track
    /// everywhere else in the app.
    /// </para>
    /// </summary>
    public List<string> ExcludedTrackIds { get; set; } = new();

    /// <summary>
    /// Cover art as a data URL, or null when the playlist has none of its own. Kept inline rather
    /// than as a file path so the value cannot point outside the app's data directory.
    /// </summary>
    public string? Artwork { get; set; }

    /// <summary>The description sent to each destination on a sync.</summary>
    public string? Description { get; set; }

    public DateTimeOffset? LastRunAtUtc { get; set; }

    public string? LastRunMessage { get; set; }

    public bool LastRunSucceeded { get; set; }

    /// <summary>
    /// When the next pass becomes due. Stored rather than recomputed from LastRunAtUtc so a
    /// restart cannot shift the cadence, and so a pass that failed does not immediately retry in a
    /// tight loop.
    /// </summary>
    public DateTimeOffset? NextRunAtUtc { get; set; }

    public string Key => $"{SourceService}:{SourcePlaylistId}";
}

/// <summary>
/// The per-playlist sync schedule, persisted as JSON under the app data directory.
/// <para>
/// Modelled on <c>MelodaySettingsStore</c>: load, cache, and write atomically, and treat a corrupt
/// file as "no schedule" rather than as a fatal error. A schedule is a convenience - losing it means
/// playlists stop syncing on their own, which is a far better outcome than an app that will not
/// start.
/// </para>
/// <para>
/// Every read goes through the cache and every write goes through a semaphore, so a scheduled pass
/// and a settings change in the UI cannot interleave and lose an entry.
/// </para>
/// </summary>
public sealed class PlaylistSyncScheduleStore
{
    private readonly string _schedulePath;
    private readonly ILogger<PlaylistSyncScheduleStore> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _changeSignal = new(0, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private IReadOnlyList<PlaylistSyncScheduleEntry>? _cached;

    public PlaylistSyncScheduleStore(IWebHostEnvironment env, ILogger<PlaylistSyncScheduleStore> logger)
    {
        _logger = logger;
        var dataDir = Path.Join(AppDataPaths.GetDataRoot(env), "playlist-sync");
        Directory.CreateDirectory(dataDir);
        _schedulePath = Path.Join(dataDir, "schedule.json");
    }

    /// <summary>Raised after any write, so a waiting scheduler can re-read the schedule promptly.</summary>
    public Task WaitForChangeAsync(CancellationToken cancellationToken)
        => _changeSignal.WaitAsync(cancellationToken);

    public async Task<IReadOnlyList<PlaylistSyncScheduleEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            return await ReadFromDiskAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Reads the cache or the file, populating the cache. The caller must hold
    /// <see cref="_writeGate"/>, which is why this is separate from <see cref="LoadAsync"/>: the
    /// mutating paths already hold the gate and a SemaphoreSlim is not reentrant, so calling
    /// LoadAsync from one of them would deadlock.
    /// </summary>
    private async Task<IReadOnlyList<PlaylistSyncScheduleEntry>> ReadFromDiskAsync(
        CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        if (!File.Exists(_schedulePath))
        {
            _cached = Array.Empty<PlaylistSyncScheduleEntry>();
            return _cached;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_schedulePath, cancellationToken);
            var stored = JsonSerializer.Deserialize<List<PlaylistSyncScheduleEntry>>(json, _jsonOptions);
            _cached = (stored ?? new List<PlaylistSyncScheduleEntry>())
                .Where(static entry => !string.IsNullOrWhiteSpace(entry.SourceService)
                    && !string.IsNullOrWhiteSpace(entry.SourcePlaylistId))
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A schedule is a convenience. An unreadable file means playlists stop syncing on their
            // own, which is recoverable; refusing to start is not.
            _logger.LogWarning(ex, "Playlist sync schedule could not be read; treating it as empty.");
            _cached = Array.Empty<PlaylistSyncScheduleEntry>();
        }

        return _cached;
    }

    public async Task<PlaylistSyncScheduleEntry?> GetAsync(
        string sourceService,
        string sourcePlaylistId,
        CancellationToken cancellationToken = default)
    {
        var entries = await LoadAsync(cancellationToken);
        var key = BuildKey(sourceService, sourcePlaylistId);
        return entries.FirstOrDefault(entry =>
            string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Sets one playlist's cadence and destinations, creating the entry when it is new.
    /// <para>
    /// Switching a playlist back to <see cref="PlaylistSyncCadence.Manual"/> clears the next-run
    /// time so a scheduler that is mid-wait does not start a pass for a schedule the user has just
    /// cancelled.
    /// </para>
    /// </summary>
    public async Task<PlaylistSyncScheduleEntry> SaveAsync(
        string sourceService,
        string sourcePlaylistId,
        PlaylistSyncCadence cadence,
        IReadOnlyCollection<string>? targets,
        IReadOnlyCollection<string>? excludedTrackIds = null,
        string? artworkDataUrl = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceService) || string.IsNullOrWhiteSpace(sourcePlaylistId))
        {
            throw new ArgumentException("A schedule needs a source service and playlist id.");
        }

        if (!Enum.IsDefined(cadence))
        {
            throw new ArgumentOutOfRangeException(
                nameof(cadence), cadence, "That cadence is not one this app offers.");
        }

        // The browser checks these too, but the server is the only place they are enforced for a
        // caller that is not the browser. An oversized or unsupported upload is rejected here rather
        // than written into the schedule file and failing on every later pass.
        var artwork = ValidateArtwork(artworkDataUrl);

        var key = BuildKey(sourceService, sourcePlaylistId);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var entries = ((await ReadFromDiskAsync(cancellationToken)).ToList());
            var index = entries.FindIndex(entry =>
                string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));

            var entry = index >= 0 ? entries[index] : new PlaylistSyncScheduleEntry
            {
                SourceService = sourceService.Trim().ToLowerInvariant(),
                SourcePlaylistId = sourcePlaylistId.Trim(),
            };

            entry.Cadence = cadence;
            entry.Targets = (targets ?? Array.Empty<string>())
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToList();

            entry.ExcludedTrackIds = (excludedTrackIds ?? Array.Empty<string>())
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToList();

            if (artwork is not null)
            {
                entry.Artwork = artwork;
            }
            else if (!string.IsNullOrWhiteSpace(artworkDataUrl))
            {
                throw new ArgumentException("That artwork could not be accepted.", nameof(artworkDataUrl));
            }

            if (description is not null)
            {
                entry.Description = description.Trim();
            }

            if (cadence == PlaylistSyncCadence.Manual)
            {
                entry.NextRunAtUtc = null;
            }
            else if (entry.NextRunAtUtc is null || entry.NextRunAtUtc <= DateTimeOffset.UtcNow)
            {
                entry.NextRunAtUtc = NextBoundary(DateTimeOffset.UtcNow, cadence);
            }

            if (index >= 0)
            {
                entries[index] = entry;
            }
            else
            {
                entries.Add(entry);
            }

            _cached = entries;
            await FlushAsync(cancellationToken);
            SignalChange();
            return entry;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Accepts an artwork data URL, or returns null when none was supplied.
    /// <para>
    /// A data URL rather than an upload stream: the value is validated for type and size here and
    /// then persisted alongside the rest of the schedule, so a later scheduled pass has everything
    /// it needs without a second fetch. The 15MB ceiling is for the animated formats - a still JPEG
    /// is orders of magnitude smaller, and rejecting a legitimate animated cover because of a limit
    /// sized for a photograph would be the wrong trade.
    /// </para>
    /// </summary>
    internal static string? ValidateArtwork(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl))
        {
            return null;
        }

        var trimmed = dataUrl.Trim();
        const string prefix = "data:";
        var commaIndex = trimmed.IndexOf(',', StringComparison.Ordinal);
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || commaIndex <= prefix.Length)
        {
            throw new ArgumentException("That artwork is not a data URL.", nameof(dataUrl));
        }

        var metadata = trimmed[prefix.Length..commaIndex];
        var parts = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var contentType = parts.FirstOrDefault(
            static part => part.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
        if (!PlaylistSyncService.IsAllowedMergeArtworkContentType(contentType)
            || !parts.Any(static part => part.Equals("base64", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Artwork must be a JPEG, PNG, WebP or GIF image.", nameof(dataUrl));
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(trimmed[(commaIndex + 1)..]);
        }
        catch (FormatException)
        {
            throw new ArgumentException("That artwork could not be read.", nameof(dataUrl));
        }

        if (bytes.Length is 0 || bytes.Length > PlaylistSyncService.MaxArtworkBytes)
        {
            throw new ArgumentException(
                $"Artwork must be between 1 byte and {PlaylistSyncService.MaxArtworkBytes / (1024 * 1024)} MB.",
                nameof(dataUrl));
        }

        return trimmed;
    }

    /// <summary>
    /// Records the outcome of a pass and schedules the next one.
    /// <para>
    /// A failed pass is rescheduled like a successful one rather than retried immediately. Playlist
    /// writes are destructive when they are wrong, and a destination that is refusing requests
    /// usually stays refusing for a while; retrying in a tight loop would hammer it and bury the
    /// user in failures.
    /// </para>
    /// </summary>
    public async Task RecordRunAsync(
        PlaylistSyncScheduleEntry entry,
        bool succeeded,
        string? message,
        CancellationToken cancellationToken = default)
    {
        if (entry is null || entry.Cadence == PlaylistSyncCadence.Manual)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            entry.LastRunAtUtc = DateTimeOffset.UtcNow;
            entry.LastRunSucceeded = succeeded;
            entry.LastRunMessage = message;

            // Advance past the occurrence that was just served, rather than recomputing from the
            // current time. Recomputing looks equivalent but is not: a pass that starts a moment
            // before its boundary would be assigned that same boundary as its new due time, so the
            // entry is immediately due again and the pass runs twice for one interval.
            var cadence = entry.Cadence;
            var advanced = entry.NextRunAtUtc is { } due
                ? NextBoundary(due, cadence)
                : NextBoundary(DateTimeOffset.UtcNow, cadence);
            entry.NextRunAtUtc = advanced;
            _cached = (await ReadFromDiskAsync(cancellationToken))
                .Select(existing => string.Equals(existing.Key, entry.Key, StringComparison.OrdinalIgnoreCase)
                    ? entry
                    : existing)
                .ToList();
            await FlushAsync(cancellationToken);
            SignalChange();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task RemoveAsync(string sourceService, string sourcePlaylistId, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(sourceService, sourcePlaylistId);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            _cached = (await ReadFromDiskAsync(cancellationToken))
                .Where(entry => !string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
                .ToList();
            await FlushAsync(cancellationToken);
            SignalChange();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// The next wall-clock boundary that is a whole multiple of the interval.
    /// <para>
    /// Aligning to absolute boundaries rather than to "interval since the last run" is what makes the
    /// cadence predictable and restart-proof: a 6-hour schedule lands on the same six-hour marks
    /// whether or not the app was running when they passed. Landing exactly on a boundary yields a
    /// full interval, so a pass cannot fire twice for one boundary.
    /// </para>
    /// </summary>
    internal static DateTimeOffset NextBoundary(DateTimeOffset now, PlaylistSyncCadence cadence)
    {
        if (cadence == PlaylistSyncCadence.Manual)
        {
            return DateTimeOffset.MaxValue;
        }

        var minutes = (long)cadence;
        var epochMinutes = now.ToUnixTimeSeconds() / 60;
        var next = ((epochMinutes / minutes) + 1) * minutes;
        return DateTimeOffset.FromUnixTimeSeconds(next * 60);
    }

    private static string BuildKey(string sourceService, string sourcePlaylistId)
        => $"{sourceService?.Trim().ToLowerInvariant()}:{sourcePlaylistId?.Trim()}";

    private void SignalChange()
    {
        // Release only if a waiter has not already been signalled, so a burst of writes does not
        // build up a backlog the scheduler has to drain one item at a time.
        if (_changeSignal.CurrentCount == 0)
        {
            try
            {
                _changeSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // A waiter arrived between the check and the release. Nothing to do.
            }
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(_cached ?? Array.Empty<PlaylistSyncScheduleEntry>(), _jsonOptions);
            var temp = _schedulePath + ".tmp";
            await File.WriteAllTextAsync(temp, json, cancellationToken);
            // Written to a sibling and moved into place, so a crash mid-write cannot leave a
            // half-written schedule that fails to parse on the next start.
            File.Move(temp, _schedulePath, overwrite: true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Playlist sync schedule could not be written to {Path}.", _schedulePath);
        }
    }
}
