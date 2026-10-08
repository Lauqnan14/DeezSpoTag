using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Phase 3: the Sonic settings store and the Vibe Analysis panel contract.
///
/// <para>Two things are protected here. First, Sonic settings live in their own
/// file with their own record, because the Vibe settings record is a
/// guardrail-pinned positional type and must keep exactly three fields. Second,
/// the panel must not leak implementation detail: dimension, pooling and index
/// parameters are not things a library owner can meaningfully tune, and exposing
/// them would freeze them into a contract they need to be free to change.</para>
/// </summary>
public sealed class SonicAnalysisSettingsAndPanelTest
{
    [Fact]
    public void SonicSettingsLiveInTheirOwnFileBesideTheVibeSettings()
    {
        var store = Read("DeezSpoTag.Web", "Services", "SonicAnalysisSettingsStore.cs");

        Assert.Contains("\"sonic-settings.json\"", store, StringComparison.Ordinal);
        Assert.Contains("Path.Join(baseDataDir, \"analysis\")", store, StringComparison.Ordinal);

        // It must never be written to the vibe settings file.
        Assert.DoesNotContain("\"settings.json\"", store, StringComparison.Ordinal);
    }

    [Fact]
    public void SonicDoesNotExtendTheGuardrailPinnedVibeSettingsRecord()
    {
        // VibeAnalysisSettingsDto is asserted on exactly by a source-text guardrail.
        // Sonic has to stay out of it entirely.
        var store = Read("DeezSpoTag.Web", "Services", "VibeAnalysisSettingsStore.cs");
        Assert.Contains("public sealed record VibeAnalysisSettingsDto(", store, StringComparison.Ordinal);
        Assert.DoesNotContain("Sonic", store, StringComparison.Ordinal);

        var analysisService = Read("DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs");
        Assert.DoesNotContain("VibeAnalysisSettingsDto(\n", analysisService, StringComparison.Ordinal);
    }

    [Fact]
    public void SonicSettingsClampTheirBatchSize()
    {
        var store = Read("DeezSpoTag.Web", "Services", "SonicAnalysisSettingsStore.cs");
        Assert.Contains("Math.Clamp(ReanalyzeBatchSize, 10, 500)", store, StringComparison.Ordinal);
    }

    [Fact]
    public void SonicDefaultsToOff()
    {
        var store = Read("DeezSpoTag.Web", "Services", "SonicAnalysisSettingsStore.cs");
        Assert.Contains("Enabled = false", store, StringComparison.Ordinal);

        var settings = Read("DeezSpoTag.Web", "appsettings.json");
        Assert.Contains("\"SonicAnalysis\"", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void SonicPersistsTheSameWayVibeDoes_AndReportsWriteFailures()
    {
        // A settings write that does not persist must be reported, or the UI shows
        // a saved state that silently reverts on the next restart.
        var store = Read("DeezSpoTag.Web", "Services", "SonicAnalysisSettingsStore.cs");
        Assert.Contains("throw new IOException", store, StringComparison.Ordinal);
    }

    [Fact]
    public void TheApiSurfaceIsSonicOnlyAndCarriesNoVector()
    {
        var controller = Read("DeezSpoTag.Web", "Controllers", "Api", "SonicAnalysisApiController.cs");

        Assert.Contains("[Route(\"api/library/analysis/sonic\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[Authorize]", controller, StringComparison.Ordinal);

        // Every mutating endpoint needs the antiforgery attribute its neighbours use.
        Assert.Equal(3, Occurrences(controller, "[ValidateAntiForgeryToken]"));
        Assert.Contains("HttpGet(\"status\")", controller, StringComparison.Ordinal);
        Assert.Contains("HttpGet(\"settings\")", controller, StringComparison.Ordinal);
        Assert.Contains("HttpPost(\"settings\")", controller, StringComparison.Ordinal);
        Assert.Contains("HttpPost(\"rebuild-index\")", controller, StringComparison.Ordinal);
        Assert.Contains("HttpPost(\"reanalyze\")", controller, StringComparison.Ordinal);

        // No endpoint may hand a raw vector or a tuning parameter to the client.
        Assert.DoesNotContain("float[]", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("Vector", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePanelLivesInsideTheVibeAnalysisCard()
    {
        // Sonic belongs inside the existing Vibe Analysis area, not on a new page.
        var view = Read("DeezSpoTag.Web", "Views", "Activities", "Index.cshtml");

        var cardStart = view.IndexOf("id=\"analysis-card\"", StringComparison.Ordinal);
        Assert.True(cardStart > 0, "analysis-card not found in the Activities view.");

        var sonicStart = view.IndexOf("Sonic analysis", StringComparison.Ordinal);
        Assert.True(sonicStart > cardStart, "The Sonic section is not inside the Vibe analysis card.");

        // No separate Sonic page or controller.
        Assert.DoesNotContain("View(\"Sonic", view, StringComparison.Ordinal);
        var controllers = Directory
            .GetFiles(RepoPath("DeezSpoTag.Web", "Controllers"), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToList();
        Assert.DoesNotContain(
            controllers,
            text => text.Contains("Sonic") && text.Contains("class SonicPageController"));
    }

    [Fact]
    public void ThePanelExposesNoVectorMathematics()
    {
        // The panel may report the dimension as read-only context, but must not
        // offer pooling, normalization, index or metric controls.
        var view = Read("DeezSpoTag.Web", "Views", "Activities", "Index.cshtml");

        foreach (var forbidden in new[]
        {
            "sonic-dimensions", "sonic-pooling", "sonic-normalization",
            "sonic-distance", "sonic-hnsw", "sonic-ef-construction", "sonic-metric",
        })
        {
            Assert.DoesNotContain(forbidden, view, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePanelReportsCoverageStalenessAndTheModel()
    {
        var view = Read("DeezSpoTag.Web", "Views", "Activities", "Index.cshtml");

        // Kebab-case ids, matching the markup rather than the JS variable names.
        foreach (var id in new[]
        {
            "id=\"sonic-coverage\"", "id=\"sonic-embedded\"",
            "id=\"sonic-pending\"", "id=\"sonic-stale\"",
            "id=\"sonic-model\"", "id=\"sonic-enabled\"",
            "id=\"sonic-reanalyze\"", "id=\"sonic-rebuild-index\"",
            "id=\"sonic-reanalyze-batch\"", "id=\"sonic-message\"",
        })
        {
            Assert.Contains(id, view, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EverySonicElementIdReferencedByScriptExistsInTheMarkup()
    {
        // A script reaching for a missing element silently does nothing, so a
        // toggle would appear to work while never firing. That is the "the button
        // does nothing" class of bug, and only this pairing catches it.
        var view = Read("DeezSpoTag.Web", "Views", "Activities", "Index.cshtml");

        var referenced = System.Text.RegularExpressions.Regex
            .Matches(view, "getElementById\\('(sonic-[a-z-]+)'\\)")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        var defined = System.Text.RegularExpressions.Regex
            .Matches(view, "id=\"(sonic-[a-z-]+)\"")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToArray();

        Assert.NotEmpty(referenced);
        foreach (var id in referenced)
        {
            Assert.Contains(id, defined);
        }

        // Nothing is rendered that the script can never reach.
        foreach (var id in defined)
        {
            Assert.Contains(id, referenced);
        }
    }

    [Fact]
    public void EnablingSonicReplacesARunningAnalyzerWorker()
    {
        // A long-lived worker started without the flag loaded no Sonic extractor,
        // so it can never produce a vector. Enabling Sonic has to replace it or
        // the setting appears to do nothing.
        var service = Read("DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs");
        Assert.Contains("ApplySonicSettingsAsync", service, StringComparison.Ordinal);
        Assert.Contains("StopAnalyzerWorkerAsync", service, StringComparison.Ordinal);
    }

    [Fact]
    public void ReanalyzeIsBoundedAndRefusesWhenDisabled()
    {
        var service = Read("DeezSpoTag.Web", "Services", "SonicAnalysisService.cs");

        // A whole-library catch-up would monopolise the analysis queue.
        Assert.Contains("ReanalyzeBatchSize", service, StringComparison.Ordinal);
        Assert.Contains("Sonic Analysis is disabled", service, StringComparison.Ordinal);
        Assert.Contains("ReanalyzeStale", service, StringComparison.Ordinal);

        // Re-embedding must go through the analysis service so decode, subprocess
        // and write can never diverge from how analysis actually runs.
        Assert.Contains("_analysisService.AnalyzeTrackByIdAsync", service, StringComparison.Ordinal);
    }

    [Fact]
    public void RebuildIndexIsDistinctFromReanalyze()
    {
        // Reloading vectors is cheap; re-embedding is not. Conflating them would
        // make a maintenance button look like a full analysis run.
        var service = Read("DeezSpoTag.Web", "Services", "SonicAnalysisService.cs");
        Assert.Contains("RebuildIndexAsync", service, StringComparison.Ordinal);

        var rebuild = service.Substring(
            service.IndexOf("public async Task<SonicAnalysisStatusDto> RebuildIndexAsync", StringComparison.Ordinal),
            1200);
        Assert.DoesNotContain("AnalyzeTrackByIdAsync", rebuild, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverageIsReportedPerLibraryNotAsALibraryWideGuess()
    {
        var service = Read("DeezSpoTag.Web", "Services", "SonicAnalysisService.cs");
        Assert.Contains("GetEnabledLibraryScopesAsync", service, StringComparison.Ordinal);
        Assert.Contains("CountStaleSonicEmbeddingsAsync", service, StringComparison.Ordinal);
        Assert.Contains("StaleEmbeddings", service, StringComparison.Ordinal);
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string Read(params string[] parts) => File.ReadAllText(RepoPath(parts));

    private static string RepoPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Web")))
        {
            directory = directory.Parent;
        }

        var path = directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root not found.");
        foreach (var part in parts)
        {
            path = Path.Join(path, part);
        }

        return path;
    }
}
