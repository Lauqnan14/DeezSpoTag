using System.Text.Json;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Settings;

public sealed class PlatformCapabilitiesStore
{
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
        var root = ResolveDataRoot(dataRoot);
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

        var normalized = platformId.Trim().ToLowerInvariant();
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
            return snapshot ?? new PlatformCapabilitiesSnapshot();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed reading platform capabilities; starting fresh.");
            return new PlatformCapabilitiesSnapshot();
        }
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

    private static string ResolveDataRoot(string? dataRoot)
    {
        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            return dataRoot.Trim();
        }

        var configDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            return configDir.Trim();
        }

        var dataDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            return dataDir.Trim();
        }

        return Path.Join(AppContext.BaseDirectory, "Data");
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
