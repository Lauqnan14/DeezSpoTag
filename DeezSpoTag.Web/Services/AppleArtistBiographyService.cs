using System.Net;
using System.Text.Json;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Services;

public sealed class AppleArtistBiographyService
{
    private const string DefaultStorefront = "us";
    private const string DefaultLanguage = "en-US";
    private const string AttributesField = "attributes";
    private const string NameField = "name";
    private const string EditorialNotesField = "editorialNotes";
    private const string StandardField = "standard";
    private const string ShortField = "short";
    private const string JsonLdScriptType = "application/ld+json";
    private const string MusicGroupType = "MusicGroup";
    private const string DescriptionField = "description";
    private const string TypeJsonField = "@type";

    private readonly AppleMusicCatalogService _catalog;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AppleArtistBiographyService> _logger;

    public AppleArtistBiographyService(
        AppleMusicCatalogService catalog,
        DeezSpoTagSettingsService settingsService,
        IHttpClientFactory httpClientFactory,
        ILogger<AppleArtistBiographyService> logger)
    {
        _catalog = catalog;
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<AppleArtistBiographyResult?> ResolveByArtistIdAsync(
        string appleArtistId,
        string? expectedArtistName,
        CancellationToken cancellationToken)
    {
        var id = (appleArtistId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var storefront = GetStorefront();
        using var doc = await _catalog.GetArtistAsync(id, storefront, DefaultLanguage, cancellationToken);
        if (!AppleCatalogJsonHelper.TryGetDataArray(doc.RootElement, out var dataArr)
            || dataArr.GetArrayLength() == 0)
        {
            return new AppleArtistBiographyResult(id, string.Empty, string.Empty, string.Empty);
        }

        var item = dataArr[0];
        var attrs = item.TryGetProperty(AttributesField, out var attributes) && attributes.ValueKind == JsonValueKind.Object
            ? attributes
            : default;
        var name = attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty(NameField, out var nameElement)
            ? nameElement.GetString() ?? string.Empty
            : string.Empty;
        var image = attrs.ValueKind == JsonValueKind.Object
            ? AppleCatalogJsonHelper.ResolveArtwork(attrs)
            : string.Empty;
        var biography = await ResolveAppleArtistBiographyAsync(id, FirstNonEmpty(name, expectedArtistName), storefront, attrs, cancellationToken);
        return new AppleArtistBiographyResult(id, name, image, biography ?? string.Empty);
    }

    public async Task<AppleArtistBiographyResult?> ResolveByExactArtistNameAndTracksAsync(
        string artistName,
        IReadOnlyCollection<string> trackTitles,
        CancellationToken cancellationToken)
    {
        var normalizedName = NormalizeArtistName(artistName);
        if (string.IsNullOrWhiteSpace(normalizedName) || trackTitles.Count == 0)
        {
            return null;
        }

        var storefront = GetStorefront();
        using var doc = await _catalog.SearchAsync(
            normalizedName,
            10,
            storefront,
            DefaultLanguage,
            cancellationToken,
            new AppleMusicCatalogService.AppleSearchOptions(TypesOverride: "artists"));
        if (!TryFindExactArtist(doc.RootElement, normalizedName, out var appleId))
        {
            return null;
        }

        if (!await HasMatchingAppleTrackAsync(normalizedName, trackTitles, storefront, cancellationToken))
        {
            return null;
        }

        return await ResolveByArtistIdAsync(appleId, normalizedName, cancellationToken);
    }

    private async Task<bool> HasMatchingAppleTrackAsync(
        string normalizedArtistName,
        IReadOnlyCollection<string> trackTitles,
        string storefront,
        CancellationToken cancellationToken)
    {
        foreach (var trackTitle in trackTitles
                     .Select(NormalizeTrackTitle)
                     .Where(static title => title.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(8))
        {
            using var doc = await _catalog.SearchAsync(
                $"{normalizedArtistName} {trackTitle}",
                10,
                storefront,
                DefaultLanguage,
                cancellationToken,
                new AppleMusicCatalogService.AppleSearchOptions(TypesOverride: "songs"));
            if (AppleSongSearchHasExactTrack(doc.RootElement, normalizedArtistName, trackTitle))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AppleSongSearchHasExactTrack(JsonElement root, string normalizedArtistName, string normalizedTrackTitle)
    {
        if (!root.TryGetProperty("results", out var results)
            || !results.TryGetProperty("songs", out var songs)
            || !songs.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in data.EnumerateArray())
        {
            var attrs = item.TryGetProperty(AttributesField, out var attributes) && attributes.ValueKind == JsonValueKind.Object
                ? attributes
                : default;
            if (attrs.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var artistName = attrs.TryGetProperty("artistName", out var artistElement)
                ? NormalizeArtistName(artistElement.GetString())
                : string.Empty;
            var trackName = attrs.TryGetProperty(NameField, out var nameElement)
                ? NormalizeTrackTitle(nameElement.GetString())
                : string.Empty;
            if (string.Equals(artistName, normalizedArtistName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(trackName, normalizedTrackTitle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<string?> ResolveAppleArtistBiographyAsync(
        string id,
        string artistName,
        string storefront,
        JsonElement attributes,
        CancellationToken cancellationToken)
    {
        var editorialNotes = ResolveEditorialNotes(attributes);
        if (!string.IsNullOrWhiteSpace(editorialNotes))
        {
            return editorialNotes;
        }

        return await ResolveAppleArtistPageBiographyAsync(id, artistName, storefront, cancellationToken);
    }

    private async Task<string?> ResolveAppleArtistPageBiographyAsync(
        string id,
        string artistName,
        string storefront,
        CancellationToken cancellationToken)
    {
        var url = BuildAppleArtistPageUrl(id, storefront);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 AppleWebKit/537.36 Chrome/125 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            var client = _httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Apple artist biography page returned HTTP {StatusCode} for artist Id", (int)response.StatusCode);
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            return ResolveAppleArtistPageBiography(html, id, artistName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Apple artist biography page fetch failed for artist Id");
            return null;
        }
    }

    private string GetStorefront()
    {
        var settings = _settingsService.LoadSettings();
        return string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront)
            ? DefaultStorefront
            : settings.AppleMusic!.Storefront;
    }

    private static bool TryFindExactArtist(JsonElement root, string expectedArtistName, out string appleId)
    {
        appleId = string.Empty;
        if (!root.TryGetProperty("results", out var results)
            || !results.TryGetProperty("artists", out var artists)
            || !artists.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement))
            {
                continue;
            }

            var id = idElement.GetString()?.Trim() ?? string.Empty;
            var attrs = item.TryGetProperty(AttributesField, out var attributes) && attributes.ValueKind == JsonValueKind.Object
                ? attributes
                : default;
            var name = attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty(NameField, out var nameElement)
                ? NormalizeArtistName(nameElement.GetString())
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(id)
                && string.Equals(name, expectedArtistName, StringComparison.OrdinalIgnoreCase))
            {
                appleId = id;
                return true;
            }
        }

        return false;
    }

    private static string BuildAppleArtistPageUrl(string id, string storefront)
    {
        var normalizedStorefront = string.IsNullOrWhiteSpace(storefront) ? DefaultStorefront : storefront.Trim();
        return $"https://music.apple.com/{Uri.EscapeDataString(normalizedStorefront)}/artist/{Uri.EscapeDataString(id)}";
    }

    private static string? ResolveAppleArtistPageBiography(string html, string id, string artistName)
    {
        foreach (var json in EnumerateJsonLdScripts(html))
        {
            var decoded = WebUtility.HtmlDecode(json);
            try
            {
                using var doc = JsonDocument.Parse(decoded);
                var resolvedDescription = ResolveMusicGroupDescription(doc.RootElement, id, artistName);
                if (!string.IsNullOrWhiteSpace(resolvedDescription))
                {
                    return resolvedDescription;
                }
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateJsonLdScripts(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            yield break;
        }

        var searchIndex = 0;
        while (searchIndex < html.Length)
        {
            var scriptStart = html.IndexOf("<script", searchIndex, StringComparison.OrdinalIgnoreCase);
            if (scriptStart < 0)
            {
                yield break;
            }

            var openEnd = html.IndexOf('>', scriptStart);
            if (openEnd < 0)
            {
                yield break;
            }

            var openTag = html[scriptStart..openEnd];
            var closeStart = html.IndexOf("</script>", openEnd + 1, StringComparison.OrdinalIgnoreCase);
            if (closeStart < 0)
            {
                yield break;
            }

            searchIndex = closeStart + "</script>".Length;
            if (openTag.IndexOf(JsonLdScriptType, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            yield return html[(openEnd + 1)..closeStart].Trim();
        }
    }

    private static string? ResolveMusicGroupDescription(JsonElement root, string id, string artistName)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                var itemDescription = ResolveMusicGroupDescription(item, id, artistName);
                if (!string.IsNullOrWhiteSpace(itemDescription))
                {
                    return itemDescription;
                }
            }

            return null;
        }

        if (root.ValueKind != JsonValueKind.Object
            || !JsonStringEquals(root, TypeJsonField, MusicGroupType)
            || !ArtistPageMatches(root, id, artistName))
        {
            return null;
        }

        return TryGetNonEmptyString(root, DescriptionField, out var description)
            ? NormalizeBiography(description)
            : null;
    }

    private static bool ArtistPageMatches(JsonElement root, string id, string artistName)
    {
        if (TryGetNonEmptyString(root, "url", out var url)
            && url.Contains($"/{id}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!TryGetNonEmptyString(root, NameField, out var name))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(artistName)
            && name.Equals(artistName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveEditorialNotes(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object
            || !attributes.TryGetProperty(EditorialNotesField, out var notes)
            || notes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryGetNonEmptyString(notes, StandardField, out var standard))
        {
            return standard;
        }

        return TryGetNonEmptyString(notes, ShortField, out var shortNote)
            ? shortNote
            : null;
    }

    private static bool JsonStringEquals(JsonElement root, string propertyName, string expected)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && string.Equals(property.GetString(), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetNonEmptyString(JsonElement obj, string propertyName, out string value)
    {
        value = string.Empty;
        if (obj.ValueKind != JsonValueKind.Object
            || !obj.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()?.Trim() ?? string.Empty;
        return value.Length > 0;
    }

    private static string NormalizeBiography(string value)
    {
        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    private static string NormalizeArtistName(string? value)
        => string.Join(' ', (value ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizeTrackTitle(string? value)
        => string.Join(' ', (value ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string FirstNonEmpty(params string?[] values)
        => values.Select(value => (value ?? string.Empty).Trim()).FirstOrDefault(value => value.Length > 0) ?? string.Empty;
}

public sealed record AppleArtistBiographyResult(string AppleId, string Name, string Image, string Biography);
