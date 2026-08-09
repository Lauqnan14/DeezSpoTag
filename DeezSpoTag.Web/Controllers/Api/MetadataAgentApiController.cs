using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/metadata-agent")]
[Authorize]
public sealed partial class MetadataAgentApiController(
    LibraryRepository libraryRepository,
    SpotifyArtistService spotifyArtistService) : ControllerBase
{
    private const string NavidromeSource = "navidrome";
    private const string SpotifySource = "spotify";
    private const int MaxTopSongs = 100;

    [HttpGet("artist/biography")]
    public async Task<IActionResult> Biography(
        [FromQuery] string? id,
        [FromQuery] string? name,
        [FromQuery] string? preferredSource,
        CancellationToken cancellationToken)
    {
        var artistId = await ResolveArtistIdAsync(id, name, cancellationToken);
        if (artistId is null)
        {
            return NoContent();
        }

        var cached = await libraryRepository.GetArtistBiographyCacheAsync(
            artistId.Value,
            string.IsNullOrWhiteSpace(preferredSource) ? null : preferredSource.Trim(),
            allowFallback: true,
            cancellationToken);
        if (cached is null || string.IsNullOrWhiteSpace(cached.Biography))
        {
            return NoContent();
        }

        var biography = CleanBiography(cached.Biography);
        return string.IsNullOrWhiteSpace(biography)
            ? NoContent()
            : Ok(new { biography, source = cached.Source });
    }

    internal static string CleanBiography(string? biography)
    {
        if (string.IsNullOrWhiteSpace(biography))
        {
            return string.Empty;
        }

        var unwrapped = AnchorTagPattern().Replace(biography, "$1");
        var stripped = HtmlTagPattern().Replace(unwrapped, string.Empty);
        var decoded = System.Net.WebUtility.HtmlDecode(stripped);
        return WhitespaceRunPattern().Replace(decoded, " ").Trim();
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        "<a\\b[^>]*>(.*?)</a\\s*>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex AnchorTagPattern();

    [System.Text.RegularExpressions.GeneratedRegex("<[^>]+>")]
    private static partial System.Text.RegularExpressions.Regex HtmlTagPattern();

    [System.Text.RegularExpressions.GeneratedRegex("\\s{2,}")]
    private static partial System.Text.RegularExpressions.Regex WhitespaceRunPattern();

    [HttpGet("artist/top-songs")]
    public async Task<IActionResult> TopSongs(
        [FromQuery] string? id,
        [FromQuery] string? name,
        [FromQuery] int count,
        CancellationToken cancellationToken)
    {
        var artistId = await ResolveArtistIdAsync(id, name, cancellationToken);
        if (artistId is null)
        {
            return NoContent();
        }

        var spotifyId = await libraryRepository.GetArtistSourceIdAsync(
            artistId.Value,
            SpotifySource,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return NoContent();
        }

        var artistPage = await spotifyArtistService.TryGetCachedArtistPageAsync(
            spotifyId,
            name?.Trim() ?? string.Empty,
            allowStale: true,
            cancellationToken);
        if (artistPage?.TopTracks is not { Count: > 0 })
        {
            return NoContent();
        }

        var limit = count <= 0 ? MaxTopSongs : Math.Min(count, MaxTopSongs);
        var artistName = string.IsNullOrWhiteSpace(artistPage.Artist?.Name)
            ? name?.Trim() ?? string.Empty
            : artistPage.Artist.Name;

        var songs = new List<object>(limit);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in artistPage.TopTracks)
        {
            if (songs.Count >= limit)
            {
                break;
            }

            var title = track.Name?.Trim();
            if (string.IsNullOrWhiteSpace(title) || !seen.Add(title))
            {
                continue;
            }

            songs.Add(new
            {
                name = title,
                isrc = string.IsNullOrWhiteSpace(track.Isrc) ? null : track.Isrc.Trim(),
                artist = artistName,
                album = track.AlbumName?.Trim(),
                durationMs = track.DurationMs > 0 ? track.DurationMs : 0,
            });
        }

        return songs.Count == 0 ? NoContent() : Ok(new { songs });
    }

    private async Task<long?> ResolveArtistIdAsync(string? navidromeId, string? name, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(navidromeId))
        {
            var mapped = await libraryRepository.FindArtistIdBySourceIdAsync(
                NavidromeSource,
                navidromeId,
                cancellationToken);
            if (mapped is not null)
            {
                return mapped;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var byName = await libraryRepository.FindArtistIdByNameAsync(name, cancellationToken);
        if (byName is null || string.IsNullOrWhiteSpace(navidromeId))
        {
            return byName;
        }

        await libraryRepository.UpsertArtistSourceIdAsync(
            byName.Value,
            NavidromeSource,
            navidromeId.Trim(),
            cancellationToken);
        return byName;
    }
}
