using System;
using System.Linq;
using DeezSpoTag.Services.Genre;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// The city/country distinction is data, not display. A slug is one or the other,
/// and a term that cites an unknown slug is a corrupt catalog.
/// </summary>
public sealed class GenreResearchGeoTest
{
    private static GenreResearchGeoCatalog Geo => GenreResearchGeoCatalog.Current;

    [Fact]
    public void CountriesAndPlacesAreDisjointClosedVocabularies()
    {
        Assert.Equal(175, Geo.Countries.Count);
        Assert.Equal(1239, Geo.Places.Count);
        Assert.Equal("Brazil", Geo.CountryName("brazil"));
        Assert.Equal("United Kingdom", Geo.CountryName("united-kingdom"));
        Assert.Equal("DR Congo", Geo.CountryName("drc"));
        Assert.Equal("Côte d'Ivoire", Geo.CountryName("cote-d-ivoire"));
        Assert.Equal("Lagos", Geo.PlaceName("lagos"));
        Assert.Contains("nigeria", Geo.Places["lagos"].Countries);
        Assert.DoesNotContain("ghana", Geo.Places["lagos"].Countries);
        Assert.Empty(Geo.Countries.Keys.Intersect(Geo.Places.Keys));
        Assert.All(Geo.Countries.Keys, slug => Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", slug));
        Assert.All(Geo.Places.Keys, slug => Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", slug));
    }

    [Fact]
    public void ShippedTermsKeepCitiesOutOfTheCountryList()
    {
        var catalog = ResearchedGenreCatalog.Current;
        Assert.True(catalog.TryGetById("aboio", out var aboio));
        Assert.Equal(new[] { "brazil" }, aboio.Research?.MemberCountries);
        Assert.DoesNotContain("brazil", aboio.Research?.Places!);

        Assert.True(catalog.TryGetById("afrobeat", out var afrobeat));
        Assert.Contains("lagos", afrobeat.Research?.Places!);
        Assert.Equal(new[] { "nigeria" }, afrobeat.Research?.MemberCountries);
        Assert.DoesNotContain("lagos", afrobeat.Research?.MemberCountries!);
        Assert.DoesNotContain("nigeria", afrobeat.Research?.Places!);
    }

    [Fact]
    public void AnUnknownPlaceOrCountrySlugFailsCatalogLoad()
    {
        const string document = """
            {
              "version": "candidate",
              "source": { "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
              "counts": { "genres": 1, "styles": 0, "terms": 1, "aliases": 0, "regions": 10, "exclusions": 0, "ambiguous": 0, "parentEdges": 0, "multiParentTerms": 0 },
              "terms": [
                {
                  "id": "made-up",
                  "name": "Made Up",
                  "kind": "Genre",
                  "regions": [],
                  "aliases": [],
                  "parentIds": [],
                  "research": {
                    "status": "Verified",
                    "places": ["not-a-city"],
                    "memberCountries": ["brazil"]
                  }
                }
              ]
            }
            """;

        var error = Assert.Throws<InvalidOperationException>(() => ResearchedGenreCatalog.BuildFromJson(document));
        Assert.Contains("not-a-city", error.Message);
    }

    [Fact]
    public void APlaceThatIsAlsoACountryFailsGeoLoad()
    {
        const string document = """
            {
              "version": "candidate",
              "countries": { "brazil": "Brazil" },
              "places": { "brazil": { "name": "Brazil", "countries": [] } }
            }
            """;

        var error = Assert.Throws<InvalidOperationException>(() => GenreResearchGeoCatalog.BuildFromJson(document));
        Assert.Contains("both a place and a country", error.Message);
    }
}
