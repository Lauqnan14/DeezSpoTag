using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DeezSpoTag.Web.Services.AutoTag;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Services;

public class AutoTagMetadataService
{
    private readonly ILogger<AutoTagMetadataService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly object _cacheLock = new();
    private string? _cachedJson;
    private DateTimeOffset _cacheExpiresAt = DateTimeOffset.MinValue;
    private DateTimeOffset _cachedCapabilitiesUpdatedAt = DateTimeOffset.MinValue;
    private readonly PortedPlatformRegistry _portedPlatforms;
    private readonly PlatformCapabilitiesStore _capabilitiesStore;

    public AutoTagMetadataService(IWebHostEnvironment env, PortedPlatformRegistry portedPlatforms, PlatformCapabilitiesStore capabilitiesStore, ILogger<AutoTagMetadataService> logger)
    {
        _logger = logger;
        _portedPlatforms = portedPlatforms;
        _capabilitiesStore = capabilitiesStore;
    }

    public Task<string?> GetPlatformsJsonAsync()
    {
        lock (_cacheLock)
        {
            if (_cachedJson != null
                && DateTimeOffset.UtcNow < _cacheExpiresAt
                && _cachedCapabilitiesUpdatedAt == _capabilitiesStore.LastUpdated)
            {
                return Task.FromResult<string?>(_cachedJson);
            }
        }

        try
        {
            var merged = MergePortedPlatforms(null, _capabilitiesStore.GetSnapshot());
            if (string.IsNullOrWhiteSpace(merged))
            {
                _logger.LogWarning("Platform metadata unavailable.");
                return Task.FromResult<string?>(null);
            }

            JsonSerializer.Deserialize<JsonElement>(merged, _jsonOptions);

            lock (_cacheLock)
            {
                _cachedJson = merged;
                _cacheExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
                _cachedCapabilitiesUpdatedAt = _capabilitiesStore.LastUpdated;
            }

            return Task.FromResult<string?>(merged);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to read platform metadata.");
            return Task.FromResult<string?>(null);
        }
    }

    private static readonly Dictionary<string, SupportedTag> SupportedTagLookup = CreateSupportedTagLookup();

    private static Dictionary<string, SupportedTag> CreateSupportedTagLookup()
    {
        var lookup = new Dictionary<string, SupportedTag>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = SupportedTag.Title,
            ["artist"] = SupportedTag.Artist,
            ["albumArtist"] = SupportedTag.AlbumArtist,
            ["album"] = SupportedTag.Album,
            ["albumArt"] = SupportedTag.AlbumArt,
            ["version"] = SupportedTag.Version,
            ["remixer"] = SupportedTag.Remixer,
            ["genre"] = SupportedTag.Genre,
            ["style"] = SupportedTag.Style,
            ["label"] = SupportedTag.Label,
            ["releaseId"] = SupportedTag.ReleaseId,
            ["trackId"] = SupportedTag.TrackId
        };

        SupportedTagFeatureMappings.AddAudioFeatureTags(lookup);

        lookup["catalogNumber"] = SupportedTag.CatalogNumber;
        lookup["trackNumber"] = SupportedTag.TrackNumber;
        lookup["discNumber"] = SupportedTag.DiscNumber;
        lookup["duration"] = SupportedTag.Duration;
        lookup["trackTotal"] = SupportedTag.TrackTotal;
        lookup["isrc"] = SupportedTag.ISRC;
        lookup["publishDate"] = SupportedTag.PublishDate;
        lookup["releaseDate"] = SupportedTag.ReleaseDate;
        lookup["url"] = SupportedTag.URL;
        lookup["otherTags"] = SupportedTag.OtherTags;
        lookup["metaTags"] = SupportedTag.MetaTags;
        lookup["unsyncedLyrics"] = SupportedTag.UnsyncedLyrics;
        lookup["syncedLyrics"] = SupportedTag.SyncedLyrics;
        lookup["ttmlLyrics"] = SupportedTag.TtmlLyrics;
        lookup["explicit"] = SupportedTag.Explicit;
        return lookup;
    }

    private string? MergePortedPlatforms(string? nativeJson, PlatformCapabilitiesSnapshot? snapshot) // NOSONAR
    {
        var ported = _portedPlatforms.DescribeAll();
        if (ported.Count == 0)
        {
            return nativeJson;
        }

        var array = new JsonArray();
        AppendNativePlatforms(array, nativeJson, ported);
        AppendPortedPlatforms(array, ported, snapshot);

        return array.ToJsonString(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        });
    }

    private static void AppendNativePlatforms(
        JsonArray array,
        string? nativeJson,
        IReadOnlyList<AutoTagPlatformDescriptor> ported)
    {
        if (string.IsNullOrWhiteSpace(nativeJson) || JsonNode.Parse(nativeJson) is not JsonArray nativeArray)
        {
            return;
        }

        foreach (var node in nativeArray)
        {
            if (node is not JsonObject obj)
            {
                continue;
            }

            var id = obj["id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id)
                && ported.Any(platform => string.Equals(platform.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            array.Add(obj);
        }
    }

    private void AppendPortedPlatforms(
        JsonArray array,
        IEnumerable<AutoTagPlatformDescriptor> ported,
        PlatformCapabilitiesSnapshot? snapshot)
    {
        foreach (var platform in ported)
        {
            if (snapshot != null && snapshot.Platforms.TryGetValue(platform.Id, out var entry))
            {
                ApplyCapabilities(platform, entry);
            }

            array.Add(JsonSerializer.SerializeToNode(platform, _jsonOptions));
        }
    }

    private static void ApplyCapabilities(AutoTagPlatformDescriptor platform, PlatformCapabilitiesEntry entry)
    {
        if (entry.DownloadTags.Count > 0)
        {
            var downloadTags = entry.DownloadTags
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (downloadTags.Count > 0)
            {
                var mergedDownloadTags = MergeDownloadTags(platform, downloadTags);
                platform.DownloadTags = mergedDownloadTags;
                platform.Platform.DownloadTags = mergedDownloadTags;
            }
        }

        if (entry.SupportedTags.Count > 0)
        {
            var supportedTags = entry.SupportedTags
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => SupportedTagLookup.TryGetValue(t.Trim(), out var mapped) ? mapped : (SupportedTag?)null)
                .Where(t => t.HasValue)
                .Select(t => t!.Value)
                .Distinct()
                .ToList();

            if (supportedTags.Count > 0)
            {
                var mergedSupportedTags = MergeSupportedTags(platform, supportedTags);
                platform.SupportedTags = mergedSupportedTags;
                platform.Platform.SupportedTags = mergedSupportedTags;
            }
        }
    }

    private static List<string> MergeDownloadTags(AutoTagPlatformDescriptor platform, IEnumerable<string> snapshotTags)
    {
        var merged = new List<string>();

        void AddRange(IEnumerable<string>? source)
        {
            if (source == null)
            {
                return;
            }

            foreach (var tag in source)
            {
                if (string.IsNullOrWhiteSpace(tag))
                {
                    continue;
                }

                var normalized = tag.Trim();
                if (merged.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                merged.Add(normalized);
            }
        }

        AddRange(platform.DownloadTags);
        AddRange(platform.Platform.DownloadTags);
        AddRange(snapshotTags);

        return merged;
    }

    private static List<SupportedTag> MergeSupportedTags(AutoTagPlatformDescriptor platform, IEnumerable<SupportedTag> snapshotTags)
    {
        var merged = new List<SupportedTag>();

        void AddRange(IEnumerable<SupportedTag>? source)
        {
            if (source == null)
            {
                return;
            }

            foreach (var tag in source)
            {
                if (merged.Contains(tag))
                {
                    continue;
                }

                merged.Add(tag);
            }
        }

        AddRange(platform.SupportedTags);
        AddRange(platform.Platform.SupportedTags);
        AddRange(snapshotTags);

        return merged;
    }

}
