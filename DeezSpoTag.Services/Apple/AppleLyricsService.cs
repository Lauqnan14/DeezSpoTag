using System.Text.Json;
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
    private const string MediaUserTokenHeader = "Media-User-Token";
    private const string UserAgentHeader = "User-Agent";
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
                    ValidationType = ValidationType.Schema,
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
