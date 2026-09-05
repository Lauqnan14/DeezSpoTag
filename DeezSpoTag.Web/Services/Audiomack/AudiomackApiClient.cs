using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Security;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services.Audiomack;

/// <summary>An Audiomack artist resolved from the public search API.</summary>
public sealed record AudiomackArtistCandidate(long? Id, string UrlSlug, string Name, bool Verified);

/// <summary>Request body for the library "Edit Audiomack ID" action.</summary>
public sealed record AudiomackIdUpdateRequest(string AudiomackId);

/// <summary>
/// Minimal client for Audiomack's first-party web API (api.audiomack.com/v1).
/// Requests carry no cookies, no login and no user tokens: they are signed with
/// the same two-legged OAuth 1.0a HMAC-SHA1 identity the audiomack.com player
/// publishes in its own JavaScript bundle. That identity is discovered at
/// runtime by <see cref="AudiomackWebCredentialsProvider"/> (with last-known
/// values only as a bootstrap fallback), so an Audiomack-side rotation
/// self-heals instead of failing forever.
/// </summary>
public sealed class AudiomackApiClient
{
    public const string SourceName = "audiomack";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AudiomackWebCredentialsProvider _credentialsProvider;
    private readonly ILogger<AudiomackApiClient> _logger;

    public AudiomackApiClient(
        IHttpClientFactory httpClientFactory,
        AudiomackWebCredentialsProvider credentialsProvider,
        ILogger<AudiomackApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _credentialsProvider = credentialsProvider;
        _logger = logger;
    }

    /// <summary>
    /// Searches Audiomack for an artist by name. Prefers the verified-artist
    /// result when its name matches; otherwise takes the first uploader whose
    /// name matches. Returns null when nothing matches confidently.
    /// </summary>
    public async Task<AudiomackArtistCandidate?> SearchArtistAsync(string? artistName, CancellationToken cancellationToken = default)
    {
        var query = artistName?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        try
        {
            var credentials = await _credentialsProvider.GetAsync(cancellationToken).ConfigureAwait(false);
            var candidate = await SearchOnceAsync(credentials, query, cancellationToken).ConfigureAwait(false);
            if (candidate != null)
            {
                return candidate;
            }

            // A rejected signature is indistinguishable from "no results" only via
            // the status code, so a 401 triggers one re-discovery + retry before
            // concluding the artist was not found.
            if (!_credentialsWasRejected)
            {
                return null;
            }

            _credentialsProvider.Invalidate();
            var refreshed = await _credentialsProvider.GetAsync(cancellationToken).ConfigureAwait(false);
            return await SearchOnceAsync(refreshed, query, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Audiomack artist search failed ({ArtistName})", LogSanitizer.OneLine(query));
            return null;
        }
    }

    private bool _credentialsWasRejected;

    private async Task<AudiomackArtistCandidate?> SearchOnceAsync(
        AudiomackWebCredentials credentials,
        string query,
        CancellationToken cancellationToken)
    {
        _credentialsWasRejected = false;
        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);

        var url = BuildSignedUrl(credentials, "search", new Dictionary<string, string>
        {
            ["q"] = query,
            ["type"] = "artists"
        });
        using var response = await httpClient.GetAsync(url, timeoutCts.Token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _credentialsWasRejected = true;
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        return ParseSearchResponse(json, query);
    }

    internal static AudiomackArtistCandidate? ParseSearchResponse(string? json, string expectedArtistName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using var _ = document;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("verified_artist", out var verified) && verified.ValueKind == JsonValueKind.Object)
        {
            var candidate = ToCandidate(verified, verified: true);
            if (candidate != null && NameMatches(candidate.Name, expectedArtistName))
            {
                return candidate;
            }
        }

        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("uploader", out var uploader)
                    || uploader.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var candidate = ToCandidate(uploader, verified: false);
                if (candidate != null && NameMatches(candidate.Name, expectedArtistName))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static AudiomackArtistCandidate? ToCandidate(JsonElement element, bool verified)
    {
        var slug = element.TryGetProperty("url_slug", out var slugElement) && slugElement.ValueKind == JsonValueKind.String
            ? slugElement.GetString()?.Trim()
            : null;
        var name = element.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()?.Trim()
            : null;
        long? id = null;
        if (element.TryGetProperty("id", out var idElement))
        {
            // The API returns numeric ids for artist objects but string ids on uploaders.
            if (idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt64(out var numericId))
            {
                id = numericId;
            }
            else if (idElement.ValueKind == JsonValueKind.String && long.TryParse(idElement.GetString(), out var stringId))
            {
                id = stringId;
            }
        }

        if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new AudiomackArtistCandidate(id, slug!, name!, verified);
    }

    /// <summary>Mirrors the artist-page parser's name tolerance: normalized equality or a prefix match on both sides (≥4 chars).</summary>
    internal static bool NameMatches(string? candidateName, string expectedArtistName)
    {
        if (string.IsNullOrWhiteSpace(candidateName) || string.IsNullOrWhiteSpace(expectedArtistName))
        {
            return false;
        }

        var expected = NormalizeName(expectedArtistName);
        var actual = NormalizeName(candidateName);
        if (expected.Length == 0 || actual.Length == 0)
        {
            return false;
        }

        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return true;
        }

        const int minimumNameLengthForPrefixMatch = 4;
        return expected.Length >= minimumNameLengthForPrefixMatch
            && actual.Length >= minimumNameLengthForPrefixMatch
            && (expected.StartsWith(actual, StringComparison.Ordinal) || actual.StartsWith(expected, StringComparison.Ordinal));
    }

    private static string NormalizeName(string value) =>
        new(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Builds the request URL with a two-legged OAuth 1.0a HMAC-SHA1 signature query, matching the Audiomack web player.</summary>
    internal static string BuildSignedUrl(AudiomackWebCredentials credentials, string endpointPath, IReadOnlyDictionary<string, string> queryParameters)
    {
        var baseUrl = credentials.ApiBaseUrl.TrimEnd('/') + "/" + endpointPath;
        var oauthParameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["oauth_consumer_key"] = credentials.ConsumerKey,
            ["oauth_nonce"] = Guid.NewGuid().ToString("N"),
            ["oauth_signature_method"] = "HMAC-SHA1",
            ["oauth_timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["oauth_version"] = "1.0"
        };

        var signatureBaseParameters = new SortedDictionary<string, string>(oauthParameters, StringComparer.Ordinal);
        foreach (var parameter in queryParameters)
        {
            signatureBaseParameters[parameter.Key] = parameter.Value;
        }

        var parameterString = string.Join("&", signatureBaseParameters.Select(p => $"{Encode(p.Key)}={Encode(p.Value)}"));
        var signatureBaseString = string.Join("&", "GET", Encode(baseUrl), Encode(parameterString));
        using var hmac = new HMACSHA1(Encoding.ASCII.GetBytes($"{Encode(credentials.ConsumerSecret)}&"));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.ASCII.GetBytes(signatureBaseString)));
        oauthParameters["oauth_signature"] = signature;

        var query = string.Join("&", queryParameters.Select(p => $"{Encode(p.Key)}={Encode(p.Value)}"));
        var oauth = string.Join("&", oauthParameters.Select(p => $"{Encode(p.Key)}={Encode(p.Value)}"));
        return $"{baseUrl}?{query}&{oauth}";
    }

    // RFC 3986 percent-encoding (Uri.EscapeDataString matches the unreserved set OAuth 1.0a requires).
    private static string Encode(string value) => Uri.EscapeDataString(value);
}
