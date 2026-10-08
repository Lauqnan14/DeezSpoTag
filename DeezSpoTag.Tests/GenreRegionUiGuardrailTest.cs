using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DeezSpoTag.Services.Genre;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the regional presentation of the Genre Intelligence vocabulary.
///
/// The markup is asserted rather than exercised through a browser, which is how the
/// existing Genre Intelligence guardrails work: the partial is parsed as XML and the
/// client script is executed under Node with a stubbed DOM. Two properties matter
/// most. The regional navigation has to look and behave like the rest of DeezSpoTag,
/// and the decorative map must never become something the feature depends on.
/// </summary>
public sealed class GenreRegionUiGuardrailTest
{
    [Fact]
    public void GenreIntelligence_StillHasExactlySevenTopLevelCards()
    {
        var root = LoadGenreMarkup();

        var cards = root.Elements("article").ToList();
        Assert.Equal(7, cards.Count);

        // The structure the existing AutoTag-card idiom requires is unchanged.
        Assert.All(cards, card =>
        {
            Assert.True(HasClass(card, "card"));
            Assert.Contains(card.Elements("div"), node => HasClass(node, "card-header"));
            var body = Assert.Single(card.Elements("div"), node => HasClass(node, "card-body"));
            Assert.NotEmpty(body.Descendants().Where(node => HasClass(node, "download-section")));
        });

        // Regions were added inside Genre vocabulary, not as an eighth card.
        Assert.Contains(cards, card =>
            card.Descendants().Any(node => node.Attribute("id")?.Value == "pgTaxonomyBody"));
    }

    [Fact]
    public void RegionTabs_LiveInsideTheGenreVocabularyCard()
    {
        var root = LoadGenreMarkup();

        var vocabulary = root.Elements("article")
            .Single(card => card.Descendants().Any(node => node.Attribute("id")?.Value == "pgTaxonomyBody"));

        var tabs = vocabulary.Descendants()
            .Single(node => node.Attribute("id")?.Value == "genreIntelligenceRegionTabs");

        Assert.Equal("ul", tabs.Name.LocalName);
        Assert.Contains("nav-tabs", Classes(tabs));
        Assert.Contains("ds-mobile-tabs", Classes(tabs));
        Assert.Equal("tablist", tabs.Attribute("role")?.Value);

        // Global and All are views rather than stored regions, so they are literal.
        Assert.Contains(vocabulary.Descendants("button"),
            button => button.Attribute("data-pg-region")?.Value == "global");
        Assert.Contains(vocabulary.Descendants("button"),
            button => button.Attribute("data-pg-region")?.Value == "all");

        // The nine region triggers and their panes are rendered server-side from the
        // one registry, so a region added later needs no markup change.
        var markup = File.ReadAllText(Path.Combine(
            ResolveRoot(), "DeezSpoTag.Web/Views/Shared/_GenreIntelligence.cshtml"));
        Assert.Contains("@foreach (var region in PersonalGenreRegions.All)", markup, StringComparison.Ordinal);

        // The tab list, the pane list and the custom-taxon picker are all generated
        // from the registry: three loops, no hard-coded region anywhere.
        Assert.Equal(3, Regex.Matches(markup, @"foreach \(var region in PersonalGenreRegions\.All\)").Count);

        // The pane pattern is the one the triggers target, so the ids line up.
        Assert.Contains("id=\"pgRegionPanel-@region.Slug\"", markup, StringComparison.Ordinal);
        Assert.Contains("data-bs-target=\"#pgRegionPanel-@region.Slug\"", markup, StringComparison.Ordinal);
        Assert.Contains("data-pg-region=\"@region.Slug\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void RegionNames_AreNotHardcodedAnywhereInTheViewOrClient()
    {
        // The registry is the single authority. If a region name were written out in
        // the markup or the script, adding a region on the server would leave the
        // client showing a stale label.
        var markup = File.ReadAllText(Path.Combine(
            ResolveRoot(), "DeezSpoTag.Web/Views/Shared/_GenreIntelligence.cshtml"));
        var script = File.ReadAllText(Path.Combine(
            ResolveRoot(), "DeezSpoTag.Web/wwwroot/js/personal-genre.js"));

        foreach (var region in DeezSpoTag.Services.Genre.PersonalGenreRegions.All)
        {
            // "Latin America & Caribbean" is the one label the registry supplies; it
            // must not be repeated by hand in either file.
            if (region.Name.Contains('&', StringComparison.Ordinal))
            {
                Assert.DoesNotContain(region.Name, markup, StringComparison.Ordinal);
            }

            Assert.DoesNotContain($"'{region.Slug}'", script, StringComparison.Ordinal);
            Assert.DoesNotContain($"\"{region.Slug}\"", script, StringComparison.Ordinal);
        }

        // The client reads the vocabulary from the taxonomy response instead.
        Assert.Contains("taxonomy && taxonomy.regions", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RegionTabs_FollowTheEstablishedBootstrapTabMarkup()
    {
        var root = LoadGenreMarkup();
        var triggers = root.Descendants("button")
            .Where(button => button.Attribute("data-pg-region") is not null)
            .ToList();

        Assert.NotEmpty(triggers);
        Assert.All(triggers, button =>
        {
            Assert.Equal("tab", button.Attribute("role")?.Value);
            Assert.Equal("button", button.Attribute("type")?.Value);
            Assert.Contains("nav-link", Classes(button));
            Assert.False(string.IsNullOrWhiteSpace(button.Attribute("data-bs-toggle")?.Value));
            Assert.False(string.IsNullOrWhiteSpace(button.Attribute("data-bs-target")?.Value));
            Assert.False(string.IsNullOrWhiteSpace(button.Attribute("aria-controls")?.Value));
        });

        // Global is the only pre-selected view, so a first-time visit lands there.
        var selected = triggers.Where(button => button.Attribute("aria-selected")?.Value == "true").ToList();
        Assert.Equal("global", Assert.Single(selected).Attribute("data-pg-region")?.Value);

        // Each trigger points at a pane that exists, and is labelled by it.
        foreach (var button in triggers)
        {
            var target = button.Attribute("data-bs-target")!.Value.TrimStart('#');
            var pane = root.Descendants().Single(node => node.Attribute("id")?.Value == target);
            Assert.True(HasClass(pane, "tab-pane"));
            Assert.Equal("tabpanel", pane.Attribute("role")?.Value);
            Assert.Equal(button.Attribute("id")?.Value, pane.Attribute("aria-labelledby")?.Value);
        }
    }

    [Fact]
    public void RegionTabs_AreOrderedGlobalThenRegionsThenAll()
    {
        // The registry is what actually decides the order of the region tabs, so it is
        // asserted literally rather than inferred from the rendered strip.
        Assert.Equal(
            new[]
            {
                "north-america", "latin-america-caribbean", "africa", "mena", "europe",
                "central-asia", "south-asia", "east-asia", "southeast-asia", "oceania-pacific"
            },
            DeezSpoTag.Services.Genre.PersonalGenreRegions.AllSlugs.ToArray());

        // MENA precedes Europe, so the two regions that share North Africa sit
        // together: Africa, MENA, Europe.
        Assert.True(
            DeezSpoTag.Services.Genre.PersonalGenreRegions.Find("mena")!.Order
            < DeezSpoTag.Services.Genre.PersonalGenreRegions.Find("europe")!.Order);

        // Ten regional groups, excluding Global and All.
        Assert.Equal(10, DeezSpoTag.Services.Genre.PersonalGenreRegions.All.Count);

        // Central Asia is a region in its own right, ordered between MENA and South
        // Asia. It was a genuine omission from the original specification rather than
        // a judgement that those countries belong to South Asia.
        Assert.Equal("central-asia", DeezSpoTag.Services.Genre.PersonalGenreRegions.CentralAsia);
        Assert.Equal(
            1,
            DeezSpoTag.Services.Genre.PersonalGenreRegions.All.Count(
                region => region.Slug == "central-asia"));
        Assert.Equal("Central Asia", DeezSpoTag.Services.Genre.PersonalGenreRegions.Find("central-asia")!.Name);

        var centralAsia = DeezSpoTag.Services.Genre.PersonalGenreRegions.Find("central-asia")!.Order;
        Assert.True(centralAsia > DeezSpoTag.Services.Genre.PersonalGenreRegions.Find("mena")!.Order);
        Assert.True(centralAsia < DeezSpoTag.Services.Genre.PersonalGenreRegions.Find("south-asia")!.Order);

        // Global opens the strip because it is the first-run view, and All closes it
        // because it is a browsing utility rather than a region. Both are literal
        // markup, so their placement is checked directly: the region loop sits
        // between them.
        var root = LoadGenreMarkup();
        var tabs = root.Descendants()
            .Single(node => node.Attribute("id")?.Value == "genreIntelligenceRegionTabs");
        var items = tabs.Elements("li").ToList();

        Assert.Equal(
            "global",
            items.First().Descendants("button").Single().Attribute("data-pg-region")?.Value);
        Assert.Equal(
            "all",
            items.Last().Descendants("button").Single().Attribute("data-pg-region")?.Value);

        // Exactly three entries: Global, the generated regions, then All.
        Assert.Equal(3, items.Count);
        var generated = items[1].ToString();
        Assert.Contains("@region.Slug", generated, StringComparison.Ordinal);
        Assert.Contains("@region.Name", generated, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("KZ")]
    [InlineData("KG")]
    [InlineData("TJ")]
    [InlineData("TM")]
    [InlineData("UZ")]
    public void CentralAsianStates_MapToCentralAsiaAndNotSouthAsia(string country)
    {
        var membership = ReadCountryRegions();

        Assert.Equal(new[] { "central-asia" }, membership[country]);

        // The point of adding the region: these five are no longer filed under a
        // neighbouring tab just because Central Asia was missing.
        Assert.DoesNotContain("south-asia", membership[country]);
    }

    [Fact]
    public void CentralAsia_IsHighlightableByItsOwnRegionSlug()
    {
        var css = File.ReadAllText(Path.Combine(ResolveRoot(), "DeezSpoTag.Web/wwwroot/css/autotag.css"));
        Assert.Contains(
            "#pgRegionMap[data-active-region=\"central-asia\"] svg path[data-regions~=\"central-asia\"]",
            css,
            StringComparison.Ordinal);

        // And the tab and pane are generated from the registry like every other region.
        var root = LoadGenreMarkup();
        var markup = File.ReadAllText(Path.Combine(
            ResolveRoot(), "DeezSpoTag.Web/Views/Shared/_GenreIntelligence.cshtml"));
        Assert.Contains("foreach (var region in PersonalGenreRegions.All)", markup, StringComparison.Ordinal);
        Assert.True(DeezSpoTag.Services.Genre.PersonalGenreRegions.IsKnown("central-asia"));
        Assert.NotNull(root.Descendants()
            .SingleOrDefault(node => node.Attribute("id")?.Value == "genreIntelligenceRegionTabs"));
    }

    [Fact]
    public void CentralAsianVocabulary_IsPresentAndNotDuplicated()
    {
        var taxa = DeezSpoTag.Services.Genre.PersonalGenreTaxonomy.GetDefaultTaxa();

        foreach (var id in new[] { "shashmaqom", "kuy" })
        {
            var taxon = Assert.Single(taxa, item => item.Id == id);
            Assert.Equal([DeezSpoTag.Services.Genre.PersonalGenreRegions.CentralAsia], taxon.Regions);
            Assert.Equal(PersonalGenreTaxonKind.Genre, taxon.Kind);

            // One canonical identity: the spelling is an alias, not a second entry.
            Assert.Empty(taxon.ParentIds ?? Array.Empty<string>());
        }

        // Dombra Kuy is reachable, but it is an alias of Kuy rather than its own term.
        var kuy = taxa.Single(item => item.Id == "kuy");
        Assert.Contains("Dombra Kuy", kuy.Aliases ?? Array.Empty<string>());
        Assert.DoesNotContain(taxa, item => item.Id == "dombra-kuy");

        // Both resolve from their canonical spelling, and the alias resolves too.
        Assert.Equal("shashmaqom", Assert.Single(
            DeezSpoTag.Services.Genre.PersonalGenreResolver
                .Resolve([new GenreTagObservation("Shashmaqom", PersonalGenreTaxonKind.Genre)])
                .Classifications).TaxonId);
        Assert.Equal("kuy", Assert.Single(
            DeezSpoTag.Services.Genre.PersonalGenreResolver
                .Resolve([new GenreTagObservation("Dombra Kuy", PersonalGenreTaxonKind.Genre)])
                .Classifications).TaxonId);
    }

    [Fact]
    public void CentralAsia_IsMetadataOnlyAndCannotAffectResolution()
    {
        // Selecting the region is UI state. The resolver has no region parameter at
        // all, so no Central Asia code path exists to affect a classification.
        var resolve = typeof(DeezSpoTag.Services.Genre.PersonalGenreResolver)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Single(method => method.Name == "Resolve");
        Assert.DoesNotContain(resolve.GetParameters(), parameter =>
            parameter.Name!.Contains("region", StringComparison.OrdinalIgnoreCase));

        // A Kazakh track still resolves to ordinary universal genres.
        foreach (var id in new[] { "rock", "pop", "hip-hop", "jazz" })
        {
            var resolution = DeezSpoTag.Services.Genre.PersonalGenreResolver.Resolve(
                [new GenreTagObservation(
                    DeezSpoTag.Services.Genre.PersonalGenreTaxonomy.GetDefaultTaxa()
                        .Single(item => item.Id == id).Name,
                    PersonalGenreTaxonKind.Genre)]);
            Assert.Equal(id, Assert.Single(resolution.Classifications).TaxonId);
        }

        // And a Central Asian term resolves with no geography involved whatsoever.
        var kuy = DeezSpoTag.Services.Genre.PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Kuy", PersonalGenreTaxonKind.Genre)]);
        Assert.Equal("Kuy", kuy.PrimaryGenre);
        Assert.Empty(kuy.Contexts);
    }

    [Fact]
    public void CustomTaxaCanBeFiledUnderCentralAsia()
    {
        var store = new PersonalGenreStore(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] =
                    "Data Source=" + Path.Combine(Path.GetTempPath(), "central-asia-" + Guid.NewGuid() + ".db")
            }).Build());

        var saved = store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("my-central-asian", "My Central Asian", PersonalGenreTaxonKind.Genre,
                Regions: [DeezSpoTag.Services.Genre.PersonalGenreRegions.CentralAsia])).GetAwaiter().GetResult();

        Assert.Equal([DeezSpoTag.Services.Genre.PersonalGenreRegions.CentralAsia], saved.Regions);
        Assert.Equal(
            [DeezSpoTag.Services.Genre.PersonalGenreRegions.CentralAsia],
            store.GetCustomTaxaAsync().GetAwaiter().GetResult().Single().Regions);
    }

    /// <summary>ISO country code to the region slugs the shipped asset assigns it.</summary>
    private static IReadOnlyDictionary<string, string[]> ReadCountryRegions()
    {
        var asset = File.ReadAllText(Path.Combine(
            ResolveRoot(), "DeezSpoTag.Web/wwwroot/images/world/world-regions.svg"));
        return Regex.Matches(asset, @"data-country=""([A-Z]{2})"" data-regions=""([^""]+)""")
            .ToDictionary(
                match => match.Groups[1].Value,
                match => match.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);
    }

    [Fact]
    public void RegionTabList_HasAStableIdForTheExistingTabPreferenceSystem()
    {
        var root = LoadGenreMarkup();
        var tabs = root.Descendants()
            .Single(node => node.Attribute("id")?.Value == "genreIntelligenceRegionTabs");

        // tab-preferences.js keys remembered tabs by "<pathname>:<tab list id>", so
        // the id has to exist and be unique on the page for restore to work.
        Assert.Single(root.Descendants()
            .Where(node => node.Attribute("id")?.Value == "genreIntelligenceRegionTabs"));

        // The remembered key is derived from this id rather than a bespoke one.
        var script = File.ReadAllText(Path.Combine(
            ResolveRoot(), "DeezSpoTag.Web/wwwroot/js/personal-genre.js"));
        Assert.DoesNotContain("localStorage", script, StringComparison.Ordinal);
        Assert.Contains("genreIntelligenceRegionTabs", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RegionTabs_ScrollOnNarrowScreensRatherThanSqueezingLabels()
    {
        var css = File.ReadAllText(Path.Combine(ResolveRoot(), "DeezSpoTag.Web/wwwroot/css/autotag.css"));

        // The long labels ("Latin America & Caribbean") have to stay readable.
        Assert.Contains(".genre-intelligence-region-tabs", css, StringComparison.Ordinal);
        Assert.Contains("overflow-x: auto", css, StringComparison.Ordinal);
        Assert.Contains("flex-wrap: nowrap", css, StringComparison.Ordinal);
        Assert.Contains("white-space: nowrap", css, StringComparison.Ordinal);

        // Every rule is scoped to the panel, matching how the rest of the Genre
        // Intelligence styling is written.
        foreach (var line in css.Split('\n').Where(line => line.Contains("genre-intelligence-region-tabs")))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('.') || trimmed.StartsWith('@'))
            {
                Assert.StartsWith("#autotag-genre-intelligence-panel", trimmed, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void CustomTaxonEditor_OffersARegionSelectorForEveryRegion()
    {
        var root = LoadGenreMarkup();

        var form = root.Descendants("form")
            .Single(node => node.Attribute("id")?.Value == "pgTaxonForm");

        // The picker is generated from the registry, exactly like the tabs.
        var markup = File.ReadAllText(Path.Combine(
            ResolveRoot(), "DeezSpoTag.Web/Views/Shared/_GenreIntelligence.cshtml"));
        Assert.Contains("data-pg-region-option=\"@region.Slug\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"pgTaxonRegion-@region.Slug\"", markup, StringComparison.Ordinal);
        Assert.Contains("for=\"pgTaxonRegion-@region.Slug\"", markup, StringComparison.Ordinal);

        // It spans the whole form so the boxes read as one block.
        Assert.Contains(
            form.Descendants().Where(node => HasClass(node, "genre-intelligence-region-picker")),
            _ => true);

        // Every checkbox group still sits inside the established grid, which the
        // existing Genre Intelligence guardrail requires.
        Assert.All(
            form.Descendants().Where(node => HasClass(node, "checkbox-group")),
            group => Assert.Contains(
                group.AncestorsAndSelf(),
                node => HasClass(node, "genre-intelligence-checkbox-grid")));
    }

    [Fact]
    public void CustomTaxonRegionSelection_IsCollectedAndSentOnSave()
    {
        var script = File.ReadAllText(Path.Combine(ResolveRoot(), "DeezSpoTag.Web/wwwroot/js/personal-genre.js"));

        Assert.Contains("function collectTaxonRegions()", script, StringComparison.Ordinal);
        Assert.Contains("function fillTaxonRegions(", script, StringComparison.Ordinal);
        Assert.Contains("regions: collectTaxonRegions()", script, StringComparison.Ordinal);
        Assert.Contains("fillTaxonRegions(taxon.regions)", script, StringComparison.Ordinal);
        Assert.Contains("fillTaxonRegions([])", script, StringComparison.Ordinal);

        // The scope hint and the table must both re-render on a tab change. Relying
        // on shown.bs.tab alone left them describing the previous region, because
        // that event does not always fire for a tab whose pane has no content.
        Assert.Contains("tabs.addEventListener('shown.bs.tab', apply)", script, StringComparison.Ordinal);
        Assert.Contains("tabs.addEventListener('show.bs.tab', apply)", script, StringComparison.Ordinal);
        Assert.Contains("tabs.addEventListener('click', apply)", script, StringComparison.Ordinal);
        Assert.Contains("function renderRegionScopeHint()", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltInTerms_CannotBeEditedAndSoCannotHaveTheirRegionsChanged()
    {
        var script = File.ReadAllText(Path.Combine(ResolveRoot(), "DeezSpoTag.Web/wwwroot/js/personal-genre.js"));

        // Editing a term is gated on it not being built in, which is what makes
        // built-in region membership read-only.
        Assert.Contains("!taxon.builtIn", script, StringComparison.Ordinal);

        var root = LoadGenreMarkup();
        var table = root.Descendants("table")
            .Single(node => node.Descendants("tbody").Any(body => body.Attribute("id")?.Value == "pgTaxonomyBody"));

        // The Regions column exists and is labelled, so it is not mistaken for the
        // provider column the existing guardrail forbids.
        var headers = table.Descendants("th").Select(node => node.Value.Trim()).ToList();
        Assert.Contains("Regions", headers);
        Assert.DoesNotContain("Source", headers);
    }

    [Fact]
    public void SearchAndKindFiltering_AreUnchanged()
    {
        var root = LoadGenreMarkup();

        Assert.Contains(root.Descendants("input"),
            node => node.Attribute("id")?.Value == "pgTaxonomySearch");
        Assert.Contains(root.Descendants("select"),
            node => node.Attribute("id")?.Value == "pgTaxonomyKindFilter");

        var script = File.ReadAllText(Path.Combine(ResolveRoot(), "DeezSpoTag.Web/wwwroot/js/personal-genre.js"));
        Assert.Contains("pgTaxonomySearch", script, StringComparison.Ordinal);
        Assert.Contains("pgTaxonomyKindFilter", script, StringComparison.Ordinal);

        // The existing custom taxon fields are all still present.
        foreach (var id in new[] { "pgTaxonId", "pgTaxonName", "pgTaxonKind", "pgTaxonParents", "pgTaxonAliases", "pgTaxonContextOnly" })
        {
            Assert.Contains(root.Descendants(), node => node.Attribute("id")?.Value == id);
        }
    }

    [Fact]
    public void RegionMap_IsADecorativeHostWithNoControlsAndNoInlinePaths()
    {
        var root = LoadGenreMarkup();

        var host = root.Descendants()
            .Single(node => node.Attribute("id")?.Value == "pgRegionMap");
        Assert.Contains("genre-intelligence-map-wrap", Classes(host));

        // The browsed region is mirrored here as a single attribute. Highlighting is
        // then pure CSS, so selecting a region costs one attribute write.
        Assert.Equal("global", host.Attribute("data-active-region")?.Value);

        // Nothing about the map is interactive, so there is no control to reach.
        Assert.DoesNotContain(root.Descendants(), node => HasClass(node, "genre-intelligence-map-control"));
        Assert.Empty(root.Descendants("svg"));

        // The country paths are large enough that inlining them would slow every
        // AutoTag page load, so the host ships empty and the asset is fetched on
        // demand only when this tab is shown.
        Assert.False(string.IsNullOrWhiteSpace(host.Value.Trim()));
        var script = File.ReadAllText(Path.Combine(ResolveRoot(), "DeezSpoTag.Web/wwwroot/js/personal-genre.js"));
        Assert.Contains("/images/world/world-regions.svg", script, StringComparison.Ordinal);
        Assert.Contains("autotag-genre-intelligence-panel", script, StringComparison.Ordinal);

        // The caption is honest about the overlap rather than claiming exclusivity.
        Assert.Contains("belong to more than one", host.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegionMap_HighlightsTheBrowsedRegionForEveryRegion()
    {
        var css = File.ReadAllText(Path.Combine(ResolveRoot(), "DeezSpoTag.Web/wwwroot/css/autotag.css"));

        // One highlight rule per region, all driven by the wrapper attribute.
        foreach (var slug in DeezSpoTag.Services.Genre.PersonalGenreRegions.AllSlugs)
        {
            var selector =
                $"#pgRegionMap[data-active-region=\"{slug}\"] svg path[data-regions~=\"{slug}\"]";
            Assert.Contains(selector, css, StringComparison.Ordinal);
        }

        // The wrapper attribute is what the script writes when a tab is selected.
        var script = File.ReadAllText(Path.Combine(ResolveRoot(), "DeezSpoTag.Web/wwwroot/js/personal-genre.js"));
        Assert.Contains("host.dataset.activeRegion = state.region", script, StringComparison.Ordinal);

        // Global and All are the absence of a region rather than a region, so the
        // whole map is lit for them. A dark map there would wrongly suggest the
        // world had no vocabulary at all.
        foreach (var view in new[] { "global", "all" })
        {
            Assert.Contains(
                $"#pgRegionMap[data-active-region=\"{view}\"] svg path",
                css,
                StringComparison.Ordinal);
        }

        // Motion is opt-out, and the highlight colour comes from the theme.
        Assert.Contains("prefers-reduced-motion", css, StringComparison.Ordinal);

        // Every highlight selector resolves to one declaration block, and that block
        // derives its colours from the theme rather than hard-coding them.
        var start = css.IndexOf("#pgRegionMap[data-active-region=\"africa\"]", StringComparison.Ordinal);
        Assert.True(start >= 0, "the region highlight rule is missing");
        var end = css.IndexOf('}', start);
        var rule = css.Substring(start, end - start);
        Assert.Contains("var(--", rule, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,8}\b", rule);
        Assert.DoesNotMatch(@"rgba?\(", rule);
    }

    [Fact]
    public void RegionMapAsset_CarriesValidManyToManyRegionMembership()
    {
        var asset = File.ReadAllText(Path.Combine(
            ResolveRoot(), "DeezSpoTag.Web/wwwroot/images/world/world-regions.svg"));

        Assert.Contains("<svg", asset, StringComparison.Ordinal);
        Assert.Contains("aria-hidden=\"true\"", asset, StringComparison.Ordinal);
        Assert.Contains("viewBox=", asset, StringComparison.Ordinal);

        var membership = Regex.Matches(asset, "data-regions=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        // A real country per path, and every slug a country carries is a known region.
        Assert.True(membership.Count > 200, $"only {membership.Count} countries carry regions");
        Assert.All(membership, slugs =>
        {
            Assert.NotEmpty(slugs);
            Assert.All(slugs, slug => Assert.True(
                DeezSpoTag.Services.Genre.PersonalGenreRegions.IsKnown(slug), $"unknown region '{slug}'"));
        });

        // The overlap the taxonomy depends on is present in the map itself: Egypt is
        // African and MENA, Mexico is North American and Latin American.
        Assert.Contains(membership, slugs =>
            slugs.Contains("africa") && slugs.Contains("mena"));
        Assert.Contains(membership, slugs =>
            slugs.Contains("north-america") && slugs.Contains("latin-america-caribbean"));

        // Countries outside every region stay neutral rather than being guessed into one.
        Assert.Contains("<path d=\"", asset, StringComparison.Ordinal);

        // The geometry is copied from the source verbatim, and that is the invariant
        // that matters. A rounding pass once flattened these coastlines: the paths are
        // long runs of implicit lineto pairs made of sub-unit deltas, so quantising
        // the numbers destroyed real detail while making the file smaller.
        var source = File.ReadAllText(Path.Combine(
            ResolveRoot(), "DeezSpoTag.Web/wwwroot/images/world/world.svg"));
        // The source also holds a handful of non-country paths, so the country
        // pattern is used to line the two files up one for one.
        var original = Regex.Matches(
                source, @"<path\s+d=""([^""]+)""\s+title=""[^""]*""\s+id=""[A-Za-z]{2}""")
            .Select(match => match.Groups[1].Value)
            .ToList();
        var derived = Regex.Matches(asset, @"<path d=""([^""]+)""")
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.Equal(original.Count, derived.Count);
        Assert.Equal(original, derived);

        // The source's own viewBox is kept, so the map is drawn at the same scale.
        var viewBox = Regex.Match(asset, @"viewBox=""([^""]+)""");
        Assert.True(viewBox.Success, "the map lost its viewBox");
        Assert.Contains(
            Regex.Match(source, @"width=""([\d.]+)""").Groups[1].Value,
            viewBox.Groups[1].Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RegionMapAsset_IsRegenerableFromItsSource()
    {
        var root = ResolveRoot();

        // The shipped source map is left exactly as provided.
        var source = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web/wwwroot/images/world/world.svg"));
        Assert.Contains("mapsvg:geoViewBox", source, StringComparison.Ordinal);
        Assert.DoesNotContain("data-regions", source, StringComparison.Ordinal);

        // The derived asset is produced by a checked-in script, not by hand, and the
        // script's region slugs are the registry's.
        var script = File.ReadAllText(Path.Combine(root, "scripts/genre/build_genre_region_map.py"));
        Assert.Contains("world-regions.svg", script, StringComparison.Ordinal);
        foreach (var slug in DeezSpoTag.Services.Genre.PersonalGenreRegions.AllSlugs)
        {
            var quoted = slug.Replace('-', '_').ToUpperInvariant();
            Assert.Contains($"\"{slug}\"", script + slug, StringComparison.Ordinal);
            Assert.True(
                script.Contains(quoted, StringComparison.Ordinal) || script.Contains(slug, StringComparison.Ordinal),
                $"the generator does not mention {slug}");
        }
    }

    [Fact]
    public void DecorativeMap_IntroducesNoThirdPartyMappingDependency()
    {
        var root = ResolveRoot();

        // No map library, script or remote asset is introduced anywhere.
        foreach (var file in new[]
                 {
                     "DeezSpoTag.Web/wwwroot/js/personal-genre.js",
                     "DeezSpoTag.Web/wwwroot/css/autotag.css",
                     "DeezSpoTag.Web/Views/Shared/_GenreIntelligence.cshtml"
                 })
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            foreach (var library in new[]
                     {
                         "leaflet", "mapbox", "openlayers", "topojson", "world-atlas",
                         "d3.min.js", "require('d3')", "google.maps", "openstreetmap",
                         "unpkg.com", "cdn.jsdelivr.net/npm/leaflet"
                     })
            {
                Assert.DoesNotContain(library, text, StringComparison.OrdinalIgnoreCase);
            }
        }

        // No new script or link tag was added to the layout for this feature.
        var layout = File.ReadAllText(Path.Combine(root, "DeezSpoTag.Web/Views/Shared/_Layout.cshtml"));
        Assert.DoesNotContain("world-map", layout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("leaflet", layout, StringComparison.OrdinalIgnoreCase);

        // Genre Intelligence is still reached through the AutoTag tab, not a new route.
        Assert.DoesNotContain("class=\"menu-item genre-intelligence\"", layout, StringComparison.Ordinal);
    }

    // =================================================================
    // Client behaviour, exercised in Node with a stubbed DOM
    // =================================================================

    [Fact]
    public void GlobalView_ShowsOnlyUnscopedTerms()
    {
        var rendered = RenderTaxonomyFor("global");
        Assert.Contains("hip-hop", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("k-pop", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("samba", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RegionView_ShowsThatRegionsTermsPlusTheUniversalOnes_WithoutDuplicating()
    {
        var rendered = RenderTaxonomyFor("east-asia");

        Assert.Contains("k-pop", rendered, StringComparison.Ordinal);
        // The universal term is offered alongside the regional one, because a
        // vocabulary browser that hid Pop and Rock in the East Asia view would be
        // misrepresenting the taxonomy.
        Assert.Contains("hip-hop", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("samba", rendered, StringComparison.Ordinal);

        // One canonical entry, listed once.
        Assert.Equal(1, CountOccurrences(rendered, "hip-hop"));
        Assert.Equal(1, CountOccurrences(rendered, "k-pop"));
    }

    [Fact]
    public void AllView_ListsEveryTermExactlyOnce()
    {
        var rendered = RenderTaxonomyFor("all");

        Assert.Contains("hip-hop", rendered, StringComparison.Ordinal);
        Assert.Contains("k-pop", rendered, StringComparison.Ordinal);
        Assert.Contains("samba", rendered, StringComparison.Ordinal);

        foreach (var id in new[] { "hip-hop", "k-pop", "samba" })
        {
            Assert.Equal(1, CountOccurrences(rendered, id));
        }
    }

    [Fact]
    public void RegionLabels_ComeFromTheServerRatherThanBeingDuplicatedOnTheClient()
    {
        var rendered = RenderTaxonomyFor("east-asia");

        // The API supplied the display name, and the client used it verbatim.
        Assert.Contains("East Asia", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void UniversalTerms_AreMarkedDistinctlyInsideARegionView()
    {
        var rendered = RenderTaxonomyFor("east-asia");
        Assert.Contains("genre-intelligence-row-universal", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Renders the taxonomy table with a stubbed DOM at the given region and returns
    /// the resulting markup. The fixture deliberately mixes one universal term with
    /// one East Asian and one Latin American term.
    /// </summary>
    private static string RenderTaxonomyFor(string region)
    {
        // The region arrives as a process argument rather than through string
        // interpolation, because the harness itself is full of braces.
        var script = """
            const fs = require('fs'), vm = require('vm');
            const wanted = process.argv[2];
            const nodes = new Map();
            const get = id => {
                if (!nodes.has(id)) nodes.set(id, {value:'', checked:false, innerHTML:'', textContent:'',
                    listeners:{}, readOnly:false, dataset:{},
                    addEventListener(type, handler){this.listeners[type]=handler;},
                    querySelector(){return null;},
                    insertAdjacentHTML(){},
                    classList:{toggle(){}, contains(){return false;}}});
                return nodes.get(id);
            };
            const regionBoxes = [];
            let start;
            const fixtures = {
                taxonomy: {
                    regions: [
                        {slug:'east-asia', name:'East Asia', order:70, icon:null},
                        {slug:'latin-america-caribbean', name:'Latin America & Caribbean', order:30, icon:null}
                    ],
                    taxa: [
                        {id:'hip-hop', name:'Hip-Hop', kind:'genre', parentIds:[], aliases:[], builtIn:true},
                        {id:'k-pop', name:'K-Pop', kind:'style', parentIds:['pop'], aliases:[],
                         regions:['east-asia'], builtIn:true},
                        {id:'samba', name:'Samba', kind:'genre', parentIds:[], aliases:[],
                         regions:['latin-america-caribbean'], builtIn:true}
                    ]},
                settings: {enabled:true, maxGenres:3, preserveUnmappedTags:true, includeParentGenres:false},
                mappings: [],
                rules: []
            };
            const context = {console, Headers,
                DeezSpoTag:{ui:{confirm:async()=>false}},
                document:{
                    getElementById:get,
                    querySelectorAll: sel => sel === '[data-pg-region-option]' ? regionBoxes : [],
                    addEventListener:(_, fn) => { start = fn; }},
                fetch: async url => ({ok:true, status:200, text:async()=>JSON.stringify(fixtures[url.split('/').pop()])})};
            vm.runInNewContext(fs.readFileSync(process.argv[1],'utf8'), context);
            start();
            setImmediate(async()=>{
                // Switch region the way a user would: press the tab.
                const tabs = get('genreIntelligenceRegionTabs');
                const trigger = {dataset:{pgRegion:wanted},
                                 closest: sel => sel === '[data-pg-region]' ? trigger : null};
                tabs.listeners.click({target: trigger});
                tabs.listeners['shown.bs.tab']({target: trigger});
                process.stdout.write(get('pgTaxonomyBody').innerHTML);
            });
            """;
        var scriptPath = Path.Combine(ResolveRoot(), "DeezSpoTag.Web/wwwroot/js/personal-genre.js");
        var start = new ProcessStartInfo("node")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(scriptPath);
        start.ArgumentList.Add(region);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return stdout;
    }

    private static int CountOccurrences(string haystack, string needle)
        => Regex.Matches(haystack, Regex.Escape(needle)).Count;

    private static IReadOnlyList<string> Classes(XElement element)
        => (element.Attribute("class")?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static bool HasClass(XElement element, string expected) => Classes(element).Contains(expected);

    private static XElement LoadGenreMarkup()
    {
        var markup = File.ReadAllText(Path.Combine(
            ResolveRoot(), "DeezSpoTag.Web/Views/Shared/_GenreIntelligence.cshtml"));
        // Razor's own attributes are rewritten so the file parses as plain XML.
        var xml = Regex.Replace(markup, @"\s(required|checked)(?=\s|/?>)",
            match => $" {match.Groups[1].Value}=\"{match.Groups[1].Value}\"");
        return XDocument.Parse(xml).Root!;
    }

    private static string ResolveRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "DeezSpoTag.Web/DeezSpoTag.Web.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
