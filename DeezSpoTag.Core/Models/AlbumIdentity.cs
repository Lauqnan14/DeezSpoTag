using System.Globalization;
using DeezSpoTag.Core.Utils;

namespace DeezSpoTag.Core.Models;

public sealed record AlbumIdentity(string? ReleaseDate, string? AlbumId, string? AlbumArtistId)
{
    public static readonly AlbumIdentity Empty = new(null, null, null);

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(ReleaseDate)
        && string.IsNullOrWhiteSpace(AlbumId)
        && string.IsNullOrWhiteSpace(AlbumArtistId);

    public AlbumIdentity CoalesceWith(AlbumIdentity? candidate)
    {
        if (candidate is null)
        {
            return this;
        }

        return new AlbumIdentity(
            Prefer(ReleaseDate, candidate.ReleaseDate),
            Prefer(AlbumId, candidate.AlbumId),
            Prefer(AlbumArtistId, candidate.AlbumArtistId));
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

        var editions = AlbumTitleNormalizer.EditionIntent(albumTitle);
        var editionPart = editions.Count == 0
            ? string.Empty
            : $"+{string.Join('|', editions.OrderBy(value => value, StringComparer.Ordinal))}";
        return $"{artist}\u001f{core}\u001f{editionPart}";
    }

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

    public AlbumIdentity Establish(string? key, AlbumIdentity candidate, AlbumIdentity? seed = null)
    {
        if (string.IsNullOrEmpty(key))
        {
            return candidate;
        }

        if (!_identities.TryGetValue(key, out var established))
        {
            established = (seed ?? AlbumIdentity.Empty).CoalesceWith(candidate);
            _identities[key] = established;
            _updatedUtc[key] = DateTimeOffset.UtcNow;
            IsDirty = true;
            return established;
        }

        var merged = established.CoalesceWith(candidate);
        if (!merged.Equals(established))
        {
            IsDirty = true;
            _updatedUtc[key] = DateTimeOffset.UtcNow;
        }

        _identities[key] = merged;
        return merged;
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
