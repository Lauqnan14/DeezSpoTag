using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// The vocabulary table is a reference surface over roughly 3,900 terms, so it is paged
/// rather than rendered whole.
/// </summary>
/// <remarks>
/// <para>
/// These run the script in Node against a stub DOM instead of asserting on its text.
/// The properties that matter here are behavioural: that the number of rows handed to
/// the DOM never exceeds the selected page size, and that a term which was originally on
/// a later page is still reachable. Both can be satisfied by code that looks correct and
/// behaves wrongly, which is exactly the gap that let the built-in flag defect through
/// with the rest of the suite green.
/// </para>
/// <para>
/// The vocabulary is filtered and sorted before it is paged, in that order. Paging first
/// would make a page hold a slice of a different ordering and would hide terms from
/// search entirely.
/// </para>
/// </remarks>
public sealed class GenreVocabularyPagingSurfaceTest
{
    private static string RepoFile(params string[] parts) => Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray());

    private static bool NodeIsAvailable()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("node", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (probe is null)
            {
                return false;
            }

            return probe.WaitForExit(10000) && probe.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The paging surface, lifted out of the page script with just enough scaffolding to
    /// run: the real filter, sort, slice, row renderer and pager.
    /// </summary>
    private static string Harness(string assertions)
    {
        var script = File.ReadAllText(RepoFile("DeezSpoTag.Web", "wwwroot", "js", "personal-genre.js"));

        var regionStart = script.IndexOf("    function matchesRegion(", StringComparison.Ordinal);
        var regionEnd = script.IndexOf("    // The world map is a separate", StringComparison.Ordinal);
        var controlsStart = script.IndexOf("    /// Wires the vocabulary filters, sort and pager.", StringComparison.Ordinal);
        var controlsEnd = script.IndexOf("    function collectTaxonRegions(", StringComparison.Ordinal);
        var pageStart = script.IndexOf("    const taxonomyPage =", StringComparison.Ordinal);
        var pageEnd = script.IndexOf("    function fillTaxonForm(", StringComparison.Ordinal);

        Assert.True(regionStart >= 0 && regionEnd > regionStart, "The region filter must stay findable.");
        Assert.True(controlsStart >= 0 && controlsEnd > controlsStart, "The control bindings must stay findable.");
        Assert.True(pageStart >= 0 && pageEnd > pageStart, "The paging surface must stay findable.");

        var region = script[regionStart..regionEnd];
        // The bindings are included so the tests drive the controls the user actually
        // touches. Calling the handlers directly would let a pager pass while its
        // button was wired to nothing.
        var controls = script[controlsStart..controlsEnd];
        var paging = script[pageStart..pageEnd];
        var escapeStart = script.IndexOf("    function escapeHtml(", StringComparison.Ordinal);
        var escapeEnd = script.IndexOf("    function escapeAttribute(", escapeStart, StringComparison.Ordinal);
        Assert.True(escapeStart >= 0 && escapeEnd > escapeStart);
        var escaping = script[escapeStart..escapeEnd];

        return $$"""
            const assert = require('node:assert/strict');

            // --- stub DOM ---------------------------------------------------------
            const elements = new Map();
            const element = (id, value) => {
                const node = { id, value: value ?? '', textContent: '', disabled: false,
                    innerHTML: '', dataset: {}, handlers: {},
                    addEventListener(event, handler) { this.handlers[event] = handler; },
                    closest() { return null; } };
                elements.set(id, node);
                return node;
            };
            element('pgTaxonomyBody', '');
            element('pgTaxonomySearch', '');
            element('pgTaxonomyKindFilter', '');
            element('pgTaxonomyOriginFilter', '');
            element('pgTaxonomySort', 'name');
            element('pgTaxonomyPageSize', '100');
            element('pgTaxonomyPagerSummary', '');
            element('pgTaxonomyPagerPosition', '');
            element('pgTaxonomyPrev', '');
            element('pgTaxonomyNext', '');

            const document = { getElementById: id => elements.get(id) || null };
            const byId = id => elements.get(id) || null;

            // --- stubs the paging surface legitimately depends on ------------------
            const state = { taxonomy: [], region: 'global' };
            {{escaping}}
            const escapeAttribute = escapeHtml;
            const regionName = slug => slug;
            const renderRegionScopeHint = () => {};
            const fillTaxonForm = () => {};

            function rowCount() {
                return (byId('pgTaxonomyBody').innerHTML.match(/<tr/g) || []).length;
            }

            function makeTerm(index, overrides) {
                return Object.assign({
                    id: 'term-' + index,
                    name: 'Term ' + index,
                    kind: 'genre',
                    origin: 'researched',
                    editable: false,
                    regions: [],
                    parentIds: [],
                    aliases: []
                }, overrides || {});
            }

            {{region}}

            {{controls}}

            {{paging}}

            bindTaxonomyControls();

            {{assertions}}
            """;
    }

    private static void Run(string assertions)
    {
        if (!NodeIsAvailable())
        {
            // Node is used by other tests in this suite but is not a build dependency,
            // so its absence skips rather than fails.
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("node")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;
        process.StandardInput.Write(Harness(assertions));
        process.StandardInput.Close();

        Assert.True(process.WaitForExit(30000));
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public void TableUsesEscapedDisplayNameWithoutChangingIdentity()
    {
        Run("""
            state.taxonomy = [makeTerm(0, {name:'alternative ccm', displayName:'Alternative CCM <test>', editable:true, origin:'custom'})];
            renderTaxonomy();
            const html = byId('pgTaxonomyBody').innerHTML;
            assert.ok(html.includes('<strong>Alternative CCM &lt;test&gt;</strong>'));
            assert.ok(html.includes('data-pg-edit-taxon="term-0"'));
            assert.equal(state.taxonomy[0].name, 'alternative ccm');
            """);
    }

    // ------------------------------------------------------------------ row count

    /// <summary>
    /// The whole point of paging: a 3,900-term vocabulary must not become 3,900 rows in
    /// the document. The default page is 100.
    /// </summary>
    [Fact]
    public void TheRenderedRowCountNeverExceedsTheDefaultPageSize()
    {
        Run("""
            state.taxonomy = Array.from({ length: 3901 }, (_, index) => makeTerm(index));

            renderTaxonomy();
            assert.equal(rowCount(), 100, 'default page is 100 rows');
            assert.equal(byId('pgTaxonomyPagerSummary').textContent, '1–100 of 3901');

            // Every page holds the same bounded number of rows.
            byId('pgTaxonomyNext').handlers.click();
            assert.equal(rowCount(), 100);
            assert.equal(byId('pgTaxonomyPagerPosition').textContent, 'Page 2 of 40');

            byId('pgTaxonomyNext').handlers.click();
            assert.equal(byId('pgTaxonomyPagerSummary').textContent, '201–300 of 3901');
            """);
    }

    /// <summary>
    /// The last page is short, and a partial page is not padded or dropped.
    /// </summary>
    [Fact]
    public void TheLastPageHoldsOnlyTheRowsThatRemain()
    {
        Run("""
            state.taxonomy = Array.from({ length: 250 }, (_, index) => makeTerm(index));

            renderTaxonomy();
            assert.equal(rowCount(), 100);
            assert.equal(byId('pgTaxonomyPagerSummary').textContent, '1–100 of 250');

            byId('pgTaxonomyNext').handlers.click();
            assert.equal(rowCount(), 100);

            byId('pgTaxonomyNext').handlers.click();
            assert.equal(rowCount(), 50, 'the last page holds what is left');
            assert.equal(byId('pgTaxonomyPagerSummary').textContent, '201–250 of 250');
            assert.equal(byId('pgTaxonomyNext').disabled, true);
            """);
    }

    [Theory]
    [InlineData("50", 50)]
    [InlineData("100", 100)]
    [InlineData("250", 250)]
    public void TheSelectedPageSizeIsWhatReachesTheDocument(string selected, int expected)
    {
        Run($$"""
            state.taxonomy = Array.from({ length: 900 }, (_, index) => makeTerm(index));
            byId('pgTaxonomyPageSize').value = '{{selected}}';

            byId('pgTaxonomyPageSize').handlers.change({ target: byId('pgTaxonomyPageSize') });

            assert.equal(rowCount(), {{expected}});
            assert.equal(
                byId('pgTaxonomyPagerSummary').textContent,
                '1–' + {{expected}} + ' of 900');
            """);
    }

    /// <summary>
    /// Only 50, 100 and 250 are offered. Anything else falls back to the default rather
    /// than silently rendering the entire vocabulary.
    /// </summary>
    [Fact]
    public void AnUnlistedPageSizeFallsBackToTheDefault()
    {
        Run("""
            state.taxonomy = Array.from({ length: 900 }, (_, index) => makeTerm(index));
            byId('pgTaxonomyPageSize').value = '900';

            byId('pgTaxonomyPageSize').handlers.change({ target: byId('pgTaxonomyPageSize') });

            assert.equal(rowCount(), 100);
            """);
    }

    // ------------------------------------------------------------------ reachability

    /// <summary>
    /// Filtering runs over the whole vocabulary, not the current page, so a term that was
    /// originally hundreds of rows down is still found by searching for it. Paging first
    /// would make search silently miss it.
    /// </summary>
    [Fact]
    public void SearchFindsATermThatWasOnAnotherPage()
    {
        Run("""
            state.taxonomy = Array.from({ length: 3901 }, (_, index) => makeTerm(index));
            // Named so that it sorts last by name, which is the position a paging bug
            // would strand it in.
            state.taxonomy[3456] = makeTerm(3456, { id: 'zz-needle', name: 'Zz Needle In A Haystack' });

            renderTaxonomy();
            assert.ok(!byId('pgTaxonomyBody').innerHTML.includes('Zz Needle'),
                'it must genuinely be off the first page');
            for (let page = 0; page < 20; page += 1) byId('pgTaxonomyNext').handlers.click();
            assert.ok(!byId('pgTaxonomyBody').innerHTML.includes('Zz Needle'));

            byId('pgTaxonomySearch').value = 'needle in a haystack';
            byId('pgTaxonomySearch').handlers.input();

            assert.equal(rowCount(), 1);
            assert.ok(byId('pgTaxonomyBody').innerHTML.includes('Zz Needle In A Haystack'));
            assert.equal(byId('pgTaxonomyPagerSummary').textContent, '1–1 of 1');
            """);
    }

    [Fact]
    public void SearchAlsoMatchesIdsParentsAndAliases()
    {
        Run("""
            state.taxonomy = Array.from({ length: 500 }, (_, index) => makeTerm(index));
            state.taxonomy[400] = makeTerm(400, {
                id: 'zz-alias-needle',
                name: 'ZZ Alias Needle',
                aliases: ['sharptown'],
                parentIds: ['drum-and-bass']
            });

            for (const [field, value] of [['zz-alias', ''], ['sharptown', ''], ['drum-and-bass', '']]) {
                byId('pgTaxonomySearch').value = field;
                byId('pgTaxonomySearch').handlers.input();
                assert.equal(rowCount(), 1, 'search must find it by ' + field);
            }
            """);
    }

    [Fact]
    public void TheOriginFilterNarrowsTheWholeVocabulary()
    {
        Run("""
            state.taxonomy = [
                ...Array.from({ length: 300 }, (_, i) => makeTerm(i, { origin: 'researched' })),
                ...Array.from({ length: 150 }, (_, i) => makeTerm(1000 + i, { origin: 'core' })),
                ...Array.from({ length: 40 }, (_, i) => makeTerm(2000 + i, { origin: 'custom', editable: true }))
            ];

            for (const [origin, total] of [['built-in', 450], ['custom', 40]]) {
                byId('pgTaxonomyOriginFilter').value = origin;
                byId('pgTaxonomyOriginFilter').handlers.input();

                // Counting is over the whole filtered result, so the page count is the
                // filtered total, not a slice of the 490 on offer.
                assert.equal(
                    byId('pgTaxonomyPagerPosition').textContent,
                    'Page 1 of ' + Math.ceil(total / 100), origin);
                assert.equal(byId('pgTaxonomyPagerSummary').textContent,
                    '1–' + Math.min(100, total) + ' of ' + total, origin);
                assert.equal(rowCount(), Math.min(100, total), origin);
            }
            """);
    }

    [Fact]
    public void TheKindFilterNarrowsTheWholeVocabulary()
    {
        Run("""
            state.taxonomy = [
                ...Array.from({ length: 200 }, (_, i) => makeTerm(i, { kind: 'genre' })),
                ...Array.from({ length: 120 }, (_, i) => makeTerm(500 + i, { kind: 'style' })),
                ...Array.from({ length: 5 }, (_, i) => makeTerm(900 + i, { kind: 'context' }))
            ];

            byId('pgTaxonomyKindFilter').value = 'context';
            byId('pgTaxonomyKindFilter').handlers.input();

            assert.equal(rowCount(), 5);
            assert.equal(byId('pgTaxonomyPagerSummary').textContent, '1–5 of 5');
            """);
    }

    /// <summary>
    /// Changing any filter returns to the first page. Without this the user can be left
    /// on page 40 of a result set that now has one page, staring at an empty table.
    /// </summary>
    [Fact]
    public void AnyFilterChangeReturnsToTheFirstPage()
    {
        Run("""
            // Two kinds and three origins, so every filter leaves a result set of a
            // known size and the page count after filtering is predictable.
            state.taxonomy = Array.from({ length: 3900 }, (_, index) => makeTerm(index, {
                kind: index % 2 ? 'style' : 'genre',
                origin: index % 3 ? 'researched' : 'core'
            }));

            const advance = () => { for (let p = 0; p < 5; p += 1) byId('pgTaxonomyNext').handlers.click(); };

            // Each control is exercised from page 6, where a missing reset would leave
            // the table on the last page or on an empty one, never on page 1.
            for (const [id, value, pages] of [
                ['pgTaxonomySearch', 'term', 39],        // every term
                ['pgTaxonomyKindFilter', 'style', 20],   // 1,950 of them
                ['pgTaxonomyOriginFilter', 'built-in', 39], // all shipped terms
                ['pgTaxonomySort', 'kind', 39]           // reorders only
            ]) {
                byId('pgTaxonomySearch').value = '';
                byId('pgTaxonomyKindFilter').value = '';
                byId('pgTaxonomyOriginFilter').value = '';
                byId('pgTaxonomySort').value = 'name';

                renderTaxonomy();
                assert.equal(byId('pgTaxonomyPagerPosition').textContent, 'Page 1 of 39', 'setup for ' + id);
                advance();
                assert.equal(byId('pgTaxonomyPagerPosition').textContent, 'Page 6 of 39', 'setup for ' + id);

                byId(id).value = value;
                byId(id).handlers.input();

                assert.equal(
                    byId('pgTaxonomyPagerPosition').textContent,
                    'Page 1 of ' + pages, id + ' must return to the first page');
            }
            """);
    }

    /// <summary>
    /// A region change is a filter change, and goes through the same reset.
    /// </summary>
    [Fact]
    public void ChangingThePageSizeReturnsToTheFirstPage()
    {
        Run("""
            state.taxonomy = Array.from({ length: 3901 }, (_, index) => makeTerm(index));
            renderTaxonomy();
            byId('pgTaxonomyNext').handlers.click();
            byId('pgTaxonomyNext').handlers.click();
            assert.equal(byId('pgTaxonomyPagerPosition').textContent, 'Page 3 of 40');

            byId('pgTaxonomyPageSize').value = '250';
            byId('pgTaxonomyPageSize').handlers.change({ target: byId('pgTaxonomyPageSize') });

            assert.equal(byId('pgTaxonomyPagerPosition').textContent, 'Page 1 of 16');
            assert.equal(rowCount(), 250);
            """);
    }

    /// <summary>
    /// The pager cannot walk past either end.
    /// </summary>
    [Fact]
    public void ThePagerIsBoundedAtBothEnds()
    {
        Run("""
            state.taxonomy = Array.from({ length: 3901 }, (_, index) => makeTerm(index));
            renderTaxonomy();

            assert.equal(byId('pgTaxonomyPrev').disabled, true);
            for (let page = 0; page < 60; page += 1) byId('pgTaxonomyNext').handlers.click();

            assert.equal(byId('pgTaxonomyNext').disabled, true);
            assert.equal(byId('pgTaxonomyPagerPosition').textContent, 'Page 40 of 40');
            assert.equal(byId('pgTaxonomyPagerSummary').textContent, '3901–3901 of 3901');
            """);
    }

    // ------------------------------------------------------------------ sorting

    /// <summary>
    /// Sorting orders the whole filtered set, so the first page is the true top of the
    /// ordering rather than a slice of it.
    /// </summary>
    [Fact]
    public void SortingAppliesToTheWholeSetNotJustTheCurrentPage()
    {
        Run("""
            // Names are the reverse of their position, so a whole-set sort and a
            // sort-of-the-current-page produce visibly different first pages.
            state.taxonomy = Array.from({ length: 300 }, (_, index) =>
                makeTerm(index, { name: 'Term ' + (300 - index) }));

            renderTaxonomy();
            let html = byId('pgTaxonomyBody').innerHTML;

            assert.ok(!html.includes('Term 300'),
                'sorting only the current page would put Term 300, the raw first row, on page one');
            assert.ok(html.includes('Term 1<'), 'page one begins at the top of the name ordering');
            assert.ok(html.includes('Term 101'), 'page one holds the first 100 of the ordering');

            // Moving through the ordering must arrive at the largest name, not at the
            // end of the raw array.
            for (let page = 0; page < 2; page += 1) byId('pgTaxonomyNext').handlers.click();
            html = byId('pgTaxonomyBody').innerHTML;
            assert.ok(html.includes('Term 300'), 'the largest names are on the final page');
            assert.equal(rowCount(), 100);

            // Sorting by kind over the same set stays bounded and stays a whole-set sort.
            byId('pgTaxonomySort').value = 'kind';
            byId('pgTaxonomySort').handlers.input();
            assert.equal(rowCount(), 100);
            """);
    }

    // ------------------------------------------------------------------ provenance

    [Theory]
    [InlineData("core", "In-built", "built-in")]
    [InlineData("researched", "In-built", "built-in")]
    [InlineData("custom", "Custom", "custom")]
    public void EachOriginIsBadgedWithItsOwnLabel(string origin, string label, string modifier)
    {
        Run($$"""
            state.taxonomy = [makeTerm(0, { origin: '{{origin}}', editable: {{(origin == "custom").ToString().ToLowerInvariant()}} })];
            renderTaxonomy();

            const html = byId('pgTaxonomyBody').innerHTML;
            assert.ok(html.includes('genre-intelligence-badge-{{modifier}}'), 'badge modifier for ' + '{{origin}}');
            assert.ok(html.includes('>{{label}}<'), 'badge label for ' + '{{origin}}');
            """);
    }

    /// <summary>
    /// Core and Researched are both protected, so neither may offer Edit or Delete.
    /// Custom is the user's own and does.
    /// </summary>
    [Theory]
    [InlineData("core", false)]
    [InlineData("researched", false)]
    [InlineData("custom", true)]
    public void RowActionsAreOfferedOnlyForCustomTerms(string origin, bool expected)
    {
        Run($$"""
            state.taxonomy = [makeTerm(0, { origin: '{{origin}}', editable: {{(expected ? "true" : "false")}} })];
            renderTaxonomy();

            const html = byId('pgTaxonomyBody').innerHTML;
            assert.equal(html.includes('data-pg-edit-taxon'), {{(expected ? "true" : "false")}}, '{{origin}} Edit');
            assert.equal(html.includes('data-pg-delete-taxon'), {{(expected ? "true" : "false")}}, '{{origin}} Delete');
            """);
    }

    /// <summary>
    /// An empty result says so rather than rendering a header-only table.
    /// </summary>
    [Fact]
    public void AnEmptyResultIsReportedAsSuch()
    {
        Run("""
            state.taxonomy = Array.from({ length: 300 }, (_, index) => makeTerm(index));
            byId('pgTaxonomySearch').value = 'nothing matches this';
            byId('pgTaxonomySearch').handlers.input();

            assert.equal(rowCount(), 1, 'a single explanatory row, not zero rows');
            assert.ok(byId('pgTaxonomyBody').innerHTML.includes('No matching taxonomy terms'));
            assert.equal(byId('pgTaxonomyPagerSummary').textContent, '0 terms');
            """);
    }
}
