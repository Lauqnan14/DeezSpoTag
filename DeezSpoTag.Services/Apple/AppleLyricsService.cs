using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Utils;
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
    private const string AppleSource = "apple";
    private const string MediaUserTokenHeader = "Media-User-Token";
    private const string UserAgentHeader = "User-Agent";
    private static readonly string[] AppleIdKeys = ["apple_track_id", "apple_id", "appleid", AppleSource];
    private static readonly string[] AppleUrlKeys = ["apple_url", "source_url"];
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex AppleTrackUrlRegex = CreateRegex(@"music\.apple\.com\/[^\/]+\/(?:song|album)\/[^\/]+\/(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AppleQueryIdRegex = CreateRegex(@"(?:[?&]i=)(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static Regex CreateRegex(string pattern, RegexOptions options)
        => new(pattern, options, RegexTimeout);

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

        var mediaUserToken = NormalizeMediaUserToken(settings.AppleMusic?.MediaUserToken);
        var storefront = await _catalogService.ResolveStorefrontAsync(
            settings.AppleMusic?.Storefront,
            mediaUserToken,
            cancellationToken);
        var language = string.IsNullOrWhiteSpace(settings.DeezerLanguage) ? DefaultLanguage : settings.DeezerLanguage;
        var lrcType = string.IsNullOrWhiteSpace(settings.LrcType) ? SyncedLyricsType : settings.LrcType;

        var ttml = await FetchLyricsTtmlAsync(appleId, storefront, language, lrcType, mediaUserToken, cancellationToken);
        if (string.IsNullOrWhiteSpace(ttml) && string.IsNullOrWhiteSpace(mediaUserToken))
        {
            mediaUserToken = NormalizeMediaUserToken(await TryResolveWrapperMusicTokenAsync(cancellationToken));
            if (!string.IsNullOrWhiteSpace(mediaUserToken))
            {
                storefront = await _catalogService.ResolveStorefrontAsync(
                    settings.AppleMusic?.Storefront,
                    mediaUserToken,
                    cancellationToken);
                ttml = await FetchLyricsTtmlAsync(appleId, storefront, language, lrcType, mediaUserToken, cancellationToken);
            }
        }

        if (string.IsNullOrWhiteSpace(ttml))
        {
            return AppleLyrics.CreateError("Apple Music lyrics not available.");
        }

        return AppleLyrics.FromTtml(ttml);
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

        ApplyResolvedAppleIdToTrack(track, appleId);
        return await ResolveLyricsAsync(appleId, settings, cancellationToken);
    }

    private static void ApplyResolvedAppleIdToTrack(Track track, string appleId)
    {
        if (track == null || string.IsNullOrWhiteSpace(appleId))
        {
            return;
        }

        var normalizedAppleId = appleId.Trim();
        track.Urls["apple_track_id"] = normalizedAppleId;
        track.Urls["apple_id"] = normalizedAppleId;
        track.Urls[AppleSource] = $"https://music.apple.com/us/song/{normalizedAppleId}?i={normalizedAppleId}";
    }

    private static string? TryExtractAppleIdFromTrack(Track track)
    {
        if (track == null)
        {
            return null;
        }

        if (string.Equals(track.Source, AppleSource, StringComparison.OrdinalIgnoreCase)
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

        var fromId = TryResolveAppleIdFromKeySet(urls, AppleIdKeys, key => !key.Equals(AppleSource, StringComparison.OrdinalIgnoreCase));
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

        return AppleIdParser.TryExtractFromUri(uri);
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
        if (string.IsNullOrWhiteSpace(mediaUserToken))
        {
            return null;
        }

        using var client = _httpClientFactory.CreateClient();
        return await TryFetchLyricsFromTypedEndpointsAsync(
            client,
            new TypedLyricsRequestContext(token, appleId, storefront, mediaUserToken),
            typeCandidates,
            languageCandidates,
            cancellationToken);
    }

    private static string NormalizeMediaUserToken(string? mediaUserToken)
    {
        var trimmed = mediaUserToken?.Trim();
        return trimmed is { Length: >= MinMediaUserTokenLength } ? trimmed : string.Empty;
    }

    private async Task<string?> TryFetchLyricsFromTypedEndpointsAsync(
        HttpClient client,
        TypedLyricsRequestContext context,
        IEnumerable<string> typeCandidates,
        IEnumerable<string> languageCandidates,
        CancellationToken cancellationToken)
    {
        string? plainTextTtml = null;
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
                if (IsTimedTtml(ttml))
                {
                    return ttml;
                }

                if (plainTextTtml == null && TryExtractPlainLyrics(ttml, out _))
                {
                    plainTextTtml = ttml;
                }
            }
        }

        return plainTextTtml;
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
        if (IsUsableAppleLyricsTtml(directTtml))
        {
            return directTtml;
        }

        var localizedTtml = TryReadLocalizedTtml(attrs, preferredLanguage);
        return IsUsableAppleLyricsTtml(localizedTtml) ? localizedTtml : null;
    }

    private static bool IsUsableAppleLyricsTtml(string? ttml)
        => IsTimedTtml(ttml) || TryExtractPlainLyrics(ttml, out _);

    public static bool IsTimedTtml(string? ttml)
    {
        if (!TryReadTtmlDocument(ttml, out var document))
        {
            return false;
        }

        var root = document.Root!;
        var timing = root.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals("timing", StringComparison.OrdinalIgnoreCase))?
            .Value.Trim();
        if (string.Equals(timing, "None", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(timing, "Word", StringComparison.OrdinalIgnoreCase))
        {
            return document.Descendants().Any(element =>
                element.Name.LocalName.Equals("span", StringComparison.OrdinalIgnoreCase)
                && HasAppleTimestamp(element, "begin")
                && HasAppleTimestamp(element, "end")
                && !string.IsNullOrWhiteSpace(element.Value));
        }

        return document.Descendants().Any(element =>
            element.Name.LocalName.Equals("p", StringComparison.OrdinalIgnoreCase)
            && HasAppleTimestamp(element, "begin")
            && !string.IsNullOrWhiteSpace(element.Value));
    }

    public static bool TryExtractPlainLyrics(string? ttml, out string plainLyrics)
    {
        plainLyrics = string.Empty;
        if (!TryReadTtmlDocument(ttml, out var document))
        {
            return false;
        }

        var timing = document.Root!.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals("timing", StringComparison.OrdinalIgnoreCase))?
            .Value.Trim();
        if (!string.Equals(timing, "None", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lines = document.Descendants()
            .Where(element => element.Name.LocalName.Equals("p", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (lines.Length == 0)
        {
            return false;
        }

        plainLyrics = string.Join('\n', lines);
        return true;
    }

    private static bool TryReadTtmlDocument(string? ttml, out XDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(ttml))
        {
            return false;
        }

        try
        {
            using var reader = XmlReader.Create(
                new StringReader(ttml),
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 4 * 1024 * 1024
                });
            document = XDocument.Load(reader, LoadOptions.None);
            return document.Root?.Name.LocalName.Equals("tt", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static bool HasAppleTimestamp(XElement element, string attributeName)
    {
        var value = element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(attributeName, StringComparison.OrdinalIgnoreCase))?
            .Value.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds >= 0;
        }

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var timestamp)
               && timestamp >= TimeSpan.Zero;
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

            var validation = ValidateAppleCandidate(attrs, track, id);
            if (validation.IsMatch && validation.Score > bestScore)
            {
                bestScore = validation.Score;
                bestId = id;
            }
        }

        return bestId;
    }

    private static LyricsIdentityValidationResult ValidateAppleCandidate(JsonElement attrs, Track track, string appleId)
    {
        if (attrs.ValueKind == JsonValueKind.Undefined)
        {
            return new LyricsIdentityValidationResult(false, "Apple candidate had no attributes.", 0);
        }

        var title = attrs.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        var artist = attrs.TryGetProperty("artistName", out var artistEl) ? artistEl.GetString() : null;
        var album = attrs.TryGetProperty("albumName", out var albumEl) ? albumEl.GetString() : null;
        var durationMs = attrs.TryGetProperty("durationInMillis", out var durEl) && durEl.TryGetInt32(out var dur) ? dur : 0;
        var isrc = attrs.TryGetProperty("isrc", out var isrcEl) ? isrcEl.GetString() : null;

        return LyricsIdentityValidator.ValidateSearchCandidate(
            track,
            new LyricsCandidateIdentity(
                AppleSource,
                appleId,
                title,
                artist,
                album,
                durationMs > 0 ? Math.Max(1, durationMs / 1000) : null,
                isrc),
            durationToleranceSeconds: 10,
            requireArtist: true);
    }

    private sealed class AppleLyrics : LyricsBase
    {
        public static AppleLyrics CreateError(string message)
        {
            var lyrics = new AppleLyrics();
            lyrics.SetErrorMessage(message);
            return lyrics;
        }

        public static AppleLyrics FromTtml(string ttml)
        {
            if (IsTimedTtml(ttml))
            {
                return new AppleLyrics
                {
                    TtmlLyrics = ttml,
                    TtmlLyricsSourceFormat = LyricsSourceFormat.DownloadedTtml
                };
            }

            if (TryExtractPlainLyrics(ttml, out var plainLyrics))
            {
                return new AppleLyrics
                {
                    UnsyncedLyrics = plainLyrics,
                    UnsyncedLyricsSourceFormat = LyricsSourceFormat.DownloadedPlainText
                };
            }

            return CreateError("Apple Music returned unusable lyrics timing.");
        }
    }
}
