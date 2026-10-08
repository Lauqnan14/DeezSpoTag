using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Genre;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the regional organization of the Genre Intelligence taxonomy.
///
/// The whole feature rests on one claim: a region says where a term is filed, not
/// which genres may be used. These tests therefore check two separate things.
/// First, that organization behaves — one identity per term, the original African
/// vocabulary intact, every new region populated, regions many-to-many. Second, and
/// more importantly, that organization is inert to classification: the resolver has
/// no region input at all, and existing user data keeps working without migration.
/// </summary>
public sealed class GenreTaxonomyRegionTest : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "genre-regions-" + Guid.NewGuid());

    public GenreTaxonomyRegionTest() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }

        GC.SuppressFinalize(this);
    }

    private PersonalGenreStore CreateStore(string name = "library.db")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(_directory, name)
        }).Build();
        return new PersonalGenreStore(configuration);
    }

    private PersonalGenreService CreateService(PersonalGenreStore store)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(_directory, "library.db")
        }).Build();
        var repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
        return new PersonalGenreService(
            store, repository, NullLogger<PersonalGenreService>.Instance);
    }

    private static IReadOnlyList<PersonalGenreTaxon> Taxa => PersonalGenreTaxonomy.GetDefaultTaxa();

    private static PersonalGenreTaxon Taxon(string id) =>
        Taxa.Single(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> RegionsOf(string id) =>
        Taxon(id).Regions ?? Array.Empty<string>();

    // =================================================================
    // One canonical identity
    // =================================================================

    [Fact]
    public void HipHop_ExistsExactlyOnce_AndIsGlobal()
    {
        Assert.Single(Taxa, item => item.Id == "hip-hop");

        var hipHop = Taxon("hip-hop");
        Assert.Empty(hipHop.Regions ?? Array.Empty<string>());

        // No taxon is namespaced under a region. That is the structural guarantee
        // that "africa/hip-hop" and "europe/hip-hop" cannot exist: a region is
        // metadata on a flat id, never a path segment in one.
        Assert.DoesNotContain(Taxa, item => item.Id.Contains('/', StringComparison.Ordinal));
        Assert.DoesNotContain(Taxa, item => PersonalGenreRegions.AllSlugs.Contains(item.Id));
    }

    [Fact]
    public void EveryTaxonId_IsUnique()
    {
        var duplicates = Taxa
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void NoNameOrAlias_CollidesAcrossTaxa()
    {
        // The catalog resolves a value by normalized name, id and alias, and the
        // first writer wins. A collision across two taxa would silently make one of
        // them unreachable, so it is checked directly rather than left to chance.
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        var collisions = new List<string>();

        foreach (var taxon in Taxa)
        {
            foreach (var value in new[] { taxon.Name, taxon.Id }.Concat(taxon.Aliases ?? Array.Empty<string>()))
            {
                var key = PersonalGenreTaxonomy.Normalize(value);
                if (key.Length == 0)
                {
                    continue;
                }

                if (owner.TryGetValue(key, out var existing) && existing != taxon.Id)
                {
                    collisions.Add($"{key}: {existing} vs {taxon.Id}");
                }
                else
                {
                    owner[key] = taxon.Id;
                }
            }
        }

        Assert.Empty(collisions);
    }

    [Fact]
    public void EveryParentReference_Resolves()
    {
        var ids = Taxa.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var taxon in Taxa)
        {
            foreach (var parent in taxon.ParentIds ?? Array.Empty<string>())
            {
                Assert.Contains(parent, ids);
            }
        }
    }

    [Fact]
    public void CatalogMatchesAGlobalTerm_RegardlessOfRegionBrowsing()
    {
        // The catalog is built once from built-ins plus custom taxa and holds no
        // notion of a selected region, which is why browsing cannot fork identity.
        var catalog = new PersonalGenreCatalog();

        Assert.True(catalog.TryMatch("Hip Hop", out var viaSpelling, out var match));
        Assert.Equal("hip-hop", viaSpelling.Id);

        // The same id comes back whether it is reached by name, id or alias.
        Assert.True(catalog.TryGetById("hip-hop", out var byId));
        Assert.Equal(viaSpelling.Id, byId.Id);
        Assert.True(catalog.TryMatch("hip-hop", out var direct));
        Assert.Equal(byId.Id, direct.Id);
        Assert.Equal("Hip-Hop", viaSpelling.Name);
        Assert.False(match.IsCustom);
    }

    // =================================================================
    // The existing African taxonomy is preserved
    // =================================================================

    [Fact]
    public void AllFiftyEightOriginalTaxa_KeepTheirExactIds()
    {
        // The identity of every shipped term, captured before regions existed. If
        // any of these changed, a user's mappings, rules and locks would dangle.
        string[] original =
        [
            "afrobeat", "afrobeats", "bongo-flava", "amapiano", "hip-hop", "rnb", "reggae",
            "dancehall", "gospel", "electronic", "house", "pop", "soul", "rock", "jazz",
            "classical", "genge", "benga", "ohangla", "mugithi", "taarab", "singeli", "soukous",
            "congolese-rumba", "highlife", "coupe-decale", "kwaito", "afropop", "afro-fusion",
            "afro-soul", "alt-rnb", "trap", "boom-bap", "deep-house", "afro-house", "praise",
            "worship", "bongo-flava-pop", "bongo-flava-rnb", "bongo-flava-rap", "gengetone",
            "kenyan-drill", "ndombolo", "east-african-gospel", "swahili-gospel", "swahili-pop",
            "afrosounds", "east-africa", "swahili", "tanzania", "kenya", "uganda", "south-africa",
            "nigeria", "ghana", "congo", "drc", "zilizopendwa"
        ];

        Assert.Equal(58, original.Length);

        var ids = Taxa.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in original)
        {
            Assert.Contains(id, ids);
        }
    }

    [Fact]
    public void AfricanParentRelationships_AreUnchanged()
    {
        // The parent graph is what "include parent genres" walks, so it is asserted
        // literally rather than by count.
        (string Id, string[] Parents)[] expected =
        [
            ("afropop", ["afrobeats", "pop"]),
            ("afro-fusion", ["afrobeats"]),
            ("afro-soul", ["soul", "afrobeats"]),
            ("afro-house", ["house", "afrobeats"]),
            ("bongo-flava-pop", ["bongo-flava", "pop"]),
            ("bongo-flava-rnb", ["bongo-flava", "rnb"]),
            ("bongo-flava-rap", ["bongo-flava", "hip-hop"]),
            ("gengetone", ["genge", "hip-hop"]),
            ("kenyan-drill", ["hip-hop"]),
            ("ndombolo", ["soukous", "congolese-rumba"]),
            ("east-african-gospel", ["gospel"]),
            ("swahili-gospel", ["gospel"]),
            ("swahili-pop", ["bongo-flava", "afropop"])
        ];

        foreach (var (id, parents) in expected)
        {
            Assert.Equal(parents, Taxon(id).ParentIds ?? Array.Empty<string>());
        }
    }

    [Fact]
    public void AfricanAliases_AreUnchanged()
    {
        Assert.Equal(["Bongo-Flava", "BongoFlava"], Taxon("bongo-flava").Aliases);
        Assert.Equal(["Congo Rumba", "Rumba Congolaise"], Taxon("congolese-rumba").Aliases);
        Assert.Equal(["Democratic Republic of the Congo", "DRC"], Taxon("drc").Aliases);
        Assert.Equal(["Swahili Oldies"], Taxon("zilizopendwa").Aliases);
        Assert.Equal(["Afro-Pop", "Afro Pop"], Taxon("afropop").Aliases);
    }

    [Theory]
    [InlineData("bongo-flava")]
    [InlineData("amapiano")]
    [InlineData("genge")]
    [InlineData("kwaito")]
    [InlineData("zilizopendwa")]
    [InlineData("swahili-pop")]
    public void ExistingAfricanTerms_AreFiledUnderAfrica(string id)
        => Assert.Equal([PersonalGenreRegions.Africa], RegionsOf(id));

    [Fact]
    public void AfricanTerms_StillResolveAsBefore()
    {
        // Region is metadata, so adding it cannot change what a file resolves to.
        var bongo = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Bongo Flava", PersonalGenreTaxonKind.Genre)]);
        Assert.Equal("Bongo Flava", bongo.PrimaryGenre);

        // Kenyan Drill is a Style under Hip-Hop. It stays a Style, and the parent is
        // still reachable through the existing "include parent genres" setting.
        var drill = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Kenyan Drill", PersonalGenreTaxonKind.Genre)]);
        Assert.Equal(["Kenyan Drill"], drill.Styles);
        Assert.Empty(drill.Genres);

        var withParents = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Kenyan Drill", PersonalGenreTaxonKind.Genre)],
            settings: new PersonalGenreSettings(IncludeParentGenres: true));
        Assert.Equal(["Hip-Hop"], withParents.Genres);
    }

    // =================================================================
    // Every region ships a usable vocabulary
    // =================================================================

    [Fact]
    public void EveryRegionInTheRegistry_IsRepresentedExactlyOnce()
    {
        // Ten regional groups, excluding Global and All.
        Assert.Equal(10, PersonalGenreRegions.All.Count);

        var slugs = PersonalGenreRegions.AllSlugs;
        Assert.Equal(10, slugs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("central-asia", slugs);

        // The orders are strictly increasing, so document order is navigation order.
        var orders = PersonalGenreRegions.All.Select(item => item.Order).ToArray();
        Assert.Equal(orders.OrderBy(value => value).ToArray(), orders);
        Assert.Equal(orders.Length, orders.Distinct().Count());
    }

    [Fact]
    public void CentralAsia_IsOrderedBetweenMenaAndSouthAsia()
    {
        var central = PersonalGenreRegions.Find("central-asia")!;
        Assert.Equal("Central Asia", central.Name);
        Assert.True(central.Order > PersonalGenreRegions.Find("mena")!.Order);
        Assert.True(central.Order < PersonalGenreRegions.Find("south-asia")!.Order);

        // MENA is immediately before Europe, and Europe before Central Asia, so the
        // strip reads Africa, MENA, Europe, Central Asia. MENA leading Europe was an
        // explicit request; it must not be undone by an unrelated reorder.
        var slugs = PersonalGenreRegions.AllSlugs.ToList();
        Assert.True(
            slugs.IndexOf("mena") < slugs.IndexOf("europe"),
            "MENA must come before Europe");
        Assert.Equal("europe", slugs[slugs.IndexOf("europe")]);
        Assert.Equal("central-asia", slugs[slugs.IndexOf("europe") + 1]);
    }

    [Fact]
    public void CentralAsianTerms_ResolveAndCarryOnlyCentralAsia()
    {
        foreach (var id in new[] { "shashmaqom", "kuy" })
        {
            var taxon = Assert.Single(Taxa, item => item.Id == id);
            Assert.Equal(PersonalGenreRegions.CentralAsia, Assert.Single(taxon.Regions!));
            Assert.Equal(PersonalGenreTaxonKind.Genre, taxon.Kind);
            Assert.Empty(taxon.ParentIds ?? Array.Empty<string>());
        }

        // Neither is a duplicate, and the alias reaches the one canonical entry.
        Assert.Equal(2, Taxa.Count(item => (item.Regions ?? Array.Empty<string>()).Contains(
            PersonalGenreRegions.CentralAsia)));
        Assert.DoesNotContain(Taxa, item => item.Id == "dombra-kuy");

        Assert.Equal("kuy", Assert.Single(PersonalGenreResolver
            .Resolve([new GenreTagObservation("Dombra Kuy", PersonalGenreTaxonKind.Genre)])
            .Classifications).TaxonId);
        Assert.Equal("shashmaqom", Assert.Single(PersonalGenreResolver
            .Resolve([new GenreTagObservation("Shashmaqom", PersonalGenreTaxonKind.Genre)])
            .Classifications).TaxonId);
    }

    [Fact]
    public void CulturalPracticesAndInstruments_AreNotPaddedIntoCentralAsia()
    {
        // Akyn, aitys, epic storytelling and instrument names are deliberately not
        // genres. Whether any belongs under Context, Scene or Style is a separate
        // semantic decision, so none was added to fill the region out.
        foreach (var label in new[] { "akyn", "aitys", "dombra", "epic", "storytelling" })
        {
            Assert.DoesNotContain(Taxa, item =>
                string.Equals(item.Id, label, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, label, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void EveryBuiltInRegion_ContainsAtLeastOneTerm()
    {
        foreach (var region in PersonalGenreRegions.All)
        {
            Assert.Contains(Taxa, item => (item.Regions ?? Array.Empty<string>()).Contains(region.Slug));
        }
    }

    [Fact]
    public void RegionSlugs_AreUniqueAndOrdered()
    {
        Assert.Equal(PersonalGenreRegions.AllSlugs.Count, PersonalGenreRegions.AllSlugs.Distinct().Count());

        var orders = PersonalGenreRegions.All.Select(item => item.Order).ToArray();
        Assert.Equal(orders.OrderBy(value => value).ToArray(), orders);
        Assert.All(PersonalGenreRegions.All, region => Assert.False(string.IsNullOrWhiteSpace(region.Name)));
    }

    [Theory]
    // North America
    [InlineData("Bluegrass", "bluegrass")]
    [InlineData("Zydeco", "zydeco")]
    [InlineData("Tejano", "tejano")]
    // Latin America & Caribbean
    [InlineData("Reggaeton", "reggaeton")]
    [InlineData("Samba", "samba")]
    [InlineData("Bossa Nova", "bossa-nova")]
    [InlineData("MPB", "mpb")]
    // Europe
    [InlineData("Fado", "fado")]
    [InlineData("Flamenco", "flamenco")]
    [InlineData("UK Garage", "uk-garage")]
    [InlineData("Drum and Bass", "drum-and-bass")]
    // MENA
    [InlineData("Raï", "rai")]
    [InlineData("Gnawa", "gnawa")]
    [InlineData("Khaliji", "khaliji")]
    // South Asia
    [InlineData("Hindustani", "hindustani")]
    [InlineData("Bhangra", "bhangra")]
    [InlineData("Qawwali", "qawwali")]
    // East Asia
    [InlineData("K-Pop", "k-pop")]
    [InlineData("J-Pop", "j-pop")]
    [InlineData("Enka", "enka")]
    [InlineData("Pansori", "pansori")]
    // Southeast Asia
    [InlineData("Dangdut", "dangdut")]
    [InlineData("Gamelan", "gamelan")]
    [InlineData("Mor Lam", "mor-lam")]
    // Oceania / Pacific
    [InlineData("Jawaiian", "jawaiian")]
    [InlineData("Aboriginal Rock", "aboriginal-rock")]
    [InlineData("Australian Country", "australian-country")]
    public void RegionalBuiltIn_ResolvesFromItsCanonicalSpelling(string spelling, string id)
    {
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation(spelling, PersonalGenreTaxonKind.Genre)]);

        var classified = Assert.Single(resolution.Classifications);
        Assert.Equal(id, classified.TaxonId);
    }

    [Fact]
    public void CrossRegionTerm_Rai_BelongsToBothAfricaAndMena()
        => Assert.Equal(
            new[] { PersonalGenreRegions.Africa, PersonalGenreRegions.Mena }.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            RegionsOf("rai").OrderBy(x => x, StringComparer.Ordinal).ToArray());

    [Fact]
    public void CrossRegionTerm_Gnawa_BelongsToBothAfricaAndMena()
        => Assert.Equal(
            new[] { PersonalGenreRegions.Africa, PersonalGenreRegions.Mena }.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            RegionsOf("gnawa").OrderBy(x => x, StringComparer.Ordinal).ToArray());

    [Fact]
    public void CrossRegionTerm_Tejano_BelongsToNorthAmericaAndLatinAmerica()
        => Assert.Equal(
            new[] { PersonalGenreRegions.NorthAmerica, PersonalGenreRegions.LatinAmericaCaribbean }.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            RegionsOf("tejano").OrderBy(x => x, StringComparer.Ordinal).ToArray());

    [Fact]
    public void BollywoodAndOpm_StayContextRatherThanBecomingGenres()
    {
        // Both are industry or entertainment contexts spanning many musical forms,
        // so neither may be classified as a canonical Genre.
        Assert.Equal(PersonalGenreTaxonKind.Context, Taxon("bollywood").Kind);
        Assert.True(Taxon("bollywood").ContextOnly);
        Assert.Equal(PersonalGenreTaxonKind.Context, Taxon("opm").Kind);
        Assert.True(Taxon("opm").ContextOnly);

        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Bollywood", PersonalGenreTaxonKind.Genre)]);
        Assert.Null(resolution.PrimaryGenre);
        Assert.Contains("Bollywood", resolution.Contexts);
    }

    [Fact]
    public void RegionalVocabulary_IsNotGatedByGeography()
    {
        // The central claim of the feature, asserted directly: a genre filed under
        // one region resolves from a file that says nothing about where it came from.
        foreach (var (value, expected) in new[]
                 {
                     ("K-Pop", "k-pop"), ("Fado", "fado"), ("Bhangra", "bhangra"),
                     ("Reggaeton", "reggaeton"), ("Samba", "samba"), ("Gnawa", "gnawa")
                 })
        {
            var resolution = PersonalGenreResolver.Resolve(
                [new GenreTagObservation(value, PersonalGenreTaxonKind.Genre)]);
            Assert.Equal(expected, Assert.Single(resolution.Classifications).TaxonId);
        }

        // A universal genre resolves unchanged, and no region contributes to that.
        var rock = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Rock", PersonalGenreTaxonKind.Genre)]);
        Assert.Equal("Rock", rock.PrimaryGenre);
    }

    [Fact]
    public void EveryRegionSlug_UsedByATaxon_IsRecognised()
    {
        foreach (var taxon in Taxa)
        {
            foreach (var slug in taxon.Regions ?? Array.Empty<string>())
            {
                Assert.True(PersonalGenreRegions.IsKnown(slug), $"Unknown region '{slug}' on '{taxon.Id}'");
            }
        }
    }

    // =================================================================
    // Global / universal vocabulary
    // =================================================================

    [Theory]
    [InlineData("hip-hop")]
    [InlineData("rnb")]
    [InlineData("reggae")]
    [InlineData("dancehall")]
    [InlineData("gospel")]
    [InlineData("electronic")]
    [InlineData("house")]
    [InlineData("pop")]
    [InlineData("soul")]
    [InlineData("rock")]
    [InlineData("jazz")]
    [InlineData("classical")]
    // Added to the universal vocabulary.
    [InlineData("blues")]
    [InlineData("funk")]
    [InlineData("disco")]
    [InlineData("metal")]
    [InlineData("punk")]
    [InlineData("folk")]
    [InlineData("country")]
    [InlineData("techno")]
    public void UniversalGenre_IsUnscopedAndTopLevel(string id)
    {
        var taxon = Taxon(id);
        Assert.Equal(PersonalGenreTaxonKind.Genre, taxon.Kind);
        Assert.Empty(taxon.Regions ?? Array.Empty<string>());
        Assert.True(taxon.ContextOnly == false);
    }

    [Fact]
    public void UniversalGenre_ResolvesWithoutAnyRegionMetadata()
    {
        foreach (var value in new[] { "Hip-Hop", "Rock", "Jazz", "Funk", "Metal", "Techno" })
        {
            var resolution = PersonalGenreResolver.Resolve(
                [new GenreTagObservation(value, PersonalGenreTaxonKind.Genre)]);
            Assert.Equal(value, resolution.PrimaryGenre);
        }
    }

    /// <summary>
    /// Regions must not become a back door to changing how Rap is classified.
    /// </summary>
    /// <remarks>
    /// This test previously asserted that Rap produced no primary genre, because the
    /// 156-term taxonomy had no Rap term. The researched master carries Rap as a
    /// Genre of its own, so it now resolves; what the test protects is unchanged,
    /// which is that no taxonomy entry treats Rap as a spelling of Hip-Hop.
    /// </remarks>
    [Fact]
    public void RapIsStillNotSilentlyMergedIntoHipHop()
    {
        Assert.DoesNotContain(Taxa, item =>
            (item.Aliases ?? Array.Empty<string>()).Any(alias =>
                string.Equals(alias, "Rap", StringComparison.OrdinalIgnoreCase)));

        // "Rap" is the researched master's own term, so it resolves to itself rather
        // than to Hip-Hop.
        Assert.True(new PersonalGenreCatalog().TryMatch("Rap", out var rap, out _));
        Assert.NotEqual("hip-hop", rap.Id);

        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Rap", PersonalGenreTaxonKind.Genre)]);
        Assert.Equal("rap", resolution.PrimaryGenre);
        Assert.Equal(["rap"], resolution.Genres);
    }

    // =================================================================
    // The resolver stays region-blind
    // =================================================================

    [Fact]
    public void Resolver_HasNoRegionInputAtAll()
    {
        var resolve = typeof(PersonalGenreResolver)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(PersonalGenreResolver.Resolve));

        Assert.DoesNotContain(
            resolve.GetParameters(),
            parameter =>
                parameter.Name!.Contains("region", StringComparison.OrdinalIgnoreCase) ||
                parameter.ParameterType.Name.Contains("Region", StringComparison.Ordinal));

        // The taxon record itself carries regions, so the resolver necessarily sees
        // them as data. What matters is that it cannot act on them: resolution of a
        // term is identical no matter which regions the catalog happens to hold.
        var withRegions = new[] { new PersonalGenreTaxon("k-pop", "K-Pop", PersonalGenreTaxonKind.Genre, Regions: [PersonalGenreRegions.EastAsia]) };
        var withoutRegions = new[] { new PersonalGenreTaxon("k-pop", "K-Pop", PersonalGenreTaxonKind.Genre) };

        var a = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("K-Pop", PersonalGenreTaxonKind.Genre)], customTaxa: withRegions);
        var b = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("K-Pop", PersonalGenreTaxonKind.Genre)], customTaxa: withoutRegions);

        Assert.Equal(a.PrimaryGenre, b.PrimaryGenre);
        Assert.Equal(a.Genres, b.Genres);
        Assert.Equal(a.ResolverVersion, b.ResolverVersion);
    }

    [Fact]
    public void AutoTagKeepsExactlyOneGenreIntelligenceStage()
    {
        // Region must not have become a per-region runtime switch. The per-profile
        // runtime options are the only Genre Intelligence settings AutoTag carries.
        var settings = typeof(DeezSpoTag.Core.Models.Settings.AutoTagGenreIntelligenceSettings);
        Assert.DoesNotContain(
            settings.GetProperties(),
            property => property.Name.Contains("Region", StringComparison.OrdinalIgnoreCase));

        var resolverVersion = typeof(PersonalGenreResolver)
            .GetField("Version", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null) as string;
        Assert.False(string.IsNullOrWhiteSpace(resolverVersion));
    }

    // =================================================================
    // Custom taxa and stored user data
    // =================================================================

    [Fact]
    public async Task CustomTaxon_CanBeGlobalOneRegionOrMany()
    {
        var store = CreateStore();

        await store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("my-global", "My Global", PersonalGenreTaxonKind.Genre));
        await store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("my-european", "My European", PersonalGenreTaxonKind.Genre,
                Regions: [PersonalGenreRegions.Europe]));
        await store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("my-shared", "My Shared", PersonalGenreTaxonKind.Genre,
                Regions: [PersonalGenreRegions.SouthAsia, PersonalGenreRegions.EastAsia]));

        var stored = (await store.GetCustomTaxaAsync()).ToDictionary(item => item.Id, StringComparer.Ordinal);

        Assert.Null(stored["my-global"].Regions);
        Assert.Equal([PersonalGenreRegions.Europe], stored["my-european"].Regions);
        Assert.Equal(2, stored["my-shared"].Regions!.Count);

        // Region order is normalized to the built-in order on the way in.
        Assert.Equal(
            [PersonalGenreRegions.SouthAsia, PersonalGenreRegions.EastAsia],
            stored["my-shared"].Regions);
    }

    [Fact]
    public async Task UpdatingRegions_ChangesNothingElseAboutTheTaxon()
    {
        var store = CreateStore();
        var created = await store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("my-mix", "My Mix", PersonalGenreTaxonKind.Style,
                ParentIds: ["hip-hop"], Aliases: ["My Mixxx"]));

        await store.UpsertMappingAsync(
            new PersonalGenreMapping(0, "My Mixxx", "my-mix"));
        await store.UpsertRuleAsync(new PersonalGenreRule(0, "Mix It Up", "my-mix"));

        var moved = await store.UpsertCustomTaxonAsync(created with
        {
            Regions = [PersonalGenreRegions.NorthAmerica]
        });

        Assert.Equal(created.Id, moved.Id);
        Assert.Equal(created.Name, moved.Name);
        Assert.Equal(created.Kind, moved.Kind);
        Assert.Equal(created.ParentIds, moved.ParentIds);
        Assert.Equal(created.Aliases, moved.Aliases);
        Assert.Equal([PersonalGenreRegions.NorthAmerica], moved.Regions);

        // The user's mappings and rules still point at a taxon that exists.
        Assert.Equal("my-mix", Assert.Single(await store.GetMappingsAsync()).TargetTaxonId);
        Assert.Equal("my-mix", Assert.Single(await store.GetRulesAsync()).TargetTaxonId);
    }

    [Fact]
    public async Task ExistingCustomTaxon_WithoutRegionsColumn_LoadsAsUnscoped()
    {
        // A database written before regions existed has no regions_json at all. The
        // additive migration must let it load rather than failing or hiding it.
        var path = Path.Combine(_directory, "legacy.db");
        var store = CreateStore("legacy.db");
        await store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("legacy-term", "Legacy Term", PersonalGenreTaxonKind.Genre));
        await store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("scoped-term", "Scoped Term", PersonalGenreTaxonKind.Genre,
                Regions: [PersonalGenreRegions.Europe]));

        // Rewrite the table into its pre-region shape and drop the scoped row's
        // metadata, exactly as an older installation would present it.
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
        {
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE personal_genre_taxon;";
            await drop.ExecuteNonQueryAsync();

            await using var legacy = connection.CreateCommand();
            legacy.CommandText = """
                CREATE TABLE personal_genre_taxon (
                    id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    parent_ids_json TEXT NOT NULL DEFAULT '[]',
                    context_only INTEGER NOT NULL DEFAULT 0,
                    aliases_json TEXT NOT NULL DEFAULT '[]',
                    created_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    updated_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
                INSERT INTO personal_genre_taxon (id, name, kind, parent_ids_json, context_only, aliases_json)
                    VALUES ('legacy-term', 'Legacy Term', 'genre', '[]', 0, '[]');
                """;
            await legacy.ExecuteNonQueryAsync();
        }

        var reopened = CreateStore("legacy.db");
        var taxa = await reopened.GetCustomTaxaAsync();

        // No row disappeared, and the pre-region row reads as unscoped.
        Assert.Equal("legacy-term", Assert.Single(taxa).Id);
        Assert.Null(taxa[0].Regions);
    }

    [Fact]
    public async Task RegionsJson_IsCreatedAdditively_AndExistingRowsSurvive()
    {
        var path = Path.Combine(_directory, "additive.db");
        var store = CreateStore("additive.db");
        await store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("keep-me", "Keep Me", PersonalGenreTaxonKind.Genre,
                Regions: [PersonalGenreRegions.Europe]));

        // Simulate a pre-region database by removing only the new column.
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
        {
            await connection.OpenAsync();
            await using (var rebuild = connection.CreateCommand())
            {
                rebuild.CommandText = """
                    CREATE TABLE rebuilt_taxon (
                        id TEXT NOT NULL PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL,
                        parent_ids_json TEXT NOT NULL DEFAULT '[]',
                        context_only INTEGER NOT NULL DEFAULT 0,
                        aliases_json TEXT NOT NULL DEFAULT '[]',
                        created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL);
                    INSERT INTO rebuilt_taxon SELECT id, name, kind, parent_ids_json, context_only,
                        aliases_json, created_at_utc, updated_at_utc FROM personal_genre_taxon;
                    DROP TABLE personal_genre_taxon;
                    ALTER TABLE rebuilt_taxon RENAME TO personal_genre_taxon;
                    """;
                await rebuild.ExecuteNonQueryAsync();
            }
        }

        // A fresh store runs the additive migration on first use.
        var migrated = CreateStore("additive.db");
        var reloaded = await migrated.GetCustomTaxaAsync();

        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
        {
            await connection.OpenAsync();
            await using var columns = connection.CreateCommand();
            columns.CommandText = "PRAGMA table_info(personal_genre_taxon);";
            await using var reader = await columns.ExecuteReaderAsync();
            var names = new List<string>();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(1));
            }

            Assert.Contains("regions_json", names);
        }

        // The row survived the column addition and can be given a region afterwards.
        var term = Assert.Single(reloaded);
        Assert.Equal("keep-me", term.Id);
        Assert.Null(term.Regions);

        await migrated.UpsertCustomTaxonAsync(term with { Regions = [PersonalGenreRegions.EastAsia] });
        Assert.Equal([PersonalGenreRegions.EastAsia], Assert.Single(await migrated.GetCustomTaxaAsync()).Regions);
    }

    [Fact]
    public async Task EmptyAndNullRegions_AreStoredAsUnscoped()
    {
        var store = CreateStore();

        await store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("empty-regions", "Empty Regions", PersonalGenreTaxonKind.Genre,
                Regions: Array.Empty<string>()));
        await store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("blank-regions", "Blank Regions", PersonalGenreTaxonKind.Genre,
                Regions: ["  ", ""]));
        await store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("dup-regions", "Dup Regions", PersonalGenreTaxonKind.Genre,
                Regions: [PersonalGenreRegions.Europe, PersonalGenreRegions.Europe, " europe "]));

        var stored = (await store.GetCustomTaxaAsync()).ToDictionary(item => item.Id, StringComparer.Ordinal);

        Assert.Null(stored["empty-regions"].Regions);
        Assert.Null(stored["blank-regions"].Regions);
        Assert.Equal([PersonalGenreRegions.Europe], stored["dup-regions"].Regions);
    }

    // =================================================================
    // Import and export
    // =================================================================

    [Fact]
    public async Task Export_DeclaresSchemaVersionThree_AndCarriesRegions()
    {
        var store = CreateStore();
        var service = CreateService(store);

        await store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("reggae-adjacent", "Reggae Adjacent", PersonalGenreTaxonKind.Style,
                ParentIds: ["reggae"], Regions: [PersonalGenreRegions.OceaniaPacific]));

        var exported = await service.ExportConfigurationAsync();

        Assert.Equal(3, exported.SchemaVersion);
        Assert.Equal(PersonalGenreTaxonomy.Version, exported.TaxonomyVersion);
        Assert.Equal([PersonalGenreRegions.OceaniaPacific], Assert.Single(exported.CustomTaxa).Regions);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Import_AcceptsEverySupportedSchemaVersion(int schemaVersion)
    {
        var store = CreateStore();
        var service = CreateService(store);

        var configuration = new PersonalGenreConfiguration(
            schemaVersion,
            PersonalGenreTaxonomy.Version,
            DateTimeOffset.UtcNow,
            new PersonalGenreSettings(),
            [new PersonalGenreTaxon("imported-" + schemaVersion, "Imported", PersonalGenreTaxonKind.Genre)],
            [],
            []);

        var result = await service.ImportConfigurationAsync(configuration);

        Assert.Equal(1, result.CustomTaxaImported);
        Assert.Single(await store.GetCustomTaxaAsync());
    }

    [Fact]
    public async Task Import_RejectsAFutureSchemaVersion()
    {
        var service = CreateService(CreateStore());
        var configuration = new PersonalGenreConfiguration(
            99, PersonalGenreTaxonomy.Version, DateTimeOffset.UtcNow,
            new PersonalGenreSettings(), [], [], []);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ImportConfigurationAsync(configuration));
    }

    [Fact]
    public async Task OldExportWithNoRegions_ImportsAsUnscoped()
    {
        var store = CreateStore();
        var service = CreateService(store);

        // A version 2 payload as an installation from before this change would hold.
        var legacy = new PersonalGenreConfiguration(
            2,
            PersonalGenreTaxonomy.Version,
            DateTimeOffset.UtcNow,
            new PersonalGenreSettings(),
            [new PersonalGenreTaxon("legacy-custom", "Legacy Custom", PersonalGenreTaxonKind.Style)],
            [],
            []);

        await service.ImportConfigurationAsync(legacy);

        var stored = Assert.Single(await store.GetCustomTaxaAsync());
        Assert.Equal("legacy-custom", stored.Id);
        Assert.Null(stored.Regions);
    }

    [Fact]
    public async Task ExportThenImport_PreservesRegions()
    {
        var store = CreateStore();
        var service = CreateService(store);

        await store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("round-trip", "Round Trip", PersonalGenreTaxonKind.Genre,
                Regions: [PersonalGenreRegions.Mena, PersonalGenreRegions.Africa]));
        await store.UpsertMappingAsync(new PersonalGenreMapping(0, "Round Trip", "round-trip"));
        await store.UpsertRuleAsync(new PersonalGenreRule(0, "Trip Round", "round-trip"));

        var exported = await service.ExportConfigurationAsync();

        // Import into a clean database.
        var second = CreateStore("second.db");
        var secondService = CreateService(second);
        var result = await secondService.ImportConfigurationAsync(exported);

        Assert.Equal(1, result.CustomTaxaImported);
        Assert.Equal(1, result.MappingsImported);
        Assert.Equal(1, result.RulesImported);

        var stored = Assert.Single(await second.GetCustomTaxaAsync());
        Assert.Equal([PersonalGenreRegions.Africa, PersonalGenreRegions.Mena], stored.Regions);
    }

    [Fact]
    public async Task TaxonomyEndpoint_ExposesRegionsAndTheRegistry()
    {
        var store = CreateStore();
        var service = CreateService(store);
        await store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("api-visible", "Api Visible", PersonalGenreTaxonKind.Genre,
                Regions: [PersonalGenreRegions.EastAsia]));

        var controller = new DeezSpoTag.Web.Controllers.Api.PersonalGenreApiController(service);
        var result = await controller.GetTaxonomy(CancellationToken.None);
        var payload = System.Text.Json.JsonSerializer.Serialize(
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result).Value);

        // The registry travels with the taxonomy, so the client can build its
        // navigation without a second copy of the region names.
        foreach (var region in PersonalGenreRegions.All)
        {
            Assert.Contains(region.Slug, payload, StringComparison.Ordinal);
        }

        // Per-taxon regions are exposed, and an unscoped term says nothing.
        Assert.Contains("api-visible", payload, StringComparison.Ordinal);
        Assert.Contains(PersonalGenreRegions.EastAsia, payload, StringComparison.Ordinal);

        // The pre-existing response shape is unchanged, so existing consumers that
        // ignore these fields keep working.
        Assert.Contains("\"version\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"input\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"taxa\"", payload, StringComparison.Ordinal);
        Assert.Contains("audio-file-tags", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocksAndTrackHistory_AreUnaffectedByRegions()
    {
        var store = CreateStore();

        // The history tables reference track(id), and foreign keys are enforced, so
        // the track the result hangs off has to exist.
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            "Data Source=" + Path.Combine(_directory, "library.db")))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS track (id INTEGER PRIMARY KEY);
                INSERT OR IGNORE INTO track (id) VALUES (1);
                CREATE TABLE IF NOT EXISTS audio_file (id INTEGER PRIMARY KEY, path TEXT);
                CREATE TABLE IF NOT EXISTS track_local (id INTEGER PRIMARY KEY, track_id INTEGER, audio_file_id INTEGER);
                """;
            await create.ExecuteNonQueryAsync();
        }

        var observations = new[]
        {
            new GenreTagObservation("K-Pop", PersonalGenreTaxonKind.Genre, 0),
            new GenreTagObservation("Hip-Hop", PersonalGenreTaxonKind.Genre, 1)
        };

        var resolution = PersonalGenreResolver.Resolve(observations);
        await store.SaveTrackResultAsync(new PersonalGenreTrackResult(1, resolution, DateTimeOffset.UtcNow));

        // K-Pop is filed under East Asia, yet a stored classification keeps reporting
        // exactly what it reported, and history is still written.
        var loaded = await store.GetTrackResultAsync(1);
        Assert.NotNull(loaded);
        Assert.Equal(resolution.PrimaryGenre, loaded!.Resolution.PrimaryGenre);
        Assert.Equal(resolution.Genres, loaded.Resolution.Genres);

        var history = await store.GetTrackHistoryAsync(1);
        Assert.NotEmpty(history);
        Assert.Contains(history, item => item.PrimaryGenre == resolution.PrimaryGenre);
    }
}
