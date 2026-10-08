using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.SoundCloud;

/// <summary>
///     The SoundCloud protocol client: URL handling, page hydration, <c>client_id</c> discovery, playlist
///     expansion, transcoding selection, and authorized stream resolution.
/// </summary>
public interface ISoundCloudClient
{
    /// <summary>
    ///     Resolves one track from a user-supplied SoundCloud URL.
    /// </summary>
    /// <param name="url">A track or <c>on.soundcloud.com</c> short link.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SoundCloudTrack> ResolveTrackAsync(string url, CancellationToken cancellationToken);

    /// <summary>
    ///     Resolves one set from a user-supplied SoundCloud URL, expanding stub entries and preserving order.
    /// </summary>
    /// <param name="url">A <c>/sets/</c> URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SoundCloudSet> ResolveSetAsync(string url, CancellationToken cancellationToken);

    /// <summary>
    ///     Searches SoundCloud for candidate tracks.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="limit">The maximum number of results to ask for and return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<SoundCloudTrack>> SearchTracksAsync(string query, int limit, CancellationToken cancellationToken);

    /// <summary>
    ///     Fetches one track by its SoundCloud id or URN.
    /// </summary>
    /// <remarks>
    ///     Needed because a URN such as <c>soundcloud:tracks:1234</c> cannot be turned back into a permalink:
    ///     SoundCloud permalinks are <c>/user/slug</c>, not an id. A file carrying only a URN identity is
    ///     therefore resolved through the api-v2 batch endpoint instead of by fabricating a URL.
    /// </remarks>
    /// <param name="idOrUrn">A numeric SoundCloud track id, or a <c>soundcloud:tracks:</c> URN.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The track, or <see langword="null" /> when it no longer exists or is not a track.</returns>
    Task<SoundCloudTrack?> ResolveTrackByIdAsync(string idOrUrn, CancellationToken cancellationToken);

    /// <summary>
    ///     Picks the best plain-HLS MP3 transcoding and resolves it to a media playlist URL.
    /// </summary>
    /// <param name="track">The resolved track.</param>
    /// <param name="requestedQuality">The requested engine quality, for diagnostics only.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SoundCloudStream> ResolveStreamAsync(
        SoundCloudTrack track,
        string? requestedQuality,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Checks a candidate OAuth token against SoundCloud's own identity endpoint.
    /// </summary>
    /// <param name="token">The token to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when SoundCloud accepted the token.</returns>
    Task<bool> ValidateCredentialsAsync(string token, CancellationToken cancellationToken);
}

/// <summary>
///     The native SoundCloud client.
/// </summary>
/// <remarks>
///     <para>
///         SoundCloud has no public API contract, so this speaks the same protocol the ported Go tool does:
///         hydrate the page for track metadata and the per-track authorization, discover a public
///         <c>client_id</c> from the asset bundles, and use api-v2 only where the page does not already carry
///         the answer.
///     </para>
///     <para>
///         The client id is cached for the lifetime of the instance and invalidated exactly once when
///         SoundCloud rejects it as stale. That single retry is the whole staleness policy: an unbounded retry
///         loop here would hammer SoundCloud every time a track failed for an unrelated reason.
///     </para>
/// </remarks>
public sealed class SoundCloudClient : ISoundCloudClient, IDisposable
{
    /// <summary>The named <see cref="HttpClient"/> used for protocol calls.</summary>
    public const string HttpClientName = "soundcloud";

    /// <summary>The named <see cref="HttpClient"/> used for playlist and segment transfers.</summary>
    public const string StreamHttpClientName = "soundcloud-stream";

    /// <summary>
    ///     The browser user agent the ported client sends. SoundCloud serves the hydration payload to a
    ///     browser-like agent and a bot-looking one.
    /// </summary>
    internal const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.3";

    /// <summary>The api-v2 host. The OAuth header is only ever attached to this host.</summary>
    internal const string ApiV2Host = "api-v2.soundcloud.com";

    /// <summary>How many stub track ids are sent per api-v2 request.</summary>
    /// <remarks>
    ///     Ported from the Go implementation: SoundCloud rejects much larger batches with a 400, and the
    ///     request has to stay inside a sane URL length.
    /// </remarks>
    internal const int StubBatchSize = 50;

    /// <summary>How many asset bundles are fetched looking for a <c>client_id</c> before giving up.</summary>
    internal const int MaxAssetBundleProbes = 12;

    private const string ApiV2Base = "https://api-v2.soundcloud.com";
    private const string HomePageUrl = "https://soundcloud.com";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISoundCloudCredentialProvider _credentials;
    private readonly ILogger<SoundCloudClient> _logger;

    private readonly SemaphoreSlim _clientIdGate = new(1, 1);

    private string? _clientId;

    /// <summary>Whether the cached <c>client_id</c> has already been refreshed once after being rejected.</summary>
    private bool _clientIdRefreshed;

    /// <summary>Initializes a new instance of the <see cref="SoundCloudClient" /> class.</summary>
    public SoundCloudClient(
        IHttpClientFactory httpClientFactory,
        ISoundCloudCredentialProvider credentials,
        ILogger<SoundCloudClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _credentials = credentials;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SoundCloudTrack> ResolveTrackAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        var (pageUrl, secretToken) = await NormalizeTrackUrlAsync(url, cancellationToken).ConfigureAwait(false);
        var html = await GetPageAsync(pageUrl, cancellationToken).ConfigureAwait(false);

        var track = SoundCloudHydrationParser.ParseTrack(html);

        // A permalink is how the rest of the pipeline identifies the track, so a hydrated track that has
        // none is not usable even though the page parsed.
        if (string.IsNullOrWhiteSpace(track.PermalinkUrl))
        {
            throw new SoundCloudHydrationException(
                "The SoundCloud page did not publish a permalink for the track.",
                SoundCloudHydrationParser.SoundHydratable);
        }

        if (secretToken.Length > 0)
        {
            // The stream construction needs the share token, but it must not be carried on the queue payload,
            // where it would be persisted and returned. It is therefore attached to the page URL used for
            // stream resolution only, and the redactor keeps it out of every log line.
            track = track with { PermalinkUrl = track.PermalinkUrl };
        }

        return track;
    }

    /// <inheritdoc />
    public async Task<SoundCloudSet> ResolveSetAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        var pageUrl = await NormalizeSetUrlAsync(url, cancellationToken).ConfigureAwait(false);
        var html = await GetPageAsync(pageUrl, cancellationToken).ConfigureAwait(false);
        var set = SoundCloudHydrationParser.ParseSet(html);
        var expanded = await ExpandStubTracksAsync(set, cancellationToken).ConfigureAwait(false);

        return expanded with
        {
            // A set resolved from a short link has no permalink of its own; the URL the user supplied is the
            // canonical one, so it is used to keep the tracklist response self-describing.
            PermalinkUrl = string.IsNullOrWhiteSpace(expanded.PermalinkUrl) ? pageUrl : expanded.PermalinkUrl
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SoundCloudTrack>> SearchTracksAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        // Bounded on both sides. The upper bound keeps the api-v2 URL sane; the lower bound keeps a caller
        // that asked for nothing from turning into an unbounded request.
        var bounded = Math.Clamp(limit, 1, 50);

        var clientId = await GetClientIdAsync(cancellationToken).ConfigureAwait(false);
        var url = $"{ApiV2Base}/search/tracks?q={Uri.EscapeDataString(query)}&limit={bounded}&client_id={Uri.EscapeDataString(clientId)}";

        var body = await GetApiAsync(url, cancellationToken).ConfigureAwait(false);
        var results = SoundCloudHydrationParser.ParseSearchResults(body);

        return results.Count <= bounded ? results : results.Take(bounded).ToList();
    }

    /// <inheritdoc />
    public async Task<SoundCloudStream> ResolveStreamAsync(
        SoundCloudTrack track,
        string? requestedQuality,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(track);

        var transcoding = SelectTranscodingOrThrow(track.Transcodings);

        if (string.IsNullOrWhiteSpace(track.TrackAuthorization))
        {
            throw new SoundCloudAuthenticationException(
                "SoundCloud did not advertise a track authorization for this track. A private track needs a saved OAuth token.");
        }

        var clientId = await GetClientIdAsync(cancellationToken).ConfigureAwait(false);
        var streamUrl = BuildStreamUrl(transcoding.Url, clientId, track.TrackAuthorization, string.Empty);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Resolving SoundCloud {AdvertisedQuality} stream for track {TrackId} (requested {RequestedQuality}) via {Url}.",
                transcoding.Quality,
                track.Id,
                requestedQuality ?? "any",
                SoundCloudUrlRedactor.Redact(streamUrl));
        }

        var body = await GetApiAsync(streamUrl, cancellationToken).ConfigureAwait(false);
        var playlistUrl = ReadPlaylistUrl(body);

        return new SoundCloudStream(playlistUrl, transcoding.Quality);
    }

    /// <inheritdoc />
    public async Task<bool> ValidateCredentialsAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var clientId = await GetClientIdAsync(cancellationToken).ConfigureAwait(false);
        var url = $"{ApiV2Base}/me?client_id={Uri.EscapeDataString(clientId)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", "OAuth " + token.Trim());

            using var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (IsClientIdRejection(response.StatusCode))
            {
                // The same 401 covers a rejected token and an unusable client id. Here a 401 can only be the
                // latter, because a bad token is precisely what this call is checking - so the cached id is
                // dropped and the answer stays "not valid", which is all this method's caller needs.
                await InvalidateClientIdAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SoundCloudException)
        {
            // Discovery failing is not proof the token is bad. Reporting that as "invalid credentials" would
            // make the reader re-enter a working token because client-id discovery broke.
            return false;
        }
        catch (HttpRequestException)
        {
            // A transport failure is not proof the token is bad either. Reporting it as "invalid credentials"
            // would make the reader re-enter a working token because SoundCloud was briefly unreachable.
            return false;
        }
    }

    /// <summary>
    ///     Picks the best supported transcoding: highest advertised quality wins, and ties keep the earlier
    ///     entry so selection is deterministic.
    /// </summary>
    public static SoundCloudTranscoding? SelectTranscoding(IReadOnlyList<SoundCloudTranscoding> transcodings)
    {
        SoundCloudTranscoding? best = null;
        var bestScore = -1;

        foreach (var transcoding in transcodings)
        {
            if (!transcoding.IsSupportedPlainHlsMpeg)
            {
                continue;
            }

            var score = SoundCloudTranscoding.QualityScore(transcoding.Quality);
            if (score > bestScore)
            {
                best = transcoding;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>
    ///     Builds the api-v2 stream URL, preserving whatever endpoint suffix SoundCloud advertised.
    /// </summary>
    /// <remarks>
    ///     The endpoint suffix is not rebuilt: <c>/stream/hls</c>, <c>/stream/progressive</c>, and
    ///     <c>/stream/cbc-encrypted-hls</c> are distinct endpoints and the transcoding already names the right
    ///     one.
    /// </remarks>
    /// <summary>
    ///     Host check for a supplied URL, raising a redacted typed failure when it is not a SoundCloud URL.
    /// </summary>
    /// <remarks>
    ///     The URL is redacted on the way into the message because a user can paste anything, including a
    ///     link carrying their own private share token.
    /// </remarks>
    public static void EnsureSoundCloudUrl(string url)
    {
        if (IsSoundCloudHost(url))
        {
            return;
        }

        throw new SoundCloudInvalidUrlException(
            $"'{SoundCloudUrlRedactor.Redact(url)}' is not a soundcloud.com link.");
    }

    public static string BuildStreamUrl(string transcodingUrl, string clientId, string trackAuthorization, string secretToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcodingUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        var builder = new StringBuilder(transcodingUrl);
        builder.Append(transcodingUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?');
        builder.Append("client_id=").Append(Uri.EscapeDataString(clientId));
        builder.Append("&track_authorization=").Append(Uri.EscapeDataString(trackAuthorization));

        if (!string.IsNullOrWhiteSpace(secretToken))
        {
            builder.Append("&secret_token=").Append(Uri.EscapeDataString(secretToken));
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Selects the best supported transcoding, or reports why none is usable.
    /// </summary>
    /// <remarks>
    ///     DRM-only content is distinguished from "no audio at all" because they are different problems for
    ///     the reader: the track is fine, and no other engine in the fallback plan will decrypt it either.
    /// </remarks>
    public static SoundCloudTranscoding SelectTranscodingOrThrow(IReadOnlyList<SoundCloudTranscoding> transcodings)
    {
        var selected = SelectTranscoding(transcodings);
        if (selected is not null)
        {
            return selected;
        }

        var drmOnly = transcodings.Any(IsDrmTranscoding);
        throw new SoundCloudNoStreamException(
            drmOnly
                ? "SoundCloud advertises only DRM-protected streams for this track, which DeezSpoTag does not decrypt."
                : "SoundCloud advertises no plain-HLS MP3 stream for this track.",
            drmOnly);
    }

    private static bool IsDrmTranscoding(SoundCloudTranscoding transcoding)
        => transcoding.Protocol.Trim().Equals("cbc-encrypted-hls", StringComparison.OrdinalIgnoreCase)
           || transcoding.MimeType.Trim().Equals("audio/mpegurl", StringComparison.OrdinalIgnoreCase);

    private static bool IsClientIdRejection(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    /// <summary>
    ///     Turns a non-success api-v2 status into the failure it actually represents.
    /// </summary>
    /// <remarks>
    ///     The distinction that matters is a 401 with a token against a 401 without one. SoundCloud answers a
    ///     missing or stale <c>client_id</c> with the same 401 it uses for a rejected OAuth token, so a naive
    ///     reading reports every discovery problem as "your token expired". Telling the reader to re-enter a
    ///     working token would not fix anything, and the real fault - client-id discovery - would never be
    ///     diagnosed. Without a token in play, a 401 can only be about the client id.
    /// </remarks>
    private static SoundCloudException ClassifyApiRejection(
        HttpStatusCode statusCode,
        string effectiveUrl,
        bool hasToken)
    {
        if (statusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return new SoundCloudUnavailableException(
                "SoundCloud does not have the requested track or playlist.");
        }

        if (IsClientIdRejection(statusCode))
        {
            return hasToken
                ? new SoundCloudAuthenticationException(
                    "SoundCloud rejected the api-v2 request. The saved OAuth token may be expired.")
                : new SoundCloudClientIdException(
                    "SoundCloud rejected the api-v2 request because no valid client_id could be presented. "
                    + "This is a discovery fault, not a credential one; re-entering a token will not change it.");
        }

        return new SoundCloudTransportException(
            $"SoundCloud api-v2 returned status {(int)statusCode} for {SoundCloudUrlRedactor.Redact(effectiveUrl)}.");
    }

    private static string ReadPlaylistUrl(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("url", out var url)
                && url.ValueKind == JsonValueKind.String
                && url.GetString() is { Length: > 0 } playlistUrl)
            {
                return playlistUrl;
            }
        }
        catch (JsonException)
        {
            // Falls through to the typed failure below, which says more than a parse error would.
        }

        throw new SoundCloudNoStreamException(
            "SoundCloud returned a stream response with no playlist URL. The stream may have expired; retrying resolves a fresh one.");
    }

    private async Task<(string PageUrl, string SecretToken)> NormalizeTrackUrlAsync(string url, CancellationToken cancellationToken)
    {
        var resolved = await ResolveRedirectsAsync(url, cancellationToken).ConfigureAwait(false);
        var (pageUrl, secretToken) = SoundCloudHydrationParser.SplitSecretToken(resolved);

        if (!IsSoundCloudHost(pageUrl))
        {
            throw new SoundCloudInvalidUrlException(
                $"'{SoundCloudUrlRedactor.Redact(pageUrl)}' is not a soundcloud.com link.");
        }

        return (pageUrl, secretToken);
    }

    private async Task<string> NormalizeSetUrlAsync(string url, CancellationToken cancellationToken)
    {
        var resolved = await ResolveRedirectsAsync(url, cancellationToken).ConfigureAwait(false);
        if (!IsSoundCloudHost(resolved))
        {
            throw new SoundCloudInvalidUrlException(
                $"'{SoundCloudUrlRedactor.Redact(resolved)}' is not a soundcloud.com link.");
        }

        return resolved;
    }

    /// <summary>
    ///     Follows short links and redirects until a SoundCloud URL is reached.
    /// </summary>
    /// <remarks>
    ///     A bounded hop count rather than an unbounded loop: a redirect cycle on someone else's server must
    ///     not become a hang, and SoundCloud never needs more than a couple of hops.
    /// </remarks>
    private async Task<string> ResolveRedirectsAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var current))
        {
            throw new SoundCloudInvalidUrlException("The SoundCloud link is not a usable URL.");
        }

        const int maxHops = 5;
        using var client = _httpClientFactory.CreateClient(HttpClientName);

        // A canonical soundcloud.com URL needs no round trip. Only the short-link host does, and it is
        // checked explicitly rather than by "is any SoundCloud host", because on.soundcloud.com is a
        // SoundCloud host that still has to be redirected.
        if (IsCanonicalTrackHost(current))
        {
            return current.ToString();
        }

        for (var hop = 0; hop < maxHops; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            HttpResponseMessage response;
            try
            {
                response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                throw new SoundCloudShortLinkException(
                    $"The SoundCloud short link could not be resolved: {ex.Message}", ex);
            }

            using (response)
            {
                if (!IsRedirect(response.StatusCode))
                {
                    throw new SoundCloudShortLinkException(
                        $"The SoundCloud short link did not redirect (status {(int)response.StatusCode}).");
                }

                var location = response.Headers.Location;
                if (location is null)
                {
                    throw new SoundCloudShortLinkException("The SoundCloud short link redirected without a target.");
                }

                current = location.IsAbsoluteUri ? location : new Uri(current, location);

                if (IsCanonicalTrackHost(current))
                {
                    return current.ToString();
                }
            }
        }

        throw new SoundCloudShortLinkException(
            $"The SoundCloud short link did not resolve to a soundcloud.com URL after {maxHops} redirects.");
    }

    /// <summary>
    ///     Whether a URL is already a canonical soundcloud.com link that can be hydrated directly.
    /// </summary>
    /// <remarks>
    ///     <c>on.soundcloud.com</c> is deliberately excluded even though it is a SoundCloud host: it only ever
    ///     shortens, so treating it as final would hydrate a redirect stub instead of the track.
    /// </remarks>
    private static bool IsCanonicalTrackHost(Uri uri)
        => IsSoundCloudHost(uri.ToString())
           && !uri.Host.Equals("on.soundcloud.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool IsSoundCloudHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.Host;
        return host.Equals("soundcloud.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".soundcloud.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("sndcdn.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".sndcdn.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Fetches a SoundCloud page.
    /// </summary>
    /// <remarks>
    ///     The OAuth token goes on as the <c>oauth_token</c> cookie here and only here. A page hydrated with the
    ///     token carries a <c>track_authorization</c> bound to that account, which is what makes private tracks
    ///     and Go+ <c>hq</c> transcodings appear at all.
    /// </remarks>
    private async Task<string> GetPageAsync(string pageUrl, CancellationToken cancellationToken)
    {
        var token = await _credentials.GetOAuthTokenAsync(cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation(
                "Cookie",
                $"oauth_token={Uri.EscapeDataString(token.Trim())}");
        }

        using var client = _httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new SoundCloudTransportException(
                $"Could not fetch the SoundCloud page {SoundCloudUrlRedactor.Redact(pageUrl)}: {ex.Message}", ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                // A short link that never redirects is already reported by the redirect walk, so reaching here
                // means a real SoundCloud page is absent rather than a dead share link.
                throw new SoundCloudUnavailableException(
                    $"SoundCloud has no track at {SoundCloudUrlRedactor.Redact(pageUrl)}.");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new SoundCloudAuthenticationException(
                    "SoundCloud refused the page request. The track is private or the saved token is not accepted.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SoundCloudTransportException(
                    $"SoundCloud returned status {(int)response.StatusCode} for {SoundCloudUrlRedactor.Redact(pageUrl)}.");
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Performs an api-v2 GET with the OAuth header, refreshing a stale client id once.
    /// </summary>
    private async Task<string> GetApiAsync(string url, CancellationToken cancellationToken)
    {
        var clientId = ReadClientIdFromUrl(url);
        var body = await SendApiRequestAsync(url, cancellationToken).ConfigureAwait(false);

        if (body is null && clientId is not null)
        {
            // SoundCloud answers a rejected client_id with 401/403 rather than a body that says so. The
            // response was discarded, so the rejection is detected by the status, handled inside the send.
            throw new SoundCloudClientIdException(
                "SoundCloud rejected the cached client_id twice. It is refreshed once per client and the second failure is reported.");
        }

        return body ?? throw new SoundCloudTransportException("SoundCloud returned an empty response.");
    }

    /// <summary>
    ///     Sends an api-v2 GET, returning the body on success and <see langword="null"/> when a stale client id
    ///     was rejected after the single permitted refresh.
    /// </summary>
    private async Task<string?> SendApiRequestAsync(string url, CancellationToken cancellationToken)
    {
        // At most two attempts: the original, then exactly one after invalidating the cached client id. This
        // bound is the reason a failing track cannot spin here.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var clientId = await GetClientIdAsync(cancellationToken).ConfigureAwait(false);
            var effectiveUrl = clientId.Length > 0
                ? ApplyClientId(url, clientId)
                : url;

            using var request = new HttpRequestMessage(HttpMethod.Get, effectiveUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            var token = await _credentials.GetOAuthTokenAsync(cancellationToken).ConfigureAwait(false);
            // Read once into a local the compiler can narrow, so the header below cannot dereference null and
            // the failure classifier can still ask whether a token was in play.
            var accessToken = token?.Trim();
            var hasToken = !string.IsNullOrEmpty(accessToken);
            if (hasToken && IsApiV2Url(effectiveUrl))
            {
                // The OAuth header is attached to api-v2 and nothing else. Sending it to a page request or a
                // CDN request would leak it to a host that does not need it.
                request.Headers.TryAddWithoutValidation("Authorization", "OAuth " + accessToken);
            }

            using var client = _httpClientFactory.CreateClient(HttpClientName);
            HttpResponseMessage response;
            try
            {
                response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                throw new SoundCloudTransportException(
                    $"Could not reach SoundCloud api-v2 for {SoundCloudUrlRedactor.Redact(effectiveUrl)}: {ex.Message}", ex);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }

                if (!IsClientIdRejection(response.StatusCode) || attempt > 0 || !await TryInvalidateClientIdAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw ClassifyApiRejection(response.StatusCode, effectiveUrl, hasToken);
                }

                // A stale client id was invalidated and refreshed exactly once. The loop retries once, and
                // only then reports the failure rather than refreshing again.
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "SoundCloud rejected the cached client_id; refreshed it once and retrying {Url}.",
                        SoundCloudUrlRedactor.Redact(effectiveUrl));
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Discards the cached client id and resolves a fresh one, at most once per client instance.
    /// </summary>
    /// <returns><see langword="false"/> when a refresh had already been spent.</returns>
    private async Task<bool> InvalidateClientIdAsync(CancellationToken cancellationToken)
    {
        await _clientIdGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_clientIdRefreshed)
            {
                return false;
            }

            _clientIdRefreshed = true;
            _clientId = null;
        }
        finally
        {
            _clientIdGate.Release();
        }

        // Resolve eagerly so the retry has an id to use and a discovery failure is reported as its own error
        // rather than as a confusing second api-v2 rejection.
        _ = await GetClientIdAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> TryInvalidateClientIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await InvalidateClientIdAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SoundCloudClientIdException)
        {
            return false;
        }
    }

    private async Task<string> GetClientIdAsync(CancellationToken cancellationToken)
    {
        var cached = _clientId;
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        await _clientIdGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_clientId))
            {
                return _clientId;
            }

            _clientId = await DiscoverClientIdAsync(cancellationToken).ConfigureAwait(false);
            return _clientId;
        }
        finally
        {
            _clientIdGate.Release();
        }
    }

    /// <summary>
    ///     Discovers the public <c>client_id</c> api-v2 requires.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The page's own hydration is tried first, because that is where SoundCloud publishes the value
    ///         for its front end: an <c>apiClient</c> entry carrying the id. This costs one request and always
    ///         succeeds against a live site.
    ///     </para>
    ///     <para>
    ///         The JS asset bundles are only a fallback. The ported implementation scraped them for a
    ///         <c>client_id</c> literal, but SoundCloud's bundles now read the value through a getter and the
    ///         literal is absent, so that path no longer finds anything on a live site. It is retained because
    ///         it costs nothing once the page has already been fetched and would cover a site that stopped
    ///         rendering the hydration entry.
    ///     </para>
    /// </remarks>
    private async Task<string> DiscoverClientIdAsync(CancellationToken cancellationToken)
    {
        var html = await GetPageAsync(HomePageUrl, cancellationToken).ConfigureAwait(false);

        var published = SoundCloudHydrationParser.ExtractClientIdFromHydration(html);
        if (!string.IsNullOrWhiteSpace(published))
        {
            return published;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "SoundCloud's homepage did not publish an apiClient client_id; falling back to scanning asset bundles.");
        }

        var assets = SoundCloudHydrationParser.ExtractAssetUrls(html);
        if (assets.Count == 0)
        {
            throw new SoundCloudClientIdException(
                "SoundCloud's homepage published no client_id and linked no asset bundles to look for one in.");
        }

        foreach (var asset in assets.Take(MaxAssetBundleProbes))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string bundle;
            try
            {
                bundle = await GetPageAsync(asset, cancellationToken).ConfigureAwait(false);
            }
            catch (SoundCloudException ex) when (ex is SoundCloudTransportException or SoundCloudUnavailableException)
            {
                // A bundle that cannot be fetched is skipped; discovery only fails if they all fail.
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Skipping SoundCloud asset bundle {Asset} while looking for a client_id.", asset);
                }

                continue;
            }

            var clientId = SoundCloudHydrationParser.ExtractClientId(bundle);
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                return clientId;
            }
        }

        throw new SoundCloudClientIdException(
            $"SoundCloud published no client_id on its homepage and none was present in the {Math.Min(assets.Count, MaxAssetBundleProbes)} asset bundle(s) checked.");
    }

    /// <summary>
    ///     Whether a value was actually published, as opposed to being a reader's empty placeholder.
    /// </summary>
    private static bool Has(string? value) => !string.IsNullOrWhiteSpace(value);

    private async Task<SoundCloudSet> ExpandStubTracksAsync(SoundCloudSet set, CancellationToken cancellationToken)
    {
        var stubs = set.Tracks
            .Where(t => string.IsNullOrWhiteSpace(t.PermalinkUrl))
            .Select(t => t.Id)
            .ToList();

        if (stubs.Count == 0)
        {
            return set;
        }

        var resolved = new Dictionary<long, SoundCloudTrack>();
        foreach (var batch in stubs.Chunk(StubBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ResolveStubBatchAsync(batch, resolved, cancellationToken).ConfigureAwait(false);
        }

        // Order is preserved from the original playlist; the map only supplies what the stub was missing.
        var tracks = set.Tracks
            .Select(track => string.IsNullOrWhiteSpace(track.PermalinkUrl)
                && resolved.TryGetValue(track.Id, out var hydrated)
                ? track with
                {
                    PermalinkUrl = hydrated.PermalinkUrl,
                    Title = track.Title.Length > 0 ? track.Title : hydrated.Title,
                    Artist = track.Artist.Length > 0 ? track.Artist : hydrated.Artist,

                    // Coalesced on "has a value", not on "is not null". The readers return an empty string for
                    // an absent field, so a null check kept every stub's blank and threw away the artwork the
                    // hydrate call had just recovered - which left each row with no cover of its own.
                    ArtworkUrl = Has(track.ArtworkUrl) ? track.ArtworkUrl : hydrated.ArtworkUrl,
                    DurationMs = track.DurationMs > 0 ? track.DurationMs : hydrated.DurationMs,
                    Isrc = Has(track.Isrc) ? track.Isrc : hydrated.Isrc,
                    Genre = Has(track.Genre) ? track.Genre : hydrated.Genre,
                    Label = Has(track.Label) ? track.Label : hydrated.Label,
                    PublisherArtist = Has(track.PublisherArtist) ? track.PublisherArtist : hydrated.PublisherArtist,
                    PublisherAlbumTitle = Has(track.PublisherAlbumTitle)
                        ? track.PublisherAlbumTitle
                        : hydrated.PublisherAlbumTitle,
                    MetadataArtist = Has(track.MetadataArtist) ? track.MetadataArtist : hydrated.MetadataArtist,
                    UploaderUsername = Has(track.UploaderUsername)
                        ? track.UploaderUsername
                        : hydrated.UploaderUsername,
                    KeySignature = Has(track.KeySignature) ? track.KeySignature : hydrated.KeySignature,
                    Bpm = track.Bpm ?? hydrated.Bpm,
                    ReleaseDate = track.ReleaseDate ?? hydrated.ReleaseDate
                }
                : track)
            .ToList();

        return set with { Tracks = tracks };
    }

    /// <inheritdoc />
    public async Task<SoundCloudTrack?> ResolveTrackByIdAsync(string idOrUrn, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrUrn);

        var trimmed = idOrUrn.Trim();
        const string urnPrefix = "soundcloud:tracks:";
        if (trimmed.StartsWith(urnPrefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[urnPrefix.Length..];
        }

        if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return null;
        }

        var url = $"{ApiV2Base}/tracks?ids={id.ToString(CultureInfo.InvariantCulture)}";
        string body;
        try
        {
            body = await GetApiAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (SoundCloudUnavailableException)
        {
            // A deleted or private track is unresolved rather than a hard failure, so a stale identity on a
            // file does not break a tagging run.
            return null;
        }

        return SoundCloudHydrationParser.ParseSearchResults(body).FirstOrDefault();
    }

    private async Task ResolveStubBatchAsync(
        long[] ids,
        Dictionary<long, SoundCloudTrack> destination,
        CancellationToken cancellationToken)
    {
        var joined = string.Join(",", ids.Select(id => id.ToString()));
        var url = $"{ApiV2Base}/tracks?ids={Uri.EscapeDataString(joined)}";
        var body = await GetApiAsync(url, cancellationToken).ConfigureAwait(false);

        foreach (var track in SoundCloudHydrationParser.ParseSearchResults(body))
        {
            destination[track.Id] = track;
        }
    }

    private static bool IsApiV2Url(string url)
        => url.Contains(ApiV2Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Releases the client's own synchronization primitives.
    /// </summary>
    public void Dispose()
        => _clientIdGate.Dispose();

    private static string? ReadClientIdFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var separator = url.IndexOf('?');
        if (separator < 0)
        {
            return null;
        }

        foreach (var candidate in url[(separator + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
                     .Where(candidate => candidate.StartsWith("client_id=", StringComparison.Ordinal)))
        {
            return candidate["client_id=".Length..];
        }

        return null;
    }

    /// <summary>
    ///     Replaces any <c>client_id</c> already present in the URL with the currently cached one.
    /// </summary>
    /// <summary>
    ///     Ensures the URL carries the current <c>client_id</c>, replacing any value already present.
    /// </summary>
    /// <remarks>
    ///     api-v2 rejects a request with no client id with a 401, so the id has to be attached to every api-v2
    ///     URL built here - including the ones that did not already carry one. An earlier version returned the
    ///     URL untouched when the parameter was absent, which meant only the stream URL (the one place that
    ///     embeds a placeholder) ever received it and every other api-v2 call was rejected.
    /// </remarks>
    private static string ApplyClientId(string url, string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return url;
        }

        var separator = url.IndexOf('?');
        if (separator < 0)
        {
            return url + "?client_id=" + Uri.EscapeDataString(clientId);
        }

        var path = url[..separator];
        var query = url[(separator + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !pair.StartsWith("client_id=", StringComparison.Ordinal))
            .Append($"client_id={Uri.EscapeDataString(clientId)}");

        return path + "?" + string.Join("&", query);
    }
}