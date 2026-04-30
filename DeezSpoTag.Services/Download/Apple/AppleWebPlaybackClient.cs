using System.Net.Http.Json;
using System.Text.Json;
using DeezSpoTag.Services.Apple;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Apple;

public sealed class AppleWebPlaybackClient
{
    private const string ApplePlaybackHost = "play.music.apple.com";
    private const string AppleStorefrontHost = "music.apple.com";
    private static readonly string[] PreviewUrlMarkers =
    [
        "audiopreview",
        "audio-preview",
        "/preview/",
        "preview.m4a",
        ".p.m4a",
        "preview"
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AppleWebPlaybackClient> _logger;

    public AppleWebPlaybackClient(IHttpClientFactory httpClientFactory, ILogger<AppleWebPlaybackClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<AppleWebPlaybackResult?> GetWebPlaybackAsync(string adamId, string authorizationToken, string mediaUserToken, CancellationToken cancellationToken)
    {
        var payload = await GetPayloadJsonAsync(adamId, authorizationToken, mediaUserToken, cancellationToken);
        if (payload == null)
        {
            return null;
        }

        var (hlsPlaylistUrl, assetUrl) = ParseWebPlaybackPayload(payload.RootElement);
        if (string.IsNullOrWhiteSpace(assetUrl))
        {
            LogWebPlaybackAssets(payload.RootElement);
            return null;
        }

        return new AppleWebPlaybackResult(hlsPlaylistUrl ?? string.Empty, assetUrl);
    }

    public async Task<string?> GetWebPlaybackPlaylistAsync(string adamId, string authorizationToken, string mediaUserToken, CancellationToken cancellationToken)
    {
        var payload = await GetPayloadJsonAsync(adamId, authorizationToken, mediaUserToken, cancellationToken);
        if (payload == null)
        {
            return null;
        }

        var (hlsPlaylistUrl, _) = ParseWebPlaybackPayload(payload.RootElement);
        return hlsPlaylistUrl;
    }

    private async Task<JsonDocument?> GetPayloadJsonAsync(string adamId, string authorizationToken, string mediaUserToken, CancellationToken cancellationToken)
    {
        if (!ValidatePlaybackInputs(adamId, authorizationToken, mediaUserToken))
        {
            return null;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Apple webPlayback request starting: adamId={AdamId}, authToken={AuthTokenLength}chars, mediaUserToken={MediaUserTokenLength}chars",
                adamId,
                authorizationToken.Length,
                mediaUserToken.Length);        }

        var client = _httpClientFactory.CreateClient();
        const int maxAttempts = 3;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            using var request = BuildWebPlaybackRequest(adamId, authorizationToken, mediaUserToken);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await ParseWebPlaybackResponseAsync(response, adamId, cancellationToken);
            }
            await LogPlaybackFailureAsync(response, attempt, maxAttempts, cancellationToken);
            if (await DelayForPlaybackRetryIfNeededAsync(response, attempt, maxAttempts, cancellationToken))
            {
                continue;
            }

            return null;
        }

        return null;
    }

    private async Task LogPlaybackFailureAsync(
        HttpResponseMessage response,
        int attempt,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogWarning(
            "Apple webPlayback request failed: StatusCode={StatusCode}, Attempt={Attempt}/{MaxAttempts}, Response={Response}",
            response.StatusCode,
            attempt + 1,
            maxAttempts,
            errorBody.Length > 500 ? errorBody[..500] + "..." : errorBody);
        LogPlaybackAuthFailure(response.StatusCode);
    }

    private async Task<bool> DelayForPlaybackRetryIfNeededAsync(
        HttpResponseMessage response,
        int attempt,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        if (!ShouldRetryPlayback(response.StatusCode, attempt, maxAttempts))
        {
            return false;
        }

        var delay = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
            ? response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt))
            : TimeSpan.FromSeconds(Math.Pow(2, attempt));
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Apple webPlayback retry ({StatusCode}) after {Delay}ms...", response.StatusCode, delay.TotalMilliseconds);
        }
        await Task.Delay(delay, cancellationToken);
        return true;
    }

    private bool ValidatePlaybackInputs(string adamId, string authorizationToken, string mediaUserToken)
    {
        if (string.IsNullOrWhiteSpace(adamId))
        {
            _logger.LogWarning("Apple webPlayback request failed: adamId is empty or null.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(authorizationToken))
        {
            _logger.LogWarning("Apple webPlayback request failed: authorizationToken is empty or null. This token is required for AAC downloads.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(mediaUserToken))
        {
            _logger.LogWarning("Apple webPlayback request failed: mediaUserToken is empty or null. This token is required for AAC downloads.");
            return false;
        }

        if (mediaUserToken.Length < 50)
        {
            _logger.LogWarning(
                "Apple webPlayback request failed: mediaUserToken appears invalid (length={Length}, expected >50). Check your media-user-token configuration.",
                mediaUserToken.Length);
            return false;
        }

        return true;
    }

    private static HttpRequestMessage BuildWebPlaybackRequest(string adamId, string authorizationToken, string mediaUserToken)
    {
        var playbackUri = new UriBuilder(Uri.UriSchemeHttps, ApplePlaybackHost)
        {
            Path = "WebObjects/MZPlay.woa/wa/webPlayback"
        }.Uri;
        var storefrontBaseUri = new UriBuilder(Uri.UriSchemeHttps, AppleStorefrontHost).Uri;

        var request = new HttpRequestMessage(HttpMethod.Post, playbackUri)
        {
            Content = JsonContent.Create(new { salableAdamId = adamId })
        };

        request.Headers.TryAddWithoutValidation("Origin", storefrontBaseUri.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation("Referer", $"{storefrontBaseUri.GetLeftPart(UriPartial.Authority)}/");
        request.Headers.TryAddWithoutValidation("User-Agent", AppleUserAgentPool.GetAuthenticatedUserAgent());
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {authorizationToken}");
        request.Headers.TryAddWithoutValidation("x-apple-music-user-token", mediaUserToken);
        request.Headers.TryAddWithoutValidation("Media-User-Token", mediaUserToken);
        request.Headers.TryAddWithoutValidation("Cookie", $"media-user-token={mediaUserToken}");
        request.Headers.TryAddWithoutValidation("x-apple-renewal", "true");
        return request;
    }

    private async Task<JsonDocument?> ParseWebPlaybackResponseAsync(
        HttpResponseMessage response,
        string adamId,
        CancellationToken cancellationToken)
    {
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            var doc = JsonDocument.Parse(responseBody);
            return IsUsablePayload(doc, adamId, responseBody) ? doc : null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Apple webPlayback response JSON parse failed. Response: {Response}",
                responseBody.Length > 500 ? responseBody[..500] + "..." : responseBody);
            return null;
        }
    }

    private bool IsUsablePayload(JsonDocument doc, string adamId, string responseBody)
    {
        if (doc.RootElement.TryGetProperty("failureType", out var failureType))
        {
            var failureCode = failureType.GetString() ?? "";
            var customerMessage = doc.RootElement.TryGetProperty("customerMessage", out var msgProp)
                ? msgProp.GetString() ?? "Unknown error"
                : "Unknown error";

            _logger.LogWarning(
                "Apple webPlayback returned error for adamId={AdamId}: failureType={FailureType}, message={Message}",
                adamId,
                failureCode,
                customerMessage);

            if (failureCode == "3077")
            {
                _logger.LogError(
                    "Apple webPlayback error 3077: Track not available through AAC-LC API. Try using ALAC/Atmos with the device wrapper, or the track may be region-restricted.");
            }

            return false;
        }

        if (!doc.RootElement.TryGetProperty("songList", out var songList))
        {
            _logger.LogWarning(
                "Apple webPlayback response missing songList for adamId={AdamId}. Response: {Response}",
                adamId,
                responseBody.Length > 500 ? responseBody[..500] + "..." : responseBody);
            return false;
        }

        if (songList.ValueKind != JsonValueKind.Array || songList.GetArrayLength() == 0)
        {
            _logger.LogWarning(
                "Apple webPlayback returned empty songList for adamId={AdamId}. This may indicate an invalid/expired mediaUserToken or regional restriction.",
                adamId);
            return false;
        }

        return true;
    }

    private void LogPlaybackAuthFailure(System.Net.HttpStatusCode statusCode)
    {
        if (statusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _logger.LogError(
                "Apple webPlayback 401 Unauthorized: Your authorizationToken may be expired or invalid. Try refreshing it.");
            return;
        }

        if (statusCode == System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogError(
                "Apple webPlayback 403 Forbidden: Your mediaUserToken may be expired or invalid. Try re-extracting it from music.apple.com cookies.");
        }
    }

    private static bool ShouldRetryPlayback(System.Net.HttpStatusCode statusCode, int attempt, int maxAttempts)
    {
        if (attempt >= maxAttempts - 1)
        {
            return false;
        }

        return statusCode == System.Net.HttpStatusCode.TooManyRequests || (int)statusCode >= 500;
    }

    private static (string? HlsPlaylistUrl, string? AssetUrl) ParseWebPlaybackPayload(JsonElement root)
    {
        if (!TryGetSongList(root, out var songList))
        {
            return (null, null);
        }

        var entry = songList[0];
        var hlsPlaylistUrl = TryGetStringProperty(entry, "hls-playlist-url");
        if (IsPreviewUrl(hlsPlaylistUrl))
        {
            return (null, null);
        }

        if (!TryGetAssetsArray(entry, out var assets))
        {
            return (hlsPlaylistUrl, null);
        }

        return (hlsPlaylistUrl, FindPreferredAssetUrl(assets));
    }

    private static bool TryGetSongList(JsonElement root, out JsonElement songList)
    {
        if (!root.TryGetProperty("songList", out songList))
        {
            return false;
        }

        return songList.ValueKind == JsonValueKind.Array && songList.GetArrayLength() > 0;
    }

    private static bool TryGetAssetsArray(JsonElement entry, out JsonElement assets)
    {
        if (!entry.TryGetProperty("assets", out assets))
        {
            return false;
        }

        return assets.ValueKind == JsonValueKind.Array;
    }

    private static string? TryGetStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var propertyValue) || propertyValue.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return propertyValue.GetString();
    }

    private static string? FindPreferredAssetUrl(JsonElement assets)
    {
        foreach (var asset in assets.EnumerateArray())
        {
            var flavor = TryGetStringProperty(asset, "flavor");
            if (IsPreviewAsset(asset, flavor))
            {
                continue;
            }

            if (!string.Equals(flavor, "28:ctrp256", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = TryGetStringProperty(asset, "URL");
            return IsPreviewUrl(url) ? null : url;
        }

        return null;
    }

    private static bool IsPreviewAsset(JsonElement asset, string? flavor)
    {
        if (ContainsPreviewMarker(flavor))
        {
            return true;
        }

        foreach (var property in asset.EnumerateObject())
        {
            if (ContainsPreviewMarker(property.Name))
            {
                return true;
            }

            if (property.Value.ValueKind == JsonValueKind.String && ContainsPreviewMarker(property.Value.GetString()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPreviewUrl(string? url) => ContainsPreviewMarker(url);

    private static bool ContainsPreviewMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return PreviewUrlMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private void LogWebPlaybackAssets(JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("songList", out var songList) || songList.ValueKind != JsonValueKind.Array || songList.GetArrayLength() == 0)
            {
                return;
            }

            var entry = songList[0];
            var hasPlaylist = entry.TryGetProperty("hls-playlist-url", out var hlsProp) && hlsProp.ValueKind == JsonValueKind.String;
            if (!entry.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("Apple webPlayback assets missing. hlsPlaylistPresent={HasPlaylist}", hasPlaylist);
                return;
            }

            var flavors = new List<string>();
            foreach (var asset in assets.EnumerateArray()
                         .Where(static asset => asset.TryGetProperty("flavor", out var flavorProp)
                             && flavorProp.ValueKind == JsonValueKind.String
                             && !string.IsNullOrWhiteSpace(flavorProp.GetString())))
            {
                var flavor = asset.GetProperty("flavor").GetString()!;
                var hasUrl = asset.TryGetProperty("URL", out var urlProp) && urlProp.ValueKind == JsonValueKind.String;
                flavors.Add(hasUrl ? $"{flavor}:url" : $"{flavor}:no-url");
            }

            _logger.LogWarning(
                "Apple webPlayback asset selection failed. hlsPlaylistPresent={HasPlaylist} flavors=[{Flavors}]",
                hasPlaylist,
                string.Join(", ", flavors));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Apple webPlayback asset logging failed.");
        }
    }
}

public sealed record AppleWebPlaybackResult(string HlsPlaylistUrl, string AssetUrl);
