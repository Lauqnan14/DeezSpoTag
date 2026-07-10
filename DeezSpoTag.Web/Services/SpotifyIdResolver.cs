using System.Collections.Generic;
using DeezSpoTag.Services.Download;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyIdResolver : ISpotifyIdResolver
{
    private readonly SpotifySearchService _searchService;
    private static readonly string[] AlbumSeparators = { "•", "-", "|", "/" };

    public SpotifyIdResolver(SpotifySearchService searchService)
    {
        _searchService = searchService;
    }

    public async Task<string?> ResolveTrackIdAsync(
        string title,
        string artist,
        string? album,
        string? isrc,
        CancellationToken cancellationToken)
    {
        var searchedMetadataForIsrc = false;
        if (!string.IsNullOrWhiteSpace(isrc))
        {
            var metadataQueryForIsrc = BuildPlainQuery(title, artist, album);
            if (!string.IsNullOrWhiteSpace(metadataQueryForIsrc))
            {
                searchedMetadataForIsrc = true;
                var metadataResponse = await _searchService.SearchByTypeAsync(
                    metadataQueryForIsrc,
                    "track",
                    10,
                    0,
                    cancellationToken,
                    hydrateTrackIsrcs: false,
                    allowTrackFallbacks: false);
                var metadataItems = metadataResponse?.Items;
                var metadataCandidate = SelectBestCandidate(
                    metadataItems,
                    title,
                    artist,
                    album,
                    isrc,
                    allowFirstWhenMetadataMissing: false);
                if (!string.IsNullOrWhiteSpace(metadataCandidate?.Id))
                {
                    return metadataCandidate.Id;
                }

                if (metadataItems is { Count: > 0 }
                    && metadataItems.All(static item => string.IsNullOrWhiteSpace(item.Isrc)))
                {
                    var metadataOnlyCandidate = SelectBestCandidate(
                        metadataItems,
                        title,
                        artist,
                        album,
                        isrc: null,
                        allowFirstWhenMetadataMissing: false);
                    if (!string.IsNullOrWhiteSpace(metadataOnlyCandidate?.Id))
                    {
                        return metadataOnlyCandidate.Id;
                    }
                }
            }
        }

        if (searchedMetadataForIsrc)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        var query = BuildPlainQuery(title, artist, album);
        var response = await _searchService.SearchByTypeAsync(
            query,
            "track",
            8,
            0,
            cancellationToken,
            hydrateTrackIsrcs: false,
            allowTrackFallbacks: false);
        if (response?.Items == null || response.Items.Count == 0)
        {
            return null;
        }

        return SelectBestCandidate(
            response.Items,
            title,
            artist,
            album,
            isrc,
            allowFirstWhenMetadataMissing: false)?.Id;
    }

    private static string BuildPlainQuery(string title, string artist, string? album)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(title))
        {
            parts.Add(title.Trim());
        }

        if (!string.IsNullOrWhiteSpace(artist))
        {
            parts.Add(artist.Trim());
        }

        if (!string.IsNullOrWhiteSpace(album))
        {
            parts.Add(album.Trim());
        }

        return string.Join(" ", parts);
    }

    private static SpotifySearchItem? SelectBestCandidate(
        List<SpotifySearchItem>? items,
        string title,
        string artist,
        string? album,
        string? isrc,
        bool allowFirstWhenMetadataMissing)
    {
        if (items == null || items.Count == 0)
        {
            return null;
        }

        var normalizedIsrc = NormalizeIsrc(isrc);
        var targetTitle = SpotifyTextNormalizer.NormalizeToken(title);
        var targetArtist = SpotifyTextNormalizer.NormalizeToken(artist);
        var targetAlbum = SpotifyTextNormalizer.NormalizeToken(album);
        if (!string.IsNullOrWhiteSpace(normalizedIsrc))
        {
            var exactIsrc = items.FirstOrDefault(item =>
                string.Equals(NormalizeIsrc(item.Isrc), normalizedIsrc, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item.Id));
            if (exactIsrc != null)
            {
                return exactIsrc;
            }

            return null;
        }

        var hasMetadataTarget = !string.IsNullOrWhiteSpace(targetTitle)
            || !string.IsNullOrWhiteSpace(targetArtist)
            || !string.IsNullOrWhiteSpace(targetAlbum);
        if (!hasMetadataTarget)
        {
            return allowFirstWhenMetadataMissing
                ? items.FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item.Id))
                : null;
        }

        SpotifySearchItem? best = null;
        var bestScore = -1;
        var bestAcceptable = false;

        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Isrc)
                && !string.IsNullOrWhiteSpace(normalizedIsrc)
                && !string.Equals(NormalizeIsrc(item.Isrc), normalizedIsrc, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var score = CalculateMatchScore(item, targetTitle, targetArtist, targetAlbum, out var acceptable);

            if (score > bestScore)
            {
                bestScore = score;
                best = item;
                bestAcceptable = acceptable;
            }
        }

        return bestAcceptable ? best : null;
    }

    private static int CalculateMatchScore(
        SpotifySearchItem item,
        string targetTitle,
        string targetArtist,
        string targetAlbum,
        out bool acceptable)
    {
        var itemTitle = SpotifyTextNormalizer.NormalizeToken(item.Name);
        var titleScore = CalculateTitleScore(itemTitle, targetTitle);

        var (itemArtists, itemAlbum) = ParseSubtitle(item.Subtitle);
        var artistScore = CalculateArtistScore(SpotifyTextNormalizer.NormalizeToken(itemArtists), targetArtist);
        var albumScore = CalculateAlbumScore(SpotifyTextNormalizer.NormalizeToken(itemAlbum), targetAlbum);
        acceptable = IsAcceptableMatch(titleScore, artistScore, albumScore, targetTitle, targetArtist, targetAlbum);
        return titleScore + artistScore + albumScore;
    }

    private static bool IsAcceptableMatch(
        int titleScore,
        int artistScore,
        int albumScore,
        string targetTitle,
        string targetArtist,
        string targetAlbum)
    {
        if (!string.IsNullOrWhiteSpace(targetTitle) && !string.IsNullOrWhiteSpace(targetArtist))
        {
            return titleScore > 0 && artistScore > 0;
        }

        if (!string.IsNullOrWhiteSpace(targetTitle))
        {
            return titleScore > 0;
        }

        if (!string.IsNullOrWhiteSpace(targetArtist))
        {
            return artistScore > 0;
        }

        return !string.IsNullOrWhiteSpace(targetAlbum) && albumScore > 0;
    }

    private static int CalculateTitleScore(string itemTitle, string targetTitle)
    {
        if (string.IsNullOrWhiteSpace(itemTitle))
        {
            return 0;
        }

        if (itemTitle == targetTitle)
        {
            return 4;
        }

        return !string.IsNullOrWhiteSpace(targetTitle) && ContainsEitherWay(itemTitle, targetTitle)
            ? 2
            : 0;
    }

    private static int CalculateArtistScore(string itemArtists, string targetArtist)
    {
        if (string.IsNullOrWhiteSpace(itemArtists) || string.IsNullOrWhiteSpace(targetArtist))
        {
            return 0;
        }

        return itemArtists.Contains(targetArtist, StringComparison.Ordinal) ? 2 : 0;
    }

    private static int CalculateAlbumScore(string itemAlbum, string targetAlbum)
    {
        if (string.IsNullOrWhiteSpace(targetAlbum) || string.IsNullOrWhiteSpace(itemAlbum))
        {
            return 0;
        }

        return itemAlbum == targetAlbum || itemAlbum.Contains(targetAlbum, StringComparison.Ordinal)
            ? 1
            : 0;
    }

    private static bool ContainsEitherWay(string left, string right)
        => left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal);

    private static string NormalizeIsrc(string? isrc)
        => string.IsNullOrWhiteSpace(isrc) ? string.Empty : isrc.Trim().ToUpperInvariant();

    private static (string? Artists, string? Album) ParseSubtitle(string? subtitle)
    {
        if (string.IsNullOrWhiteSpace(subtitle))
        {
            return (null, null);
        }

        foreach (var separator in AlbumSeparators)
        {
            var parts = subtitle.Split(separator, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                return (parts[0].Trim(), parts[1].Trim());
            }
        }

        return (subtitle.Trim(), null);
    }
}
