using System.Globalization;
using DeezSpoTag.Core.Utils;

namespace DeezSpoTag.Core.Models;

/// <summary>
/// Album-scoped identity values that a single provider natively returned.
/// Presence in <see cref="AlbumIdentity.ProviderIdentities"/> means the value was
/// confirmed from a native payload; a null member never authorizes deletion.
/// </summary>
public sealed record ProviderAlbumIdentity(
    string? AlbumId,
    string? ReleaseId,
    string? AlbumArtistId)
{
    public static readonly ProviderAlbumIdentity Empty = new(null, null, null);

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(AlbumId)
        && string.IsNullOrWhiteSpace(ReleaseId)
        && string.IsNullOrWhiteSpace(AlbumArtistId);

    public ProviderAlbumIdentity CoalesceWith(ProviderAlbumIdentity? candidate)
    {
        if (candidate is null)
        {
            return this;
        }

        return new ProviderAlbumIdentity(
            PreferIdentityValue(AlbumId, candidate.AlbumId),
            PreferIdentityValue(ReleaseId, candidate.ReleaseId),
            PreferIdentityValue(AlbumArtistId, candidate.AlbumArtistId));
    }

    private static string? PreferIdentityValue(string? established, string? candidate)
        => string.IsNullOrWhiteSpace(established)
            ? string.IsNullOrWhiteSpace(candidate) ? null : candidate.Trim()
            : established.Trim();
}

public sealed record AlbumIdentity(
    string? ReleaseDate,
    string? AlbumId,
    string? AlbumArtistId,
    string? ReleaseGroupId = null,
    string? ReleaseStatus = null,
    string? ReleaseCountry = null,
    string? Barcode = null,
    string? ReleaseType = null,
    IReadOnlyDictionary<string, string>? PlatformReleaseIds = null,
    IReadOnlySet<string>? ConfirmedPlatformReleaseIdKeys = null,
    string? CanonicalAlbumTitle = null,
    string? CanonicalAlbumArtist = null,
    string? AlbumRelativePath = null,
    IReadOnlyDictionary<string, ProviderAlbumIdentity>? ProviderIdentities = null)
{
    public static readonly AlbumIdentity Empty = new(null, null, null);

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(ReleaseDate)
        && string.IsNullOrWhiteSpace(AlbumId)
        && string.IsNullOrWhiteSpace(AlbumArtistId)
        && string.IsNullOrWhiteSpace(ReleaseGroupId)
        && string.IsNullOrWhiteSpace(ReleaseStatus)
        && string.IsNullOrWhiteSpace(ReleaseCountry)
        && string.IsNullOrWhiteSpace(Barcode)
        && string.IsNullOrWhiteSpace(ReleaseType)
        && !HasPlatformReleaseIds(PlatformReleaseIds)
        && !HasConfirmedPlatformReleaseIds(ConfirmedPlatformReleaseIdKeys)
        && !HasProviderIdentities(ProviderIdentities)
        && string.IsNullOrWhiteSpace(CanonicalAlbumTitle)
        && string.IsNullOrWhiteSpace(CanonicalAlbumArtist)
        && string.IsNullOrWhiteSpace(AlbumRelativePath);

    public AlbumIdentity CoalesceWith(AlbumIdentity? candidate)
    {
        if (candidate is null)
        {
            return this;
        }

        return new AlbumIdentity(
            Prefer(ReleaseDate, candidate.ReleaseDate),
            Prefer(AlbumId, candidate.AlbumId),
            Prefer(AlbumArtistId, candidate.AlbumArtistId),
            Prefer(ReleaseGroupId, candidate.ReleaseGroupId),
            Prefer(ReleaseStatus, candidate.ReleaseStatus),
            Prefer(ReleaseCountry, candidate.ReleaseCountry),
            Prefer(Barcode, candidate.Barcode),
            Prefer(ReleaseType, candidate.ReleaseType),
            CoalescePlatformReleaseIds(PlatformReleaseIds, candidate.PlatformReleaseIds),
            CoalesceConfirmedPlatformReleaseIdKeys(
                ConfirmedPlatformReleaseIdKeys,
                candidate.ConfirmedPlatformReleaseIdKeys),
            Prefer(CanonicalAlbumTitle, candidate.CanonicalAlbumTitle),
            Prefer(CanonicalAlbumArtist, candidate.CanonicalAlbumArtist),
            Prefer(AlbumRelativePath, candidate.AlbumRelativePath),
            CoalesceProviderIdentities(ProviderIdentities, candidate.ProviderIdentities));
    }

    /// <summary>
    /// Records the album-scoped values a single provider natively returned. The map key is
    /// the normalized provider ID, so one provider's IDs can never leak into another's.
    /// </summary>
    public AlbumIdentity WithProviderIdentity(string providerId, ProviderAlbumIdentity value, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(providerId) || value is null)
        {
            return this;
        }

        var key = NormalizeProviderId(providerId);
        if (key.Length == 0)
        {
            return this;
        }

        if (value.IsEmpty)
        {
            // Presence without a value carries no confirmation and never authorizes deletion.
            return this;
        }

        var values = new Dictionary<string, ProviderAlbumIdentity>(StringComparer.OrdinalIgnoreCase);
        AddProviderIdentities(values, ProviderIdentities);
        if (!overwrite && values.TryGetValue(key, out var established) && !established.IsEmpty)
        {
            values[key] = established.CoalesceWith(value);
        }
        else
        {
            values[key] = overwrite ? value : (values.TryGetValue(key, out var current) ? current.CoalesceWith(value) : value);
        }

        return this with { ProviderIdentities = values.Count == 0 ? null : values };
    }

    /// <summary>Returns the confirmed album-scoped identity for one provider, or null
    /// when that provider never confirmed one.</summary>
    public ProviderAlbumIdentity? GetProviderIdentity(string? providerId)
    {
        var normalized = NormalizeProviderId(providerId);
        return normalized.Length > 0
            && ProviderIdentities is not null
            && ProviderIdentities.TryGetValue(normalized, out var value)
                ? value
                : null;
    }

    /// <summary>Normalizes a provider ID: Apple spellings collapse onto iTunes, and the
    /// rest are trimmed and lower-cased.</summary>
    public static string NormalizeProviderId(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return string.Empty;
        }

        var normalized = providerId.Trim().ToLowerInvariant();
        return normalized.Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty) switch
        {
            "apple" or "applemusic" => "itunes",
            _ => normalized
        };
    }

    public bool IsPlatformReleaseIdConfirmed(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName) || ConfirmedPlatformReleaseIdKeys is null)
        {
            return false;
        }

        var normalized = rawName.Trim();
        return ConfirmedPlatformReleaseIdKeys.Any(key =>
            string.Equals(key?.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public AlbumIdentity WithPlatformReleaseId(string rawName, string? value, bool confirmed)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return this;
        }

        var key = rawName.Trim().ToUpperInvariant();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddPlatformReleaseIds(values, PlatformReleaseIds);
        values.Remove(key);
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[key] = value.Trim();
        }

        var confirmedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddConfirmedPlatformReleaseIdKeys(confirmedKeys, ConfirmedPlatformReleaseIdKeys);
        if (confirmed)
        {
            confirmedKeys.Add(key);
        }

        return this with
        {
            PlatformReleaseIds = values.Count == 0 ? null : values,
            ConfirmedPlatformReleaseIdKeys = confirmedKeys.Count == 0 ? null : confirmedKeys
        };
    }

    public static string? BuildKey(string? albumArtist, string? albumTitle)
    {
        var artist = Normalize(albumArtist);
        var title = Normalize(albumTitle);
        return string.IsNullOrEmpty(title) ? null : $"{artist}\u001f{title}";
    }

    /// <summary>
    /// Edition-aware consensus key: "Album (Deluxe)" and "Album (Deluxe Edition)"
    /// share one key (different wording of the same edition), while the plain
    /// "Album" gets its own key — a standard album and a deluxe edition must never
    /// converge onto one identity.
    /// </summary>
    public static string? BuildEditionAwareKey(string? albumArtist, string? albumTitle)
    {
        var artist = Normalize(albumArtist);
        var core = AlbumTitleNormalizer.CoreTitle(albumTitle);
        if (string.IsNullOrEmpty(core))
        {
            return null;
        }

        var editionSignature = AlbumTitleNormalizer.EditionSignature(albumTitle);
        var editionPart = editionSignature.Length == 0 ? string.Empty : $"+{editionSignature}";
        return $"{artist}\u001f{core}\u001f{editionPart}";
    }

    public static string? BuildScopedEditionAwareKey(
        string? libraryScope,
        string? albumArtist,
        string? albumTitle)
    {
        var releaseKey = BuildEditionAwareKey(albumArtist, albumTitle);
        if (releaseKey is null || string.IsNullOrWhiteSpace(libraryScope))
        {
            return null;
        }

        var scope = libraryScope.Trim()
            .Replace('\\', '/')
            .TrimEnd('/')
            .ToLowerInvariant();
        return scope.Length == 0 ? null : $"{scope}\u001e{releaseKey}";
    }

    public static string? BuildFolderScopedKey(string? libraryScope, string? albumRelativePath)
    {
        var scope = NormalizePathPart(libraryScope);
        var relativePath = NormalizePathPart(albumRelativePath);
        return scope.Length == 0 || relativePath.Length == 0
            ? null
            : $"folder:{scope}\u001e{relativePath}";
    }

    private static string NormalizePathPart(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim()
                .Replace('\\', '/')
                .Trim('/')
                .ToLowerInvariant();

    public static string? FormatReleaseDate(DateTime? value)
        => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateTime? ParseReleaseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (DateTime.TryParseExact(
                trimmed,
                ["yyyy-MM-dd", "yyyy-MM", "yyyy"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var exact))
        {
            return exact;
        }

        return DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private static string? Prefer(string? established, string? candidate)
        => string.IsNullOrWhiteSpace(established) ? NullIfBlank(candidate) : established;

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool HasPlatformReleaseIds(IReadOnlyDictionary<string, string>? values)
        => values?.Any(entry => !string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value)) == true;

    private static bool HasConfirmedPlatformReleaseIds(IReadOnlySet<string>? values)
        => values?.Any(value => !string.IsNullOrWhiteSpace(value)) == true;

    private static bool HasProviderIdentities(IReadOnlyDictionary<string, ProviderAlbumIdentity>? values)
        => values?.Any(entry =>
            !string.IsNullOrWhiteSpace(entry.Key) && entry.Value is not null && !entry.Value.IsEmpty) == true;

    private static IReadOnlyDictionary<string, ProviderAlbumIdentity>? CoalesceProviderIdentities(
        IReadOnlyDictionary<string, ProviderAlbumIdentity>? established,
        IReadOnlyDictionary<string, ProviderAlbumIdentity>? candidate)
    {
        var merged = new Dictionary<string, ProviderAlbumIdentity>(StringComparer.OrdinalIgnoreCase);
        AddProviderIdentities(merged, established);
        AddProviderIdentities(merged, candidate);
        return merged.Count == 0 ? null : merged;
    }

    private static void AddProviderIdentities(
        Dictionary<string, ProviderAlbumIdentity> target,
        IReadOnlyDictionary<string, ProviderAlbumIdentity>? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null || value.IsEmpty)
            {
                continue;
            }

            var normalized = NormalizeProviderId(key);
            target[normalized] = target.TryGetValue(normalized, out var existing)
                ? existing.CoalesceWith(value)
                : value;
        }
    }

    private static IReadOnlyDictionary<string, string>? CoalescePlatformReleaseIds(
        IReadOnlyDictionary<string, string>? established,
        IReadOnlyDictionary<string, string>? candidate)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddPlatformReleaseIds(merged, established);
        AddPlatformReleaseIds(merged, candidate);
        return merged.Count == 0 ? null : merged;
    }

    private static void AddPlatformReleaseIds(
        Dictionary<string, string> target,
        IReadOnlyDictionary<string, string>? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(key)
                || string.IsNullOrWhiteSpace(value)
                || target.ContainsKey(key.Trim()))
            {
                continue;
            }

            target[key.Trim()] = value.Trim();
        }
    }

    private static IReadOnlySet<string>? CoalesceConfirmedPlatformReleaseIdKeys(
        IReadOnlySet<string>? established,
        IReadOnlySet<string>? candidate)
    {
        var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddConfirmedPlatformReleaseIdKeys(merged, established);
        AddConfirmedPlatformReleaseIdKeys(merged, candidate);
        return merged.Count == 0 ? null : merged;
    }

    private static void AddConfirmedPlatformReleaseIdKeys(
        HashSet<string> target,
        IReadOnlySet<string>? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                target.Add(value.Trim().ToUpperInvariant());
            }
        }
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
}

public sealed class AlbumIdentityRegistry
{
    private readonly Dictionary<string, AlbumIdentity> _identities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _updatedUtc = new(StringComparer.Ordinal);

    public bool IsDirty { get; private set; }

    public bool TryGet(string? key, out AlbumIdentity identity)
    {
        identity = AlbumIdentity.Empty;
        return !string.IsNullOrEmpty(key) && _identities.TryGetValue(key, out identity!);
    }

    public AlbumIdentity Establish(
        string? key,
        AlbumIdentity candidate,
        AlbumIdentity? seed = null,
        string? providerId = null,
        ProviderAlbumIdentity? providerIdentity = null,
        bool hasAuthoritativePlatformResult = false,
        bool overwriteAuthoritativeProviderIdentity = false)
    {
        if (string.IsNullOrEmpty(key))
        {
            return ApplyAuthoritativeProviderIdentity(
                candidate,
                providerId,
                providerIdentity,
                hasAuthoritativePlatformResult,
                overwriteAuthoritativeProviderIdentity);
        }

        if (!_identities.TryGetValue(key, out var established))
        {
            established = (seed ?? AlbumIdentity.Empty).CoalesceWith(candidate);
            established = ApplyAuthoritativeProviderIdentity(
                established,
                providerId,
                providerIdentity,
                hasAuthoritativePlatformResult,
                overwriteAuthoritativeProviderIdentity);
            _identities[key] = established;
            _updatedUtc[key] = DateTimeOffset.UtcNow;
            IsDirty = true;
            return established;
        }

        var merged = established.CoalesceWith(candidate);
        merged = ApplyAuthoritativeProviderIdentity(
            merged,
            providerId,
            providerIdentity,
            hasAuthoritativePlatformResult,
            overwriteAuthoritativeProviderIdentity);
        if (!merged.Equals(established))
        {
            IsDirty = true;
            _updatedUtc[key] = DateTimeOffset.UtcNow;
        }

        _identities[key] = merged;
        return merged;
    }

    /// <summary>
    /// Merges the album-scoped values a provider natively confirmed. A null member is an
    /// absence, never a deletion; it can only be replaced when the caller explicitly asks
    /// to overwrite the provider's established identity.
    /// </summary>
    private static AlbumIdentity ApplyAuthoritativeProviderIdentity(
        AlbumIdentity identity,
        string? providerId,
        ProviderAlbumIdentity? value,
        bool hasAuthoritativeResult,
        bool overwrite)
    {
        if (!hasAuthoritativeResult || value is null || value.IsEmpty)
        {
            return identity;
        }

        var normalized = AlbumIdentity.NormalizeProviderId(providerId);
        if (normalized.Length == 0)
        {
            return identity;
        }

        var established = identity.ProviderIdentities is not null
            && identity.ProviderIdentities.TryGetValue(normalized, out var existing)
            ? existing
            : ProviderAlbumIdentity.Empty;
        var merged = overwrite || established.IsEmpty
            ? value
            : established.CoalesceWith(value);
        if (merged.IsEmpty)
        {
            return identity;
        }

        var updated = identity.WithProviderIdentity(normalized, merged, overwrite: true);
        if (normalized != "musicbrainz" || string.IsNullOrWhiteSpace(merged.AlbumId) && string.IsNullOrWhiteSpace(merged.ReleaseId))
        {
            return updated;
        }

        // MusicBrainz is the one provider whose album id also fills the shared folder
        // album-id slot; the provider's own album id wins over its release id.
        return updated with { AlbumId = merged.AlbumId ?? merged.ReleaseId };
    }

    /// <summary>Pre-populates the registry from the persisted cross-run store.</summary>
    public void Seed(string key, AlbumIdentity identity, DateTimeOffset updatedAt)
    {
        if (string.IsNullOrEmpty(key) || identity.IsEmpty)
        {
            return;
        }

        if (_identities.TryGetValue(key, out var existing))
        {
            _identities[key] = existing.CoalesceWith(identity);
            return;
        }

        _identities[key] = identity;
        _updatedUtc[key] = updatedAt;
    }

    public IReadOnlyList<(string Key, AlbumIdentity Identity, DateTimeOffset UpdatedAt)> Snapshot()
        => _identities
            .Select(entry => (entry.Key, entry.Value, _updatedUtc.TryGetValue(entry.Key, out var updatedAt) ? updatedAt : DateTimeOffset.UtcNow))
            .ToList();
}
