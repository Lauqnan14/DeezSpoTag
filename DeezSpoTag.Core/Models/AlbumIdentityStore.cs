using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Core.Models;

/// <summary>
/// Cross-run album identity store. Enhancement runs establish one identity per
/// album (release date, album id, album-artist id); persisting them lets a track
/// downloaded months after the rest of its album converge onto the same identity
/// instead of matching a different release and splitting the album downstream.
/// </summary>
public sealed class AlbumIdentityStore
{
    private const int MaxEntries = 5000;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public List<AlbumIdentityStoreEntry> Entries { get; set; } = new();

    public static AlbumIdentityStore Load(string path)
    {
        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AlbumIdentityStore();
        }

        var document = JsonSerializer.Deserialize<AlbumIdentityStoreDocument>(json, SerializerOptions);
        if (document?.Identities is null)
        {
            return new AlbumIdentityStore();
        }

        var entries = document.Identities
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                .Select(entry => new AlbumIdentityStoreEntry(
                    entry.Key,
                    new AlbumIdentity(
                        entry.ReleaseDate,
                        entry.AlbumId,
                        entry.AlbumArtistId,
                        entry.ReleaseGroupId,
                        entry.ReleaseStatus,
                        entry.ReleaseCountry,
                        entry.Barcode,
                        entry.ReleaseType,
                        PlatformReleaseIds: null,
                        ConfirmedPlatformReleaseIdKeys: null,
                        entry.CanonicalAlbumTitle,
                        entry.CanonicalAlbumArtist,
                        entry.AlbumRelativePath,
                        ResolveProviderIdentities(entry)),
                    entry.UpdatedAt))
                .Select(MigrateFolderKey)
                .GroupBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(group =>
                {
                    var ordered = group.OrderByDescending(entry => entry.UpdatedAt).ToList();
                    var identity = ordered[0].Identity;
                    foreach (var older in ordered.Skip(1))
                    {
                        identity = identity.CoalesceWith(older.Identity);
                    }

                    return new AlbumIdentityStoreEntry(ordered[0].Key, identity, ordered[0].UpdatedAt);
                })
                .ToList();
        return new AlbumIdentityStore { Entries = entries };
    }

    private static AlbumIdentityStoreEntry MigrateFolderKey(AlbumIdentityStoreEntry entry)
    {
        if (entry.Key.StartsWith("folder:", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(entry.Identity.AlbumRelativePath))
        {
            return entry;
        }

        var separator = entry.Key.IndexOf('\u001e');
        if (separator <= 0)
        {
            return entry;
        }

        var key = AlbumIdentity.BuildFolderScopedKey(
            entry.Key[..separator],
            entry.Identity.AlbumRelativePath);
        return key is null ? entry : entry with { Key = key };
    }

    /// <summary>
    /// Reads the provider-keyed identity map. Version-2 documents carried a release-id-only
    /// map plus a set of confirmed raw names: only confirmed entries migrate, and each one
    /// moves into the release field of its own provider. Unconfirmed legacy values and the
    /// legacy generic album/album-artist ids never become trusted identities.
    /// </summary>
    private static IReadOnlyDictionary<string, ProviderAlbumIdentity>? ResolveProviderIdentities(
        AlbumIdentityStoreEntryDocument entry)
    {
        var values = new Dictionary<string, ProviderAlbumIdentity>(StringComparer.OrdinalIgnoreCase);
        if (entry.ProviderIdentities is not null)
        {
            foreach (var (key, value) in entry.ProviderIdentities)
            {
                if (value is null)
                {
                    continue;
                }

                AddProviderIdentity(values, key, new ProviderAlbumIdentity(
                    value.AlbumId,
                    value.ReleaseId,
                    value.AlbumArtistId));
            }
        }

        if (entry.ConfirmedPlatformReleaseIdKeys is null || entry.PlatformReleaseIds is null)
        {
            return values.Count == 0 ? null : values;
        }

        foreach (var rawName in entry.ConfirmedPlatformReleaseIdKeys)
        {
            if (!TryResolveLegacyProviderId(rawName, out var providerId)
                || !TryReadLegacyReleaseId(entry.PlatformReleaseIds, rawName, out var releaseId))
            {
                continue;
            }

            AddProviderIdentity(values, providerId, new ProviderAlbumIdentity(null, releaseId, null));
        }

        return values.Count == 0 ? null : values;
    }

    private static void AddProviderIdentity(
        Dictionary<string, ProviderAlbumIdentity> values,
        string? providerId,
        ProviderAlbumIdentity identity)
    {
        var normalized = AlbumIdentity.NormalizeProviderId(providerId);
        if (normalized.Length == 0 || identity.IsEmpty)
        {
            return;
        }

        values[normalized] = values.TryGetValue(normalized, out var existing)
            ? existing.CoalesceWith(identity)
            : identity;
    }

    private static bool TryReadLegacyReleaseId(
        IReadOnlyDictionary<string, string> values,
        string rawName,
        out string releaseId)
    {
        releaseId = string.Empty;
        foreach (var (key, value) in values)
        {
            if (!string.IsNullOrWhiteSpace(key)
                && key.Trim().Equals(rawName.Trim(), StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(value))
            {
                releaseId = value.Trim();
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveLegacyProviderId(string? rawName, out string providerId)
    {
        providerId = string.Empty;
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return false;
        }

        const string suffix = "_RELEASE_ID";
        var name = rawName.Trim();
        if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var provider = AlbumIdentity.NormalizeProviderId(name[..^suffix.Length]);
        if (provider.Length == 0)
        {
            return false;
        }

        providerId = provider;
        return true;
    }

    public void Merge(IReadOnlyList<(string Key, AlbumIdentity Identity, DateTimeOffset UpdatedAt)> snapshot)
    {
        var byKey = Entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        foreach (var (key, identity, updatedAt) in snapshot)
        {
            if (identity.IsEmpty)
            {
                continue;
            }

            if (byKey.TryGetValue(key, out var existing))
            {
                if (updatedAt >= existing.UpdatedAt)
                {
                    byKey[key] = new AlbumIdentityStoreEntry(
                        key,
                        identity,
                        updatedAt);
                }
            }
            else
            {
                byKey[key] = new AlbumIdentityStoreEntry(key, identity, updatedAt);
            }
        }

        Entries = byKey.Values
            .OrderByDescending(entry => entry.UpdatedAt)
            .Take(MaxEntries)
            .ToList();
    }

    public void Save(string path)
    {
        var document = new AlbumIdentityStoreDocument
        {
            Identities = Entries
                .Select(entry => new AlbumIdentityStoreEntryDocument(
                    entry.Key,
                    entry.Identity.ReleaseDate,
                    entry.Identity.AlbumId,
                    entry.Identity.AlbumArtistId,
                    entry.UpdatedAt,
                    entry.Identity.ReleaseGroupId,
                    entry.Identity.ReleaseStatus,
                    entry.Identity.ReleaseCountry,
                    entry.Identity.Barcode,
                    entry.Identity.ReleaseType,
                    null,
                    null,
                    entry.Identity.CanonicalAlbumTitle,
                    entry.Identity.CanonicalAlbumArtist,
                    entry.Identity.AlbumRelativePath,
                    entry.Identity.ProviderIdentities is null
                        ? null
                        : entry.Identity.ProviderIdentities.ToDictionary(
                            pair => AlbumIdentity.NormalizeProviderId(pair.Key),
                            pair => new ProviderAlbumIdentityDocument(
                                pair.Value.AlbumId,
                                pair.Value.ReleaseId,
                                pair.Value.AlbumArtistId),
                            StringComparer.OrdinalIgnoreCase)))
                .ToList()
        };

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, SerializerOptions));
        File.Move(temporary, path, overwrite: true);
    }
}

public sealed record AlbumIdentityStoreEntry(string Key, AlbumIdentity Identity, DateTimeOffset UpdatedAt);

public sealed class AlbumIdentityStoreDocument
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 3;

    [JsonPropertyName("identities")]
    public List<AlbumIdentityStoreEntryDocument> Identities { get; set; } = new();
}

public sealed record AlbumIdentityStoreEntryDocument(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("releaseDate")] string? ReleaseDate,
    [property: JsonPropertyName("albumId")] string? AlbumId,
    [property: JsonPropertyName("albumArtistId")] string? AlbumArtistId,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("releaseGroupId")] string? ReleaseGroupId,
    [property: JsonPropertyName("releaseStatus")] string? ReleaseStatus,
    [property: JsonPropertyName("releaseCountry")] string? ReleaseCountry,
    [property: JsonPropertyName("barcode")] string? Barcode,
    [property: JsonPropertyName("releaseType")] string? ReleaseType,
    [property: JsonPropertyName("platformReleaseIds")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string>? PlatformReleaseIds,
    [property: JsonPropertyName("confirmedPlatformReleaseIdKeys")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? ConfirmedPlatformReleaseIdKeys = null,
    [property: JsonPropertyName("canonicalAlbumTitle")] string? CanonicalAlbumTitle = null,
    [property: JsonPropertyName("canonicalAlbumArtist")] string? CanonicalAlbumArtist = null,
    [property: JsonPropertyName("albumRelativePath")] string? AlbumRelativePath = null,
    [property: JsonPropertyName("providerIdentities")] IReadOnlyDictionary<string, ProviderAlbumIdentityDocument>? ProviderIdentities = null);

/// <summary>Version-3 serialized form of one provider's album-scoped identity.</summary>
public sealed record ProviderAlbumIdentityDocument(
    [property: JsonPropertyName("albumId")] string? AlbumId = null,
    [property: JsonPropertyName("releaseId")] string? ReleaseId = null,
    [property: JsonPropertyName("albumArtistId")] string? AlbumArtistId = null);
