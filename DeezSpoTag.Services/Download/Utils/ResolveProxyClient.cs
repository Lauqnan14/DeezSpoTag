using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Utils;

public sealed partial class ResolveProxyClient
{
    private const string ClientName = "ResolveProxy";
    private const string ResolveEndpoint = "https://api.zarz.moe/v1/resolve";
    private const string SpotifyKey = "Spotify";
    private const string DeezerKey = "Deezer";
    private const string TidalKey = "Tidal";
    private const string QobuzKey = "Qobuz";
    private const string AmazonMusicKey = "AmazonMusic";
    private const string AppleMusicKey = "AppleMusic";
    private const string YouTubeMusicKey = "YouTubeMusic";
    private const string YouTubeKey = "YouTube";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ResolveProxyClient> _logger;

    public ResolveProxyClient(IHttpClientFactory httpClientFactory, ILogger<ResolveProxyClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task<SongLinkResult?> ResolveUrlAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Task.FromResult<SongLinkResult?>(null);
        }

        return ResolveAsync(new Dictionary<string, string>
        {
            ["url"] = url.Trim()
        }, cancellationToken);
    }

    public Task<ResolveProxyLookupResult> ResolveUrlWithStatusAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Task.FromResult(ResolveProxyLookupResult.NotAttempted("URL is required."));
        }

        return ResolveWithStatusAsync(new Dictionary<string, string>
        {
            ["url"] = url.Trim()
        }, cancellationToken);
    }

    public Task<SongLinkResult?> ResolvePlatformIdAsync(
        string platform,
        string entityType,
        string id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(platform)
            || string.IsNullOrWhiteSpace(entityType)
            || string.IsNullOrWhiteSpace(id))
        {
            return Task.FromResult<SongLinkResult?>(null);
        }

        return ResolveAsync(new Dictionary<string, string>
        {
            ["platform"] = platform.Trim(),
            ["type"] = entityType.Trim(),
            ["id"] = id.Trim()
        }, cancellationToken);
    }

    public Task<ResolveProxyLookupResult> ResolvePlatformIdWithStatusAsync(
        string platform,
        string entityType,
        string id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(platform)
            || string.IsNullOrWhiteSpace(entityType)
            || string.IsNullOrWhiteSpace(id))
        {
            return Task.FromResult(ResolveProxyLookupResult.NotAttempted("Platform, type, and ID are required."));
        }

        return ResolveWithStatusAsync(new Dictionary<string, string>
        {
            ["platform"] = platform.Trim(),
            ["type"] = entityType.Trim(),
            ["id"] = id.Trim()
        }, cancellationToken);
    }

    private async Task<SongLinkResult?> ResolveAsync(
        Dictionary<string, string> payload,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient(ClientName);
            using var response = await client.PostAsJsonAsync(ResolveEndpoint, payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return ParseResponse(document.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Resolve proxy lookup failed.");
            }

            return null;
        }
    }

    private async Task<ResolveProxyLookupResult> ResolveWithStatusAsync(
        Dictionary<string, string> payload,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient(ClientName);
            using var response = await client.PostAsJsonAsync(ResolveEndpoint, payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ResolveProxyLookupResult.Failed($"Resolve proxy returned HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (TryReadError(document.RootElement, out var error))
            {
                return ResolveProxyLookupResult.Failed(error);
            }

            return ResolveProxyLookupResult.Succeeded(ParseResponse(document.RootElement));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Resolve proxy lookup failed.");
            }

            return ResolveProxyLookupResult.Failed(ex.Message);
        }
    }

    internal static SongLinkResult? ParseResponse(JsonElement root)
    {
        if (!TryReadSuccess(root))
        {
            return null;
        }

        if (!root.TryGetProperty("songUrls", out var songUrls)
            || songUrls.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var spotifyUrl = ReadSongUrl(songUrls, SpotifyKey);
        var deezerUrl = ReadSongUrl(songUrls, DeezerKey);
        var tidalUrl = ReadSongUrl(songUrls, TidalKey);
        var qobuzUrl = ReadSongUrl(songUrls, QobuzKey);
        var amazonUrl = ReadSongUrl(songUrls, AmazonMusicKey);
        var appleUrl = ReadSongUrl(songUrls, AppleMusicKey);
        var youtubeUrl = ReadSongUrl(songUrls, YouTubeMusicKey) ?? ReadSongUrl(songUrls, YouTubeKey);

        var result = new SongLinkResult
        {
            SpotifyUrl = spotifyUrl,
            SpotifyId = ExtractSpotifyTrackId(spotifyUrl),
            DeezerUrl = NormalizeDeezerUrl(deezerUrl),
            DeezerId = TrackIdNormalization.NormalizeDeezerTrackIdOrNull(deezerUrl),
            TidalUrl = tidalUrl,
            QobuzUrl = qobuzUrl,
            AmazonUrl = amazonUrl,
            AppleMusicUrl = appleUrl,
            YouTubeUrl = youtubeUrl,
            YouTubeId = ExtractYouTubeId(youtubeUrl),
            Isrc = ReadString(root, "isrc"),
            SourceType = "song"
        };

        return HasAnyResolvedLink(result) ? result : null;
    }

    private static bool TryReadSuccess(JsonElement root)
    {
        if (!root.TryGetProperty("success", out var success))
        {
            return true;
        }

        return success.ValueKind == JsonValueKind.True;
    }

    private static bool TryReadError(JsonElement root, out string error)
    {
        error = string.Empty;
        if (!root.TryGetProperty("error", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        error = value.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(error);
    }

    private static string? ReadSongUrl(JsonElement songUrls, string propertyName)
    {
        if (!songUrls.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return ReadUrlValue(value);
    }

    private static string? ReadUrlValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return NormalizeUrl(value.GetString());
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in value.EnumerateArray())
        {
            var url = item.ValueKind == JsonValueKind.String
                ? NormalizeUrl(item.GetString())
                : null;
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return null;
    }

    private static string? NormalizeUrl(string? value)
    {
        var trimmed = value?.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out _) ? trimmed : null;
    }

    private static string? NormalizeDeezerUrl(string? deezerUrl)
    {
        var deezerId = TrackIdNormalization.NormalizeDeezerTrackIdOrNull(deezerUrl);
        return string.IsNullOrWhiteSpace(deezerId)
            ? deezerUrl
            : $"https://www.deezer.com/track/{deezerId}";
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? ExtractSpotifyTrackId(string? spotifyUrl)
    {
        if (string.IsNullOrWhiteSpace(spotifyUrl))
        {
            return null;
        }

        var match = SpotifyTrackRegex().Match(spotifyUrl);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static string? ExtractYouTubeId(string? youtubeUrl)
    {
        if (string.IsNullOrWhiteSpace(youtubeUrl)
            || !Uri.TryCreate(youtubeUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsolutePath.Trim('/');
        }

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in query)
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && string.Equals(parts[0], "v", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        return null;
    }

    private static bool HasAnyResolvedLink(SongLinkResult result)
    {
        return !string.IsNullOrWhiteSpace(result.DeezerId)
               || !string.IsNullOrWhiteSpace(result.DeezerUrl)
               || !string.IsNullOrWhiteSpace(result.SpotifyId)
               || !string.IsNullOrWhiteSpace(result.SpotifyUrl)
               || !string.IsNullOrWhiteSpace(result.TidalUrl)
               || !string.IsNullOrWhiteSpace(result.QobuzUrl)
               || !string.IsNullOrWhiteSpace(result.AppleMusicUrl)
               || !string.IsNullOrWhiteSpace(result.AmazonUrl)
               || !string.IsNullOrWhiteSpace(result.YouTubeUrl);
    }

    [GeneratedRegex(@"spotify\.com\/track\/(?<id>[A-Za-z0-9]+)", RegexOptions.IgnoreCase, 250)]
    private static partial Regex SpotifyTrackRegex();
}

public sealed record ResolveProxyLookupResult(bool Attempted, bool Completed, SongLinkResult? Result, string? Error)
{
    public static ResolveProxyLookupResult NotAttempted(string error)
    {
        return new ResolveProxyLookupResult(Attempted: false, Completed: false, Result: null, Error: error);
    }

    public static ResolveProxyLookupResult Failed(string error)
    {
        return new ResolveProxyLookupResult(Attempted: true, Completed: false, Result: null, Error: error);
    }

    public static ResolveProxyLookupResult Succeeded(SongLinkResult? result)
    {
        return new ResolveProxyLookupResult(Attempted: true, Completed: true, Result: result, Error: null);
    }
}
