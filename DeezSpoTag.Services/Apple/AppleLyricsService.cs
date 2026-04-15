using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Apple;

public sealed class AppleLyricsService
{
    private readonly record struct TypedLyricsRequestContext(
        string Token,
        string AppleId,
        string Storefront,
        string MediaUserToken);

    private const string DefaultLanguage = "en-US";
    private const string SyncedLyricsType = "lyrics";
    private const string SyllableLyricsType = "syllable-lyrics";
    private const string UnsyncedLyricsType = "unsynced-lyrics";
    private const int MinMediaUserTokenLength = 50;
    private const string DefaultWrapperHost = "127.0.0.1";
    private const string WrapperHostEnvironmentVariable = "DEEZSPOTAG_APPLE_WRAPPER_HOST";
    private const string AppleMusicScheme = "https";
    private const string AppleMusicHost = "music.apple.com";
    private const string AppleMusicCatalogApiHost = "amp-api.music.apple.com";
    private const string MediaUserTokenHeader = "Media-User-Token";
    private const string UserAgentHeader = "User-Agent";
    private static readonly string[] AppleIdKeys = ["apple_track_id", "apple_id", "appleid", "apple"];
    private static readonly string[] AppleUrlKeys = ["apple_url", "source_url"];
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex LrcLineRegex = CreateRegex(@"^\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\](.*)$", RegexOptions.Compiled);
    private static readonly Regex AppleTrackUrlRegex = CreateRegex(@"music\.apple\.com\/[^\/]+\/(?:song|album)\/[^\/]+\/(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AppleQueryIdRegex = CreateRegex(@"(?:[?&]i=)(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static Regex CreateRegex(string pattern, RegexOptions options)
        => new(pattern, options, RegexTimeout);
    private static string ReplaceWithTimeout(string input, string pattern, string replacement, RegexOptions options = RegexOptions.None)
        => Regex.Replace(input, pattern, replacement, options, RegexTimeout);
    private static readonly (int Start, int End)[] CjkCodeRanges = new[]
    {
        (0x1100, 0x11FF),
        (0x2E80, 0x2EFF),
        (0x2F00, 0x2FDF),
        (0x2FF0, 0x2FFF),
        (0x3000, 0x303F),
        (0x3040, 0x309F),
        (0x30A0, 0x30FF),
        (0x3130, 0x318F),
        (0x31C0, 0x31EF),
        (0x31F0, 0x31FF),
        (0x3200, 0x32FF),
        (0x3300, 0x33FF),
        (0x3400, 0x4DBF),
        (0x4E00, 0x9FFF),
        (0xA960, 0xA97F),
        (0xAC00, 0xD7AF),
        (0xD7B0, 0xD7FF),
        (0xF900, 0xFAFF),
        (0xFE30, 0xFE4F),
        (0xFF65, 0xFF9F),
        (0xFFA0, 0xFFDC),
        (0x1AFF0, 0x1AFFF),
        (0x1B000, 0x1B0FF),
        (0x1B100, 0x1B12F),
        (0x1B130, 0x1B16F),
        (0x1F200, 0x1F2FF),
        (0x20000, 0x2A6DF),
        (0x2A700, 0x2B73F),
        (0x2B740, 0x2B81F),
        (0x2B820, 0x2CEAF),
        (0x2CEB0, 0x2EBEF),
        (0x2EBF0, 0x2EE5F),
        (0x2F800, 0x2FA1F),
        (0x30000, 0x3134F),
        (0x31350, 0x323AF)
    };

    private readonly AppleMusicCatalogService _catalogService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AppleLyricsService> _logger;
    private static readonly HttpClient WrapperAccountClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public AppleLyricsService(
        AppleMusicCatalogService catalogService,
        IHttpClientFactory httpClientFactory,
        ILogger<AppleLyricsService> logger)
    {
        _catalogService = catalogService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<LyricsBase> ResolveLyricsAsync(
        string appleId,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appleId))
        {
            return AppleLyrics.CreateError("Apple Music ID is required for lyrics.");
        }

        var mediaUserToken = settings.AppleMusic?.MediaUserToken ?? string.Empty;
        if (mediaUserToken.Length < MinMediaUserTokenLength)
        {
            mediaUserToken = await TryResolveWrapperMusicTokenAsync(cancellationToken) ?? string.Empty;
        }

        if (mediaUserToken.Length < MinMediaUserTokenLength)
        {
            return AppleLyrics.CreateError("Media user token is required for Apple Music lyrics.");
        }

        var storefront = await _catalogService.ResolveStorefrontAsync(
            settings.AppleMusic?.Storefront,
            mediaUserToken,
            cancellationToken);
        var language = string.IsNullOrWhiteSpace(settings.DeezerLanguage) ? DefaultLanguage : settings.DeezerLanguage;
        var lrcType = string.IsNullOrWhiteSpace(settings.LrcType) ? SyncedLyricsType : settings.LrcType;
        var lrcFormat = string.IsNullOrWhiteSpace(settings.LrcFormat) ? "lrc" : settings.LrcFormat;

        var ttml = await FetchLyricsTtmlAsync(appleId, storefront, language, lrcType, mediaUserToken, cancellationToken);
        if (string.IsNullOrWhiteSpace(ttml))
        {
            return AppleLyrics.CreateError("Apple Music lyrics not available.");
        }

        if (string.Equals(lrcFormat, "ttml", StringComparison.OrdinalIgnoreCase))
        {
            return AppleLyrics.FromTtml(ttml);
        }

        var lrc = ConvertTtmlToLrc(ttml);
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return AppleLyrics.FromTtml(ttml);
        }

        // Preserve raw TTML regardless of selected LRC format so downstream
        // save logic can still emit .ttml when configured.
        return AppleLyrics.FromLrc(lrc, ttml);
    }

    public async Task<LyricsBase> ResolveLyricsForTrackAsync(
        Track track,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        if (track == null)
        {
            return AppleLyrics.CreateError("Track is required for Apple Music lyrics.");
        }

        var appleId = TryExtractAppleIdFromTrack(track);

        if (string.IsNullOrWhiteSpace(appleId))
        {
            appleId = await TryResolveAppleIdAsync(track, settings, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(appleId))
        {
            return AppleLyrics.CreateError("Unable to resolve Apple Music ID for lyrics.");
        }

        return await ResolveLyricsAsync(appleId, settings, cancellationToken);
    }

    private static string? TryExtractAppleIdFromTrack(Track track)
    {
        if (track == null)
        {
            return null;
        }

        if (string.Equals(track.Source, "apple", StringComparison.OrdinalIgnoreCase)
            && TryNormalizeAppleId(track.SourceId, out var directAppleId))
        {
            return directAppleId;
        }

        var fromUrls = TryResolveAppleIdFromTrackUrls(track.Urls);
        if (!string.IsNullOrWhiteSpace(fromUrls))
        {
            return fromUrls;
        }

        return TryExtractAppleIdFromValue(track.DownloadURL, allowRawNumeric: false);
    }

    private static string? TryResolveAppleIdFromTrackUrls(IDictionary<string, string>? urls)
    {
        if (urls is not { Count: > 0 })
        {
            return null;
        }

        var fromId = TryResolveAppleIdFromKeySet(urls, AppleIdKeys, key => !key.Equals("apple", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(fromId))
        {
            return fromId;
        }

        return TryResolveAppleIdFromKeySet(urls, AppleUrlKeys, static _ => false);
    }

    private static string? TryResolveAppleIdFromKeySet(
        IDictionary<string, string> urls,
        IEnumerable<string> keys,
        Func<string, bool> allowRawNumeric)
    {
        foreach (var key in keys)
        {
            if (!urls.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var extracted = TryExtractAppleIdFromValue(value, allowRawNumeric(key));
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                return extracted;
            }
        }

        return null;
    }

    private async Task<string?> TryResolveWrapperMusicTokenAsync(CancellationToken cancellationToken)
    {
        var wrapperHost = Environment.GetEnvironmentVariable(WrapperHostEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(wrapperHost))
        {
            wrapperHost = DefaultWrapperHost;
        }

        var accountUri = new UriBuilder(Uri.UriSchemeHttp, wrapperHost, 30020, "account")
        {
            Query = "include_tokens=1"
        }.Uri;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, accountUri);
            using var response = await WrapperAccountClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (TryReadWrapperMusicToken(doc.RootElement, out var musicToken))
            {
                _logger.LogDebug("Apple lyrics using media user token from wrapper account endpoint.");
                return musicToken;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Apple lyrics wrapper token lookup failed.");
        }

        return null;
    }

    private static bool TryReadWrapperMusicToken(JsonElement root, out string? musicToken)
    {
        musicToken = null;
        if (root.TryGetProperty("music_user_token", out var musicUserTokenElement)
            && musicUserTokenElement.ValueKind == JsonValueKind.String)
        {
            musicToken = musicUserTokenElement.GetString()?.Trim();
        }
        else if (root.TryGetProperty("music_token", out var musicTokenElement)
                 && musicTokenElement.ValueKind == JsonValueKind.String)
        {
            musicToken = musicTokenElement.GetString()?.Trim();
        }

        return !string.IsNullOrWhiteSpace(musicToken) && musicToken.Length >= MinMediaUserTokenLength;
    }

    private static bool TryNormalizeAppleId(string? value, out string? appleId)
    {
        appleId = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (!long.TryParse(candidate, out var numeric) || numeric <= 0)
        {
            return false;
        }

        appleId = numeric.ToString();
        return true;
    }

    private static string? TryExtractAppleIdFromValue(string? value, bool allowRawNumeric)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim();
        if (allowRawNumeric && TryNormalizeAppleId(candidate, out var numericAppleId))
        {
            return numericAppleId;
        }

        var fromUri = TryExtractAppleIdFromUri(candidate);
        if (!string.IsNullOrWhiteSpace(fromUri))
        {
            return fromUri;
        }

        var fromPattern = TryExtractAppleIdFromRegex(candidate);
        if (!string.IsNullOrWhiteSpace(fromPattern))
        {
            return fromPattern;
        }

        if (candidate.StartsWith("id", StringComparison.OrdinalIgnoreCase)
            && TryNormalizeAppleId(candidate[2..], out var prefixedId))
        {
            return prefixedId;
        }

        return allowRawNumeric && TryNormalizeAppleId(candidate, out var normalized) ? normalized : null;
    }

    private static string? TryExtractAppleIdFromUri(string candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var queryAppleId = TryExtractAppleIdFromQuery(uri);
        if (!string.IsNullOrWhiteSpace(queryAppleId))
        {
            return queryAppleId;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var segment = segments[i];
            if (segment.StartsWith("id", StringComparison.OrdinalIgnoreCase)
                && TryNormalizeAppleId(segment[2..], out var idSegment))
            {
                return idSegment;
            }

            if (TryNormalizeAppleId(segment, out var directSegment))
            {
                return directSegment;
            }
        }

        return null;
    }

    private static string? TryExtractAppleIdFromRegex(string candidate)
    {
        var queryMatch = AppleQueryIdRegex.Match(candidate);
        if (queryMatch.Success && TryNormalizeAppleId(queryMatch.Groups["id"].Value, out var queryId))
        {
            return queryId;
        }

        var urlMatch = AppleTrackUrlRegex.Match(candidate);
        if (urlMatch.Success && TryNormalizeAppleId(urlMatch.Groups["id"].Value, out var urlId))
        {
            return urlId;
        }

        return null;
    }

    private static string? TryExtractAppleIdFromQuery(Uri uri)
    {
        if (uri == null || string.IsNullOrWhiteSpace(uri.Query))
        {
            return null;
        }

        var query = uri.Query.Length > 0 && uri.Query[0] == '?' ? uri.Query[1..] : uri.Query;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var splitIndex = part.IndexOf('=');
            if (splitIndex <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(part[..splitIndex]);
            if (!key.Equals("i", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Uri.UnescapeDataString(part[(splitIndex + 1)..]);
            if (TryNormalizeAppleId(value, out var appleId))
            {
                return appleId;
            }
        }

        return null;
    }

    private async Task<string?> FetchLyricsTtmlAsync(
        string appleId,
        string storefront,
        string language,
        string lrcType,
        string mediaUserToken,
        CancellationToken cancellationToken)
    {
        var token = await _catalogService.GetCatalogTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var languageCandidates = BuildLanguageCandidates(language);
        var typeCandidates = BuildLyricsTypeCandidates(lrcType);
        var catalogTtml = await TryFetchLyricsFromCatalogApiAsync(
            appleId,
            storefront,
            mediaUserToken,
            languageCandidates,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(catalogTtml))
        {
            return catalogTtml;
        }

        using var client = _httpClientFactory.CreateClient();
        return await TryFetchLyricsFromTypedEndpointsAsync(
            client,
            new TypedLyricsRequestContext(token, appleId, storefront, mediaUserToken),
            typeCandidates,
            languageCandidates,
            cancellationToken);
    }

    private async Task<string?> TryFetchLyricsFromCatalogApiAsync(
        string appleId,
        string storefront,
        string mediaUserToken,
        IEnumerable<string> languageCandidates,
        CancellationToken cancellationToken)
    {
        foreach (var lang in languageCandidates)
        {
            try
            {
                using var doc = await _catalogService.GetSongLyricsAsync(
                    appleId,
                    storefront,
                    lang,
                    cancellationToken,
                    mediaUserToken);
                var ttml = TryExtractLyricsTtml(doc.RootElement, lang);
                if (!string.IsNullOrWhiteSpace(ttml))
                {
                    return ttml;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Apple lyrics endpoint request failed for song {AppleId} lang={Lang}", appleId, lang);                }
            }
        }

        return null;
    }

    private async Task<string?> TryFetchLyricsFromTypedEndpointsAsync(
        HttpClient client,
        TypedLyricsRequestContext context,
        IEnumerable<string> typeCandidates,
        IEnumerable<string> languageCandidates,
        CancellationToken cancellationToken)
    {
        foreach (var type in typeCandidates)
        {
            foreach (var lang in languageCandidates)
            {
                var url = BuildLyricsTypeUrl(context.Storefront, context.AppleId, type, lang);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Origin", BuildAppleMusicOrigin());
                request.Headers.TryAddWithoutValidation("Referer", BuildAppleMusicReferer());
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {context.Token}");
                request.Headers.TryAddWithoutValidation(MediaUserTokenHeader, context.MediaUserToken);
                request.Headers.TryAddWithoutValidation("Cookie", BuildMediaUserCookie(context.MediaUserToken, context.Storefront));
                request.Headers.TryAddWithoutValidation(UserAgentHeader, AppleUserAgentPool.GetAuthenticatedUserAgent());
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("Accept-Language", lang);

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("Apple lyrics request failed: status={StatusCode} type={Type} lang={Lang}", response.StatusCode, type, lang);                    }
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var ttml = TryExtractLyricsTtml(doc.RootElement, lang);
                if (!string.IsNullOrWhiteSpace(ttml))
                {
                    return ttml;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildLyricsTypeCandidates(string lrcType)
    {
        var selectedTypes = ParseLyricsTypeSelection(lrcType);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in selectedTypes
                     .Select(NormalizeAppleLyricsType)
                     .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
                     .Select(static candidate => candidate!)
                     .Where(candidate => emitted.Add(candidate)))
        {
            yield return candidate;
        }

        if (emitted.Add(SyncedLyricsType))
        {
            yield return SyncedLyricsType;
        }
    }

    private static List<string> ParseLyricsTypeSelection(string? value)
    {
        var selected = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            selected.Add(SyncedLyricsType);
            selected.Add(SyllableLyricsType);
            selected.Add(UnsyncedLyricsType);
            return selected;
        }

        selected.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeLyricsTypeToken)
            .Where(static normalized => !string.IsNullOrWhiteSpace(normalized))
            .Distinct(StringComparer.OrdinalIgnoreCase)!);

        if (selected.Count == 0)
        {
            selected.Add(SyncedLyricsType);
            selected.Add(SyllableLyricsType);
            selected.Add(UnsyncedLyricsType);
        }

        return selected;
    }

    private static string? NormalizeLyricsTypeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return token.Trim().ToLowerInvariant() switch
        {
            SyncedLyricsType => SyncedLyricsType,
            "synced-lyrics" => SyncedLyricsType,
            SyllableLyricsType => SyllableLyricsType,
            "time-synced-lyrics" => SyllableLyricsType,
            "timesynced-lyrics" => SyllableLyricsType,
            "time_synced_lyrics" => SyllableLyricsType,
            "syllablelyrics" => SyllableLyricsType,
            UnsyncedLyricsType => UnsyncedLyricsType,
            "unsunsynced-lyrics" => UnsyncedLyricsType,
            "unsyncedlyrics" => UnsyncedLyricsType,
            "unsynced" => UnsyncedLyricsType,
            "unsynchronized-lyrics" => UnsyncedLyricsType,
            "unsynchronised-lyrics" => UnsyncedLyricsType,
            _ => null
        };
    }

    private static string? NormalizeAppleLyricsType(string normalizedType)
    {
        if (string.IsNullOrWhiteSpace(normalizedType))
        {
            return null;
        }

        return normalizedType switch
        {
            SyllableLyricsType => SyllableLyricsType,
            UnsyncedLyricsType => SyncedLyricsType,
            _ => SyncedLyricsType
        };
    }

    private static IEnumerable<string> BuildLanguageCandidates(string language)
    {
        var baseLang = string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language;
        var candidates = new List<string>
        {
            baseLang
        };

        var dash = baseLang.IndexOf('-');
        if (dash > 0)
        {
            candidates.Add(baseLang[..dash]);
        }

        if (!baseLang.Equals(DefaultLanguage, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(DefaultLanguage);
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildMediaUserCookie(string mediaUserToken, string? storefront)
    {
        var cookie = $"media-user-token={mediaUserToken}";
        if (!string.IsNullOrWhiteSpace(storefront))
        {
            cookie += $"; itua={storefront}";
        }

        return cookie;
    }

    private static string? TryExtractLyricsTtml(JsonElement root, string preferredLanguage)
    {
        if (!TryGetLyricsAttributes(root, out var attrs))
        {
            return null;
        }

        var directTtml = TryReadDirectTtml(attrs);
        if (!string.IsNullOrWhiteSpace(directTtml))
        {
            return directTtml;
        }

        return TryReadLocalizedTtml(attrs, preferredLanguage);
    }

    private static bool TryGetLyricsAttributes(JsonElement root, out JsonElement attrs)
    {
        attrs = default;
        if (!root.TryGetProperty("data", out var dataArr)
            || dataArr.ValueKind != JsonValueKind.Array
            || dataArr.GetArrayLength() == 0)
        {
            return false;
        }

        return dataArr[0].TryGetProperty("attributes", out attrs) && attrs.ValueKind == JsonValueKind.Object;
    }

    private static string? TryReadDirectTtml(JsonElement attrs)
    {
        if (!attrs.TryGetProperty("ttml", out var ttmlEl) || ttmlEl.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var ttml = ttmlEl.GetString();
        return string.IsNullOrWhiteSpace(ttml) ? null : ttml;
    }

    private static string? TryReadLocalizedTtml(JsonElement attrs, string preferredLanguage)
    {
        if (!attrs.TryGetProperty("ttmlLocalizations", out var localizedEl))
        {
            return null;
        }

        if (localizedEl.ValueKind == JsonValueKind.String)
        {
            return localizedEl.GetString();
        }

        if (localizedEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var langKey = preferredLanguage.Replace('_', '-');
        if (localizedEl.TryGetProperty(langKey, out var exact) && exact.ValueKind == JsonValueKind.String)
        {
            return exact.GetString();
        }

        var baseLang = langKey.Split('-')[0];
        foreach (var entry in localizedEl.EnumerateObject())
        {
            if (!entry.Name.StartsWith(baseLang, StringComparison.OrdinalIgnoreCase)
                || entry.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            return entry.Value.GetString();
        }

        return localizedEl.EnumerateObject()
            .Where(static entry => entry.Value.ValueKind == JsonValueKind.String)
            .Select(static entry => entry.Value.GetString())
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    private async Task<string?> TryResolveAppleIdAsync(
        Track track,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        var storefront = await _catalogService.ResolveStorefrontAsync(
            settings.AppleMusic?.Storefront,
            settings.AppleMusic?.MediaUserToken,
            cancellationToken);
        var language = string.IsNullOrWhiteSpace(settings.DeezerLanguage) ? DefaultLanguage : settings.DeezerLanguage;
        var resolvedFromIsrc = await TryResolveAppleIdByIsrcAsync(track.ISRC, storefront, language, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolvedFromIsrc))
        {
            return resolvedFromIsrc;
        }

        return await TryResolveAppleIdBySearchTermsAsync(track, storefront, language, cancellationToken);
    }

    private async Task<string?> TryResolveAppleIdByIsrcAsync(
        string? isrc,
        string storefront,
        string language,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        try
        {
            using var doc = await _catalogService.GetSongByIsrcAsync(isrc, storefront, language, cancellationToken);
            return TryExtractAppleId(doc.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple lyrics ISRC lookup failed for {Isrc}", isrc);
            }

            return null;
        }
    }

    private async Task<string?> TryResolveAppleIdBySearchTermsAsync(
        Track track,
        string storefront,
        string language,
        CancellationToken cancellationToken)
    {
        foreach (var term in BuildSearchTerms(track))
        {
            try
            {
                using var doc = await _catalogService.SearchAsync(
                    term,
                    limit: 25,
                    storefront: storefront,
                    language: language,
                    cancellationToken,
                    new AppleMusicCatalogService.AppleSearchOptions(
                        TypesOverride: "songs",
                        IncludeRelationshipsTracks: false));

                var bestId = FindBestAppleSongId(doc.RootElement, track);
                if (!string.IsNullOrWhiteSpace(bestId))
                {
                    return bestId;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Apple lyrics search lookup failed for {Term}", term);
                }
            }
        }

        return null;
    }

    private static string? TryExtractAppleId(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array || dataArr.GetArrayLength() == 0)
        {
            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Object &&
                results.TryGetProperty("songs", out var songs) && songs.ValueKind == JsonValueKind.Object &&
                songs.TryGetProperty("data", out var songData) && songData.ValueKind == JsonValueKind.Array &&
                songData.GetArrayLength() > 0)
            {
                var entry = songData[0];
                return entry.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            }

            return null;
        }

        return dataArr[0].TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
    }

    private static List<string> BuildSearchTerms(Track track)
    {
        var terms = new List<string>();
        var title = track.Title?.Trim();
        var artist = track.MainArtist?.Name?.Trim();
        var album = track.Album?.Title?.Trim();

        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist))
        {
            terms.Add($"{title} {artist}");
        }
        if (!string.IsNullOrWhiteSpace(title))
        {
            terms.Add(title);
        }
        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(album))
        {
            terms.Add($"{title} {album}");
        }
        if (!string.IsNullOrWhiteSpace(title) && track.Artists.Count > 0)
        {
            terms.Add($"{title} {track.Artists[0]}");
        }

        return terms.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string BuildAppleMusicOrigin()
        => new UriBuilder(AppleMusicScheme, AppleMusicHost).Uri.ToString().TrimEnd('/');

    private static string BuildAppleMusicReferer()
        => $"{BuildAppleMusicOrigin()}/";

    private static string BuildLyricsTypeUrl(string storefront, string appleId, string type, string language)
        => new UriBuilder(AppleMusicScheme, AppleMusicCatalogApiHost)
        {
            Path = $"/v1/catalog/{Uri.EscapeDataString(storefront)}/songs/{Uri.EscapeDataString(appleId)}/{Uri.EscapeDataString(type)}",
            Query = $"l={Uri.EscapeDataString(language)}&extend=ttmlLocalizations"
        }.Uri.ToString();

    private static string? FindBestAppleSongId(JsonElement root, Track track)
    {
        if (!root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Object
            || !results.TryGetProperty("songs", out var songs)
            || songs.ValueKind != JsonValueKind.Object
            || !songs.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var bestScore = -1;
        string? bestId = null;

        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var attrs = item.TryGetProperty("attributes", out var attrsEl) && attrsEl.ValueKind == JsonValueKind.Object
                ? attrsEl
                : default;

            var score = ScoreAppleCandidate(attrs, track);
            if (score > bestScore)
            {
                bestScore = score;
                bestId = id;
            }
        }

        return bestScore >= 65 ? bestId : null;
    }

    private static int ScoreAppleCandidate(JsonElement attrs, Track track)
    {
        if (attrs.ValueKind == JsonValueKind.Undefined)
        {
            return 0;
        }

        var title = attrs.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        var artist = attrs.TryGetProperty("artistName", out var artistEl) ? artistEl.GetString() : null;
        var album = attrs.TryGetProperty("albumName", out var albumEl) ? albumEl.GetString() : null;
        var durationMs = attrs.TryGetProperty("durationInMillis", out var durEl) && durEl.TryGetInt32(out var dur) ? dur : 0;

        var score = 0;
        score += ScoreTextMatch(track.Title, title, 60);
        score += ScoreTextMatch(track.MainArtist?.Name, artist, 30);
        score += ScoreTextMatch(track.Album?.Title, album, 15);

        if (track.Duration > 0 && durationMs > 0)
        {
            var diffMs = Math.Abs(durationMs - track.Duration * 1000);
            if (diffMs <= 2000)
            {
                score += 10;
            }
            else if (diffMs <= 5000)
            {
                score += 6;
            }
            else if (diffMs <= 10000)
            {
                score += 3;
            }
        }

        return score;
    }

    private static int ScoreTextMatch(string? expected, string? actual, int maxScore)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
        {
            return 0;
        }

        var normExpected = NormalizeForCompare(expected);
        var normActual = NormalizeForCompare(actual);
        if (string.IsNullOrWhiteSpace(normExpected) || string.IsNullOrWhiteSpace(normActual))
        {
            return 0;
        }

        if (string.Equals(normExpected, normActual, StringComparison.OrdinalIgnoreCase))
        {
            return maxScore;
        }

        var expectedTokens = normExpected.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var actualTokens = normActual.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (expectedTokens.Length == 0 || actualTokens.Length == 0)
        {
            return 0;
        }

        var overlap = expectedTokens.Intersect(actualTokens).Count();
        var ratio = (double)overlap / expectedTokens.Length;
        return (int)Math.Round(maxScore * ratio);
    }

    private static string NormalizeForCompare(string value)
    {
        var normalized = value.ToLowerInvariant();
        normalized = ReplaceWithTimeout(normalized, @"\((feat|ft)\.?.*?\)", "", RegexOptions.IgnoreCase);
        normalized = ReplaceWithTimeout(normalized, @"\[.*?\]", "");
        normalized = ReplaceWithTimeout(normalized, @"[^a-z0-9\s]", " ");
        normalized = ReplaceWithTimeout(normalized, @"\s+", " ").Trim();
        return normalized;
    }

    private static string ConvertTtmlToLrc(string ttml)
    {
        try
        {
            var doc = XDocument.Parse(ttml);
            var tt = doc.Root;
            if (tt == null)
            {
                return string.Empty;
            }

            var timing = tt.Attributes().FirstOrDefault(a => a.Name.LocalName == "timing")?.Value;
            if (string.Equals(timing, "Word", StringComparison.OrdinalIgnoreCase))
            {
                return ConvertSyllableTtmlToLrc(tt);
            }

            if (string.Equals(timing, "None", StringComparison.OrdinalIgnoreCase))
            {
                return ConvertUnsyncedTtmlToText(tt);
            }

            var body = tt.Descendants().FirstOrDefault(e => e.Name.LocalName == "body");
            if (body == null)
            {
                return string.Empty;
            }

            var metadata = FindMetadata(tt);
            var lrcLines = new List<string>();
            foreach (var item in body.Elements())
            {
                foreach (var lyric in item.Elements())
                {
                    AppendTimedLyricLine(lrcLines, lyric, metadata);
                }
            }

            return string.Join("\n", lrcLines);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static string ConvertUnsyncedTtmlToText(XElement tt)
    {
        var lines = tt.Descendants()
            .Where(e => e.Name.LocalName == "p")
            .Select(e => e.Value.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v));
        return string.Join("\n", lines);
    }

    private static void AppendTimedLyricLine(List<string> lrcLines, XElement lyric, XElement? metadata)
    {
        var beginAttr = lyric.Attributes().FirstOrDefault(a => a.Name.LocalName == "begin");
        if (beginAttr == null || !TryParseTime(beginAttr.Value, out _, out var lrcTimestamp))
        {
            return;
        }

        var lineKey = lyric.Attributes().FirstOrDefault(a => a.Name.LocalName == "key")?.Value;
        var text = ExtractLyricText(lyric);
        var (translation, transliteration) = ResolveLocalizationPair(metadata, lineKey);

        if (!string.IsNullOrWhiteSpace(translation))
        {
            lrcLines.Add($"{lrcTimestamp}{translation}");
        }

        if (!string.IsNullOrWhiteSpace(transliteration) && ContainsCjk(text))
        {
            lrcLines.Add($"{lrcTimestamp}{transliteration}");
            return;
        }

        lrcLines.Add($"{lrcTimestamp}{text}");
    }

    private static (string Translation, string Transliteration) ResolveLocalizationPair(XElement? metadata, string? lineKey)
    {
        if (metadata == null || string.IsNullOrWhiteSpace(lineKey))
        {
            return (string.Empty, string.Empty);
        }

        var translation = FindLocalizedText(metadata, "translations", lineKey, includeSpanTimestamps: false);
        var transliteration = FindLocalizedText(metadata, "transliterations", lineKey, includeSpanTimestamps: false);
        return (translation, transliteration);
    }

    public static string ConvertTtmlToLrcPublic(string ttml)
    {
        return ConvertTtmlToLrc(ttml);
    }

    private static string ConvertSyllableTtmlToLrc(XElement tt)
    {
        var lrcLines = new List<string>();
        var body = tt.Descendants().FirstOrDefault(e => e.Name.LocalName == "body");
        if (body == null)
        {
            return string.Empty;
        }

        var metadata = FindMetadata(tt);
        foreach (var div in body.Elements())
        {
            foreach (var item in div.Elements())
            {
                var state = BuildSyllableLineState(item, metadata);
                AppendSyllableLines(lrcLines, state);
            }
        }

        return string.Join("\n", lrcLines);
    }

    private sealed class SyllableLineState
    {
        public List<string> Syllables { get; } = new();
        public string EndTime { get; set; } = string.Empty;
        public string TransliterationLine { get; set; } = string.Empty;
        public string TranslationLine { get; set; } = string.Empty;
        public int Index { get; set; }
    }

    private static SyllableLineState BuildSyllableLineState(XElement item, XElement? metadata)
    {
        var state = new SyllableLineState();
        foreach (var node in item.Nodes())
        {
            if (node is XText)
            {
                if (state.Index > 0)
                {
                    state.Syllables.Add(" ");
                }
                continue;
            }

            if (node is not XElement lyric)
            {
                continue;
            }

            if (!TryGetSyllableTiming(lyric, out var lrcBegin, out var lrcEnd))
            {
                continue;
            }

            state.EndTime = lrcEnd;
            AppendSyllableText(state, lyric, lrcBegin);
            PopulateSyllableLocalizations(item, metadata, lrcBegin, state);
            state.Index++;
        }

        return state;
    }

    private static bool TryGetSyllableTiming(XElement lyric, out string lrcBegin, out string lrcEnd)
    {
        lrcBegin = string.Empty;
        lrcEnd = string.Empty;
        var beginAttr = lyric.Attributes().FirstOrDefault(a => a.Name.LocalName == "begin");
        var endAttr = lyric.Attributes().FirstOrDefault(a => a.Name.LocalName == "end");
        if (beginAttr == null || endAttr == null)
        {
            return false;
        }

        if (!TryParseTime(beginAttr.Value, out _, out lrcBegin))
        {
            return false;
        }

        return TryParseTime(endAttr.Value, out _, out lrcEnd, timestampStyle: TimestampStyle.Inline);
    }

    private static void AppendSyllableText(SyllableLineState state, XElement lyric, string lrcBegin)
    {
        var text = ExtractLyricText(lyric);
        var inlineBegin = lrcBegin.Replace('[', '<').Replace(']', '>');
        var prefix = state.Index == 0 ? $"{lrcBegin}{inlineBegin}" : inlineBegin;
        state.Syllables.Add($"{prefix}{text}");
    }

    private static void PopulateSyllableLocalizations(
        XElement item,
        XElement? metadata,
        string lrcBegin,
        SyllableLineState state)
    {
        if (state.Index != 0 || metadata == null)
        {
            return;
        }

        var lineKey = item.Attributes().FirstOrDefault(a => a.Name.LocalName == "key")?.Value;
        if (string.IsNullOrWhiteSpace(lineKey))
        {
            return;
        }

        var translit = FindLocalizedText(metadata, "transliterations", lineKey, includeSpanTimestamps: true);
        if (!string.IsNullOrWhiteSpace(translit))
        {
            state.TransliterationLine = translit;
        }

        var translation = FindLocalizedText(metadata, "translations", lineKey, includeSpanTimestamps: false);
        if (!string.IsNullOrWhiteSpace(translation))
        {
            state.TranslationLine = $"{lrcBegin}{translation}";
        }
    }

    private static void AppendSyllableLines(List<string> lrcLines, SyllableLineState state)
    {
        if (!string.IsNullOrWhiteSpace(state.TranslationLine))
        {
            lrcLines.Add(state.TranslationLine);
        }

        var combined = string.Join("", state.Syllables) + state.EndTime;
        if (!string.IsNullOrWhiteSpace(state.TransliterationLine) && ContainsCjk(combined))
        {
            lrcLines.Add(state.TransliterationLine);
            return;
        }

        lrcLines.Add(combined);
    }

    private static XElement? FindMetadata(XElement tt)
    {
        return tt.Descendants().FirstOrDefault(e => e.Name.LocalName == "iTunesMetadata");
    }

    private static string ExtractLyricText(XElement element)
    {
        var textAttr = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "text");
        if (textAttr != null)
        {
            return textAttr.Value;
        }

        var builder = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            if (node is XText textNode)
            {
                builder.Append(textNode.Value);
            }
            else if (node is XElement child)
            {
                builder.Append(child.Value);
            }
        }

        return builder.ToString();
    }

    private static string FindLocalizedText(XElement metadata, string sectionName, string key, bool includeSpanTimestamps)
    {
        var section = metadata.Descendants().FirstOrDefault(e => e.Name.LocalName == sectionName);
        if (section == null)
        {
            return string.Empty;
        }

        var entry = FindLocalizationEntry(section, sectionName, key);
        if (entry == null)
        {
            return string.Empty;
        }

        if (!includeSpanTimestamps)
        {
            return GetLocalizationPlainText(entry, key);
        }

        return GetLocalizationInlineText(entry);
    }

    private static XElement? FindLocalizationEntry(XElement section, string sectionName, string key)
    {
        var entry = section.Descendants().FirstOrDefault(e =>
            e.Name.LocalName.Trim() == sectionName.TrimEnd('s') &&
            e.Elements().Any(child => child.Name.LocalName == "text" && GetAttrValue(child, "for") == key));
        if (entry != null)
        {
            return entry;
        }

        return section.Descendants().FirstOrDefault(e => e.Name.LocalName == "text" && GetAttrValue(e, "for") == key)?.Parent;
    }

    private static string GetLocalizationPlainText(XElement entry, string key)
    {
        var text = entry.Descendants().FirstOrDefault(e => e.Name.LocalName == "text" && GetAttrValue(e, "for") == key);
        return text?.Value.Trim() ?? entry.Value.Trim();
    }

    private static string GetLocalizationInlineText(XElement entry)
    {
        var parts = new List<string>();
        string? startTimestamp = null;
        foreach (var span in entry.Descendants().Where(e => e.Name.LocalName == "span"))
        {
            if (!TryGetInlineSpanText(span, out var inlineTimestamp, out var text))
            {
                continue;
            }

            startTimestamp ??= inlineTimestamp.Replace('<', '[').Replace('>', ']');
            parts.Add($"{inlineTimestamp}{text}");
        }

        if (startTimestamp == null)
        {
            return string.Empty;
        }

        return $"{startTimestamp}{string.Join(" ", parts)}";
    }

    private static bool TryGetInlineSpanText(XElement span, out string inlineTimestamp, out string text)
    {
        inlineTimestamp = string.Empty;
        text = string.Empty;
        var beginAttr = span.Attributes().FirstOrDefault(a => a.Name.LocalName == "begin");
        if (beginAttr == null)
        {
            return false;
        }

        if (!TryParseTime(beginAttr.Value, out _, out inlineTimestamp, TimestampStyle.Inline))
        {
            return false;
        }

        text = span.Value;
        return true;
    }

    private static string? GetAttrValue(XElement element, string localName)
    {
        return element.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;
    }

    private static bool TryParseTime(string value, out int milliseconds, out string lrcTimestamp, TimestampStyle timestampStyle = TimestampStyle.Bracket)
    {
        milliseconds = 0;
        lrcTimestamp = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var components = ParseTimeComponents(value);
        milliseconds = (components.Minutes * 60 + components.Seconds) * 1000 + components.Milliseconds;
        var centiseconds = components.Milliseconds / 10;
        var stamp = $"{components.Minutes:00}:{components.Seconds:00}.{centiseconds:00}";
        lrcTimestamp = timestampStyle == TimestampStyle.Bracket ? $"[{stamp}]" : $"<{stamp}>";
        return true;
    }

    private static (int Minutes, int Seconds, int Milliseconds) ParseTimeComponents(string value)
    {
        var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries);
        int hours = 0;
        int minutes = 0;
        int seconds = 0;
        int ms = 0;

        if (parts.Length == 3)
        {
            hours = int.TryParse(parts[0], out var h) ? h : 0;
            minutes = int.TryParse(parts[1], out var m) ? m : 0;
            (seconds, ms) = ParseSecondsAndMilliseconds(parts[2]);
        }
        else if (parts.Length == 2)
        {
            minutes = int.TryParse(parts[0], out var m) ? m : 0;
            (seconds, ms) = ParseSecondsAndMilliseconds(parts[1]);
        }
        else
        {
            (seconds, ms) = ParseSecondsAndMilliseconds(value);
        }

        minutes += hours * 60;
        return (minutes, seconds, ms);
    }

    private static (int Seconds, int Milliseconds) ParseSecondsAndMilliseconds(string raw)
    {
        var secParts = raw.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var seconds = int.TryParse(secParts[0], out var s) ? s : 0;
        var milliseconds = secParts.Length > 1 ? ParseMilliseconds(secParts[1]) : 0;
        return (seconds, milliseconds);
    }

    private static int ParseMilliseconds(string raw)
    {
        if (!int.TryParse(raw, out var ms))
        {
            return 0;
        }

        return raw.Length switch
        {
            1 => ms * 100,
            2 => ms * 10,
            _ => ms
        };
    }

    private static bool ContainsCjk(string value)
    {
        return value
            .Select(static ch => (int)ch)
            .Any(code => CjkCodeRanges.Any(range => code >= range.Start && code <= range.End));
    }

    private sealed class AppleLyrics : LyricsBase
    {
        public static AppleLyrics CreateError(string message)
        {
            var lyrics = new AppleLyrics();
            lyrics.SetErrorMessage(message);
            return lyrics;
        }

        public static AppleLyrics FromLrc(string lrc, string? ttml = null)
        {
            var lyrics = new AppleLyrics();
            lyrics.TtmlLyrics = ttml;
            var lines = lrc.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var groups in lines
                .Select(static raw => LrcLineRegex.Match(raw.TrimEnd('\r')))
                .Where(static candidate => candidate.Success)
                .Select(static match => match.Groups))
            {
                var minutes = int.TryParse(groups[1].Value, out var m) ? m : 0;
                var seconds = int.TryParse(groups[2].Value, out var s) ? s : 0;
                var ms = groups[3].Success ? ParseMilliseconds(groups[3].Value) : 0;
                var timestamp = $"[{minutes:00}:{seconds:00}.{ms / 10:00}]";
                var text = groups[4].Value.Trim();
                var totalMs = (minutes * 60 + seconds) * 1000 + ms;
                lyrics.SyncedLyrics?.Add(new SynchronizedLyric(text, timestamp, totalMs));
            }

            if (lyrics.SyncedLyrics?.Count == 0)
            {
                lyrics.UnsyncedLyrics = lrc;
            }

            return lyrics;
        }

        public static AppleLyrics FromTtml(string ttml)
        {
            var lyrics = new AppleLyrics();
            lyrics.TtmlLyrics = ttml;
            return lyrics;
        }
    }

    private enum TimestampStyle
    {
        Bracket,
        Inline
    }
}
