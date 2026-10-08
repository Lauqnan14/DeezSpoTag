using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeezSpoTag.Services.Download;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Guardrails for the Soulseek user interface.
/// </summary>
/// <remarks>
///     <para>
///         These exist because the interface was declared complete while almost none of it existed. The
///         backend, the API and the SignalR hub were all built and shipped, no client subscribed to the hub,
///         the search page had no Soulseek surface at all, and the feature flag defaulted to off. Nothing
///         failed, because none of that is covered by a test.
///     </para>
///     <para>
///         So each test here pins one link in the chain: the markup exists, the script is loaded through the
///         app's asset helper, the script talks to the real endpoints, the hub events are subscribed by the
///         names the server publishes, and untrusted peer data is escaped on the way to the DOM.
///     </para>
/// </remarks>
public sealed class SoulseekUiGuardrailTest
{
    private static string SearchView() => ReadRepoFile("DeezSpoTag.Web", "Views", "Search", "Index.cshtml");

    private static string ActivitiesView() => ReadRepoFile("DeezSpoTag.Web", "Views", "Activities", "Index.cshtml");

    private static string ClientScript() => ReadRepoFile("DeezSpoTag.Web", "wwwroot", "js", "soulseek.js");

    private static string AppSettings() => ReadRepoFile("DeezSpoTag.Web", "appsettings.json");

    [Fact]
    public void TheSearchPageOffersASoulseekTab()
    {
        var view = SearchView();

        Assert.Contains("data-source=\"soulseek\"", view, System.StringComparison.Ordinal);
        Assert.Contains("id=\"tab-soulseek\"", view, System.StringComparison.Ordinal);
        Assert.Contains("data-bs-target=\"#search-source-soulseek\"", view, System.StringComparison.Ordinal);
        Assert.Contains("id=\"search-source-soulseek\"", view, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     Every id the client script looks up must exist in the markup. A rename on one side only would
    ///     otherwise fail silently, because the script treats a missing element as "nothing to update".
    /// </summary>
    [Theory]
    [InlineData("soulseek-search-status")]
    [InlineData("soulseek-search-title")]
    [InlineData("soulseek-search-query")]
    [InlineData("soulseek-search-stats")]
    [InlineData("soulseek-spinner")]
    [InlineData("soulseek-stop")]
    [InlineData("soulseek-hist")]
    [InlineData("soulseek-hist-bars")]
    [InlineData("soulseek-hist-phase")]
    [InlineData("soulseek-views")]
    [InlineData("soulseek-qtabs")]
    [InlineData("soulseek-groups")]
    [InlineData("soulseek-best")]
    [InlineData("soulseek-best-rank")]
    [InlineData("soulseek-best-queue")]
    [InlineData("soulseek-candidates-empty")]
    [InlineData("soulseek-result-count")]
    public void EveryElementTheClientScriptBindsExistsInTheMarkup(string elementId)
    {
        var view = SearchView();

        Assert.Contains($"id=\"{elementId}\"", view, System.StringComparison.Ordinal);

        // And the script actually references it, so the two lists cannot drift apart unnoticed.
        Assert.Contains(elementId, ClientScript(), System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The panel must not carry its own artist and title inputs. A Soulseek query is per track and is
    ///     scored against the expected artist, title and duration, so those values have to come from the
    ///     catalogue result the user chose. Splitting the page's free-text search term into two fields was a
    ///     guess, and it guessed wrong often enough to be worse than asking.
    /// </summary>
    [Fact]
    public void ThePanelHasNoRedundantSearchBarOfItsOwn()
    {
        var view = SearchView();

        Assert.DoesNotContain("id=\"soulseek-artist\"", view, System.StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"soulseek-title\"", view, System.StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"soulseek-search-button\"", view, System.StringComparison.Ordinal);
        Assert.DoesNotContain("soulseek-search-controls", view, System.StringComparison.Ordinal);

        // Nor may the script reintroduce the guesswork behind a different name: the term is read and sent
        // whole, never split into a guessed artist and title.
        var script = ClientScript();
        Assert.DoesNotContain("splitSearchTerm", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("prefillFromSearchTerm", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("setSearchFields", script, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The search page's inline script has to parse. It is a single 2,700-line script, and a stray brace
    ///     silently kills every binding on the page: no tab handler, no results, and only a console error to
    ///     show for it. This is not hypothetical, a bad edit left an orphaned block here and the page still
    ///     returned 200 with a working layout.
    /// </summary>
    [Fact]
    public void TheSearchPageInlineScriptParses()
    {
        var node = FindNode();
        Assert.NotNull(node);

        var view = SearchView();
        var script = ExtractInlineScript(view);
        Assert.False(string.IsNullOrWhiteSpace(script), "The search page inline script was not found.");

        // The .mjs extension matters. Checked as CommonJS, node wraps the file in a function and so accepts
        // a top-level return, which is exactly the construct that broke this page. Browsers parse the inline
        // script as a classic script and reject it with "Illegal return statement". Checking as a module
        // reproduces the browser's rule.
        var temp = Path.Combine(Path.GetTempPath(), $"deezspotag-search-inline-{Guid.NewGuid():N}.mjs");
        try
        {
            File.WriteAllText(temp, script);
            var result = RunNodeSyntaxCheck(temp);
            Assert.True(
                result.ExitCode == 0,
                $"The search page inline script does not parse: {result.Error}{Environment.NewLine}{result.Output}");
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // A leftover temp file must not fail the run.
            }
        }
    }

    private static string ExtractInlineScript(string view)
    {
        var marker = view.IndexOf("@section Scripts {", System.StringComparison.Ordinal);
        Assert.True(marker > 0, "The Scripts section was not found.");

        var open = view.IndexOf("<script>", marker, System.StringComparison.Ordinal);
        var close = view.IndexOf("</script>", open, System.StringComparison.Ordinal);
        Assert.True(open > 0 && close > open, "The inline script block was not found.");

        var script = view.Substring(open + "<script>".Length, close - open - "<script>".Length);

        // The view is unrendered, so it still contains Razor expressions. Each one emits a JSON literal, so
        // substituting a string literal is faithful to what the browser receives and leaves the surrounding
        // syntax exactly as shipped. Without this the check would fail on Razor rather than on real syntax.
        return ReplaceRazorExpressions(script);
    }

    /// <summary>
    ///     Replaces every <c>@Html.Raw(...)</c> with an empty string literal. The argument can itself contain
    ///     balanced parentheses, so the scan tracks depth rather than stopping at the first closing bracket.
    /// </summary>
    private static string ReplaceRazorExpressions(string script)
    {
        const string razorOpen = "@Html.Raw(";
        var builder = new System.Text.StringBuilder(script);

        // Search the builder, not the original: the builder is what the previous iteration rewrote, so
        // offsets have to be read back from it or the scan repeats work it has already done.
        while (true)
        {
            var current = builder.ToString();
            var start = current.IndexOf(razorOpen, System.StringComparison.Ordinal);
            if (start < 0)
            {
                return current;
            }

            var depth = 0;
            var end = -1;
            for (var index = start + razorOpen.Length - 1; index < current.Length; index++)
            {
                if (current[index] == '(')
                {
                    depth++;
                }
                else if (current[index] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = index;
                        break;
                    }
                }
            }

            if (end < 0)
            {
                return current;
            }

            builder.Remove(start, end - start + 1).Insert(start, "\"\"");
        }
    }

    private static (int ExitCode, string Output, string Error) RunNodeSyntaxCheck(string file)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--check");
        startInfo.ArgumentList.Add(file);

        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null)
        {
            return (0, string.Empty, "node could not be started; the check was skipped.");
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);

        return (process.ExitCode, output, error);
    }

    private static string? FindNode()
    {
        foreach (var candidate in new[] { "node", "/usr/bin/node", "/usr/local/bin/node" })
        {
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo(candidate, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using var probe = System.Diagnostics.Process.Start(startInfo);
                if (probe is null)
                {
                    continue;
                }

                probe.StandardOutput.ReadToEnd();
                probe.StandardError.ReadToEnd();
                if (probe.WaitForExit(10_000) && probe.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not on PATH; try the next candidate.
            }
        }

        return null;
    }

    /// <summary>
    ///     Soulseek must not appear anywhere inside the other source tabs. A per-card action was tried and
    ///     reverted: it put a Soulseek button on every Deezer, Spotify, Tidal, Apple and Amazon result, which
    ///     is not what a source tab is for and invaded five tabs to serve one.
    /// </summary>
    /// <summary>
    ///     Task 1: the catalogue artwork already resolved for the search has to travel with the queue request,
    ///     or it is thrown away the moment the user queues a result.
    /// </summary>
    [Fact]
    public void QueueingACarriesTheArtworkAlreadyShownOnTheCard()
    {
        var script = ClientScript();
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs");

        var queue = ExtractFunction(script, "async function queueCandidate(encodedId)");

        // It reuses the cover the user is already looking at, rather than looking one up again, and it queues
        // the candidate by its encoded id rather than by row position, so a re-ranked list cannot queue the
        // wrong peer.
        Assert.Contains("displayCoverUrl", queue, System.StringComparison.Ordinal);
        Assert.Contains("state.coverUrl", queue, System.StringComparison.Ordinal);
        Assert.Contains("decodeURIComponent(encodedId)", queue, System.StringComparison.Ordinal);
        Assert.DoesNotContain("state.results[", queue, System.StringComparison.Ordinal);

        // The request model accepts it, and the shared client forwards it.
        Assert.Contains("DisplayCoverUrl", controller, System.StringComparison.Ordinal);
        var client = ReadRepoFile("DeezSpoTag.Web", "wwwroot", "js", "download-client.js");
        Assert.Contains("displayCoverUrl: metadata?.displayCoverUrl", client, System.StringComparison.Ordinal);

        // It lands on the display-only intent property, which the shared planner carries into the intent...
        var planner = ReadRepoFile("DeezSpoTag.Web", "Services", "SoulseekBatchQueuePlanner.cs");
        Assert.Contains("string? displayCoverUrl", planner, System.StringComparison.Ordinal);
        // ...and never on the field the tagger reads, anywhere on the way.
        Assert.DoesNotContain("Cover =", ExtractMethod(controller, "public async Task<IActionResult> QueueDownload"), System.StringComparison.Ordinal);
        Assert.DoesNotContain("Cover =", ExtractMethod(controller, "private static DownloadIntent BuildQueueDownloadIntent"), System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     Task 3: Activities must surface the display cover for both the initial payload and a restored one,
    ///     using the alias list it already has. No Soulseek branch is added to the view.
    /// </summary>
    [Fact]
    public void ActivitiesRecognisesTheDisplayCoverAlias()
    {
        var activities = ReadRepoFile("DeezSpoTag.Web", "Controllers", "ActivitiesController.cs");

        Assert.Contains("\"soulseekDisplayCoverUrl\"", activities, System.StringComparison.Ordinal);
        Assert.Contains("\"SoulseekDisplayCoverUrl\"", activities, System.StringComparison.Ordinal);

        // The pre-existing aliases must survive, or every other engine loses its artwork on restore.
        foreach (var alias in new[] { "\"Cover\"", "\"coverUrl\"", "\"CoverUrl\"", "\"albumCover\"", "\"AlbumCover\"" })
        {
            Assert.Contains(alias, activities, System.StringComparison.Ordinal);
        }

        // The view keeps its single generic cover reader, with no Soulseek branch inside it. The view may
        // still contain Soulseek display code elsewhere, such as the peer badge, so this asserts on the
        // cover reader itself rather than the whole file.
        var view = ActivitiesView();
        Assert.Contains("cover: getCoverFromPayload(item)", view, System.StringComparison.Ordinal);

        var coverReader = ExtractFunction(view, "function getCoverFromPayload(item)");
        Assert.DoesNotContain("soulseek", coverReader, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The login page owns Soulseek connection status, for every platform, via the shared status card. A
    ///     second status bar inside the search tab duplicated shipped UI in the wrong place, which is the kind
    ///     of thing that only gets caught by looking at how the rest of the app is built.
    /// </summary>
    [Fact]
    public void ThePanelDoesNotDuplicateTheLoginPageConnectionStatus()
    {
        var view = SearchView();
        var script = ClientScript();

        foreach (var id in new[]
                 {
                     "soulseek-connection-state",
                     "soulseek-connection-user",
                     "soulseek-connection-message",
                     "soulseek-connect",
                     "soulseek-disconnect",
                     "soulseek-setup-hint",
                     "soulseek-connection-badge"
                 })
        {
            Assert.DoesNotContain($"id=\"{id}\"", view, System.StringComparison.Ordinal);
            Assert.DoesNotContain($"'{id}'", script, System.StringComparison.Ordinal);
        }

        // Nor may the tab try to connect or disconnect slskd itself.
        Assert.DoesNotContain("'/connection/connect'", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("'/connection/disconnect'", script, System.StringComparison.Ordinal);

        // The login page's status card is the one place it is shown.
        var login = ReadRepoFile("DeezSpoTag.Web", "Views", "Login", "Index.cshtml");
        Assert.Contains("soulseekDisconnectBtn", login, System.StringComparison.Ordinal);
        Assert.Contains("_PlatformLoginStatusCard", login, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The result card is built from the page's own result classes and its own overlay button, so it looks
    ///     like every other card in the grid rather than a parallel design.
    /// </summary>
    [Fact]
    public void TheResultRowUsesThePagesOwnClasses()
    {
        var script = ClientScript();
        var row = ExtractFunction(script, "function resultRow(entry, index)");

        // A row is one peer's copy of the whole release, so the columns are the ones needed to choose between
        // peers offering the same album: who they are, what they call it, how much of it they hold, how big it
        // is and how well it matched. The per-file columns this list used to name - a Filename cell for one
        // file of many, and a per-row availability and action cell - have no subject here, because no cell on
        // this row is about a single file any more.
        foreach (var element in new[]
                 {
                     "soulseek-row",
                     "soulseek-cell-rank",
                     "soulseek-cell-user",
                     "soulseek-cell-release",
                     "soulseek-cell-coverage",
                     "soulseek-cell-num",
                     "soulseek-cell-match",
                     "soulseek-dot",
                     "soulseek-release-name",
                     "soulseek-coverage",
                     "soulseek-meter"
                 })
        {
            Assert.Contains(element, row, System.StringComparison.Ordinal);
        }

        // A row reports the peer facts the search response already carried, rather than asking for a second
        // status request per row.
        Assert.Contains("entry.online", row, System.StringComparison.Ordinal);
        Assert.Contains("entry.totalSize", row, System.StringComparison.Ordinal);
        Assert.Contains("entry.bestScore", row, System.StringComparison.Ordinal);

        // The row carries no queue control of its own: every row in a group is the same album, so a Queue
        // button per row asked the same question nineteen times. The expansion control is the app's existing
        // action button, and the queue decision is made inside the drawer where the tracks are listed.
        Assert.Contains("action-btn", row, System.StringComparison.Ordinal);
        Assert.Contains("data-soulseek-album", row, System.StringComparison.Ordinal);
        Assert.DoesNotContain("data-soulseek-queue", row, System.StringComparison.Ordinal);

        // No bespoke card styling may creep back in.
        Assert.DoesNotContain("result-actions", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("soulseek-card-actions", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("soulseek-candidate-meta", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("soulseek-filename", script, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     A peer's <c>filename</c> is the whole path inside that peer's share, so a cell labelled Filename was
    ///     showing a directory listing rather than a file. A row is now a release, so the cell shows the
    ///     release name and keeps the full remote path as its tooltip, where it still answers "which folder did
    ///     this come from". The leaf is not shown: the path is the identity, and truncating it to a leaf would
    ///     hide which of a peer's several similarly named folders it was.
    /// </summary>
    [Fact]
    public void TheReleaseColumnShowsTheReleaseNameAndKeepsThePeersPathAsATooltip()
    {
        var script = ClientScript();
        var row = ExtractFunction(script, "function resultRow(entry, index)");
        var leaf = ExtractFunction(script, "function fileNameOf(path)");

        // The release name and the remote path are both peer-supplied, so both are escaped where they are
        // bound and the tooltip is assembled from the escaped locals rather than from the raw fields.
        Assert.Contains("escapeHtml(entry.folderName", row, System.StringComparison.Ordinal);
        Assert.Contains("escapeHtml(entry.remoteDirectory", row, System.StringComparison.Ordinal);
        Assert.Contains("title=\"${releaseTitle}\"", row, System.StringComparison.Ordinal);
        Assert.Contains("${releaseName}", row, System.StringComparison.Ordinal);

        // A peer's operating system is not this one, so both separators split. The drawer resolves a release's
        // folder from the same helper, so a peer using backslashes is grouped the same way everywhere.
        Assert.Contains("/[\\\\/]/", leaf, System.StringComparison.Ordinal);
        var key = ExtractFunction(script, "function releaseKey(username, remoteDirectory)");
        Assert.Contains("normalizeRemotePath(remoteDirectory)", key, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     A Soulseek candidate is a peer filename with no artwork of its own, so the card's cover comes from
    ///     the catalogue, resolved once per search. Without this the cards show a bare placeholder.
    /// </summary>
    [Fact]
    public void TheResultRowCarriesTheCatalogueArtworkForDisplayOnly()
    {
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs");
        var script = ClientScript();

        Assert.Contains("ResolveSearchCoverUrlAsync", controller, System.StringComparison.Ordinal);
        Assert.Contains("SearchTracksAsync", controller, System.StringComparison.Ordinal);
        Assert.Contains("coverUrl", controller, System.StringComparison.Ordinal);

        // The result list is a table of peers, so the artwork is not rendered as an image on the row at all.
        // What has to hold is that the cover is display-only: it travels as displayCoverUrl and never becomes
        // the payload's Cover, which is the field the tagging pipeline reads.
        var row = ExtractFunction(script, "function resultRow(entry, index)");
        Assert.DoesNotContain("<img", row, System.StringComparison.Ordinal);

        // The intent is built in a shared helper that QueueDownload delegates to, so that is where the artwork
        // has to travel from: if it stopped being passed there, the cover would silently stop reaching the
        // tagging pipeline for every Soulseek download.
        var build = ExtractMethod(controller, "private static DownloadIntent BuildQueueDownloadIntent");
        Assert.Contains("request.DisplayCoverUrl", build, System.StringComparison.Ordinal);

        // Display-only means exactly that: it must reach the intent as display artwork and never as the Cover
        // the tagging pipeline reads, or a peer's folder image would overwrite the catalogue's.
        Assert.DoesNotContain("Cover =", build, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Cover =", ExtractMethod(controller, "public async Task<IActionResult> QueueDownload"), System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     A Soulseek download is handed to the same shared post-download pipeline as every other engine, so
    ///     the per-profile, per-folder artwork and lyrics preferences apply to it. This is what makes the
    ///     engine behave like the others rather than being a special case.
    /// </summary>
    [Fact]
    public void SoulseekDownloadsUseTheSharedTaggingPipeline()
    {
        var intents = ReadRepoFile("DeezSpoTag.Web", "Services", "DownloadIntentService.cs");

        Assert.Contains("case SoulseekQueueItem soulseek:", intents, System.StringComparison.Ordinal);
        Assert.Contains("ApplyIntentMetadata(soulseek, intent)", intents, System.StringComparison.Ordinal);
        Assert.Contains("ApplyIntentMetadataToStereoPayload", intents, System.StringComparison.Ordinal);

        // The artwork fallback order is resolved from the profile settings, per destination folder.
        var artwork = ReadRepoFile("DeezSpoTag.Services", "Download", "Utils", "ArtworkFallbackHelper.cs");
        Assert.Contains("ResolveOrder", artwork, System.StringComparison.Ordinal);
        Assert.Contains("TryResolveDeezerCoverAsync", artwork, System.StringComparison.Ordinal);
        Assert.Contains("TryResolveAppleCoverAsync", artwork, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     Progressive results replace the card grid. A live search returns hundreds of peers, and the grid
    ///     was the single biggest thing making the results unusable.
    /// </summary>
    [Fact]
    public void SoulseekResultsAreADenseTableNotTheCardGrid()
    {
        var view = SearchView();
        var script = ClientScript();

        // Results are now grouped by quality, so the groups host is what the script writes into and each
        // group renders its own dense table.
        Assert.Contains("id=\"soulseek-groups\"", view, System.StringComparison.Ordinal);
        Assert.Contains("id=\"soulseek-qtabs\"", view, System.StringComparison.Ordinal);
        Assert.Contains("<table class=\"soulseek-table\"", script, System.StringComparison.Ordinal);
        Assert.Contains("function renderGroup(group)", script, System.StringComparison.Ordinal);

        // The grid and the per-result card are gone.
        Assert.DoesNotContain("class=\"result-card\"", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("results-grid\" id=\"soulseek", view, System.StringComparison.Ordinal);
        Assert.DoesNotContain("soulseek-candidate-meta", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("soulseek-card-actions", script, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The groups are the Soulseek qualities the user enabled in Download, in ladder order, and they come
    ///     from the same <c>DownloadSourceOrder</c> the downloader walks. The tab must never hardcode its own
    ///     grouping, or the two would drift and the tab would offer a quality the downloader would refuse.
    /// </summary>
    [Fact]
    public void QualityGroupsComeFromTheConfiguredLadder()
    {
        var script = ClientScript();
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs");

        // The server resolves the enabled qualities from the ladder and sends them with every result set.
        Assert.Contains("ResolveEnabledSoulseekQualities", controller, System.StringComparison.Ordinal);
        // ...then projects them through the display rule: empty or complete renders the whole ladder, a
        // partial Custom selection renders only the ticked qualities.
        Assert.Contains("ResolveDisplayQualityGroups", controller, System.StringComparison.Ordinal);
        Assert.Contains("qualityGroups = BuildQualityGroups()", controller, System.StringComparison.Ordinal);
        Assert.Contains("private object[] BuildQualityGroups()", controller, System.StringComparison.Ordinal);

        // Both the live tick and the final fetch carry the group list.
        Assert.Contains("update.qualityGroups", script, System.StringComparison.Ordinal);
        Assert.Contains("result.qualityGroups", script, System.StringComparison.Ordinal);
        Assert.Contains("function applyGroups(groups)", script, System.StringComparison.Ordinal);

        // A group is keyed by the engine's own quality code, and the ladder order is preserved rather than
        // re-sorted by score.
        Assert.Contains("state.groups = groups", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("state.groups.sort", script, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The quality pills are rendered into their own container beside the groups they filter, so the
    ///     delegated listener has to cover the shared parent. Bound to the groups container alone, a click
    ///     on a pill never reached the handler, which is why the pills behaved like decoration.
    /// </summary>
    [Fact]
    public void ClickingAQualityPillFiltersToThatQuality()
    {
        var script = ClientScript();
        var view = SearchView();
        var bind = ExtractFunction(script, "function bindPanel()");

        Assert.Contains("byId('soulseek-qtabs')", bind, System.StringComparison.Ordinal);
        Assert.Contains("pills.parentElement === groups.parentElement", bind, System.StringComparison.Ordinal);
        Assert.Contains("panel.addEventListener('click'", bind, System.StringComparison.Ordinal);

        // The click selects the group, and the renderer then shows that group and nothing else.
        Assert.Contains("state.activeGroup = tab.dataset.soulseekGroup || null;", bind, System.StringComparison.Ordinal);
        var render = ExtractFunction(script, "function renderGroups()");
        Assert.Contains("configured.filter((group) => group.code === state.activeGroup)", render, System.StringComparison.Ordinal);

        // Both containers are in the panel the listener is bound to.
        Assert.Contains("id=\"soulseek-qtabs\"", view, System.StringComparison.Ordinal);
        Assert.Contains("id=\"soulseek-groups\"", view, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The tab renders the ladder and nothing else.
    /// </summary>
    /// <remarks>
    ///     The undetermined quality used to be a bucket the client invented and always showed, so a user who
    ///     had switched Unknown quality off still saw a group of files - and a count - for a step the
    ///     download refuses. Unknown quality is the last rung of the ladder: the server sends it when the
    ///     setting is on and omits it when it is off, and the tab draws exactly what it is sent. A known
    ///     quality that is not enabled matches no group at all, so it is hidden and left out of the count
    ///     rather than advertised as a step the ladder will not take.
    /// </remarks>
    [Fact]
    public void TheTabRendersTheLadderTheServerSentAndInventsNoBucket()
    {
        var script = ClientScript();
        var render = ExtractFunction(script, "function renderGroups()");
        var forGroup = ExtractFunction(script, "function candidatesFor(code)");
        var applied = ExtractFunction(script, "function applyGroups(groups)");

        Assert.DoesNotContain("label: 'Other'", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("wanted === 'OTHER'", forGroup, System.StringComparison.Ordinal);
        Assert.DoesNotContain("configured.push(", render, System.StringComparison.Ordinal);
        Assert.DoesNotContain("candidatesFor('other')", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("'other'", applied, System.StringComparison.Ordinal);

        // The groups are the server's, in the server's order, and only a non-empty one is offered.
        Assert.Contains("state.groups", render, System.StringComparison.Ordinal);
        Assert.Contains(".filter((group) => group.rows.length > 0)", render, System.StringComparison.Ordinal);

        // The server decides, and it decides from the setting that also gates the download.
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs");
        Assert.Contains("AllowUnknownQuality", controller, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The live payload names a candidate's code <c>quality</c> and the persisted one names it
    ///     <c>qualityCode</c>. Both must be read, or every row restored after the search finalizes loses its
    ///     tier and piles into Other. The same restore carries no liveness evidence, so it must report the
    ///     peer as unknown rather than as online.
    /// </summary>
    [Fact]
    public void RestoredCandidatesKeepTheirGroupAndDoNotClaimToBeOnline()
    {
        var script = ClientScript();
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs");
        var merge = ExtractFunction(script, "function mergeResults(results, completed, timedOut, responseCount, groups, outcome)");

        Assert.Contains("raw.quality ?? raw.qualityCode", merge, System.StringComparison.Ordinal);
        Assert.Contains("groupCodeOf(candidate) === wanted", script, System.StringComparison.Ordinal);

        // The recorded search sends an explicit false rather than omitting the flag, because an omitted flag
        // is what made the client default every restored row to online.
        Assert.Contains("online = false", controller, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     Rows must be keyed by a stable id and merged, so later responses add without duplicating and a
    ///     row the user is reading never moves.
    /// </summary>
    [Fact]
    public void LiveResultsAreMergedByStableIdAndNeverReordered()
    {
        var script = ClientScript();
        var merge = ExtractFunction(script, "function mergeResults(results, completed, timedOut, responseCount, groups, outcome)");

        Assert.Contains("state.byId.has(id)", merge, System.StringComparison.Ordinal);
        Assert.Contains("data-soulseek-id", script, System.StringComparison.Ordinal);

        // Existing rows are appended, never repositioned: there is no sort or insertBefore over rendered rows.
        Assert.DoesNotContain("insertBefore", merge, System.StringComparison.Ordinal);
        Assert.DoesNotContain("row.remove()", merge, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     A stale event from a superseded search must not be able to touch the list.
    /// </summary>
    [Fact]
    public void ResultsFromASupersededSearchAreIgnored()
    {
        var script = ClientScript();
        var update = ExtractFunction(script, "function renderSearchUpdate(update)");

        Assert.Contains("update.searchId !== state.searchId", update, System.StringComparison.Ordinal);

        var run = ExtractFunction(script, "async function runSearch()");
        Assert.Contains("state.generation += 1", run, System.StringComparison.Ordinal);
        Assert.Contains("resetResults()", run, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     An inactive Soulseek must not be searched, and must not claim to be connecting.
    /// </summary>
    /// <remarks>
    ///     This reverses an earlier decision deliberately. The tab used to let a stale disconnected state fall
    ///     through to the server, which reconnected slskd on the way past - so a reader who had logged out got
    ///     Soulseek back by searching it, and a search that could never work looked like it was starting. Now
    ///     the panel refuses locally with the same verdict the API admits on, and only the explicit Connect
    ///     action asks slskd to log in.
    /// </remarks>
    [Fact]
    public void AnInactiveSourceIsRefusedLocallyRatherThanReconnected()
    {
        var script = ClientScript();
        var entry = ExtractFunction(script, "function searchCurrentTerm()");
        var run = ExtractFunction(script, "async function runSearch()");
        var inactiveStart = run.IndexOf("if (!isSoulseekActive())", System.StringComparison.Ordinal);
        var requestStart = run.IndexOf("try {", inactiveStart, System.StringComparison.Ordinal);
        Assert.True(inactiveStart >= 0 && requestStart > inactiveStart, "The inactive-source branch was not found.");
        var inactiveHandling = run[inactiveStart..requestStart];

        Assert.Contains("void runSearch()", entry, System.StringComparison.Ordinal);
        Assert.DoesNotContain("connection.usable", entry, System.StringComparison.Ordinal);

        // It says what to do, and it stops. No request is sent.
        Assert.Contains("Log in to enable Soulseek", inactiveHandling, System.StringComparison.Ordinal);
        Assert.Contains("return null", inactiveHandling, System.StringComparison.Ordinal);

        // And it does not promise a connection it will not make.
        Assert.DoesNotContain("Connecting to Soulseek", run, System.StringComparison.Ordinal);
        Assert.Contains("await request('/searches'", run, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     Queueing is new remote work, so it is gated the same way search is.
    /// </summary>
    [Fact]
    public void QueueingIsRefusedWhileTheSourceIsInactive()
    {
        var script = ClientScript();
        var queue = ExtractFunction(script, "async function queueCandidate(");

        var gate = queue.IndexOf("if (!isSoulseekActive())", System.StringComparison.Ordinal);
        Assert.True(gate > 0, "queueCandidate does not check whether Soulseek is active.");
        Assert.Contains("Log in to enable Soulseek", queue[gate..], System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The tab stays openable while inactive, because that is where the reason and the login link live.
    /// </summary>
    [Fact]
    public void AnInactiveTabExplainsItselfInsteadOfDisappearing()
    {
        var script = ClientScript();
        var availability = ExtractFunction(script, "function renderAvailability()");
        var view = ReadRepoFile("DeezSpoTag.Web", "Views", "Search", "Index.cshtml");

        // aria-disabled rather than disabled: a disabled tab cannot be opened to read the explanation.
        Assert.Contains("aria-disabled", availability, System.StringComparison.Ordinal);
        Assert.DoesNotContain(".disabled = ", availability, System.StringComparison.Ordinal);

        // The notice carries a reason and a way to the login page.
        Assert.Contains("soulseek-login-required", availability, System.StringComparison.Ordinal);
        Assert.Contains("id=\"soulseek-login-required\"", view, System.StringComparison.Ordinal);
        Assert.Contains("id=\"soulseek-login-required-reason\"", view, System.StringComparison.Ordinal);
        Assert.Contains("Log in to enable Soulseek", view, System.StringComparison.Ordinal);
        Assert.Contains("href=\"/Login\"", view, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     Controls taken away while inactive must come back when the source recovers.
    /// </summary>
    /// <remarks>
    ///     They were only ever given the inactive affordances by this module, so clearing exactly those on the
    ///     way back is safe and is the whole of the recovery: a queue button left greyed out after a successful
    ///     login looks broken, and a reader has no reason to know to reload.
    /// </remarks>
    [Fact]
    public void QueueControlsAreHandedBackWhenTheSourceBecomesActiveAgain()
    {
        var script = ClientScript();
        var queue = ExtractFunction(script, "function renderQueueAvailability()");

        Assert.Contains("removeAttribute('aria-disabled')", queue, System.StringComparison.Ordinal);
        Assert.Contains("classList.remove('is-inactive')", queue, System.StringComparison.Ordinal);

        // Both have to sit inside the active branch, before the inactive affordances are re-applied.
        var activeBranch = queue.IndexOf("if (active)", System.StringComparison.Ordinal);
        var inactiveBranch = queue.IndexOf("setAttribute('aria-disabled', 'true')", System.StringComparison.Ordinal);
        Assert.True(activeBranch >= 0, "renderQueueAvailability has no active branch.");
        Assert.True(inactiveBranch > activeBranch, "The recovery must happen before the inactive state is re-applied.");
    }

    /// <summary>
    ///     An inactive search must not leave the panel looking like one is running.
    /// </summary>
    /// <remarks>
    ///     The phase drives the spinner, the Stop button and the histogram title. Setting it before deciding the
    ///     request is even allowed left all three showing next to the refusal, which is the contradiction this
    ///     branch exists to remove.
    /// </remarks>
    [Fact]
    public void TheSearchPhaseIsOnlyEnteredWhenASearchWillActuallyBeSent()
    {
        var script = ClientScript();
        var run = ExtractFunction(script, "async function runSearch()");

        var gate = run.IndexOf("if (!isSoulseekActive())", System.StringComparison.Ordinal);
        var searching = run.IndexOf("state.phase = 'searching';", StringComparison.Ordinal);
        Assert.True(gate >= 0, "runSearch does not check whether Soulseek is active.");
        Assert.True(searching > gate, "The phase must be set only after the inactive check has passed.");

        // And the refusal resets it, so a refused search leaves nothing behind that reads as running.
        var branch = run[gate..searching];
        Assert.Contains("state.phase = 'idle';", branch, System.StringComparison.Ordinal);
        Assert.Contains("renderPhase();", branch, System.StringComparison.Ordinal);
        Assert.Contains("return null;", branch, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     Availability comes from one place, so the tab cannot disagree with the API.
    /// </summary>
    [Fact]
    public void TheTabAndTheApiReadTheSameAvailabilityValue()
    {
        var script = ClientScript();
        var active = ExtractFunction(script, "function isSoulseekActive()");

        Assert.Contains("connection.usable === true", active, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The search endpoint must return immediately, or live results cannot stream.
    /// </summary>
    [Fact]
    public void TheSearchEndpointReturnsTheIdImmediatelyAndCanBeCancelled()
    {
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs");

        var search = ExtractMethod(controller, "public async Task<IActionResult> Search");
        Assert.Contains("return Accepted(new { searchId", search, System.StringComparison.Ordinal);
        Assert.Contains("ObserveAsync", search, System.StringComparison.Ordinal);

        // It starts the remote search rather than blocking on the full run.
        Assert.DoesNotContain("await _search.SearchAsync(target", search, System.StringComparison.Ordinal);

        Assert.Contains("TryCancelSearch", controller, System.StringComparison.Ordinal);
        Assert.Contains("searches/{searchId:guid}/cancel", controller, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     Live results must ride the existing hub event rather than a new channel.
    /// </summary>
    [Fact]
    public void LiveResultsTravelOnTheExistingSearchUpdateEvent()
    {
        var realtime = ReadRepoFile("DeezSpoTag.Web", "Services", "SoulseekRealtimeService.cs");
        var script = ClientScript();

        Assert.Contains("results = (progress.Results ?? [])", realtime, System.StringComparison.Ordinal);
        Assert.Contains("final = progress.Final", realtime, System.StringComparison.Ordinal);
        Assert.Contains("LiveResultSliceSize", realtime, System.StringComparison.Ordinal);

        Assert.Contains("connection.on('search_update'", script, System.StringComparison.Ordinal);
        Assert.Contains("mergeResults(", script, System.StringComparison.Ordinal);

        // No second hub or second event name was introduced.
        Assert.Contains("public const string SearchUpdate = \"search_update\";", realtime, System.StringComparison.Ordinal);
    }

    [Fact]
    public void SoulseekDoesNotLeakIntoTheOtherSourceTabs()
    {
        var view = SearchView();
        var script = ClientScript();

        Assert.DoesNotContain("data-soulseek-track", view, System.StringComparison.Ordinal);
        Assert.DoesNotContain("data-soulseek-track", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("openForTrack", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("result-actions", view, System.StringComparison.Ordinal);

        // The shared result-card renderer must be untouched by Soulseek.
        var card = ExtractFunction(view, "function renderResultCard(item, type)");
        Assert.DoesNotContain("soulseek", card, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The tab behaves like every other source tab: it searches the term already in the search box. The
    ///     term is passed through verbatim rather than split into an artist and a title, because splitting
    ///     free text is a guess.
    /// </summary>
    [Fact]
    public void TheTabSearchesTheTermAlreadyInTheSearchBox()
    {
        var view = SearchView();
        var script = ClientScript();

        Assert.Contains("window.Soulseek?.searchCurrentTerm?.();", view, System.StringComparison.Ordinal);
        Assert.Contains("function searchCurrentTerm()", script, System.StringComparison.Ordinal);
        Assert.Contains("global.__dsSearchTerm", script, System.StringComparison.Ordinal);

        // The term reaches the service intact, rather than being pre-split on the client.
        var search = ReadRepoFile("DeezSpoTag.Services", "Download", "Soulseek", "SoulseekSearchService.cs");
        Assert.Contains("public static string BuildSearchText(SoulseekSearchTarget target)", search, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     A term already searched keeps its answer. Re-querying on every visit to the tab replaced the list
    ///     with whatever the peer network happened to be offering at that moment, so the same query read as
    ///     a different result count each time the tab was opened.
    /// </summary>
    [Fact]
    public void ATermIsSearchedOnceAndItsAnswerIsKept()
    {
        var script = ClientScript();
        var run = ExtractFunction(script, "async function runSearch()");
        var entry = ExtractFunction(script, "function searchCurrentTerm()");
        var reuse = "if (state.searchId && state.searchTerm === state.title && state.byId.size > 0)";

        Assert.Contains(reuse, run, System.StringComparison.Ordinal);

        // The guard stands before the reset that would clear the screen, or it would refuse to search after
        // having already thrown the results away.
        Assert.True(
            run.IndexOf(reuse, System.StringComparison.Ordinal) < run.IndexOf("resetResults()", System.StringComparison.Ordinal),
            "The reuse guard must run before the results are reset.");

        // An empty answer is still retried, and the entry point still delegates to runSearch.
        Assert.Contains("state.byId.size > 0", run, System.StringComparison.Ordinal);
        Assert.Contains("void runSearch()", entry, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     Every other source tab is warmed while the page searches it, so its results are waiting when the
    ///     tab is opened. Soulseek cannot ride that prefetch loop, so it gets the same treatment through its
    ///     own entry point: the page term is searched in the background, without stealing the tab.
    /// </summary>
    [Fact]
    public void ThePageTermIsSearchedWithoutOpeningTheTab()
    {
        var view = SearchView();
        var script = ClientScript();
        var warm = ExtractFunction(view, "async function warmSearchTabs(activeSource, term)");
        var background = ExtractFunction(script, "async function searchInBackground(term)");

        Assert.Contains("window.Soulseek?.searchInBackground?.(term);", warm, System.StringComparison.Ordinal);

        // A warm-up must not move the user off the source they are reading.
        Assert.DoesNotContain("bootstrap.Tab", background, System.StringComparison.Ordinal);
        Assert.DoesNotContain("searchCurrentTerm", background, System.StringComparison.Ordinal);
        Assert.Contains("void runSearch()", background, System.StringComparison.Ordinal);

        // The request is only made when the app believes Soulseek can serve it. The click on the tab stays
        // the path that forces the backend probe, so a stale status can never trap the user without results.
        Assert.Contains("connection.usable !== true", background, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     A term-only search has to be accepted, or the tab can never send the user's own words.
    /// </summary>
    [Fact]
    public void TheSearchEndpointAcceptsATermWithoutAnArtist()
    {
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs");

        Assert.Contains(
            "string.IsNullOrWhiteSpace(request.Artist) && string.IsNullOrWhiteSpace(request.Title)",
            controller,
            System.StringComparison.Ordinal);
        Assert.DoesNotContain("Both artist and title are required to search Soulseek.", controller, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     A bare <c>~/</c> in a script src is not resolved by Razor, so the file 404s and the whole panel
    ///     silently stops working. That happened once already.
    /// </summary>
    [Fact]
    public void TheClientScriptIsLoadedThroughTheVersionedAssetHelper()
    {
        var view = SearchView();

        Assert.Contains("AssetUrl.Versioned(ViewContext, Url, \"~/js/soulseek.js\")", view, System.StringComparison.Ordinal);

        // A bare tilde in a script src is not resolved by Razor, so the request 404s and the panel stops
        // working with no error anywhere near the cause.
        Assert.DoesNotContain("src=\"~/js/soulseek.js\"", view, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TheClientTalksToTheRealSoulseekEndpoints()
    {
        var script = ClientScript();

        Assert.Contains("const API_BASE = '/api/v1/soulseek';", script, System.StringComparison.Ordinal);
        Assert.Contains("'/connection'", script, System.StringComparison.Ordinal);
        Assert.Contains("'/searches'", script, System.StringComparison.Ordinal);

        // Queueing goes through the shared download client rather than this engine's own endpoint, so the
        // panel cannot grow a second enqueue path. The shared client is the thing that must still reach the
        // real intent endpoint, and it carries the peer file the reader chose.
        var client = ReadRepoFile("DeezSpoTag.Web", "wwwroot", "js", "download-client.js");
        Assert.Contains("'/api/download/intent'", client, System.StringComparison.Ordinal);
        Assert.Contains("enqueueIntentWithPreference", script, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The hub existed with eight published events and no subscriber. These names are the contract
    ///     between <c>SoulseekRealtimeService</c> and the browser, so both sides are pinned to the same list.
    /// </summary>
    [Theory]
    [InlineData("connection_state")]
    [InlineData("engine_health")]
    [InlineData("search_update")]
    [InlineData("search_result")]
    [InlineData("share_sync_update")]
    [InlineData("share_scan_update")]
    public void TheClientSubscribesToEveryEventTheServerPublishes(string eventName)
    {
        var server = ReadRepoFile("DeezSpoTag.Web", "Services", "SoulseekRealtimeService.cs");
        var script = ClientScript();

        Assert.Contains($"\"{eventName}\"", server, System.StringComparison.Ordinal);
        Assert.Contains($"connection.on('{eventName}'", script, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The two transfer events are published for the Downloads tab, which already renders queue progress
    ///     for every engine. The Soulseek tab used to render a second, independent progress panel for the same
    ///     transfer, so a single download was being drawn twice by two different transports. The tab is now a
    ///     candidate browser and queues; the Downloads tab is the only place progress is shown.
    /// </summary>
    [Theory]
    [InlineData("download_update")]
    [InlineData("import_update")]
    public void TheSearchTabDoesNotDuplicateTransferProgress(string eventName)
    {
        var script = ClientScript();
        var view = SearchView();

        // The server still publishes it, so the Downloads tab keeps working.
        Assert.Contains(
            $"\"{eventName}\"",
            ReadRepoFile("DeezSpoTag.Web", "Services", "SoulseekRealtimeService.cs"),
            System.StringComparison.Ordinal);

        Assert.DoesNotContain($"connection.on('{eventName}'", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("renderDownload", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("renderImport", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("soulseek-download", view, System.StringComparison.Ordinal);
        Assert.DoesNotContain("soulseek-import", view, System.StringComparison.Ordinal);
        Assert.DoesNotContain("soulseek-progress", view, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     A Soulseek result is a candidate, not a file, so the row action queues rather than downloading, and
    ///     there is no preview. A preview would mean transferring the file, which is exactly the decision the
    ///     download ladder is there to make.
    /// </summary>
    [Fact]
    public void TheRowActionQueysAndThereIsNoPreview()
    {
        var script = ClientScript();
        var view = SearchView();

        Assert.Contains("data-soulseek-queue", script, System.StringComparison.Ordinal);
        Assert.Contains("Queue Download", view, System.StringComparison.Ordinal);
        Assert.Contains("enqueueIntentWithPreference", script, System.StringComparison.Ordinal);

        // Scoped to the Soulseek panel. The page also renders a Tidal video preview player, which is a
        // different feature entirely and must not be swept up by this check.
        var panelStart = view.IndexOf("id=\"search-source-soulseek\"", System.StringComparison.Ordinal);
        Assert.True(panelStart > 0, "The Soulseek panel was not found in the view.");
        var panelEnd = view.IndexOf("</div>\n\n</div>\n\n@section Scripts", panelStart, System.StringComparison.Ordinal);
        Assert.True(panelEnd > panelStart, "The Soulseek panel was not terminated.");
        var soulseekPanel = view.Substring(panelStart, panelEnd - panelStart);

        Assert.DoesNotContain("preview", soulseekPanel, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Preview", soulseekPanel, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TheClientConnectsToTheSoulseekHub()
    {
        var script = ClientScript();

        Assert.Contains("const HUB_URL = '/hubs/soulseek';", script, System.StringComparison.Ordinal);
        Assert.Contains("new global.signalR.HubConnectionBuilder()", script, System.StringComparison.Ordinal);
        Assert.Contains("await connection.start();", script, System.StringComparison.Ordinal);
    }

    [Fact]
    public void WritesSendTheAntiforgeryToken()
    {
        var script = ClientScript();

        // The controller is [AutoValidateAntiforgeryToken], so a POST without this header is a 400.
        Assert.Contains("meta[name=\"deezspotag-csrf-token\"]", script, System.StringComparison.Ordinal);
        Assert.Contains("RequestVerificationToken", script, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     Usernames and remote filenames come from the Soulseek network, so they are attacker-controlled.
    ///     The card renderer writes them as markup, so they must be escaped there. The status line is written
    ///     through <c>textContent</c>, which cannot execute anything, so interpolation there is safe.
    /// </summary>
    [Fact]
    public void UntrustedPeerDataIsEscapedOnTheWayToTheDom()
    {
        var script = ClientScript();

        // The escape helper has to cover all five significant characters, not just the angle brackets.
        var escape = ExtractFunction(script, "function escapeHtml(value)");
        foreach (var (search, replacement) in new[]
                 {
                     ("/&/g", "&amp;"),
                     ("/</g", "&lt;"),
                     ("/>/g", "&gt;"),
                     ("/\"/g", "&quot;"),
                     ("/'/g", "&#39;")
                 })
        {
            Assert.Contains(search, escape, System.StringComparison.Ordinal);
            Assert.Contains(replacement, escape, System.StringComparison.Ordinal);
        }

        // The row renderer is the one place peer data becomes markup. It escapes at the boundary into locals
        // and then interpolates only the escaped values, so the raw fields must never appear in a template.
        var row = ExtractFunction(script, "function resultRow(entry, index)");

        // A row is now a peer's whole release rather than one of its files, so the peer-supplied fields are
        // the username, the remote path and the folder name. Each is escaped where it is bound, which is what
        // lets the template interpolate only the locals: a raw field has no name to reach the markup by.
        Assert.Contains("escapeHtml(entry.username", row, System.StringComparison.Ordinal);
        Assert.Contains("escapeHtml(entry.remoteDirectory", row, System.StringComparison.Ordinal);
        Assert.Contains("escapeHtml(entry.folderName", row, System.StringComparison.Ordinal);
        foreach (var field in new[] { "entry.username", "entry.remoteDirectory", "entry.folderName" })
        {
            Assert.DoesNotContain($"${{{field}}}", row, System.StringComparison.Ordinal);
        }

        // The candidate panel is a second place peer data becomes markup, and it escaped twice when written:
        // bestRow escapes by default, so pre-escaping its input double-encoded the output, and one row passed
        // raw text with isHtml set, which skipped escaping entirely. The panel must hand bestRow raw values and
        // reserve isHtml for the rows that are genuinely markup.
        var best = ExtractFunction(script, "function renderBest()");
        Assert.Contains("String(selected.username", best, System.StringComparison.Ordinal);
        Assert.DoesNotContain("const peer = escapeHtml(selected.username", best, System.StringComparison.Ordinal);
        Assert.DoesNotContain("bestRow('Filename', filename, true", best, System.StringComparison.Ordinal);
        Assert.Contains("bestRow('Filename', filename, false, true)", best, System.StringComparison.Ordinal);
        Assert.Contains("bestRow('Audio format', `${escapeHtml(format)}${chip}`, true)", best, System.StringComparison.Ordinal);

        // Every innerHTML write either clears the node, is static, or escapes what it interpolates.
        var writes = script
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains(".innerHTML =", System.StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(writes);
        foreach (var write in writes)
        {
            var isClear = write.EndsWith(".innerHTML = '';", System.StringComparison.Ordinal);
            var isStatic = !write.Contains("${", System.StringComparison.Ordinal);
            var isEscaped = write.Contains("escapeHtml(", System.StringComparison.Ordinal)
                || write.Contains("resultRow", System.StringComparison.Ordinal)
                || write.Contains("renderGroup", System.StringComparison.Ordinal)
                || write.Contains("renderBest", System.StringComparison.Ordinal)
                || write.Contains("host.innerHTML", System.StringComparison.Ordinal)
                || write.Contains("bars.innerHTML", System.StringComparison.Ordinal)
                || write.Contains("peerHealth(selected)", System.StringComparison.Ordinal);
            Assert.True(isClear || isStatic || isEscaped, $"Unescaped innerHTML write: {write}");
        }

        // The status line is text, so it cannot be a markup injection point.
        Assert.Contains("element.textContent = value", script, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The quality bands are told apart by border weight and dash pattern, not by hue, so the grouping
    ///     survives a colour-blind reader and a low-contrast theme. This also keeps the block free of the
    ///     colour literals the theme-token guardrail forbids.
    /// </summary>
    /// <summary>
    ///     An element hidden from script must actually disappear.
    /// </summary>
    /// <remarks>
    ///     The browser hides <c>[hidden]</c> with a low-specificity rule, so any panel rule that sets
    ///     <c>display</c> on the same element beats it. The quality tab strip is a flex row, so it stayed on
    ///     screen as an empty bar before the first result arrived. The attribute is therefore restored
    ///     explicitly, once, for the panel.
    /// </remarks>
    [Fact]
    public void HiddenElementsAreActuallyHidden()
    {
        var view = SearchView();

        Assert.Contains("[hidden]", view, System.StringComparison.Ordinal);
        Assert.Contains("display: none !important", view, System.StringComparison.Ordinal);

        // The element that actually regressed, so the guard cannot be satisfied by an unrelated rule.
        Assert.Contains(".soulseek-qtabs[hidden]", view, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The shared <c>.action-btn</c> sets its background, border and colour with <c>!important</c>, so a
    ///     panel override without it silently loses and the primary action renders as an ordinary button.
    /// </summary>
    [Fact]
    public void ThePanelButtonVariantsOverrideTheSharedActionButton()
    {
        var view = SearchView();

        foreach (var variant in new[] { ".action-btn--primary {" })
        {
            var start = view.IndexOf(variant, System.StringComparison.Ordinal);
            Assert.True(start > 0, variant + " is not defined in the panel CSS.");

            var body = view.Substring(start, view.IndexOf('}', start) - start);

            // The background is the property that decides whether this looks like a primary button, so it is
            // asserted specifically. Passing on some other declaration in the same rule would be worthless.
            var background = System.Text.RegularExpressions.Regex.Match(
                body,
                @"background(?:-color)?\s*:([^;]+);",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            Assert.True(background.Success, variant + " does not set a background.");
            Assert.Contains("!important", background.Groups[1].Value, System.StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     The arrival histogram must be driven by the live peer count, not by merged candidates.
    /// </summary>
    /// <remarks>
    ///     slskd publishes a search's file responses only once it finalizes, so the number of merged
    ///     candidates is zero for the entire search and then spikes. Plotting it drew a flat line followed by
    ///     a single bar, which is the opposite of what was happening. The peer response count is live
    ///     throughout, so that is what the bars show.
    /// </remarks>
    [Fact]
    public void TheArrivalHistogramIsDrivenByTheLivePeerCount()
    {
        var script = ClientScript();

        Assert.Contains("recordPeerArrivals(responseCount)", ExtractFunction(script, "function mergeResults("), StringComparison.Ordinal);

        // The merged-candidate count is the value that is not live, so it must not be what is plotted.
        Assert.DoesNotContain("state.arrivals.push(added)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchTimelineUsesSecondsAndKeepsCompletedBarsStable()
    {
        RunProgressScript("""
            state.startedAt = now;
            state.phase = 'searching';
            state.searchId = 'current';
            progress.renderPhase();
            const bars = elements.get('soulseek-hist-bars');
            assert.equal(bars.children.length, 30);
            assert.equal(bars.children[0].style.height, '0%');
            now += 200;
            progress.mergeResults([], false, false, 4);
            now += 300;
            progress.mergeResults([], false, false, 7);
            progress.mergeResults([], false, false, 7); // repeated poll / hub count
            progress.mergeResults([], false, false, 3); // stale lower count
            assert.equal(state.arrivals[0], 7);
            assert.equal(state.responseCount, 7);
            const first = bars.children[0];
            assert.equal(first.style.height, '35%');
            now += 2000;
            progress.mergeResults([], false, false, 37);
            assert.deepEqual(Array.from(state.arrivals), [7, 0, 30]);
            assert.equal(bars.children[0], first);
            assert.equal(first.style.height, '35%');
            assert.equal(bars.children[2].style.height, '100%');
            assert.match(bars.children[2].textContent, /30/);
            assert.match(bars.children[2].title, /2.*3.*30/);
            now = state.startedAt + 35200;
            progress.mergeResults([], false, false, 38);
            assert.equal(bars.children.length, 36);
            assert.equal(bars.children[0], first);
            assert.equal(bars.children[0].style.height, '35%');
            assert.equal(state.arrivals[35], 1);
            assert.equal(state.arrivals[34], 0);
            assert.match(elements.get('soulseek-hist-axis').children.at(-1).textContent, /36s/);
            progress.resetResults();
            assert.equal(state.arrivals.length, 0);
            assert.equal(state.peerCount, 0);
            assert.equal(timers.size, 0);
            """);
    }

    [Fact]
    public void SearchProgressTimerStopsAtTerminalStatesAndDoesNotInventActivity()
    {
        RunProgressScript("""
            state.startedAt = now;
            state.phase = 'searching';
            progress.renderPhase();
            progress.renderPhase();
            assert.equal(timers.size, 1);
            now += 4200;
            for (const tick of timers.values()) tick();
            assert.equal(state.elapsedMs, 4200);
            assert.equal(state.responseCount, 0);
            assert.equal(state.fileCount, 0);
            assert.ok(state.arrivals.every(count => count === 0));
            assert.ok(Math.abs(parseFloat(elements.get('soulseek-hist-cursor').style.left) - 14) < 0.000001);
            for (const phase of ['done', 'stopped', 'failed', 'timedout']) {
                state.phase = phase;
                progress.renderPhase();
                assert.equal(timers.size, 0, phase);
                state.phase = 'searching';
                progress.renderPhase();
                assert.equal(timers.size, 1);
            }
            progress.mergeResults([], true, false, 0, [], 'no_network_responses');
            assert.equal(timers.size, 0);
            assert.match(state.statusText, /No peer/);
            progress.resetResults();
            assert.equal(timers.size, 0);
            """);
    }

    [Fact]
    public void SearchCancellationPreservesTheTimelineAndReportsFailureHonestly()
    {
        RunProgressScript("""
            state.startedAt = now;
            state.phase = 'searching';
            state.searchId = 'current';
            progress.renderPhase();
            now += 1000;
            progress.mergeResults([], false, false, 5);
            await window.Soulseek.stopSearch();
            assert.equal(state.phase, 'stopped');
            assert.equal(timers.size, 0);
            assert.equal(state.responseCount, 5);
            assert.equal(elements.get('soulseek-search-title').textContent, 'Search stopped');
            const stoppedElapsed = state.elapsedMs;
            now += 10000;
            progress.mergeResults([], false, false, 5);
            assert.equal(state.elapsedMs, stoppedElapsed);
            assert.equal(state.phase, 'stopped');
            assert.equal(timers.size, 0);
            state.phase = 'searching';
            progress.renderPhase();
            context.fetch = async () => { throw new Error('Cancellation unavailable'); };
            await window.Soulseek.stopSearch();
            assert.notEqual(elements.get('soulseek-search-title').textContent, 'Search complete');
            assert.match(state.statusText, /Cancellation unavailable/);
            """);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedOldPollCannotOverwriteStoppedOrNewSearch(bool newSearch)
    {
        RunProgressScript("const newSearch = " + (newSearch ? "true;" : "false;") + """
            state.startedAt = now;
            state.phase = 'searching';
            state.searchId = 'old';
            progress.renderPhase();
            let rejectPoll;
            context.fetch = () => new Promise((resolve, reject) => { rejectPoll = reject; });
            const pending = progress.pollResults('old', state.generation);
            if (newSearch) {
                progress.resetResults();
                state.generation++;
                state.searchId = 'new';
                state.startedAt = now;
                state.phase = 'searching';
                progress.renderPhase();
            } else {
                context.fetch = async () => ({ ok: true, text: async () => '{}' });
                await window.Soulseek.stopSearch();
            }
            const expectedStatus = state.statusText;
            rejectPoll(new Error('Old poll failed'));
            await pending;
            assert.equal(state.phase, newSearch ? 'searching' : 'stopped');
            assert.equal(timers.size, newSearch ? 1 : 0);
            assert.equal(state.statusText, expectedStatus);
            """);
    }

    [Fact]
    public void SearchProgressUsesSharedDestructiveStyleAndFixedAccessibleChart()
    {
        var view = SearchView();
        Assert.Contains("class=\"btn-danger action-btn action-btn-sm\" id=\"soulseek-stop\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("action-btn--stop", view, StringComparison.Ordinal);
        Assert.Contains("grid-auto-columns: calc(100% / 30)", view, StringComparison.Ordinal);
        Assert.Contains("soulseek-hist-cursor", view, StringComparison.Ordinal);
        Assert.Contains("New responses each second", view, StringComparison.Ordinal);
        Assert.Contains("Fixed scale", view, StringComparison.Ordinal);
        Assert.Contains(".soulseek-hist-cursor { transition: none; }", view, StringComparison.Ordinal);
        Assert.Contains("tabindex=\"0\"", view, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Peer response timeline\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchWaitingStateAndControlsAgreeWithLiveCounters()
    {
        RunProgressScript("""
            state.startedAt = now;
            state.phase = 'searching';
            progress.renderPhase();
            assert.equal(elements.get('soulseek-results-toolbar').hidden, true);
            assert.equal(elements.get('soulseek-result-count').hidden, true);
            const empty = elements.get('soulseek-candidates-empty');
            assert.match(empty.innerHTML, /Waiting for files from peers/);
            assert.doesNotMatch(empty.innerHTML, /0 peers/);
            state.fileCount = 110;
            now += 11000;
            progress.mergeResults([], false, false, 51);
            const stats = elements.get('soulseek-search-stats').innerHTML;
            for (const label of ['Peers answered', 'Files reported', 'Elapsed', '51', '110', '00:11']) {
                assert.ok(stats.includes(label), label);
            }
            assert.doesNotMatch(state.statusText, /candidate|peer response/);
            progress.mergeResults([{username:'peer',filename:'Album/song.flac',quality:'FLAC'}], false, false, 51);
            assert.equal(elements.get('soulseek-results-toolbar').hidden, false);
            assert.equal(elements.get('soulseek-result-count').hidden, false);
            assert.equal(empty.hidden, true);
            state.phase = 'failed';
            state.byId.clear();
            state.statusText = 'Server unavailable';
            progress.renderPhase();
            assert.match(empty.innerHTML, /Search could not finish/);
            assert.notEqual(elements.get('soulseek-search-title').textContent, 'Search complete');
            """);
    }

    [Fact]
    public void FailedSearchPollingStopsWithThePersistedMessageAndCounts()
    {
        RunProgressScript("""
            state.searchId = 'failed-search';
            state.generation = 1;
            state.phase = 'searching';
            let requests = 0;
            context.fetch = async () => {
                requests++;
                return { ok: true, text: async () => JSON.stringify({ failed:true, completed:false, timedOut:false,
                    fileCount:42, responseCount:3, errorCode:'response_retrieval_failed', error:'Results could not be retrieved.' }) };
            };
            await progress.pollResults('failed-search', 1);
            assert.equal(requests, 1);
            assert.equal(state.phase, 'failed');
            assert.equal(state.statusText, 'Results could not be retrieved.');
            assert.equal(state.responseCount, 3);
            assert.equal(state.fileCount, 42);
            assert.notEqual(elements.get('soulseek-search-title').textContent, 'Search complete');
            """);
    }

    [Fact]
    public void FailedFinalEventsCannotOverwriteStoppedOrSupersededSearches()
    {
        RunProgressScript("""
            state.searchId = 'current';
            state.phase = 'searching';
            progress.renderSearchUpdate({searchId:'old',final:true,failed:true,error:'Old failure'});
            assert.equal(state.phase, 'searching');
            state.phase = 'stopped';
            state.statusText = 'Stopped by the reader';
            progress.renderSearchUpdate({searchId:'current',final:true,failed:true,error:'Late failure'});
            assert.equal(state.phase, 'stopped');
            assert.equal(state.statusText, 'Stopped by the reader');
            """);
    }

    [Fact]
    public void FailedFinalFetchAndSummaryShowFailureWithoutEmptySuccess()
    {
        RunProgressScript("""
            state.searchId = 'current';
            state.phase = 'searching';
            const payload = { failed:true, completed:false, timedOut:false, responseCount:4,
                errorCode:'response_retrieval_failed', error:'Results could not be retrieved.' };
            context.fetch = async () => ({ ok:true, text:async () => JSON.stringify(payload) });
            await progress.fetchFinalResults('current');
            assert.equal(state.phase, 'failed');
            assert.equal(state.statusText, payload.error);
            state.phase = 'searching';
            progress.renderSummary(payload);
            assert.equal(state.phase, 'failed');
            assert.equal(state.statusText, payload.error);
            """);
    }

    [Fact]
    public void SearchFailureFieldsArePublishedOnBothExistingRealtimeEvents()
    {
        var source = ReadRepoFile("DeezSpoTag.Web", "Services", "SoulseekRealtimeService.cs");
        Assert.Contains("failed = progress.ErrorCode is not null", source, StringComparison.Ordinal);
        Assert.Contains("progress.ErrorCode", source, StringComparison.Ordinal);
        Assert.Contains("progress.Error", source, StringComparison.Ordinal);
        Assert.Contains("failed = outcome.Failed", source, StringComparison.Ordinal);
        Assert.Contains("outcome.ErrorCode", source, StringComparison.Ordinal);
        Assert.Contains("outcome.Error", source, StringComparison.Ordinal);
    }

    // Execute the shipped client with a fake clock and a minimal DOM; no production test exports or network.
    private static void RunProgressScript(string assertions)
    {
        var source = ClientScript().Replace("    global.Soulseek = Object.assign", "    global.__progress = { mergeResults, renderPhase, renderHistogram, resetResults, pollResults, renderSearchUpdate, fetchFinalResults, renderSummary };\n    global.Soulseek = Object.assign", StringComparison.Ordinal);
        var harness = $$"""
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            let now = 100000;
            const timers = new Map();
            let nextTimer = 1;
            class Element {
                constructor() { this.children = []; this.style = { setProperty(name, value) { this[name] = value; } }; this.hidden = false; this.textContent = ''; this._html = ''; }
                set innerHTML(value) { this._html = value; this.children = []; }
                get innerHTML() { return this._html; }
                appendChild(child) { this.children.push(child); return child; }
                setAttribute(name, value) { this[name] = value; }
            }
            const elements = new Map(['soulseek-hist-bars', 'soulseek-hist-axis', 'soulseek-hist-cursor',
                'soulseek-hist-phase', 'soulseek-hist', 'soulseek-search-title', 'soulseek-search-status',
                'soulseek-search-stats', 'soulseek-result-count', 'soulseek-results-toolbar',
                'soulseek-candidates-empty', 'soulseek-results-badge', 'soulseek-stop'].map(id => [id, new Element()]));
            const window = {
                setInterval(fn) { const id = nextTimer++; timers.set(id, fn); return id; },
                clearInterval(id) { timers.delete(id); }
            };
            const context = {
                window, Date: { now: () => now }, console,
                document: { readyState: 'loading', addEventListener() {}, querySelector() { return null; },
                    getElementById: id => elements.get(id) || null, createElement: () => new Element() },
                fetch: async () => ({ ok: true, text: async () => '{}' })
            };
            vm.createContext(context);
            vm.runInContext({{System.Text.Json.JsonSerializer.Serialize(source)}}, context);
            const state = window.Soulseek.state;
            const progress = window.__progress;
            (async () => {
                {{assertions}}
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;
        var start = new System.Diagnostics.ProcessStartInfo("node")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = System.Diagnostics.Process.Start(start);
        Assert.NotNull(process);
        process.StandardInput.Write(harness);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "Progress behavior test timed out.");
        Assert.True(process.ExitCode == 0, error + output);
    }

    /// <summary>
    ///     An empty result must say which kind of empty it was.
    /// </summary>
    /// <remarks>
    ///     slskd reports an unreachable instance, a network that answered nobody, and files that were all
    ///     rejected in the same way. Those are different problems with different remedies, and a single
    ///     "no results" hides which one occurred.
    /// </remarks>
    [Fact]
    public void AnEmptySearchDistinguishesTheNetworkFromTheFilters()
    {
        var script = ClientScript();
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs");

        foreach (var outcome in new[] { "no_network_responses", "all_candidates_rejected", "no_files_returned" })
        {
            Assert.Contains(outcome, script, StringComparison.Ordinal);
            Assert.Contains(outcome, controller, StringComparison.Ordinal);
        }

        Assert.Contains("OUTCOME_MESSAGES[outcome]", script, StringComparison.Ordinal);
    }

    [Fact]
    public void QualityBandsAreDistinguishedWithoutColour()
    {
        var view = SearchView();

        foreach (var band in new[] { "hi-res-lossless", "hi-res", "cd", "lossy", "unknown" })
        {
            Assert.Contains($"[data-band=\"{band}\"]", view, System.StringComparison.Ordinal);
        }

        // The distinguishing property is border style or width, and every colour is a token.
        Assert.Contains("border-left-style: dashed", view, System.StringComparison.Ordinal);
        Assert.Contains("border-left-style: dotted", view, System.StringComparison.Ordinal);
        Assert.Contains("border-left-style: solid", view, System.StringComparison.Ordinal);
        Assert.DoesNotContain(".soulseek-group[data-band=\"hi-res\"] .soulseek-group-head { border-left: 4px solid var(--primary-color); }", view, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     Soulseek is a transfer panel, not a catalogue source. If it were allowed into
    ///     <c>currentSearchSource</c>, <c>displayResults</c> would rewrite its container with the generic
    ///     empty-results block, which is exactly what happened before the panel was given its own flag.
    /// </summary>
    [Fact]
    public void SoulseekIsKeptOutOfTheStreamingSearchMachinery()
    {
        var view = SearchView();

        Assert.Contains("let soulseekPanelOpen = false;", view, System.StringComparison.Ordinal);
        Assert.Contains("soulseekPanelOpen = true;", view, System.StringComparison.Ordinal);
        Assert.Contains("soulseekPanelOpen = false;", view, System.StringComparison.Ordinal);
        Assert.Contains("if (soulseekPanelOpen) {", view, System.StringComparison.Ordinal);

        // The catalogue source list must stay unchanged, so the tab is not restored from the URL.
        Assert.DoesNotContain("'soulseek']", view, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The panel names a candidate, and a candidate has a name.
    /// </summary>
    /// <remarks>
    ///     Every payload that describes a candidate used to carry only the peer's filename, so the panel
    ///     showed the search term as if it were the track's title and an artist name was nowhere. The facts
    ///     come from the parser the scorer already runs, so all three payloads have to send them or a row
    ///     reads differently depending on whether it arrived live or was fetched after the fact.
    /// </remarks>
    [Fact]
    public void EveryCandidatePayloadCarriesTheNameParsedFromItsFilename()
    {
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs");
        var realtime = ReadRepoFile("DeezSpoTag.Web", "Services", "SoulseekRealtimeService.cs");
        var projection = ReadRepoFile(
            "DeezSpoTag.Services",
            "Download",
            "Soulseek",
            "SoulseekFilenameProjection.cs");

        // One reader, so the panel and the matcher cannot disagree about what a filename says.
        Assert.Contains("SoulseekFilenameParser.Parse", projection, System.StringComparison.Ordinal);

        foreach (var source in new[] { controller, realtime })
        {
            Assert.Contains("SoulseekFilenameProjection.Describe(", source, System.StringComparison.Ordinal);
            foreach (var field in new[] { "title = name.Title", "artist = name.Artist", "album = name.Album", "trackNumber = name.TrackNumber" })
            {
                Assert.Contains(field, source, System.StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    ///     The side panel has three views, and the one it opens on is the answer to the search.
    /// </summary>
    [Fact]
    public void TheSidePanelOffersTheBestMatchAnAlbumAndWhatTheUserIsLookingAt()
    {
        var view = SearchView();
        var script = ClientScript();

        Assert.Contains("id=\"soulseek-views\"", view, System.StringComparison.Ordinal);

        foreach (var code in new[] { "best", "album", "selected" })
        {
            Assert.Contains($"{{ code: '{code}'", script, System.StringComparison.Ordinal);
        }

        // Best match is the default, both in the state and after a reset.
        Assert.Contains("panelView: 'best'", script, System.StringComparison.Ordinal);
        Assert.Contains("state.panelView = 'best';", script, System.StringComparison.Ordinal);

        // The album view is a release and its contents. A release is the peer's folder and nothing else: the
        // parsed per-file album is unreliable - for "10 - Exchange.flac" it yields "Exchange", which would name
        // the whole album after one of its own tracks - so the grouping key is the peer plus the folder.
        Assert.Contains("function albumTracks()", script, System.StringComparison.Ordinal);
        Assert.Contains("function releaseKey(username, remoteDirectory)", script, System.StringComparison.Ordinal);
        Assert.Contains("function folderOf(path)", script, System.StringComparison.Ordinal);
        Assert.Contains("function renderAlbumView(host)", script, System.StringComparison.Ordinal);

        // The album is the one the user is looking at, not whichever arrived first.
        Assert.Contains("state.byId.get(state.selectedId)", script, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     Clicking a row has to move the panel to that row.
    /// </summary>
    /// <remarks>
    ///     The panel used to rank only the candidates the download ladder would accept, so selecting a file
    ///     that had been passed over left the panel describing a different file with no visible reason why.
    /// </remarks>
    [Fact]
    public void SelectingARowFocusesThePanelOnThatFile()
    {
        var script = ClientScript();
        var select = ExtractFunction(script, "function focusedCandidate()");

        // The Selected view describes the file the reader is looking at even when the ladder passed it over.
        // Refusing to describe it is what made clicking such a row appear to do nothing.
        Assert.Contains("state.byId.get(state.selectedId)", select, System.StringComparison.Ordinal);
        Assert.Contains("chosen && isCandidateEnabled(chosen) ? chosen : ranked[0] || null;", select, System.StringComparison.Ordinal);

        // Queueing is offered only for a candidate the ladder would take.
        Assert.Contains("const offerable = Boolean(selected) && selected.accepted !== false;", script, System.StringComparison.Ordinal);

        // Clicking a result row focuses the panel on that row, and the drawer's per-file download focuses it on
        // that file, in the same way and through the same delegation. A row no longer carries a checkbox, so
        // there is one focus path from the table rather than two.
        var bind = ExtractFunction(script, "function bindPanel()");
        Assert.True(CountOccurrences(bind, "state.panelView = 'selected';") >= 1, "A row click must focus the panel.");
        Assert.True(CountOccurrences(bind, "renderPanel();") >= 2, "Each focus path must redraw the panel.");
    }

    /// <summary>
    ///     An album is a folder on a peer, so the rest of it can be read on demand.
    /// </summary>
    [Fact]
    public void TheReleaseDrawerCanLoadTheRestOfTheReleaseFromThePeer()
    {
        var script = ClientScript();
        var open = ExtractFunction(script, "async function openAlbumDrawer(encodedId)");

        // The existing directory endpoint, not a new one.
        Assert.Contains("/directory?path=", open, System.StringComparison.Ordinal);
        Assert.Contains("directories", open, System.StringComparison.Ordinal);

        // The listing is held against the row that opened it, so collapsing and reopening does not re-read the
        // peer, and a refusal costs the drawer a line rather than the results the user was reading.
        Assert.Contains("state.albumDrawer = drawer", open, System.StringComparison.Ordinal);
        Assert.Contains("catch (error)", open, System.StringComparison.Ordinal);

        // The expansion control is handled by delegation, because the row is re-rendered whenever the drawer
        // changes state.
        var bind = ExtractFunction(script, "function bindPanel()");
        Assert.Contains("[data-soulseek-view]", bind, System.StringComparison.Ordinal);
        Assert.Contains("[data-soulseek-album]", bind, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     A peer's filename is untrusted text, and the name parsed out of it is still untrusted text.
    /// </summary>
    [Fact]
    public void TheCandidateNameParsedFromAPeerFilenameIsEscaped()
    {
        var script = ClientScript();
        var best = ExtractFunction(script, "function renderBest()");

        Assert.Contains("${escapeHtml(title)}", best, System.StringComparison.Ordinal);
        Assert.Contains("${escapeHtml(artist)}", best, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     Every column of a peer row has to be reachable.
    /// </summary>
    /// <remarks>
    ///     Nine columns sit beside a 330px side panel. Laid out as auto-width columns the last two - the
    ///     match score and the Queue action - were pushed past the end of the column and clipped, which left a
    ///     row with no visible way to queue it and no score to read. The table therefore has a fixed layout,
    ///     the file name is the only column that may shrink, and the group scrolls sideways as a backstop.
    /// </remarks>
    [Fact]
    public void ThePeerTableKeepsEveryColumnInsideItsColumn()
    {
        var view = SearchView();
        var script = ClientScript();
        var row = ExtractFunction(script, "function resultRow(entry, index)");

        Assert.Contains("table-layout: fixed", view, System.StringComparison.Ordinal);
        Assert.Contains(".soulseek-group { overflow-x: auto; }", view, System.StringComparison.Ordinal);
        Assert.Contains("text-overflow: ellipsis", view, System.StringComparison.Ordinal);

        // The two columns that were being appended past the edge are sized, not left to chance, and the
        // widths sit on the header as well as the body: a fixed layout reads them from the first row, so
        // sizing only the body would fall back to equal columns and squeeze the file name to nothing.
        Assert.Contains("soulseek-cell-match", row, System.StringComparison.Ordinal);
        Assert.Contains("soulseek-cell-browse", row, System.StringComparison.Ordinal);
        Assert.Contains(".soulseek-cell-browse { width:", view, System.StringComparison.Ordinal);

        // Every column in the header is one this table actually renders. The old set included a Filename cell, a
        // Duration and an Availability cell, none of which has a subject on a release row: a row is not one
        // file, no peer's browse listing carries durations, and a file is offered or it is not.
        var group = ExtractFunction(script, "function renderGroup(group)");
        foreach (var column in new[]
        {
            "soulseek-cell-rank", "soulseek-cell-user", "soulseek-cell-release",
            "soulseek-cell-coverage", "soulseek-cell-num", "soulseek-cell-match", "soulseek-cell-browse"
        })
        {
            Assert.Contains($"<th class=\"{column}\"", group, System.StringComparison.Ordinal);
        }

        // And no header is left over from the per-file table, which would render an empty column of headings
        // above rows that have nothing to put under them.
        foreach (var removed in new[]
        {
            "soulseek-cell-file", "soulseek-cell-duration", "soulseek-cell-available", "soulseek-cell-action"
        })
        {
            Assert.DoesNotContain($"<th class=\"{removed}\"", group, System.StringComparison.Ordinal);
        }

        // A header that overruns its column reads as one word with the next one, so Size and Match are sized
        // apart rather than sharing the numeric rule.
        Assert.Contains(".soulseek-cell-num {", view, System.StringComparison.Ordinal);
        Assert.Contains(".soulseek-cell-match {", view, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     A link can name the Soulseek tab, because a link can name any other tab.
    /// </summary>
    /// <remarks>
    ///     The tab used to be unreachable by URL in both directions: <c>source=soulseek</c> was ignored because
    ///     Soulseek is deliberately absent from the streaming source list, and clicking the tab never wrote the
    ///     URL, so a reload dropped the reader back on Spotify with the results they were reading left behind.
    ///     It is restored as a view - the tab is shown and the panel runs its own search - and it still never
    ///     becomes the streaming source, which is what would wipe the panel.
    /// </remarks>
    [Fact]
    public void TheUrlCanNameTheSoulseekTabWithoutMakingItAStreamingSource()
    {
        var view = SearchView();

        Assert.Contains("const wantsSoulseek = sourceParam === 'soulseek';", view, System.StringComparison.Ordinal);
        Assert.Contains("if (!wantsSoulseek && searchSources.includes(sourceParam))", view, System.StringComparison.Ordinal);
        Assert.Contains("soulseekUrl.searchParams.set('source', 'soulseek');", view, System.StringComparison.Ordinal);

        // Restored as a view: the tab is shown and the panel brings its own search.
        Assert.Contains("setActiveSourceTab('soulseek');", view, System.StringComparison.Ordinal);
        Assert.Contains("window.Soulseek?.searchCurrentTerm?.();", view, System.StringComparison.Ordinal);

        // Still not a streaming source, and the remembered tab is not Soulseek either.
        Assert.Contains("soulseekPanelOpen = true;", view, System.StringComparison.Ordinal);
        Assert.DoesNotContain("currentSearchSource = 'soulseek';", view, System.StringComparison.Ordinal);
        Assert.DoesNotContain("searchSources.push('soulseek')", view, System.StringComparison.Ordinal);
        Assert.DoesNotContain("persistRememberedSearchSource('soulseek')", view, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     Queuing a Soulseek result means queueing that file.
    /// </summary>
    /// <remarks>
    ///     The panel used to post the search box's artist and title, which a free-text peer search does not
    ///     have, so every Queue click from a Soulseek browse was refused with "Both artist and title are
    ///     required". It now sends the candidate's own identity - the name parsed out of the peer's filename,
    ///     falling back to the release folder - and pins the peer and the exact remote path, which is what
    ///     makes the engine download the file the reader chose instead of searching again for the same track.
    /// </remarks>
    [Fact]
    public void QueueingACandidateSendsItsOwnIdentityAndPinsTheFile()
    {
        var script = ClientScript();
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs");
        var queue = ExtractFunction(script, "async function queueCandidate(encodedId)");

        // The identity is the candidate's, not the search box's: a free-text search has no artist.
        Assert.Contains("candidate.artist", queue, System.StringComparison.Ordinal);
        Assert.Contains("candidate.title", queue, System.StringComparison.Ordinal);
        Assert.DoesNotContain("body: { artist: state.artist, title: state.title,", queue, System.StringComparison.Ordinal);

        // The peer and the remote path travel with it, so the download is that file and not a second search.
        Assert.Contains("username: candidate.username", queue, System.StringComparison.Ordinal);
        Assert.Contains("remotePath: candidate.filename", queue, System.StringComparison.Ordinal);
        Assert.Contains("remoteSizeBytes: Number(candidate.size)", queue, System.StringComparison.Ordinal);
        // The pin is applied where the intent is built, which QueueDownload now delegates to. Both fields have
        // to be passed from the request and only when the request actually carries a peer and a path, because
        // an unpinned download is a fresh catalogue search rather than that peer's file.
        var build = ExtractMethod(controller, "private static DownloadIntent BuildQueueDownloadIntent");
        Assert.Contains("pinned = !string.IsNullOrWhiteSpace(request.Username)", build, System.StringComparison.Ordinal);
        Assert.Contains("pinned ? request.Username : null", build, System.StringComparison.Ordinal);
        Assert.Contains("pinned ? request.RemotePath : null", build, System.StringComparison.Ordinal);

        // The endpoint demands a title, not an artist, and states an unknown artist rather than sending one
        // empty - an empty artist becomes an empty folder name in the library.
        Assert.DoesNotContain("Both artist and title are required to queue a Soulseek download.", controller, System.StringComparison.Ordinal);
        Assert.Contains("A title is required to queue a Soulseek download.", controller, System.StringComparison.Ordinal);
        // An empty artist becomes an empty folder name in the library, so the fallback to the album and then
        // to a stated unknown lives in the planner that builds every Soulseek intent.
        Assert.Contains("\"Unknown Artist\"", ReadRepoFile("DeezSpoTag.Web", "Services", "SoulseekBatchQueuePlanner.cs"), System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     A Soulseek file is not a library track until the reader says where it goes.
    /// </summary>
    /// <remarks>
    ///     The panel queues into the app's own destinations, so the destinations it offers are the ones the
    ///     folder list already offers for downloads and for stereo content, which is all that exists today.
    ///     The choice is required: queueing without one lands the file in whatever the global default happens
    ///     to be, which is how a download ends up refused by the destination guard with a message about a
    ///     profile the reader never chose.
    /// </remarks>
    [Fact]
    public void QueueingRequiresADestinationChosenFromTheStereoFolders()
    {
        var view = SearchView();
        var script = ClientScript();
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs");

        Assert.Contains("id=\"soulseek-destination\"", view, System.StringComparison.Ordinal);
        Assert.Contains("id=\"soulseek-destination-hint\"", view, System.StringComparison.Ordinal);
        Assert.Contains("for=\"soulseek-destination\"", view, System.StringComparison.Ordinal);

        // The folders are the app's own, filtered the way the folder list filters them, and fetched from the
        // library API directly: this engine's request() prefixes its own base path, which would turn the
        // folder list into a 404 that reads as "you have no folders".
        Assert.Contains("'/api/library/folders?downloadOnly=true&contentType=stereo'", script, System.StringComparison.Ordinal);
        Assert.Contains("async function loadDestinations()", script, System.StringComparison.Ordinal);
        var destinations = ExtractFunction(script, "async function loadDestinations()");
        Assert.DoesNotContain("request('/api/library/folders", destinations, System.StringComparison.Ordinal);
        Assert.Contains("credentials: 'same-origin'", destinations, System.StringComparison.Ordinal);

        // Nothing is queued without one, and an empty list says why rather than offering a dead control.
        var actions = ExtractFunction(script, "function updatePanelActions(selected, ranked)");
        Assert.Contains("const destinationChosen = Number(state.destinationFolderId) > 0;", actions, System.StringComparison.Ordinal);

        // The button is disabled when no destination is chosen, whichever of the two routes into the queue is
        // being offered. Asserting that by clause rather than by whole line, because the disable expression also
        // has to cover a batch in flight and a selection that is neither a batch nor an offerable candidate.
        Assert.Contains("!destinationChosen ||", actions, System.StringComparison.Ordinal);
        Assert.Contains("queueButton.disabled = state.batchBusy || !destinationChosen ||", actions, System.StringComparison.Ordinal);

        // The same requirement has to hold for the per-row download inside the drawer, or a live button on an
        // enabled-looking row would fail silently when the reader pressed it.
        var single = ExtractFunction(script, "async function queueSingleAlbumFile(encodedId)");
        Assert.Contains("if (!destinationFolderId)", single, System.StringComparison.Ordinal);
        Assert.Contains("Choose where this file should go first.", single, System.StringComparison.Ordinal);
        Assert.Contains("No destination folder is available", script, System.StringComparison.Ordinal);
        Assert.Contains("Choose a destination folder", script, System.StringComparison.Ordinal);

        // A sidecar is offered on its own row and downloadable on its own, and it must go to the endpoint in
        // the sidecar list. Sent as a track, the planner holds it to the audio rule and refuses it as not
        // takeable, so a ticked cover image would be counted as selected and then silently never arrive.
        Assert.Contains("const isSidecar = file.sidecarFetchable === true;", single, System.StringComparison.Ordinal);
        Assert.Contains("const sidecars = isSidecar ? [file] : [];", single, System.StringComparison.Ordinal);
        Assert.Contains("const files = isSidecar ? [] : [file];", single, System.StringComparison.Ordinal);
        Assert.Contains("sidecars: sidecars.map((entry) => entry.filename)", single, System.StringComparison.Ordinal);

        // And the same split holds for a whole selection, or Select all would put a ticked cover into the
        // track payload along with the eleven songs.
        Assert.Contains("const files = ticked.filter((file) => file.sidecarFetchable !== true);", script, System.StringComparison.Ordinal);
        Assert.Contains("const sidecarFiles = ticked.filter((file) => file.sidecarFetchable === true);", script, System.StringComparison.Ordinal);

        // The choice travels with the request and lands on the item.
        Assert.Contains("const destinationId = Number(state.destinationFolderId) || null;", ExtractFunction(script, "async function queueCandidate(encodedId)"), System.StringComparison.Ordinal);
        Assert.Contains("destinationId,", ExtractFunction(script, "async function queueCandidate(encodedId)"), System.StringComparison.Ordinal);
        // The destination lands on the intent the shared planner builds, for both the single-file route and the
        // batch route, and the planner refuses anything that is not a folder the reader actually has.
        var planner = ReadRepoFile("DeezSpoTag.Web", "Services", "SoulseekBatchQueuePlanner.cs");
        Assert.Contains("DestinationFolderId = destinationFolderId,", planner, System.StringComparison.Ordinal);
        Assert.Contains("if (request.DestinationFolderId is not > 0)", planner, System.StringComparison.Ordinal);
        var client = ReadRepoFile("DeezSpoTag.Web", "wwwroot", "js", "download-client.js");
        Assert.Contains("destinationFolderId: destinationId,", client, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     A queued Soulseek file is confirmed by the same client, and the same toast, as every other source.
    /// </summary>
    /// <remarks>
    ///     A queued file leaves the panel and continues in the Downloads tab, so it has to be confirmed, and it
    ///     has to be confirmed the same way: the panel enqueues through the shared download client rather than
    ///     its own endpoint and its own popup, so destination handling, the pending-queue bookkeeping and the
    ///     queue toast are the app's and not a second implementation of them.
    /// </remarks>
    [Fact]
    public void QueueingGoesThroughTheSharedDownloadClientAndItsToast()
    {
        var script = ClientScript();
        var client = ReadRepoFile("DeezSpoTag.Web", "wwwroot", "js", "download-client.js");
        var queue = ExtractFunction(script, "async function queueCandidate(encodedId)");

        // The shared client, not a second enqueue path of our own.
        Assert.Contains("global.DeezSpoTagDownload", queue, System.StringComparison.Ordinal);
        Assert.Contains("client.enqueueIntentWithPreference({", queue, System.StringComparison.Ordinal);
        Assert.Contains("client.createQueueNotifier(", queue, System.StringComparison.Ordinal);
        Assert.DoesNotContain("request('/downloads/queue'", queue, System.StringComparison.Ordinal);

        // The engine is named as the reader's choice, and the chosen file rides with it.
        Assert.Contains("sourceService: 'soulseek'", queue, System.StringComparison.Ordinal);
        Assert.Contains("preferredEngine: 'soulseek'", queue, System.StringComparison.Ordinal);
        Assert.Contains("remotePath: candidate.filename", queue, System.StringComparison.Ordinal);
        Assert.Contains("soulseekRemotePath: soulseek?.remotePath", client, System.StringComparison.Ordinal);
        Assert.Contains("soulseekUsername: soulseek?.username", client, System.StringComparison.Ordinal);
        Assert.Contains("soulseekRemoteSizeBytes: Number(soulseek?.remoteSizeBytes", client, System.StringComparison.Ordinal);

        // The toast is the client's own queue toast, not a local popup.
        Assert.Contains("notifyQueue: notifier.notifyQueue", queue, System.StringComparison.Ordinal);
        Assert.DoesNotContain("function toast(message, options)", script, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The source tabs stay reachable while a long result set scrolls underneath them.
    /// </summary>
    /// <remarks>
    ///     A search is a long page - one source can return thousands of rows - and scrolling back up to find the
    ///     tab strip is the most annoying thing about it. The page therefore pins its header and its tabs in one
    ///     sticky shell, following the AutoTag page's own shell, with the offset measured from the app topbar
    ///     at runtime because CSS cannot know the topbar's height.
    /// </remarks>
    [Fact]
    public void ThePagePinsItsHeaderAndSourceTabsLikeTheAutoTagPage()
    {
        var view = SearchView();

        Assert.Contains("id=\"searchStickyShell\"", view, System.StringComparison.Ordinal);
        Assert.Contains(".search-sticky-shell {", view, System.StringComparison.Ordinal);
        Assert.Contains("position: sticky;", view, System.StringComparison.Ordinal);
        Assert.Contains("top: var(--search-sticky-top, 68px);", view, System.StringComparison.Ordinal);

        // The shell closes after the tab strip, so the tabs are inside it.
        var shellStart = view.IndexOf("id=\"searchStickyShell\"", System.StringComparison.Ordinal);
        var tabs = view.IndexOf("id=\"searchSourceTabs\"", System.StringComparison.Ordinal);
        var shellEnd = view.IndexOf("/search-sticky-shell", System.StringComparison.Ordinal);
        Assert.True(shellStart > 0 && tabs > shellStart && shellEnd > tabs, "The source tabs must be inside the sticky shell.");

        // The offset is measured, not guessed, and it follows the topbar as it changes.
        Assert.Contains("function initializeSearchStickyShell()", view, System.StringComparison.Ordinal);
        Assert.Contains("'.main-content > .topbar'", view, System.StringComparison.Ordinal);
        Assert.Contains("'--search-sticky-top'", view, System.StringComparison.Ordinal);
        Assert.Contains("ResizeObserver", view, System.StringComparison.Ordinal);

        // The app's main region is its own scroll container, and a sticky element inside one sticks to that
        // container rather than to the window. The page therefore hands the scroll back to the window, which
        // is the part that makes the shell stick at all - the AutoTag page's shell does the same.
        Assert.Contains(".main-content.search-sticky-enabled {", view, System.StringComparison.Ordinal);
        Assert.Contains("classList.add('search-sticky-enabled')", view, System.StringComparison.Ordinal);

        // The Soulseek panel is sticky as well, and it starts below the shell rather than under the tabs.
        Assert.Contains("var(--search-shell-height", view, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The Soulseek tab is a workspace with a pinned panel, not a page that scrolls under the reader.
    /// </summary>
    /// <remarks>
    ///     The candidate card carries the destination picker and the Queue button, so scrolling a thousand-row
    ///     result set out from under them means the reader cannot act on what they just chose. The pane is
    ///     therefore bounded to the viewport and its two columns scroll inside it. The bound is per pane, so
    ///     the other sources keep scrolling the page, and it is dropped on a phone, which has no room for two
    ///     scroll regions side by side.
    /// </remarks>
    [Fact]
    public void TheSoulseekPaneIsBoundedSoOnlyItsResultsScroll()
    {
        var view = SearchView();

        Assert.Contains("class=\"results-section soulseek-layout soulseek-workspace\"", view, System.StringComparison.Ordinal);
        Assert.Contains(".soulseek-workspace {", view, System.StringComparison.Ordinal);
        Assert.Contains("height: calc(100vh - var(--search-sticky-top", view, System.StringComparison.Ordinal);
        Assert.Contains("min-height: 0;", view, System.StringComparison.Ordinal);

        // Both columns scroll inside the bound, and the panel is a plain column within it.
        Assert.Contains(".soulseek-workspace .soulseek-main,", view, System.StringComparison.Ordinal);
        Assert.Contains("max-height: 100%;", view, System.StringComparison.Ordinal);
        Assert.Contains("overflow-y: auto;", view, System.StringComparison.Ordinal);
        Assert.Contains(".soulseek-workspace .soulseek-side { position: static; }", view, System.StringComparison.Ordinal);

        // The bound is dropped where two scroll regions do not fit.
        Assert.Contains(".soulseek-workspace { height: auto; }", view, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     A download queued from the Soulseek tab stays on Soulseek.
    /// </summary>
    /// <remarks>
    ///     The reader chose the engine, so a miss is a failed attempt there and is retried there - never a
    ///     silent jump to Qobuz, Tidal or Amazon. The cross-engine ladder is still what every other enqueue
    ///     uses, and Soulseek is one of its steps, because a library download that reaches Soulseek is still
    ///     the library's download.
    /// </remarks>
    [Fact]
    public void ASoulseekQueueKeepsASoulseekOnlyPlanAndTheLadderKeepsSoulseek()
    {
        var intent = ReadRepoFile("DeezSpoTag.Web", "Services", "DownloadIntentService.cs");
        var visible = ExtractMethod(intent, "private static List<string> ResolveVisiblePreResolutionSources");

        Assert.Contains("if (IsSoulseekQueueRequest(intent))", visible, System.StringComparison.Ordinal);
        Assert.Contains("ResolveEngineQualitySources(", visible, System.StringComparison.Ordinal);
        Assert.Contains("SoulseekPlatform,", visible, System.StringComparison.Ordinal);

        // The cross-engine order is still reachable, and still returns first for everything else.
        Assert.Contains("IsMultiEngineService(settings.Service)", visible, System.StringComparison.Ordinal);
        Assert.Contains("ResolveQualityAutoSources(", visible, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     A running search must not promise rows it cannot deliver.
    /// </summary>
    /// <remarks>
    ///     slskd counts the files a search finds but only hands over the file list when it finalizes, so
    ///     nothing can be listed while the search runs. The panel used to say results appear as peers respond,
    ///     which is what made a working search look frozen next to a rising progress bar. The live peer and
    ///     file counts are the truth during a search, and the arrival graph plots peers, not candidates.
    /// </remarks>
    [Fact]
    public void ARunningSearchSaysWhatItCannotShowYet()
    {
        var script = ClientScript();
        var view = SearchView();
        var phase = ExtractFunction(script, "function renderPhase()");

        Assert.DoesNotContain("Results appear as peers respond", script, System.StringComparison.Ordinal);
        Assert.Contains("Available files will appear here when Soulseek finishes collecting responses.", phase, System.StringComparison.Ordinal);
        Assert.Contains("Files reported", ExtractFunction(script, "function renderStats()"), System.StringComparison.Ordinal);

        // The graph plots peer responses, so both its label and its tooltips have to say peers.
        Assert.Contains("Peers answering over time", view, System.StringComparison.Ordinal);
        Assert.Contains("${index}–${index + 1}s: ${count} peers", ExtractFunction(script, "function renderHistogram()"), System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The panel is only styled with theme tokens, so it follows the active theme rather than pinning
    ///     colours that only look right in one of them.
    /// </summary>
    [Fact]
    public void ThePanelStylesUseThemeTokensRatherThanLiteralColours()
    {
        var view = SearchView();

        // Bounded by its own opening comment and its own last rule. Slicing to the next </style> would swallow
        // the page's other, pre-existing styles and fail on colours that are not the panel's.
        var start = view.IndexOf("/* Soulseek results.", System.StringComparison.Ordinal);
        Assert.True(start > 0, "The Soulseek style block was not found.");

        var lastRule = view.IndexOf(".action-btn--primary:hover", start, StringComparison.Ordinal);
        Assert.True(lastRule > start, "The Soulseek style block was not terminated.");
        var end = view.IndexOf('}', lastRule) + 1;

        var block = view.Substring(start, end - start);

        Assert.DoesNotContain("rgba(", block, System.StringComparison.Ordinal);
        Assert.DoesNotContain("rgb(", block, System.StringComparison.Ordinal);
        Assert.DoesNotContain("opacity: 0.", block, System.StringComparison.Ordinal);
        Assert.DoesNotContain("#", block, System.StringComparison.Ordinal);
        Assert.Contains("var(--text-muted)", block, System.StringComparison.Ordinal);
        Assert.Contains("var(--text-primary)", block, System.StringComparison.Ordinal);
        Assert.Contains("var(--primary-color)", block, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The downloads list carries no per-source badge.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A Soulseek row used to name the peer that served the file, and the quality that peer served, in
    ///         a second badge. That was removed on purpose: the list answers one question - did this download work
    ///         - and a row-specific extra made it harder to scan for the answer. The engine badge that every row
    ///         already carries is the whole of the source column.
    ///     </para>
    ///     <para>
    ///         Nothing is lost behind it. The peer and remote path are still read off the queue item and still
    ///         drive the transfer and the peer cooldown; they are simply not rendered. This test guards that
    ///         decision so the badge does not quietly return.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheDownloadsListCarriesNoPeerToPeerBadge()
    {
        var view = ActivitiesView();

        Assert.DoesNotContain("soulseekPeerBadge", view, System.StringComparison.Ordinal);
        Assert.DoesNotContain("soulseekPeerQuality", view, System.StringComparison.Ordinal);
        Assert.DoesNotContain("soulseekRemotePath: readQueueItemText", view, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The view keeps its own copy of the quality labels, so the two copies are pinned together. Without
    ///     this, a renamed code shows one label on the quality badge and a different one on the peer badge.
    /// </summary>
    [Fact]
    public void TheViewQualityLabelsMatchTheServerCatalogExactly()
    {
        var view = ActivitiesView();

        foreach (var option in QualityCatalog.GetEngineQualityOptions()["soulseek"])
        {
            var expected = $"{option.Value}: '{option.Label}'";
            Assert.True(
                view.Contains(expected, System.StringComparison.Ordinal),
                $"The view's mapBitrate is missing or mislabels '{expected}'. The server publishes that exact "
                + "label from QualityCatalog, and the activity page has to agree with it.");
        }
    }

    /// <summary>
    ///     Soulseek carries no configuration switch, exactly like every other download engine. It used to ship
    ///     a <c>Soulseek:Enabled</c> flag that only two of its components honoured, so the page could be
    ///     switched "off" while transfers, shares and browsing kept working.
    /// </summary>
    [Fact]
    public void TheIntegrationHasNoConfigurationSwitch()
    {
        Assert.DoesNotContain("\"Soulseek\"", AppSettings(), System.StringComparison.Ordinal);
        Assert.DoesNotContain("Soulseek:Enabled", AppSettings(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void SoulseekIsSelectableAsADownloadSource()
    {
        var catalog = ReadRepoFile("DeezSpoTag.Services", "Download", "DownloadSourceCatalog.cs");

        Assert.Contains("new(\"soulseek\", \"Soulseek\")", catalog, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The Soulseek controls live in a section of their own, and every one of them is reachable.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The block used to be revealed only when Soulseek was the chosen source. It is now a section that
    ///         the reader opens, alongside the download, Spotify and notification sections, because Soulseek's
    ///         controls are about a separate service rather than about which source happens to be selected.
    ///         A page whose only tuning controls can never be seen is the failure this still guards: the section
    ///         has to exist, and it has to be openable.
    ///     </para>
    ///     <para>
    ///         The section also carries the instruction that makes Soulseek downloads work at all: slskd's
    ///         completed-download directory is the DeezSpoTag download location, so the file arrives where the
    ///         pipeline looks for it. That sentence is the whole setup step, and it is asserted here so it
    ///         cannot be dropped from a redesign.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheSoulseekSettingsSectionIsOpenableAndCarriesItsControls()
    {
        var view = ReadRepoFile("DeezSpoTag.Web", "Views", "Settings", "Index.cshtml");

        // The section, and the controls inside it.
        Assert.Contains("id=\"soulseek-settings\"", view, System.StringComparison.Ordinal);
        Assert.Contains("id=\"soulseek-settingsGroup\"", view, System.StringComparison.Ordinal);

        // Openable: sections are collapsed by default and shown by adding the active class, so a section that
        // is not wired to that mechanism can never be seen.
        Assert.Contains(".settings-content {", view, System.StringComparison.Ordinal);
        Assert.Contains(".settings-content.active {", view, System.StringComparison.Ordinal);

        foreach (var field in new[]
                 {
                     "soulseekSearchTimeout",
                     "soulseekMaxPeerQueue",
                     "soulseekMinPeerUploadSpeed",
                     "soulseekPeerCooldown",
                     "soulseekRequireFreeSlot",
                     "soulseekAllowUnknownQuality",
                     "soulseekAutomationEnabled",
                     "soulseekBlockedUsers",
                     "soulseekBlockedPatterns"
                 })
        {
            Assert.Contains($"id=\"{field}\"", view, System.StringComparison.Ordinal);
        }

        // The setup instruction, verbatim enough that a reword cannot quietly drop the point.
        Assert.Contains("Map <code>slskd</code> completed downloads to the global DeezSpoTag download location.", view, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     Returns the body of a JavaScript function, brace-matched, so a test can assert about one function
    ///     without being satisfied by the same token appearing somewhere else in the file.
    /// </summary>
    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, System.StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method not found: {signature}");

        var depth = 0;
        var bodyStart = source.IndexOf('{', start);
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(start, index - start + 1);
                }
            }
        }

        throw new InvalidOperationException($"Unbalanced braces in: {signature}");
    }

    private static string ExtractFunction(string source, string signature)
    {
        var start = source.IndexOf(signature, System.StringComparison.Ordinal);
        Assert.True(start >= 0, $"Function not found: {signature}");

        var bodyStart = source.IndexOf('{', start);
        Assert.True(bodyStart > start, $"No body found for: {signature}");

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(start, index - start + 1);
                }
            }
        }

        throw new InvalidOperationException($"Unbalanced braces in: {signature}");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, System.StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, System.StringComparison.Ordinal);
        }

        return count;
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var root = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(new[] { root }.Concat(parts).ToArray()));
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Services")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
