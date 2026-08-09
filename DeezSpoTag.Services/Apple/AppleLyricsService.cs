using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Apple;

public enum AppleTtmlTimingKind
{
    Invalid = 0,
    Untimed = 1,
    Line = 2,
    Word = 3
}

public sealed class AppleLyricsService
{
    private readonly record struct TypedLyricsRequestContext(
        string Token,
        string AppleId,
        string Storefront,
        string MediaUserToken);

    private sealed record AppleLyricsCandidates(
        string? WordTtml,
        string? LineTtml,
        string? UntimedTtml);

    private const string DefaultLanguage = "en-US";
    private const string SyncedLyricsType = "lyrics";
    private const string SyllableLyricsType = "syllable-lyrics";
    private const string TtmlLyricsType = "ttml-lyrics";
    private const string UnsyncedLyricsType = "unsynced-lyrics";
    private const int MinMediaUserTokenLength = 50;
    private const string DefaultWrapperHost = "127.0.0.1";
    private const string WrapperHostEnvironmentVariable = "DEEZSPOTAG_APPLE_WRAPPER_HOST";
    private const string AppleMusicScheme = "https";
    private const string AppleMusicHost = "music.apple.com";
    private const string AppleMusicCatalogApiHost = "amp-api.music.apple.com";
    private const string TtmlNamespace = "http://www.w3.org/ns/ttml";
    private const string MediaUserTokenHeader = "Media-User-Token";
    private const string UserAgentHeader = "User-Agent";
    private readonly AppleMusicCatalogService _catalogService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AppleLyricsService> _logger;
    private static readonly HttpClient WrapperAccountClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };
    private static readonly XmlSchemaSet TtmlSchemas = CreateTtmlSchemas();

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

        var candidates = await FetchLyricsTtmlAsync(
            appleId,
            storefront,
            language,
            lrcType,
            mediaUserToken,
            settings.SynthesizeLrcFromTtml,
            cancellationToken);
        if (!HasUsableCandidate(candidates) && string.IsNullOrWhiteSpace(mediaUserToken))
        {
            mediaUserToken = NormalizeMediaUserToken(await TryResolveWrapperMusicTokenAsync(cancellationToken));
            if (!string.IsNullOrWhiteSpace(mediaUserToken))
            {
                storefront = await _catalogService.ResolveStorefrontAsync(
                    settings.AppleMusic?.Storefront,
                    mediaUserToken,
                    cancellationToken);
                candidates = await FetchLyricsTtmlAsync(
                    appleId,
                    storefront,
                    language,
                    lrcType,
                    mediaUserToken,
                    settings.SynthesizeLrcFromTtml,
                    cancellationToken);
            }
        }

        if (!HasUsableCandidate(candidates))
        {
            return AppleLyrics.CreateError("Apple Music lyrics not available.");
        }

        return AppleLyrics.FromCandidates(candidates!, settings.SynthesizeLrcFromTtml);
    }

    private static bool HasUsableCandidate(AppleLyricsCandidates? candidates)
        => candidates != null
           && (!string.IsNullOrWhiteSpace(candidates.WordTtml)
               || !string.IsNullOrWhiteSpace(candidates.LineTtml)
               || !string.IsNullOrWhiteSpace(candidates.UntimedTtml));

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

    private async Task<AppleLyricsCandidates?> FetchLyricsTtmlAsync(
        string appleId,
        string storefront,
        string language,
        string lrcType,
        string mediaUserToken,
        bool synthesizeLrcFromTtml,
        CancellationToken cancellationToken)
    {
        var token = await _catalogService.GetCatalogTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var languageCandidates = BuildLanguageCandidates(language);
        var typeCandidates = BuildLyricsTypeCandidates(lrcType, synthesizeLrcFromTtml);
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

    private async Task<AppleLyricsCandidates?> TryFetchLyricsFromTypedEndpointsAsync(
        HttpClient client,
        TypedLyricsRequestContext context,
        IEnumerable<string> typeCandidates,
        IEnumerable<string> languageCandidates,
        CancellationToken cancellationToken)
    {
        string? wordTtml = null;
        string? lineTtml = null;
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
                switch (ClassifyTtml(ttml))
                {
                    case AppleTtmlTimingKind.Word when wordTtml == null:
                        wordTtml = ttml;
                        break;
                    case AppleTtmlTimingKind.Line when lineTtml == null:
                        lineTtml = ttml;
                        break;
                    case AppleTtmlTimingKind.Untimed when plainTextTtml == null:
                        plainTextTtml = ttml;
                        break;
                }
            }
        }

        return new AppleLyricsCandidates(wordTtml, lineTtml, plainTextTtml);
    }

    private static IEnumerable<string> BuildLyricsTypeCandidates(string lrcType, bool synthesizeLrcFromTtml)
    {
        var selectedTypes = ParseLyricsTypeSelection(lrcType);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (selectedTypes.Contains(TtmlLyricsType, StringComparer.OrdinalIgnoreCase)
            && emitted.Add(SyllableLyricsType))
        {
            yield return SyllableLyricsType;
        }

        if (synthesizeLrcFromTtml && emitted.Add(SyncedLyricsType))
        {
            yield return SyncedLyricsType;
        }

        if (selectedTypes.Contains(SyllableLyricsType, StringComparer.OrdinalIgnoreCase)
            && emitted.Add(SyllableLyricsType))
        {
            yield return SyllableLyricsType;
        }

        if (selectedTypes.Contains(UnsyncedLyricsType, StringComparer.OrdinalIgnoreCase)
            && emitted.Add(SyncedLyricsType))
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
            selected.Add(TtmlLyricsType);
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
            selected.Add(TtmlLyricsType);
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
            TtmlLyricsType => TtmlLyricsType,
            "ttml" => TtmlLyricsType,
            "ttmllyrics" => TtmlLyricsType,
            "ttml_lyrics" => TtmlLyricsType,
            UnsyncedLyricsType => UnsyncedLyricsType,
            "unsyncedlyrics" => UnsyncedLyricsType,
            "unsynced" => UnsyncedLyricsType,
            "unsynchronized-lyrics" => UnsyncedLyricsType,
            "unsynchronised-lyrics" => UnsyncedLyricsType,
            _ => null
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
        var localizedTtml = TryReadLocalizedTtml(attrs, preferredLanguage);
        if (IsWordSyncedTtml(directTtml))
        {
            return directTtml;
        }
        if (IsWordSyncedTtml(localizedTtml))
        {
            return localizedTtml;
        }
        if (IsUsableAppleLyricsTtml(directTtml))
        {
            return directTtml;
        }
        return IsUsableAppleLyricsTtml(localizedTtml) ? localizedTtml : null;
    }

    private static bool IsUsableAppleLyricsTtml(string? ttml)
        => ClassifyTtml(ttml) is not AppleTtmlTimingKind.Invalid;

    public static bool IsTimedTtml(string? ttml)
        => ClassifyTtml(ttml) is AppleTtmlTimingKind.Line or AppleTtmlTimingKind.Word;

    public static bool IsWordSyncedTtml(string? ttml)
        => ClassifyTtml(ttml) == AppleTtmlTimingKind.Word;

    public static bool IsLineSyncedTtml(string? ttml)
        => ClassifyTtml(ttml) == AppleTtmlTimingKind.Line;

    public static bool IsAppleNativeTtml(string? ttml)
    {
        if (!TryReadTtmlDocument(ttml, out var document))
        {
            return false;
        }

        return document.Root!.Attributes()
            .Any(attribute => attribute.IsNamespaceDeclaration
                && attribute.Value.Contains("music.apple.com/lyric-ttml", StringComparison.OrdinalIgnoreCase));
    }

    public static AppleTtmlTimingKind ClassifyTtml(string? ttml)
    {
        if (!TryReadTtmlDocument(ttml, out var document))
        {
            return AppleTtmlTimingKind.Invalid;
        }

        var root = document.Root!;
        var timing = root.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals("timing", StringComparison.OrdinalIgnoreCase))?
            .Value.Trim();
        if (string.Equals(timing, "None", StringComparison.OrdinalIgnoreCase))
        {
            return document.Descendants().Any(element =>
                element.Name.LocalName.Equals("p", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(element.Value))
                ? AppleTtmlTimingKind.Untimed
                : AppleTtmlTimingKind.Invalid;
        }

        if (string.Equals(timing, "Word", StringComparison.OrdinalIgnoreCase))
        {
            return document.Descendants()
                .Where(element => element.Name.LocalName.Equals("p", StringComparison.OrdinalIgnoreCase))
                .Any(paragraph => paragraph.Descendants().Any(element =>
                    element.Name.LocalName.Equals("span", StringComparison.OrdinalIgnoreCase)
                    && HasAppleTimestamp(element, "begin")
                    && HasAppleTimestamp(element, "end")
                    && !string.IsNullOrWhiteSpace(element.Value)))
                ? AppleTtmlTimingKind.Word
                : AppleTtmlTimingKind.Invalid;
        }

        return document.Descendants().Any(element =>
            element.Name.LocalName.Equals("p", StringComparison.OrdinalIgnoreCase)
            && HasAppleTimestamp(element, "begin")
            && !string.IsNullOrWhiteSpace(element.Value))
            ? AppleTtmlTimingKind.Line
            : AppleTtmlTimingKind.Invalid;
    }

    public static bool TryConvertTtmlToSynchronizedLyrics(
        string? ttml,
        out List<SynchronizedLyric> synchronizedLyrics)
    {
        synchronizedLyrics = new List<SynchronizedLyric>();
        var kind = ClassifyTtml(ttml);
        if (kind is not AppleTtmlTimingKind.Line and not AppleTtmlTimingKind.Word
            || !TryReadTtmlDocument(ttml, out var document))
        {
            return false;
        }

        foreach (var paragraph in document.Descendants()
                     .Where(element => element.Name.LocalName.Equals("p", StringComparison.OrdinalIgnoreCase)))
        {
            var text = kind == AppleTtmlTimingKind.Word
                ? BuildWordParagraphText(paragraph)
                : NormalizeLyricText(paragraph.Value);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!TryReadTimestampMilliseconds(paragraph, "begin", out var beginMilliseconds)
                && !paragraph.Descendants()
                    .Where(element => element.Name.LocalName.Equals("span", StringComparison.OrdinalIgnoreCase))
                    .Any(span => TryReadTimestampMilliseconds(span, "begin", out beginMilliseconds)))
            {
                continue;
            }

            var duration = 0;
            if (TryReadTimestampMilliseconds(paragraph, "end", out var endMilliseconds)
                && endMilliseconds > beginMilliseconds)
            {
                duration = endMilliseconds - beginMilliseconds;
            }

            var lyric = new SynchronizedLyric(
                text,
                SynchronizedLyric.BuildLrcTimestamp(beginMilliseconds),
                beginMilliseconds,
                duration)
            {
                Agent = paragraph.Attributes()
                    .FirstOrDefault(attribute =>
                        attribute.Name.LocalName.Equals("agent", StringComparison.OrdinalIgnoreCase))?
                    .Value.Trim(),
                IsBackground = paragraph.AncestorsAndSelf().Any(element =>
                    element.Attributes().Any(attribute =>
                        attribute.Name.LocalName.Equals("role", StringComparison.OrdinalIgnoreCase)
                        && IsBackgroundRole(attribute.Value))),
                Translation = ReadRoleText(paragraph, "x-translation"),
                Romanization = ReadRoleText(paragraph, "x-roman"),
                BackgroundVocals = ReadRoleText(paragraph, "x-bg")
            };
            if (kind == AppleTtmlTimingKind.Word)
            {
                lyric.Words = paragraph.Descendants()
                    .Where(element =>
                        element.Name.LocalName.Equals("span", StringComparison.OrdinalIgnoreCase)
                        && !HasAuxiliaryRole(element)
                        && !element.Descendants().Any(descendant =>
                            descendant.Name.LocalName.Equals("span", StringComparison.OrdinalIgnoreCase)))
                    .Select(element =>
                    {
                        var hasStart = TryReadTimestampMilliseconds(element, "begin", out var start);
                        var hasEnd = TryReadTimestampMilliseconds(element, "end", out var end);
                        var wordText = element.Value;
                        return hasStart && hasEnd && end > start && !string.IsNullOrWhiteSpace(wordText)
                            ? new SynchronizedLyricWord(wordText, start, end)
                            {
                                IsBackground = element.AncestorsAndSelf().Any(ancestor =>
                                    ancestor.Attributes().Any(attribute =>
                                        attribute.Name.LocalName.Equals("role", StringComparison.OrdinalIgnoreCase)
                                        && IsBackgroundRole(attribute.Value)))
                            }
                            : null;
                    })
                    .Where(static word => word != null)
                    .Select(static word => word!)
                    .ToList();
            }
            synchronizedLyrics.Add(lyric);
        }

        synchronizedLyrics = synchronizedLyrics
            .OrderBy(line => line.Milliseconds)
            .GroupBy(line => line.Milliseconds)
            .Select(group => group.First())
            .ToList();
        return synchronizedLyrics.Count > 0;
    }

    private static string BuildWordParagraphText(XElement paragraph)
    {
        var words = paragraph.Descendants()
            .Where(element =>
                element.Name.LocalName.Equals("span", StringComparison.OrdinalIgnoreCase)
                && !HasAuxiliaryRole(element)
                && HasAppleTimestamp(element, "begin")
                && HasAppleTimestamp(element, "end")
                && !element.Descendants().Any(descendant =>
                    descendant.Name.LocalName.Equals("span", StringComparison.OrdinalIgnoreCase)
                    && HasAppleTimestamp(descendant, "begin")
                    && HasAppleTimestamp(descendant, "end")))
            .Select(element => NormalizeLyricText(element.Value))
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        if (words.Length == 0)
        {
            return string.Empty;
        }

        return Regex.Replace(
            string.Join(' ', words),
            @"\s+([,.;:!?])",
            "$1",
            RegexOptions.CultureInvariant);
    }

    private static string NormalizeLyricText(string? value)
        => Regex.Replace(
                value?.Trim() ?? string.Empty,
                @"\s+",
                " ",
                RegexOptions.CultureInvariant)
            .Trim();

    private static string? ReadRoleText(XElement paragraph, string role)
    {
        var values = paragraph.Descendants()
            .Where(element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName.Equals("role", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attribute.Value.Trim(), role, StringComparison.OrdinalIgnoreCase)))
            .Select(element => NormalizeLyricText(element.Value))
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return values.Length == 0 ? null : string.Join(' ', values);
    }

    private static bool HasAuxiliaryRole(XElement element)
        => element.AncestorsAndSelf().Any(candidate =>
            candidate.Attributes().Any(attribute =>
                attribute.Name.LocalName.Equals("role", StringComparison.OrdinalIgnoreCase)
                && (attribute.Value.Trim().Equals("x-bg", StringComparison.OrdinalIgnoreCase)
                    || attribute.Value.Trim().Equals("x-translation", StringComparison.OrdinalIgnoreCase)
                    || attribute.Value.Trim().Equals("x-roman", StringComparison.OrdinalIgnoreCase))));

    private static bool IsBackgroundRole(string role)
        => role.Trim().Equals("x-bg", StringComparison.OrdinalIgnoreCase)
           || role.Contains("background", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadTimestampMilliseconds(XElement element, string attributeName, out int milliseconds)
    {
        milliseconds = 0;
        var value = element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(attributeName, StringComparison.OrdinalIgnoreCase))?
            .Value.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0
            && seconds <= int.MaxValue / 1000m)
        {
            milliseconds = (int)decimal.Round(seconds * 1000m, 0, MidpointRounding.AwayFromZero);
            return true;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var timestamp)
            && timestamp >= TimeSpan.Zero
            && timestamp.TotalMilliseconds <= int.MaxValue)
        {
            milliseconds = (int)Math.Round(timestamp.TotalMilliseconds, MidpointRounding.AwayFromZero);
            return true;
        }

        return false;
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
            var readerSettings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 4 * 1024 * 1024,
                MaxCharactersFromEntities = 0,
                ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings
            };
            readerSettings.ValidationType = ValidationType.Schema;
            readerSettings.Schemas = TtmlSchemas;

            using var reader = XmlReader.Create(
                new StringReader(ttml),
                readerSettings);
            document = XDocument.Load(reader, LoadOptions.None);
            return document.Root?.Name.LocalName.Equals("tt", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception ex) when (ex is XmlException or XmlSchemaException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static XmlSchemaSet CreateTtmlSchemas()
    {
        var schemas = new XmlSchemaSet
        {
            XmlResolver = null
        };
        AddTtmlSchema(schemas, TtmlNamespace);
        AddTtmlSchema(schemas, targetNamespace: null);
        schemas.Compile();
        return schemas;
    }

    private static void AddTtmlSchema(XmlSchemaSet schemas, string? targetNamespace)
    {
        var namespaceDeclaration = string.IsNullOrEmpty(targetNamespace)
            ? string.Empty
            : $"targetNamespace=\"{targetNamespace}\" xmlns:ttml=\"{targetNamespace}\"";
        var typePrefix = string.IsNullOrEmpty(targetNamespace) ? string.Empty : "ttml:";
        var schemaXml = $$"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                       {{namespaceDeclaration}}
                       elementFormDefault="qualified"
                       attributeFormDefault="unqualified">
              <xs:element name="tt" type="{{typePrefix}}TtmlRootType" />

              <xs:complexType name="TtmlRootType">
                <xs:sequence>
                  <xs:element name="head" type="{{typePrefix}}TtmlContentType" minOccurs="0" />
                  <xs:element name="body" type="{{typePrefix}}TtmlContentType" minOccurs="0" />
                </xs:sequence>
                <xs:anyAttribute namespace="##any" processContents="skip" />
              </xs:complexType>

              <xs:complexType name="TtmlContentType" mixed="true">
                <xs:choice minOccurs="0" maxOccurs="unbounded">
                  <xs:element name="body" type="{{typePrefix}}TtmlContentType" />
                  <xs:element name="div" type="{{typePrefix}}TtmlContentType" />
                  <xs:element name="p" type="{{typePrefix}}TtmlContentType" />
                  <xs:element name="span" type="{{typePrefix}}TtmlContentType" />
                  <xs:element name="br" type="{{typePrefix}}TtmlContentType" />
                  <xs:element name="metadata" type="{{typePrefix}}TtmlContentType" />
                  <xs:element name="styling" type="{{typePrefix}}TtmlContentType" />
                  <xs:element name="style" type="{{typePrefix}}TtmlContentType" />
                  <xs:element name="layout" type="{{typePrefix}}TtmlContentType" />
                  <xs:element name="region" type="{{typePrefix}}TtmlContentType" />
                  <xs:element name="set" type="{{typePrefix}}TtmlContentType" />
                  <xs:any namespace="##other" processContents="skip" />
                </xs:choice>
                <xs:anyAttribute namespace="##any" processContents="skip" />
              </xs:complexType>
            </xs:schema>
            """;

        using var schemaReader = XmlReader.Create(
            new StringReader(schemaXml),
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
        var schema = XmlSchema.Read(schemaReader, validationEventHandler: null)
            ?? throw new InvalidOperationException("The built-in TTML schema could not be loaded.");
        schemas.Add(schema);
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

        public static AppleLyrics FromCandidates(
            AppleLyricsCandidates candidates,
            bool synthesizeLrcFromTtml)
        {
            var lyrics = new AppleLyrics();
            if (IsWordSyncedTtml(candidates.WordTtml))
            {
                lyrics.TtmlLyrics = candidates.WordTtml;
                lyrics.TtmlLyricsSourceFormat = LyricsSourceFormat.DownloadedTtml;
            }

            if (synthesizeLrcFromTtml)
            {
                var lrcSource = IsLineSyncedTtml(candidates.LineTtml)
                    ? candidates.LineTtml
                    : candidates.WordTtml;
                if (TryConvertTtmlToSynchronizedLyrics(lrcSource, out var synchronizedLyrics))
                {
                    lyrics.SyncedLyrics = synchronizedLyrics;
                    lyrics.SyncedLyricsSourceFormat = LyricsSourceFormat.ConvertedFromTtml;
                }
            }

            if (TryExtractPlainLyrics(candidates.UntimedTtml, out var plainLyrics))
            {
                lyrics.UnsyncedLyrics = plainLyrics;
                lyrics.UnsyncedLyricsSourceFormat = LyricsSourceFormat.DownloadedPlainText;
            }

            return lyrics.IsLoaded()
                ? lyrics
                : CreateError("Apple Music returned unusable lyrics timing.");
        }
    }
}
