using System.Globalization;

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
        return string.IsNullOrEmpty(title) ? null : $"{artist}{title}";
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
            return established;
        }

        var merged = established.CoalesceWith(candidate);
        _identities[key] = merged;
        return merged;
    }
}
