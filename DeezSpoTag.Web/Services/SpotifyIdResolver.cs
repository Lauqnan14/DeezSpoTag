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
        if (!string.IsNullOrWhiteSpace(isrc))
        {
            var isrcQuery = $"isrc:{isrc}";
            var isrcResponse = await _searchService.SearchByTypeAsync(isrcQuery, "track", 5, 0, cancellationToken);
            var isrcItem = isrcResponse?.Items?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(isrcItem?.Id))
            {
                return isrcItem.Id;
            }
        }

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        var parts = new List<string>
        {
            $"track:{title}",
            $"artist:{artist}"
        };
        if (!string.IsNullOrWhiteSpace(album))
        {
            parts.Add($"album:{album}");
        }

        var query = string.Join(" ", parts);
        var response = await _searchService.SearchByTypeAsync(query, "track", 8, 0, cancellationToken);
        if (response?.Items == null || response.Items.Count == 0)
        {
            return null;
        }

        var best = PickBestMatch(response.Items, title, artist, album);
        return string.IsNullOrWhiteSpace(best?.Id) ? response.Items[0].Id : best.Id;
    }

    private static SpotifySearchItem? PickBestMatch(
        List<SpotifySearchItem> items,
        string title,
        string artist,
        string? album)
    {
        var targetTitle = SpotifyTextNormalizer.NormalizeToken(title);
        var targetArtist = SpotifyTextNormalizer.NormalizeToken(artist);
        var targetAlbum = SpotifyTextNormalizer.NormalizeToken(album);

        SpotifySearchItem? best = null;
        var bestScore = -1;

        foreach (var item in items)
        {
            var score = CalculateMatchScore(item, targetTitle, targetArtist, targetAlbum);

            if (score > bestScore)
            {
                bestScore = score;
                best = item;
            }
        }

        return best;
    }

    private static int CalculateMatchScore(
        SpotifySearchItem item,
        string targetTitle,
        string targetArtist,
        string targetAlbum)
    {
        var itemTitle = SpotifyTextNormalizer.NormalizeToken(item.Name);
        var titleScore = CalculateTitleScore(itemTitle, targetTitle);

        var (itemArtists, itemAlbum) = ParseSubtitle(item.Subtitle);
        var artistScore = CalculateArtistScore(SpotifyTextNormalizer.NormalizeToken(itemArtists), targetArtist);
        var albumScore = CalculateAlbumScore(SpotifyTextNormalizer.NormalizeToken(itemAlbum), targetAlbum);
        return titleScore + artistScore + albumScore;
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
