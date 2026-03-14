using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models;

namespace DeezSpoTag.Services.Utils;

public static class TrackIdNormalization
{
    private const string DeezerProvider = "deezer";
    private const string SpotifyProvider = "spotify";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly string[] DeezerLikelyIdKeys = ["deezer_track_id", "deezerid", "deezer_id"];
    private static readonly string[] DeezerLikelyUrlKeys = [DeezerProvider, "deezer_url", "source_url"];
    private static readonly string[] SpotifyLikelyIdKeys = ["spotify_track_id", "spotifyid", "spotify_id"];
    private static readonly string[] SpotifyLikelyUrlKeys = [SpotifyProvider, "spotify_url", "source_url"];
    private static readonly Regex SpotifyTrackUriRegex = new(
        @"spotify:track:(?<id>[A-Za-z0-9]{22})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    public static bool TryResolveDeezerTrackId(Track track, out string? deezerTrackId, string? explicitTrackId = null)
    {
        deezerTrackId = null;
        if (TryNormalizeDeezerTrackId(explicitTrackId, out deezerTrackId))
        {
            return true;
        }

        if (string.Equals(track.Source, DeezerProvider, StringComparison.OrdinalIgnoreCase)
            && (TryNormalizeDeezerTrackId(track.SourceId, out deezerTrackId)
                || TryNormalizeDeezerTrackId(track.Id, out deezerTrackId)))
        {
            return true;
        }

        if (track.Urls?.Count > 0)
        {
            deezerTrackId = ResolveNormalizedDeezerTrackIdFromUrls(track.Urls, DeezerLikelyIdKeys)
                ?? ResolveNormalizedDeezerTrackIdFromUrls(track.Urls, DeezerLikelyUrlKeys);
            if (!string.IsNullOrWhiteSpace(deezerTrackId))
            {
                return true;
            }
        }

        return TryNormalizeDeezerTrackId(track.DownloadURL, out deezerTrackId);
    }

    public static bool TryResolveSpotifyTrackId(Track track, out string? spotifyTrackId)
    {
        spotifyTrackId = null;
        if (TryNormalizeSpotifyTrackId(track.SourceId, out spotifyTrackId))
        {
            return true;
        }

        if (string.Equals(track.Source, SpotifyProvider, StringComparison.OrdinalIgnoreCase)
            && TryNormalizeSpotifyTrackId(track.Id, out spotifyTrackId))
        {
            return true;
        }

        if (track.Urls?.Count > 0)
        {
            spotifyTrackId = ResolveNormalizedSpotifyTrackIdFromUrls(track.Urls, SpotifyLikelyIdKeys)
                ?? ResolveNormalizedSpotifyTrackIdFromUrls(track.Urls, SpotifyLikelyUrlKeys);
            if (!string.IsNullOrWhiteSpace(spotifyTrackId))
            {
                return true;
            }
        }

        return TryNormalizeSpotifyTrackId(track.DownloadURL, out spotifyTrackId);
    }

    public static bool TryNormalizeDeezerTrackId(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (long.TryParse(candidate, out var numeric) && numeric > 0)
        {
            normalized = numeric.ToString();
            return true;
        }

        var extracted = ExtractDeezerTrackIdFromUrl(candidate);
        if (long.TryParse(extracted, out var parsed) && parsed > 0)
        {
            normalized = parsed.ToString();
            return true;
        }

        return false;
    }

    public static string? NormalizeDeezerTrackIdOrNull(string? value)
    {
        return TryNormalizeDeezerTrackId(value, out var normalized) ? normalized : null;
    }

    public static string? ResolveNormalizedDeezerTrackIdFromUrls(
        IDictionary<string, string>? urls,
        IEnumerable<string> keys)
    {
        if (urls == null || urls.Count == 0)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (!urls.TryGetValue(key, out var value))
            {
                continue;
            }

            var normalized = NormalizeDeezerTrackIdOrNull(value);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    public static string? ExtractDeezerTrackIdFromUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const string deezerTrackPrefix = "deezer:track:";
        if (value.StartsWith(deezerTrackPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return value[deezerTrackPrefix.Length..];
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host;
        if (!host.Contains("deezer.com", StringComparison.OrdinalIgnoreCase) &&
            !host.Contains("dzr.page.link", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var trackSegmentIndex = Array.FindIndex(segments, static segment =>
            segment.Equals("track", StringComparison.OrdinalIgnoreCase));
        if (trackSegmentIndex >= 0 && trackSegmentIndex + 1 < segments.Length)
        {
            return segments[trackSegmentIndex + 1];
        }

        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (long.TryParse(segments[i], out _))
            {
                return segments[i];
            }
        }

        return null;
    }

    public static bool TryNormalizeSpotifyTrackId(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length == 22 && candidate.All(char.IsLetterOrDigit))
        {
            normalized = candidate;
            return true;
        }

        var uriMatch = SpotifyTrackUriRegex.Match(candidate);
        if (uriMatch.Success)
        {
            normalized = uriMatch.Groups["id"].Value;
            return true;
        }

        var extracted = ExtractSpotifyTrackIdFromUrl(candidate);
        if (!string.IsNullOrWhiteSpace(extracted))
        {
            normalized = extracted;
            return true;
        }

        return false;
    }

    public static string? NormalizeSpotifyTrackIdOrNull(string? value)
    {
        return TryNormalizeSpotifyTrackId(value, out var normalized) ? normalized : null;
    }

    public static string? ResolveNormalizedSpotifyTrackIdFromUrls(
        IDictionary<string, string>? urls,
        IEnumerable<string> keys)
    {
        if (urls == null || urls.Count == 0)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (!urls.TryGetValue(key, out var value))
            {
                continue;
            }

            var normalized = NormalizeSpotifyTrackIdOrNull(value);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    public static string? ExtractSpotifyTrackIdFromUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const string spotifyTrackPrefix = "spotify:track:";
        if (value.StartsWith(spotifyTrackPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var uriTrackId = value[spotifyTrackPrefix.Length..].Trim();
            return uriTrackId.Length == 22 && uriTrackId.All(char.IsLetterOrDigit) ? uriTrackId : null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Host.Contains("spotify.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var trackSegmentIndex = Array.FindIndex(segments, static segment =>
            segment.Equals("track", StringComparison.OrdinalIgnoreCase));
        if (trackSegmentIndex < 0 || trackSegmentIndex + 1 >= segments.Length)
        {
            return null;
        }

        var trackId = segments[trackSegmentIndex + 1];
        return trackId.Length == 22 && trackId.All(char.IsLetterOrDigit) ? trackId : null;
    }
}
