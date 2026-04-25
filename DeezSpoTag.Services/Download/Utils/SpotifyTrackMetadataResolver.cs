using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using DeezSpoTag.Services.Utils;

namespace DeezSpoTag.Services.Download.Utils;

public sealed class SpotifyTrackMetadataResolver
{
    private static readonly TimeSpan MetadataCacheTtl = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan TokenRefreshWindow = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SpotifyTrackMetadataResolver> _logger;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;

    public SpotifyTrackMetadataResolver(
        IHttpClientFactory httpClientFactory,
        ILogger<SpotifyTrackMetadataResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<SpotifyTrackMetadata?> ResolveTrackAsync(string spotifyTrackId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spotifyTrackId))
        {
            return null;
        }

        var normalizedId = spotifyTrackId.Trim();
        if (_cache.TryGetValue(normalizedId, out var cacheEntry)
            && DateTimeOffset.UtcNow - cacheEntry.Stamp <= MetadataCacheTtl)
        {
            return cacheEntry.Metadata;
        }

        var metadata = await FetchTrackAsync(normalizedId, cancellationToken);
        _cache[normalizedId] = new CacheEntry(DateTimeOffset.UtcNow, metadata);
        return metadata;
    }

    private async Task<SpotifyTrackMetadata?> FetchTrackAsync(string spotifyTrackId, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient("SpotifyPublic");
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.spotify.com/v1/tracks/{WebUtility.UrlEncode(spotifyTrackId)}?market=from_token");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Spotify track metadata request failed: trackId={TrackId} status={Status}",
                        spotifyTrackId,
                        (int)response.StatusCode);
                }

                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<SpotifyTrackEnvelope>(stream, JsonOptions, cancellationToken);
            if (payload == null || string.IsNullOrWhiteSpace(payload.Id))
            {
                return null;
            }

            var artist = payload.Artists?.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value.Name))?.Name;
            var album = payload.Album?.Name;
            var isrc = payload.ExternalIds?.Isrc;
            var durationMs = payload.DurationMs;

            return new SpotifyTrackMetadata(
                payload.Id,
                payload.Name ?? string.Empty,
                artist,
                album,
                durationMs > 0 ? durationMs : null,
                string.IsNullOrWhiteSpace(isrc) ? null : isrc.Trim().ToUpperInvariant());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Spotify track metadata fetch failed for {TrackId}", spotifyTrackId);
            }

            return null;
        }
    }

    private async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken)
            && _accessTokenExpiresAt - DateTimeOffset.UtcNow > TokenRefreshWindow)
        {
            return _accessToken;
        }

        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_accessToken)
                && _accessTokenExpiresAt - DateTimeOffset.UtcNow > TokenRefreshWindow)
            {
                return _accessToken;
            }

            var (totp, version) = SpotifyWebPlayerTotp.Generate();
            if (string.IsNullOrWhiteSpace(totp))
            {
                return null;
            }

            var url =
                $"https://open.spotify.com/api/token?reason=init&productType=web-player&totp={totp}&totpVer={version}&totpServer={totp}";
            using var client = _httpClientFactory.CreateClient("SpotifyPublic");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Spotify token request failed: status={Status}", (int)response.StatusCode);
                }

                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<SpotifyTokenEnvelope>(stream, JsonOptions, cancellationToken);
            if (string.IsNullOrWhiteSpace(payload?.AccessToken))
            {
                return null;
            }

            _accessToken = payload.AccessToken;
            _accessTokenExpiresAt = payload.AccessTokenExpirationTimestampMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(payload.AccessTokenExpirationTimestampMs)
                : DateTimeOffset.UtcNow.AddMinutes(30);
            return _accessToken;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Spotify token request failed for track metadata resolver.");
            }

            return null;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private sealed record CacheEntry(DateTimeOffset Stamp, SpotifyTrackMetadata? Metadata);

    private sealed record SpotifyTokenEnvelope(
        string? AccessToken,
        long AccessTokenExpirationTimestampMs);

    private sealed record SpotifyTrackEnvelope(
        string? Id,
        string? Name,
        int DurationMs,
        SpotifyExternalIdsEnvelope? ExternalIds,
        SpotifyAlbumEnvelope? Album,
        List<SpotifyArtistEnvelope>? Artists);

    private sealed record SpotifyExternalIdsEnvelope(string? Isrc);

    private sealed record SpotifyAlbumEnvelope(string? Name);

    private sealed record SpotifyArtistEnvelope(string? Name);
}

public sealed record SpotifyTrackMetadata(
    string Id,
    string Title,
    string? Artist,
    string? Album,
    int? DurationMs,
    string? Isrc);
