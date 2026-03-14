using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models.Qobuz;

namespace DeezSpoTag.Services.Metadata.Qobuz;

public static class DiscographyMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex[] TitleCleanupPatterns =
    {
        new(@"\s*\(Remaster(ed)?\s*\d*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout),
        new(@"\s*\(Deluxe( Edition)?\)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout),
        new(@"\s*\(Expanded( Edition)?\)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout),
        new(@"\s*\[\d+ Remaster(ed)?\]", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout),
        new(@"\s*-\s*\d+\s*Remaster", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout)
    };

    public static QobuzAlbum? FindAlbumInDiscography(QobuzArtist artist, string albumTitle, string? releaseYear = null)
    {
        if (artist.Albums?.Items == null || string.IsNullOrWhiteSpace(albumTitle))
        {
            return null;
        }

        var normalizedTitle = NormalizeTitle(albumTitle);
        var matches = artist.Albums.Items
            .Select(album => new
            {
                Album = album,
                Score = CalculateAlbumScore(album, normalizedTitle, releaseYear)
            })
            .Where(x => x.Score > 0.85)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Album.HiRes)
            .ThenByDescending(x => x.Album.MaximumSamplingRate)
            .ToList();

        return matches.FirstOrDefault()?.Album;
    }

    private static double CalculateAlbumScore(QobuzAlbum album, string normalizedTitle, string? releaseYear)
    {
        var albumTitle = NormalizeTitle(album.Title ?? string.Empty);
        var titleScore = albumTitle == normalizedTitle ? 1.0 : 0.0;

        if (!string.IsNullOrWhiteSpace(releaseYear))
        {
            var date = album.ReleaseDateOriginal ?? album.ReleaseDateStream ?? album.ReleaseDateDownload ?? string.Empty;
            if (date.StartsWith(releaseYear, StringComparison.OrdinalIgnoreCase))
            {
                titleScore += 0.05;
            }
        }

        return titleScore;
    }

    private static string NormalizeTitle(string title)
    {
        var result = title;
        foreach (var pattern in TitleCleanupPatterns)
        {
            result = pattern.Replace(result, string.Empty);
        }

        return result.Trim().ToLowerInvariant();
    }
}
