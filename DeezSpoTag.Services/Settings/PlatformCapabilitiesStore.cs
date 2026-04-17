using System.Text.Json;
using System.Linq;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Settings;

public sealed class PlatformCapabilitiesStore
{
    private const string ITunesPlatform = "itunes";
    private const string LRCLibPlatform = "lrclib";
    private static readonly Dictionary<string, string> PlatformIdAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["apple"] = ITunesPlatform,
        ["applemusic"] = ITunesPlatform,
        ["apple-music"] = ITunesPlatform,
        ["apple_music"] = ITunesPlatform,
        ["music.apple"] = ITunesPlatform,
        ["lrcget"] = LRCLibPlatform,
        ["lrc-get"] = LRCLibPlatform,
        ["lrc_get"] = LRCLibPlatform
    };

    private readonly string _path;
    private readonly ILogger<PlatformCapabilitiesStore>? _logger;
    private readonly object _lock = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private readonly PlatformCapabilitiesSnapshot _snapshot;

    public PlatformCapabilitiesStore(string? dataRoot = null, ILogger<PlatformCapabilitiesStore>? logger = null)
    {
        _logger = logger;
        var root = DeezSpoTagDataRootResolver.Resolve(dataRoot);
        var dir = Path.Join(root, "autotag");
        Directory.CreateDirectory(dir);
        _path = Path.Join(dir, "platform-capabilities.json");
        _snapshot = LoadSnapshot();
    }

    public DateTimeOffset LastUpdated
    {
        get
        {
            lock (_lock)
            {
                return _snapshot.UpdatedAt;
            }
        }
    }

    public PlatformCapabilitiesSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return _snapshot.Clone();
        }
    }

    public void RecordDownloadTags(string? platformId, IEnumerable<string> tags)
    {
        UpdatePlatformTags(platformId, tags, isDownload: true);
    }

    public void RecordAutoTagTags(string? platformId, IEnumerable<string> tags)
    {
        UpdatePlatformTags(platformId, tags, isDownload: false);
    }

    private void UpdatePlatformTags(string? platformId, IEnumerable<string> tags, bool isDownload)
    {
        if (string.IsNullOrWhiteSpace(platformId))
        {
            return;
        }

        var normalized = NormalizePlatformId(platformId);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var normalizedTags = tags?
            .Select(t => t?.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToList() ?? new List<string>();

        if (normalizedTags.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            if (!_snapshot.Platforms.TryGetValue(normalized, out var entry))
            {
                entry = new PlatformCapabilitiesEntry();
                _snapshot.Platforms[normalized] = entry;
            }

            var changed = false;
            var target = isDownload ? entry.DownloadTags : entry.SupportedTags;
            foreach (var tag in normalizedTags)
            {
                if (target.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                target.Add(tag);
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            entry.UpdatedAt = DateTimeOffset.UtcNow;
            _snapshot.UpdatedAt = entry.UpdatedAt;
            SaveSnapshot(_snapshot);
        }
    }

    private PlatformCapabilitiesSnapshot LoadSnapshot()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new PlatformCapabilitiesSnapshot();
            }

            var json = File.ReadAllText(_path);
            var snapshot = JsonSerializer.Deserialize<PlatformCapabilitiesSnapshot>(json, _jsonOptions);
            return NormalizeSnapshot(snapshot ?? new PlatformCapabilitiesSnapshot());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed reading platform capabilities; starting fresh.");
            return new PlatformCapabilitiesSnapshot();
        }
    }

    private PlatformCapabilitiesSnapshot NormalizeSnapshot(PlatformCapabilitiesSnapshot snapshot)
    {
        if (snapshot.Platforms.Count == 0)
        {
            return snapshot;
        }

        var normalizedPlatforms = new Dictionary<string, PlatformCapabilitiesEntry>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var (platformId, entry) in snapshot.Platforms)
        {
            var normalizedId = NormalizePlatformId(platformId);
            if (string.IsNullOrWhiteSpace(normalizedId))
            {
                changed = true;
                continue;
            }

            if (!string.Equals(platformId, normalizedId, StringComparison.OrdinalIgnoreCase))
            {
                changed = true;
            }

            if (!normalizedPlatforms.TryGetValue(normalizedId, out var merged))
            {
                merged = entry.Clone();
                normalizedPlatforms[normalizedId] = merged;
                continue;
            }

            changed = true;
            MergeEntries(merged, entry);
        }

        if (!changed)
        {
            return snapshot;
        }

        snapshot.Platforms = normalizedPlatforms;
        SaveSnapshot(snapshot);
        return snapshot;
    }

    private static void MergeEntries(PlatformCapabilitiesEntry target, PlatformCapabilitiesEntry source)
    {
        target.UpdatedAt = target.UpdatedAt >= source.UpdatedAt ? target.UpdatedAt : source.UpdatedAt;
        foreach (var tag in source.DownloadTags)
        {
            if (target.DownloadTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            target.DownloadTags.Add(tag);
        }

        foreach (var tag in source.SupportedTags)
        {
            if (target.SupportedTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            target.SupportedTags.Add(tag);
        }
    }

    private static string NormalizePlatformId(string? platformId)
    {
        var normalized = platformId?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return PlatformIdAliases.TryGetValue(normalized, out var mapped)
            ? mapped
            : normalized;
    }

    private void SaveSnapshot(PlatformCapabilitiesSnapshot snapshot)
    {
        try
        {
            var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed writing platform capabilities.");
        }
    }

}

public sealed class PlatformCapabilitiesSnapshot
{
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.MinValue;
    public Dictionary<string, PlatformCapabilitiesEntry> Platforms { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public PlatformCapabilitiesSnapshot Clone()
    {
        var clone = new PlatformCapabilitiesSnapshot
        {
            UpdatedAt = UpdatedAt,
            Platforms = new Dictionary<string, PlatformCapabilitiesEntry>(StringComparer.OrdinalIgnoreCase)
        };

        foreach (var (key, value) in Platforms)
        {
            clone.Platforms[key] = value.Clone();
        }

        return clone;
    }
}

public sealed class PlatformCapabilitiesEntry
{
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.MinValue;
    public List<string> DownloadTags { get; set; } = new();
    public List<string> SupportedTags { get; set; } = new();

    public PlatformCapabilitiesEntry Clone()
    {
        return new PlatformCapabilitiesEntry
        {
            UpdatedAt = UpdatedAt,
            DownloadTags = DownloadTags.ToList(),
            SupportedTags = SupportedTags.ToList()
        };
    }
}
