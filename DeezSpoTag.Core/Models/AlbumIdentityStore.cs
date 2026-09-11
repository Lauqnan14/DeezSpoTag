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

        return new AlbumIdentityStore
        {
            Entries = document.Identities
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
                        entry.PlatformReleaseIds),
                    entry.UpdatedAt))
                .ToList()
        };
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
                        existing.Identity.CoalesceWith(identity),
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
                    entry.Identity.PlatformReleaseIds))
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
    public int Version { get; set; } = 1;

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
    [property: JsonPropertyName("platformReleaseIds")] IReadOnlyDictionary<string, string>? PlatformReleaseIds);
