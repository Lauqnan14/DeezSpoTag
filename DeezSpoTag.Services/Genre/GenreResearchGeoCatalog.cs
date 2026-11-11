namespace DeezSpoTag.Services.Genre;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// The closed country and place vocabularies the researched catalog cites.
/// </summary>
/// <remarks>
/// A value is a country or a place, never both. The catalogs are generated with
/// the taxonomy and embedded so a later Location rule can reuse the same slugs
/// without parsing prose again.
/// </remarks>
public sealed class GenreResearchGeoCatalog
{
    private const string ResourceName = "DeezSpoTag.Services.Genre.Data.genre-research-geo.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Lazy<GenreResearchGeoCatalog> Instance = new(Load, isThreadSafe: true);

    private GenreResearchGeoCatalog(
        string version,
        IReadOnlyDictionary<string, string> countries,
        IReadOnlyDictionary<string, ResearchPlace> places)
    {
        Version = version;
        Countries = countries;
        Places = places;
    }

    public static GenreResearchGeoCatalog Current => Instance.Value;

    public string Version { get; }

    public IReadOnlyDictionary<string, string> Countries { get; }

    public IReadOnlyDictionary<string, ResearchPlace> Places { get; }

    public bool IsCountry(string? slug) => slug is not null && Countries.ContainsKey(slug);

    public bool IsPlace(string? slug) => slug is not null && Places.ContainsKey(slug);

    public string CountryName(string slug)
        => Countries.TryGetValue(slug, out var name) ? name : slug;

    public string PlaceName(string slug)
        => Places.TryGetValue(slug, out var place) ? place.Name : slug;

    /// <summary>
    /// Validates a candidate geo document with the same checks as the embedded one.
    /// </summary>
    public static GenreResearchGeoCatalog BuildFromJson(string json)
    {
        var document = JsonSerializer.Deserialize<GeoDocument>(json, Options)
                       ?? throw new InvalidOperationException("The research geo document is empty.");
        return Build(document);
    }

    private static GenreResearchGeoCatalog Load()
    {
        using var stream = typeof(GenreResearchGeoCatalog).GetTypeInfo().Assembly
            .GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"The research geo resource '{ResourceName}' is missing from the assembly. "
                + "The build must embed DeezSpoTag.Services/Genre/Data/genre-research-geo.json.");
        }

        using var reader = new StreamReader(stream);
        var document = JsonSerializer.Deserialize<GeoDocument>(reader.ReadToEnd(), Options)
                       ?? throw new InvalidOperationException("The research geo resource is empty.");
        return Build(document);
    }

    private static GenreResearchGeoCatalog Build(GeoDocument document)
    {
        if (document.Countries is not { Count: > 0 })
        {
            throw new InvalidOperationException("Research geo is corrupt: it has no countries.");
        }

        var countries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (slug, name) in document.Countries)
        {
            RequireSlug(slug, "country");
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException($"Research geo is corrupt: country '{slug}' has no display name.");
            }

            countries[slug] = name.Trim();
        }

        var places = new Dictionary<string, ResearchPlace>(StringComparer.Ordinal);
        foreach (var (slug, place) in document.Places ?? new Dictionary<string, GeoPlace>())
        {
            RequireSlug(slug, "place");
            if (countries.ContainsKey(slug))
            {
                throw new InvalidOperationException(
                    $"Research geo is corrupt: '{slug}' is both a place and a country.");
            }

            if (place is null || string.IsNullOrWhiteSpace(place.Name))
            {
                throw new InvalidOperationException($"Research geo is corrupt: place '{slug}' has no display name.");
            }

            var owning = new List<string>();
            foreach (var country in place.Countries ?? [])
            {
                var countrySlug = country?.Trim() ?? string.Empty;
                if (countrySlug.Length == 0)
                {
                    continue;
                }

                if (!countries.ContainsKey(countrySlug))
                {
                    throw new InvalidOperationException(
                        $"Research geo is corrupt: place '{slug}' cites unknown country '{countrySlug}'.");
                }

                if (!owning.Contains(countrySlug))
                {
                    owning.Add(countrySlug);
                }
            }

            places[slug] = new ResearchPlace(place.Name.Trim(), owning);
        }

        return new GenreResearchGeoCatalog(document.Version ?? string.Empty, countries, places);
    }

    private static void RequireSlug(string? slug, string kind)
    {
        if (string.IsNullOrWhiteSpace(slug)
            || slug != slug.Trim()
            || slug.Split('-').Any(part => part.Length == 0 || part.Any(ch => !char.IsAsciiLetterOrDigit(ch) || !char.IsLower(ch) && !char.IsDigit(ch))))
        {
            throw new InvalidOperationException($"Research geo is corrupt: {kind} slug '{slug}' is not a lowercase slug.");
        }
    }

    private sealed class GeoDocument
    {
        public string? Version { get; set; }

        public Dictionary<string, string>? Countries { get; set; }

        public Dictionary<string, GeoPlace>? Places { get; set; }
    }

    private sealed class GeoPlace
    {
        public string? Name { get; set; }

        public List<string>? Countries { get; set; }
    }
}

/// <summary>A researched place and the countries the research links it to.</summary>
public sealed record ResearchPlace(string Name, IReadOnlyList<string> Countries);
