using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Web.Services;

public static class TaggingProfileCanonicalizer
{
    private const string DownloadTagsKey = "downloadTags";
    private const string EnrichmentTagsKey = "tags";
    private const string EnhancementTagsKey = "gapFillTags";

    private sealed record TagDescriptor(
        string CanonicalKey,
        Func<UnifiedTagConfig, TagSource> Getter,
        Action<UnifiedTagConfig, TagSource> Setter,
        params string[] Aliases);

    private static readonly PropertyInfo[] TagSourceProperties = typeof(UnifiedTagConfig)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.PropertyType == typeof(TagSource) && property.CanRead && property.CanWrite)
        .ToArray();

    private static readonly IReadOnlyList<TagDescriptor> TagDescriptors =
    [
        new("title", c => c.Title, (c, v) => c.Title = v),
        new("artist", c => c.Artist, (c, v) => c.Artist = v),
        new("artists", c => c.Artists, (c, v) => c.Artists = v),
        new("album", c => c.Album, (c, v) => c.Album = v),
        new("albumArtist", c => c.AlbumArtist, (c, v) => c.AlbumArtist = v),
        new("cover", c => c.Cover, (c, v) => c.Cover = v, "albumArt"),
        new("trackNumber", c => c.TrackNumber, (c, v) => c.TrackNumber = v),
        new("trackTotal", c => c.TrackTotal, (c, v) => c.TrackTotal = v),
        new("discNumber", c => c.DiscNumber, (c, v) => c.DiscNumber = v),
        new("discTotal", c => c.DiscTotal, (c, v) => c.DiscTotal = v),
        new("genre", c => c.Genre, (c, v) => c.Genre = v),
        new("year", c => c.Year, (c, v) => c.Year = v),
        new("date", c => c.Date, (c, v) => c.Date = v),
        new("isrc", c => c.Isrc, (c, v) => c.Isrc = v),
        new("barcode", c => c.Barcode, (c, v) => c.Barcode = v, "upc"),
        new("bpm", c => c.Bpm, (c, v) => c.Bpm = v),
        new("length", c => c.Duration, (c, v) => c.Duration = v, "duration"),
        new("replayGain", c => c.ReplayGain, (c, v) => c.ReplayGain = v),
        new("danceability", c => c.Danceability, (c, v) => c.Danceability = v),
        new("energy", c => c.Energy, (c, v) => c.Energy = v),
        new("valence", c => c.Valence, (c, v) => c.Valence = v),
        new("acousticness", c => c.Acousticness, (c, v) => c.Acousticness = v),
        new("instrumentalness", c => c.Instrumentalness, (c, v) => c.Instrumentalness = v),
        new("speechiness", c => c.Speechiness, (c, v) => c.Speechiness = v),
        new("loudness", c => c.Loudness, (c, v) => c.Loudness = v),
        new("tempo", c => c.Tempo, (c, v) => c.Tempo = v),
        new("timeSignature", c => c.TimeSignature, (c, v) => c.TimeSignature = v),
        new("liveness", c => c.Liveness, (c, v) => c.Liveness = v),
        new("label", c => c.Label, (c, v) => c.Label = v),
        new("copyright", c => c.Copyright, (c, v) => c.Copyright = v),
        new("lyrics", c => c.UnsyncedLyrics, (c, v) => c.UnsyncedLyrics = v, "unsyncedLyrics"),
        new("syncedLyrics", c => c.SyncedLyrics, (c, v) => c.SyncedLyrics = v),
        new("composer", c => c.Composer, (c, v) => c.Composer = v),
        new("involvedPeople", c => c.InvolvedPeople, (c, v) => c.InvolvedPeople = v),
        new("source", c => c.Source, (c, v) => c.Source = v),
        new("explicit", c => c.Explicit, (c, v) => c.Explicit = v),
        new("rating", c => c.Rating, (c, v) => c.Rating = v),
        new("style", c => c.Style, (c, v) => c.Style = v),
        new("releaseDate", c => c.ReleaseDate, (c, v) => c.ReleaseDate = v),
        new("publishDate", c => c.PublishDate, (c, v) => c.PublishDate = v),
        new("releaseId", c => c.ReleaseId, (c, v) => c.ReleaseId = v),
        new("trackId", c => c.TrackId, (c, v) => c.TrackId = v),
        new("catalogNumber", c => c.CatalogNumber, (c, v) => c.CatalogNumber = v),
        new("key", c => c.Key, (c, v) => c.Key = v),
        new("remixer", c => c.Remixer, (c, v) => c.Remixer = v),
        new("version", c => c.Version, (c, v) => c.Version = v),
        new("mood", c => c.Mood, (c, v) => c.Mood = v),
        new("url", c => c.Url, (c, v) => c.Url = v),
        new("otherTags", c => c.OtherTags, (c, v) => c.OtherTags = v),
        new("metaTags", c => c.MetaTags, (c, v) => c.MetaTags = v)
    ];

    private static readonly Dictionary<string, TagDescriptor> TagLookup = BuildTagLookup();

    public static bool Canonicalize(TaggingProfile profile, bool seedFromTagConfigWhenMissing = true)
    {
        if (profile == null)
        {
            return false;
        }

        profile.TagConfig ??= new UnifiedTagConfig();
        profile.AutoTag ??= new AutoTagSettings();
        profile.AutoTag.Data ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        var changed = false;
        var data = profile.AutoTag.Data;
        var baseConfig = CloneConfig(profile.TagConfig);

        var hasDownloadTags = TryReadTagArray(data, DownloadTagsKey, out var downloadTags, out var downloadKey);
        var hasEnrichmentTags = TryReadTagArray(data, EnrichmentTagsKey, out var enrichmentTags, out var enrichmentKey);

        if (!hasDownloadTags && seedFromTagConfigWhenMissing)
        {
            downloadTags = BuildTagListFromConfig(baseConfig, includeDownloadSource: true);
            changed |= WriteTagArray(data, downloadKey, downloadTags);
        }
        else if (hasDownloadTags)
        {
            changed |= WriteTagArray(data, downloadKey, downloadTags);
        }

        if (!hasEnrichmentTags && seedFromTagConfigWhenMissing)
        {
            enrichmentTags = BuildTagListFromConfig(baseConfig, includeAutoTagSource: true);
            changed |= WriteTagArray(data, enrichmentKey, enrichmentTags);
        }
        else if (hasEnrichmentTags)
        {
            changed |= WriteTagArray(data, enrichmentKey, enrichmentTags);
        }

        var enhancementKey = ResolveTagArrayKey(data, EnhancementTagsKey);
        var enhancementTags = BuildEnhancementTagParityList(downloadTags, enrichmentTags);
        changed |= WriteTagArray(data, enhancementKey, enhancementTags);

        var effectiveConfig = BuildTagConfig(baseConfig, data);
        if (!TagConfigsEqual(profile.TagConfig, effectiveConfig))
        {
            profile.TagConfig = effectiveConfig;
            changed = true;
        }

        return changed;
    }

    public static bool SyncTagArraysFromConfig(TaggingProfile profile)
    {
        if (profile == null)
        {
            return false;
        }

        profile.TagConfig ??= new UnifiedTagConfig();
        profile.AutoTag ??= new AutoTagSettings();
        profile.AutoTag.Data ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        var data = profile.AutoTag.Data;
        var changed = false;
        var downloadTags = BuildTagListFromConfig(profile.TagConfig, includeDownloadSource: true);
        var enrichmentTags = BuildTagListFromConfig(profile.TagConfig, includeAutoTagSource: true);
        changed |= WriteTagArray(data, ResolveTagArrayKey(data, DownloadTagsKey), downloadTags);
        changed |= WriteTagArray(data, ResolveTagArrayKey(data, EnrichmentTagsKey), enrichmentTags);
        changed |= WriteTagArray(data, ResolveTagArrayKey(data, EnhancementTagsKey), BuildEnhancementTagParityList(downloadTags, enrichmentTags));
        return changed;
    }

    public static Dictionary<string, JsonElement> BuildAutoTagDataFromTagConfig(
        UnifiedTagConfig? tagConfig,
        Dictionary<string, JsonElement>? existingData)
    {
        var config = tagConfig ?? new UnifiedTagConfig();
        var data = existingData is { Count: > 0 }
            ? new Dictionary<string, JsonElement>(existingData, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        var downloadKey = ResolveTagArrayKey(data, DownloadTagsKey);
        var enrichmentKey = ResolveTagArrayKey(data, EnrichmentTagsKey);
        var enhancementKey = ResolveTagArrayKey(data, EnhancementTagsKey);

        var downloadTags = BuildTagListFromConfig(config, includeDownloadSource: true);
        var enrichmentTags = BuildTagListFromConfig(config, includeAutoTagSource: true);

        WriteTagArray(data, downloadKey, downloadTags);
        WriteTagArray(data, enrichmentKey, enrichmentTags);
        WriteTagArray(data, enhancementKey, BuildEnhancementTagParityList(downloadTags, enrichmentTags));

        return data;
    }

    public static UnifiedTagConfig BuildTagConfig(
        UnifiedTagConfig? fallbackConfig,
        Dictionary<string, JsonElement>? autoTagData)
    {
        var fallback = fallbackConfig ?? new UnifiedTagConfig();
        if (autoTagData == null || autoTagData.Count == 0)
        {
            return CloneConfig(fallback);
        }

        var hasDownloadTags = TryReadTagArray(autoTagData, DownloadTagsKey, out var downloadTags, out _);
        var hasEnrichmentTags = TryReadTagArray(autoTagData, EnrichmentTagsKey, out var enrichmentTags, out _);

        var config = hasDownloadTags
            ? CreateEmptyConfig()
            : CloneConfig(fallback);

        if (hasDownloadTags)
        {
            ApplyTags(config, downloadTags, TagSource.DownloadSource);
        }

        if (hasEnrichmentTags)
        {
            ApplyTags(config, enrichmentTags, TagSource.AutoTagPlatform);
        }

        return config;
    }

    private static Dictionary<string, TagDescriptor> BuildTagLookup()
    {
        var lookup = new Dictionary<string, TagDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in TagDescriptors)
        {
            lookup[descriptor.CanonicalKey] = descriptor;
            foreach (var alias in descriptor.Aliases)
            {
                lookup[alias] = descriptor;
            }
        }

        return lookup;
    }

    private static bool TryReadTagArray(
        Dictionary<string, JsonElement> data,
        string key,
        out List<string> tags,
        out string resolvedKey)
    {
        resolvedKey = ResolveTagArrayKey(data, key);
        tags = new List<string>();
        if (!data.TryGetValue(resolvedKey, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        tags = NormalizeTagList(element.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!));
        return true;
    }

    private static string ResolveTagArrayKey(Dictionary<string, JsonElement> data, string key)
    {
        return data.Keys.FirstOrDefault(entry => string.Equals(entry, key, StringComparison.OrdinalIgnoreCase)) ?? key;
    }

    private static bool WriteTagArray(Dictionary<string, JsonElement> data, string key, IEnumerable<string> tags)
    {
        var normalized = NormalizeTagList(tags).ToArray();
        if (data.TryGetValue(key, out var existing)
            && existing.ValueKind == JsonValueKind.Array)
        {
            var current = existing.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString())
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item!)
                .Select(static item => item.Trim())
                .ToArray();
            if (current.SequenceEqual(normalized, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        data[key] = JsonSerializer.SerializeToElement(normalized);
        return true;
    }

    private static List<string> NormalizeTagList(IEnumerable<string> tags)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in tags)
        {
            var normalized = NormalizeTagKey(raw);
            if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
            {
                continue;
            }

            result.Add(normalized);
        }

        return result;
    }

    private static string NormalizeTagKey(string? raw)
    {
        var key = raw?.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        return TagLookup.TryGetValue(key, out var descriptor)
            ? descriptor.CanonicalKey
            : key;
    }

    private static List<string> BuildTagListFromConfig(
        UnifiedTagConfig config,
        bool includeDownloadSource = false,
        bool includeAutoTagSource = false)
    {
        var tags = new List<string>();
        foreach (var descriptor in TagDescriptors)
        {
            var source = descriptor.Getter(config);
            if (includeDownloadSource
                && (source == TagSource.DownloadSource || source == TagSource.Both))
            {
                tags.Add(descriptor.CanonicalKey);
            }

            if (includeAutoTagSource
                && (source == TagSource.AutoTagPlatform || source == TagSource.Both))
            {
                tags.Add(descriptor.CanonicalKey);
            }
        }

        return tags;
    }

    private static List<string> BuildEnhancementTagParityList(
        IReadOnlyCollection<string> downloadTags,
        IReadOnlyCollection<string> enrichmentTags)
    {
        var merged = new List<string>(downloadTags.Count + enrichmentTags.Count);
        merged.AddRange(downloadTags);
        merged.AddRange(enrichmentTags);
        return NormalizeTagList(merged);
    }

    private static void ApplyTags(UnifiedTagConfig config, IEnumerable<string> tags, TagSource source)
    {
        foreach (var rawTag in tags)
        {
            var tag = NormalizeTagKey(rawTag);
            if (string.IsNullOrWhiteSpace(tag) || !TagLookup.TryGetValue(tag, out var descriptor))
            {
                continue;
            }

            descriptor.Setter(config, MergeSource(descriptor.Getter(config), source));
        }
    }

    private static TagSource MergeSource(TagSource current, TagSource next)
    {
        if (current == TagSource.None)
        {
            return next;
        }

        if (next == TagSource.None || current == next)
        {
            return current;
        }

        return TagSource.Both;
    }

    private static UnifiedTagConfig CreateEmptyConfig()
    {
        var config = new UnifiedTagConfig();
        foreach (var property in TagSourceProperties)
        {
            property.SetValue(config, TagSource.None);
        }

        return config;
    }

    private static UnifiedTagConfig CloneConfig(UnifiedTagConfig source)
    {
        var clone = new UnifiedTagConfig();
        foreach (var property in TagSourceProperties)
        {
            var value = property.GetValue(source);
            property.SetValue(clone, value);
        }

        return clone;
    }

    private static bool TagConfigsEqual(UnifiedTagConfig left, UnifiedTagConfig right)
    {
        foreach (var property in TagSourceProperties)
        {
            var leftValue = (TagSource)(property.GetValue(left) ?? TagSource.None);
            var rightValue = (TagSource)(property.GetValue(right) ?? TagSource.None);
            if (leftValue != rightValue)
            {
                return false;
            }
        }

        return true;
    }
}
