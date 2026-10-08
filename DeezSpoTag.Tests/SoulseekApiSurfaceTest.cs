using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Models.Soulseek;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Integrations.Soulseek;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using DeezSpoTag.Services.Download.Soulseek;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Controllers.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Contract and security tests for the Soulseek API surface, hub and UI placement.
/// </summary>
/// <remarks>
///     These pin the design's non-negotiables: the API key must never appear in a response or a log, Soulseek
///     must be a selectable source without touching the canonical auto order, and connection UX belongs to the
///     login page rather than the download settings.
/// </remarks>
public sealed class SoulseekApiSurfaceTest
{
    [Fact]
    public void ConnectionProbesUseTheSupportedServerEndpoint()
    {
        var client = ReadRepoFile("DeezSpoTag.Integrations", "Soulseek", "SlskdClient.cs");
        var loginProbe = ReadRepoFile("DeezSpoTag.Web", "Services", "SoulseekConnectionService.cs");

        Assert.Contains("HttpMethod.Get, \"server\"", client, StringComparison.Ordinal);
        Assert.Contains("new Uri(baseUri, \"api/v0/server\")", loginProbe, StringComparison.Ordinal);
        Assert.DoesNotContain("server/state", client, StringComparison.Ordinal);
        Assert.DoesNotContain("server/state", loginProbe, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDocumentedEndpointIsRouted()
    {
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs");
        var shares = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekFolderShareApiController.cs");

        foreach (var route in new[]
        {
            "\"connection\"", "\"connection/test\"", "\"connection/connect\"",
            "\"connection/disconnect\"", "\"connection/status\"",
            "\"download-settings\"",
            "\"searches\"", "searches/queue/{queueUuid}", "users/{username}/directory",
            "searches/{searchId:guid}", "searches/cleanup", "searches/{searchId:guid}",
            "\"downloads\"", "downloads/queue", "downloads/cleanup",
            "downloads/{id:guid}", "downloads/{id:guid}/cancel",
            "templates/preview", "import/{downloadId}", "tagging/reprocess/{downloadId}"
        })
        {
            Assert.Contains(route, controller);
        }

        foreach (var route in new[]
        {
            "\"folders\"", "folders/{folderId:long}", "folders/{folderId:long}/enabled",
            "\"sync\"", "\"scan\"", "scan/status", "\"preview\"", "folders/{folderId:long}/preview"
        })
        {
            Assert.Contains(route, shares);
        }
    }

    /// <summary>
    ///     Search reports a slskd start failure rather than pretending the search began.
    /// </summary>
    /// <remarks>
    ///     The eligibility check itself moved out of the action and into the shared gate, because search, queue
    ///     and browse all need the same answer and one reason code. What is asserted here is that the action
    ///     still surfaces a start failure to the caller instead of leaving a search it never began.
    /// </remarks>
    [Fact]
    public void ManualSearchReportsSlskdStartFailures()
    {
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs");
        var start = controller.IndexOf("public async Task<IActionResult> Search", StringComparison.Ordinal);
        var end = controller.IndexOf("/// <summary>Cancels an in-flight Soulseek search.", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "The Soulseek search action was not found.");
        var search = controller[start..end];

        Assert.Contains("RequireEligibilityAsync", search, StringComparison.Ordinal);
        Assert.Contains("catch (SlskdApiException ex)", search, StringComparison.Ordinal);
        Assert.Contains("return StatusCode(503, new { error = ex.Message });", search, StringComparison.Ordinal);

        // A search must not ask slskd to log in on the way through. That is the explicit Connect action's job,
        // and doing it here is how a source comes back after the reader logged it out.
        Assert.DoesNotContain("EnsureAvailableAsync", search, StringComparison.Ordinal);
    }

        /// <summary>
    ///     A background search must not be admitted on a cached answer.
    /// </summary>
/// <remarks>
///     The connection service caches for twenty seconds so a burst of reads does not hammer slskd. That cache
///     is fine for a reader looking at a status and wrong for granting new work: a background download search
///     could otherwise start after slskd had lost its login, and only fail much later at the enqueue.
/// </remarks>
[Fact]
    public void ABackgroundSearchProbesFreshlyRatherThanReadingTheCache()
    {
        var search = ReadRepoFile("DeezSpoTag.Services", "Download", "Soulseek", "SoulseekSearchService.cs");

        Assert.Contains("GetEligibilityAsync", search, System.StringComparison.Ordinal);

        // The cached read must not be what admits the work.
        var admission = search[search.IndexOf("GetEligibilityAsync", System.StringComparison.Ordinal)..];
        admission = admission[..admission.IndexOf(";", System.StringComparison.Ordinal)];
        Assert.DoesNotContain("IsAvailableAsync", admission, System.StringComparison.Ordinal);
        Assert.DoesNotContain("GetStatusAsync", admission, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     A persisted plan must re-check the authority before it starts a Soulseek transfer.
    /// </summary>
    /// <remarks>
    ///     A plan can outlive the login it was built under. The step advance is the last point before work
    ///     starts, so a stale plan would otherwise walk straight into an engine the admission gate will refuse.
    /// </remarks>
    [Fact]
    public void AFallbackStepRechecksSoulseekEligibilityBeforeAdvancing()
    {
        var coordinator = ReadRepoFile("DeezSpoTag.Services", "Download", "Fallback", "EngineFallbackCoordinator.cs");

        var step = coordinator[coordinator.IndexOf("private async Task<bool> TryAdvanceToStepAsync", System.StringComparison.Ordinal)..];
        step = step[..step.IndexOf("\n    private ", System.StringComparison.Ordinal)];

        // The check sits before the step's URL is resolved, which is before any work is started.
        var check = step.IndexOf("ResolveSoulseekEligibilityAsync", System.StringComparison.Ordinal);
        var resolve = step.IndexOf("ResolveSourceUrlAsync", System.StringComparison.Ordinal);
        Assert.True(check >= 0, "A Soulseek step is not re-checked for eligibility.");
        Assert.True(resolve > check, "The eligibility check must come before the step resolves any URL.");

        Assert.Contains("SoulseekQueueItem.EngineId", step, System.StringComparison.Ordinal);

        // Skipped, never reported as an advance. `true` from this method ends the ladder walk, so returning it
        // for an engine that cannot run would park the item on a step nothing will ever attempt while claiming
        // the fallback succeeded. `false` is what carries the walk on to the next source.
        Assert.Contains("return false;", step[check..], System.StringComparison.Ordinal);
        var advancedOnSkip = step[check..step.IndexOf("string? resolvedUrl;", System.StringComparison.Ordinal)];
        Assert.DoesNotContain("return true;", advancedOnSkip, System.StringComparison.Ordinal);

        // And the skip is recorded, so the history explains why the ladder moved past this engine.
        Assert.Contains("soulseek_login_required", advancedOnSkip, System.StringComparison.Ordinal);

        // A pinned selection keeps its own intent and is never skipped for a login that ended.
        var advance = coordinator[coordinator.IndexOf("public Task<bool> TryAdvanceAsync", System.StringComparison.Ordinal)..];
        advance = advance[..advance.IndexOf("private", System.StringComparison.Ordinal)];
        Assert.Contains("IsManualSelection", advance, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     An eligibility answer that cannot be obtained is not permission.
    /// </summary>
    [Fact]
    public void PlanningTreatsAnUnobtainableEligibilityAnswerAsInactive()
    {
        var intent = ReadRepoFile("DeezSpoTag.Web", "Services", "DownloadIntentService.cs");
        var resolve = intent[intent.IndexOf("private async Task<bool> ResolveSoulseekEligibilityAsync", System.StringComparison.Ordinal)..];
        resolve = resolve[..resolve.IndexOf("\n    private ", System.StringComparison.Ordinal)];

        // Neither an absent authority nor a probe that threw may report "eligible".
        Assert.DoesNotContain("return true;", resolve, System.StringComparison.Ordinal);

        // A probe that threw is answered, not allowed to escape: this builds a plan, and an exception here
        // fails the enqueue instead of leaving the plan without the engine it could not verify.
        Assert.Contains("catch (Exception ex) when (ex is not OperationCanceledException)", resolve, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An unreachable engine must not abandon the whole ladder walk.
    /// </summary>
    /// <remarks>
    ///     The coordinator re-checks eligibility mid-walk, so a probe that throws there is raised on every
    ///     attempt to pass through Soulseek. Letting it escape ends the walk and reports the item exhausted,
    ///     which loses every remaining source - one unreachable engine deciding the fate of all of them.
    /// </remarks>
    [Fact]
    public void AnUnreachableSoulseekProbeSkipsTheStepRatherThanEndingTheWalk()
    {
        var coordinator = ReadRepoFile("DeezSpoTag.Services", "Download", "Fallback", "EngineFallbackCoordinator.cs");
        var resolve = coordinator[coordinator.IndexOf("private async Task<bool> ResolveSoulseekEligibilityAsync", StringComparison.Ordinal)..];
        resolve = resolve[..resolve.IndexOf("\n    private ", StringComparison.Ordinal)];

        // Cancellation still propagates - the caller asked to stop.
        Assert.Contains("catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)", resolve, StringComparison.Ordinal);

        // Everything else is a verdict, not an escape. This used to match the bare catch-all text, which
        // the project's own guardrail against unexplained catch-alls forbids. The catch now names
        // why totality is the intent, so the rule is asserted as the shape rather than as one
        // spelling: a catch over the base exception type that no exception filter excludes.
        Assert.Contains("catch (Exception ex) when (ProbeFailureMeansUnverified(ex))", resolve, StringComparison.Ordinal);
        Assert.DoesNotContain("return true;", resolve, StringComparison.Ordinal);

        // And the predicate really is total. If it ever started filtering, an unexpected failure
        // would escape the probe and abandon the rest of the fallback ladder, which is the whole
        // thing this method exists to prevent.
        var predicate = coordinator[coordinator.IndexOf("private static bool ProbeFailureMeansUnverified", StringComparison.Ordinal)..];
        predicate = predicate[..predicate.IndexOf("\n    private ", predicate.IndexOf("=>", StringComparison.Ordinal), StringComparison.Ordinal)];
        Assert.Contains("=> true;", predicate, StringComparison.Ordinal);
    }

    /// <summary>
    ///     One response cannot report two different answers about the same connection.
    /// </summary>
    [Fact]
    public void TheVerifiedProbeDecidesEveryAvailabilityFieldInTheResponse()
    {
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "PlatformAuthApiController.cs");
        var shape = controller[controller.IndexOf("private static object ToPublicSoulseek", System.StringComparison.Ordinal)..];
        shape = shape[..shape.IndexOf("\n    private ", System.StringComparison.Ordinal)];

        // The probe that ran decides status and message too, not only `active`.
        Assert.Contains("var statusOut = eligibility is null ? status : eligibility.State", shape, System.StringComparison.Ordinal);
        Assert.Contains("var active = eligibility?.IsUsable ??", shape, System.StringComparison.Ordinal);
        Assert.Contains("connected = active", shape, System.StringComparison.Ordinal);
        Assert.Contains("message = reason", shape, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     One endpoint must answer a Soulseek connection question with one probe.
/// </summary>
/// <remarks>
///     It used to run the web-tier check, persist that result, and then run the service-layer probe as well.
///     Two round trips to slskd for one question, free to disagree, and a page could render "Connected" from the
///     first while the API was already refusing work on the second. The web-tier probe now runs only where it
///     is genuinely needed: validating credentials that have not been saved yet.
/// </remarks>
    [Fact]
    public void TheConnectionEndpointProbesOnceThroughTheServiceLayer()
    {
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "PlatformAuthApiController.cs");

        var endpoint = controller[controller.IndexOf("public async Task<IActionResult> GetSoulseekConnection", StringComparison.Ordinal)..];
        endpoint = endpoint[..endpoint.IndexOf("\n    [", StringComparison.Ordinal)];

        // Exactly one probe, and it is the service-layer one the download path admits on.
        Assert.Equal(1, CountOccurrences(endpoint, "GetEligibilityAsync("));
        Assert.DoesNotContain("CheckAsync(", endpoint, System.StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshSoulseekConnectionAsync", controller, System.StringComparison.Ordinal);

        // Saving credentials still validates them before they are stored, which is a different job: the
        // service-layer provider can only probe credentials it already has.
        var save = controller[controller.IndexOf("public async Task<IActionResult> SaveSoulseek", StringComparison.Ordinal)..];
        save = save[..save.IndexOf("\n    [", StringComparison.Ordinal)];
        Assert.Contains("CheckAsync(candidate", save, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     A connection response must not be assembled from two independent answers.
    /// </summary>
    [Fact]
    public void TheConnectionResponseIsAssembledFromTheProbeThatRan()
    {
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "PlatformAuthApiController.cs");
        var shape = controller[controller.IndexOf("private static object ToPublicSoulseek", StringComparison.Ordinal)..];
        shape = shape[..shape.IndexOf("\n    private ", StringComparison.Ordinal)];

        Assert.Contains("var statusOut = eligibility is null ? status : eligibility.State", shape, StringComparison.Ordinal);
        Assert.Contains("var active = eligibility?.IsUsable ??", shape, StringComparison.Ordinal);
        Assert.Contains("connected = active", shape, StringComparison.Ordinal);
        Assert.Contains("message = reason", shape, StringComparison.Ordinal);
    }

    /// <summary>
    ///     One gate, one reason code, and no implicit reconnect on any new-work path.
    /// </summary>
    [Fact]
    public void EveryNewSoulseekRequestGoesThroughTheVerifiedEligibilityGate()
    {
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs");

        // Search, both queue entry points and browse are the requests that start remote work.
        foreach (var action in new[]
                 {
                     "Task<IActionResult> Search(",
                     "Task<IActionResult> QueueDownload(",
                     "Task<IActionResult> QueueBatchDownload(",
                     "Task<IActionResult> BrowseDirectory("
                 })
        {
            var start = controller.IndexOf(action, StringComparison.Ordinal);
            Assert.True(start > 0, $"{action} was not found.");
            var next = controller.IndexOf("\n    [Http", start, StringComparison.Ordinal);
            var body = next > start ? controller[start..next] : controller[start..];
            Assert.Contains("RequireEligibilityAsync", body, StringComparison.Ordinal);
        }

        // The gate probes and reports; it does not reconnect.
        Assert.Contains("GetEligibilityAsync", controller, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(controller, "SoulseekLoginRequiredReasonCode = \"soulseek_login_required\""));

        // Only the explicit Connect action may ask slskd to log in.
        var connectStart = controller.IndexOf("Task<IActionResult> Connect(", StringComparison.Ordinal);
        Assert.True(connectStart > 0, "The Connect action was not found.");
        var connectBody = controller[connectStart..];
        connectBody = connectBody[..connectBody.IndexOf("\n    [Http", StringComparison.Ordinal)];
        Assert.Contains("EnsureAvailableAsync", connectBody, StringComparison.Ordinal);
    }

    [Fact]
    public void SensitiveEndpointsAreRateLimitedAndAntiforgeryProtected()
    {
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs");

        Assert.Contains("[AutoValidateAntiforgeryToken]", controller);
        Assert.Contains("[Authorize]", controller);
        Assert.Contains("[EnableRateLimiting(\"DefaultApi\")]", controller);

        // Search and queue are the two endpoints that can make DeezSpoTag do real work, so both carry the
        // tighter policy.
        Assert.Contains("[HttpPost(\"searches\")]\n    [EnableRateLimiting(\"SensitiveWrites\")]", controller);
        Assert.Contains("[HttpPost(\"downloads/queue\")]\n    [EnableRateLimiting(\"SensitiveWrites\")]", controller);
    }

    [Fact]
    public void NoEndpointReturnsTheApiKey()
    {
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs");
        var settingsController = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekFolderShareApiController.cs");

        foreach (var source in new[] { controller, settingsController })
        {
            // The key must never be projected into a response.
            Assert.DoesNotContain("ApiKey", source);
            Assert.DoesNotContain("apiKey", source);
        }
    }

    [Fact]
    public void SettingsResponseCarriesBehaviourOnlyAndNoSecret()
    {
        var settings = ReadRepoFile("DeezSpoTag.Core", "Models", "Settings", "SoulseekDownloadSettings.cs");

        // The credential shape lives in Core, the settings shape must not carry one.
        Assert.DoesNotContain("ApiKey", settings);
        Assert.DoesNotContain("BaseUrl", settings);
        Assert.DoesNotContain("string? Password", settings);
    }

    [Fact]
    public void AdapterNeverLogsOrEchoesTheKey()
    {
        var client = ReadRepoFile("DeezSpoTag.Integrations", "Soulseek", "SlskdClient.cs");
        var credentials = ReadRepoFile("DeezSpoTag.Core", "Models", "Soulseek", "SlskdCredentials.cs");

        // The key goes in a header and nowhere else.
        Assert.Contains("request.Headers.TryAddWithoutValidation(ApiKeyHeader, credentials.ApiKey)", client);
        Assert.DoesNotContain("LogInformation", client);
        Assert.DoesNotContain("LogWarning", client);
        Assert.DoesNotContain("LogError", client);

        // An accidental interpolation cannot leak it, because ToString never renders it.
        Assert.Contains("public override string ToString()", credentials);
    }

    [Fact]
    public void HubIsAnEmptyAuthorizedMarkerLikeTheOtherHubs()
    {
        var hub = ReadRepoFile("DeezSpoTag.Web", "Hubs", "SoulseekHub.cs");
        var program = ReadRepoFile("DeezSpoTag.Web", "Program.cs");

        Assert.Contains("[Authorize]", hub);
        Assert.Contains("public sealed class SoulseekHub : Hub", hub);
        Assert.Contains("app.MapHub<DeezSpoTag.Web.Hubs.SoulseekHub>(\"/hubs/soulseek\")", program);
    }

    [Fact]
    public void AllEightContractEventsArePublished()
    {
        var realtime = ReadRepoFile("DeezSpoTag.Web", "Services", "SoulseekRealtimeService.cs");

        foreach (var name in new[]
        {
            "connection_state", "engine_health", "search_update", "search_result",
            "download_update", "share_sync_update", "share_scan_update", "import_update"
        })
        {
            Assert.Contains($"\"{name}\"", realtime);
        }
    }

    [Fact]
    public void ReplayNeverThrowsIntoTheDownloadPath()
    {
        var realtime = ReadRepoFile("DeezSpoTag.Web", "Services", "SoulseekRealtimeService.cs");

        // A broadcast failure must be swallowed at Debug, like ActivitiesRealtimeService.
        Assert.Contains("catch (Exception ex) when (ex is not OperationCanceledException)", realtime);
        Assert.Contains("LogLevel.Debug", realtime);
    }

    [Fact]
    public void TheServicesLayerSeamHasANoOpSoAWorkerCanRunWithoutAWebTier()
    {
        var publisher = ReadRepoFile("DeezSpoTag.Services", "Download", "Soulseek", "ISoulseekRealtimePublisher.cs");
        Assert.Contains("NullSoulseekRealtimePublisher", publisher);

        // Every service takes the publisher as an optional parameter and falls back to the no-op, so the
        // background worker needs no registration for progress reporting to work.
        foreach (var service in new[] { "SoulseekSearchService.cs", "SoulseekTransferService.cs", "SoulseekShareService.cs" })
        {
            var source = ReadRepoFile("DeezSpoTag.Services", "Download", "Soulseek", service);
            Assert.Contains("ISoulseekRealtimePublisher? realtime = null", source);
            Assert.Contains("realtime ?? NullSoulseekRealtimePublisher.Instance", source);
        }
    }

    [Fact]
    public void ConnectionStateIsReadFromTheOneSharedService()
    {
        // The login page, the sidebar and the API must all read the same connection state rather than each
        // having its own probe. The shipped login route stays as it is.
        var api = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs");
        var shipped = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "PlatformAuthApiController.cs");

        Assert.Contains("ISoulseekConnectionService", api);
        Assert.Contains("_connection.GetStatusAsync", api);

        // The shipped route the guardrail test pins must remain.
        Assert.Contains("[HttpGet(\"soulseek/connection\")]", shipped);
    }

    [Fact]
    public void DownloadSettingsShowsBehaviourOnlyAndSaysWhereConnectionLives()
    {
        var view = ReadRepoFile("DeezSpoTag.Web", "Views", "Settings", "Index.cshtml");

        Assert.Contains("id=\"soulseek-settingsGroup\"", view);

        // The block must not ask for the slskd URL or API key; those belong to the login page.
        var block = view.Substring(view.IndexOf("id=\"soulseek-settingsGroup\"", StringComparison.Ordinal));
        block = block[..block.IndexOf("id=\"spotify-settings\"", StringComparison.Ordinal)];
        Assert.DoesNotContain("soulseekBaseUrl", block);
        Assert.DoesNotContain("soulseekApiKey", block);
        Assert.DoesNotContain("soulseekStagingPath", block);
        Assert.DoesNotContain("stagingPath:", block);
        Assert.Contains("login page", block);
        Assert.Contains("global DeezSpoTag download location", block);
        Assert.Contains("incomplete", block);

        // Behaviour settings remain discoverable regardless of the selected primary source.
        Assert.DoesNotContain("id=\"soulseek-settingsGroup\" style=\"display: none;\"", view);
        Assert.DoesNotContain("soulseekGroup.style.display = normalizedSource === 'soulseek'", view);
    }

    [Fact]
    public void SoulseekControlsHaveTheirOwnSettingsAccordion()
    {
        var view = ReadRepoFile("DeezSpoTag.Web", "Views", "Settings", "Index.cshtml");
        var downloadStart = view.IndexOf("<div class=\"settings-section\" id=\"download-settings\">", StringComparison.Ordinal);
        var soulseekStart = view.IndexOf("<div class=\"settings-section\" id=\"soulseek-settings\">", StringComparison.Ordinal);
        var spotifyStart = view.IndexOf("<div class=\"settings-section\" id=\"spotify-settings\">", StringComparison.Ordinal);

        Assert.True(downloadStart >= 0 && soulseekStart > downloadStart && spotifyStart > soulseekStart,
            "Soulseek needs a separate accordion between Download Settings and Spotify.");
        var downloadSection = view[downloadStart..soulseekStart];
        var soulseekSection = view[soulseekStart..spotifyStart];
        Assert.DoesNotContain("id=\"soulseek-settingsGroup\"", downloadSection, StringComparison.Ordinal);
        Assert.Contains("id=\"soulseek-settingsGroup\"", soulseekSection, StringComparison.Ordinal);
        Assert.Contains("<div class=\"settings-header\">", soulseekSection, StringComparison.Ordinal);
        Assert.Contains("<div class=\"settings-content\">", soulseekSection, StringComparison.Ordinal);
    }

    [Fact]
    public void SoulseekSwitchTextSitsOutsideTheFixedWidthToggle()
    {
        var view = ReadRepoFile("DeezSpoTag.Web", "Views", "Settings", "Index.cshtml");
        var soulseekStart = view.IndexOf("<div class=\"settings-section\" id=\"soulseek-settings\">", StringComparison.Ordinal);
        var spotifyStart = view.IndexOf("<div class=\"settings-section\" id=\"spotify-settings\">", StringComparison.Ordinal);

        Assert.True(soulseekStart >= 0 && spotifyStart > soulseekStart, "Soulseek settings section is missing.");
        var soulseekSection = view[soulseekStart..spotifyStart];
        foreach (var id in new[]
        {
            "soulseekRequireFreeSlot", "soulseekAllowUnknownQuality", "soulseekAutomationEnabled",
            "soulseekUsePeerArtwork", "soulseekUsePeerLyrics"
        })
        {
            Assert.Contains($"<label class=\"switch-label\" for=\"{id}\">", soulseekSection, StringComparison.Ordinal);
            Assert.Contains($"<label class=\"switch\" for=\"{id}\">", soulseekSection, StringComparison.Ordinal);
            Assert.DoesNotContain($"<label class=\"switch\" for=\"{id}\">\n                            <input type=\"checkbox\" id=\"{id}\" />\n                            <span class=\"slider\"></span>\n                            <span>", soulseekSection, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LoginPageAndSidebarAreUntouchedByThisIntegration()
    {
        // The login tab and the sidebar status shipped already. The integration adds no second connection path
        // and no second sidebar status path, so neither file is modified.
        var program = ReadRepoFile("DeezSpoTag.Web", "Program.cs");
        var folders = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "LibraryFoldersApiController.cs");

        // The shipped sidebar/login wiring is still registered exactly once.
        Assert.Equal(1, CountOccurrences(program, "SoulseekConnectionService = sp.GetRequiredService"));
        Assert.Equal(1, CountOccurrences(program, "services.AddSingleton<DeezSpoTag.Web.Services.SoulseekConnectionService>();"));

        // The Soulseek API controllers are the only place /api/v1/soulseek is mounted.
        Assert.Equal(2, CountOccurrences(
            ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs")
            + ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekFolderShareApiController.cs"),
            "[Route(\"api/v1/soulseek"));

        // Folder sharing is only ever written through the folder-tab endpoint, so there is one state and one
        // place to change it.
        Assert.Contains("soulseek-share-enabled", folders);
        Assert.Equal(1, CountOccurrences(folders, "UpdateFolderSoulseekShareEnabledAsync"));
        Assert.DoesNotContain("soulseek", folders.Replace("soulseek-share-enabled", string.Empty, StringComparison.Ordinal)
            .Replace("UpdateFolderSoulseekShareEnabledAsync", string.Empty, StringComparison.Ordinal)
            .Replace("Soulseek", string.Empty, StringComparison.Ordinal)
            .Replace("soulseek", string.Empty, StringComparison.Ordinal),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SoulseekIsSelectableAndIsTheLastResortForEachTier()
    {
        var catalog = ReadRepoFile("DeezSpoTag.Services", "Download", "DownloadSourceCatalog.cs");
        var order = ReadRepoFile("DeezSpoTag.Services", "Download", "DownloadSourceOrder.cs");

        Assert.Contains("new(\"soulseek\", \"Soulseek\")", catalog);

        // The stereo ladder is the single hand-written list, and Soulseek is woven into it at its per-tier
        // position rather than appended as a block at the bottom.
        var ladder = order.Substring(order.IndexOf("StereoLadder =", StringComparison.Ordinal));
        ladder = ladder[..ladder.IndexOf("];", StringComparison.Ordinal)];
        Assert.Contains("SoulseekSource", ladder);
        Assert.Contains("new(SoulseekSource, \"Max Hi-Res (24-bit/192kHz)\", \"FLAC_HI_RES_LOSSLESS\", null)", ladder);

        // The catalogue-only view is derived from the ladder, so the two cannot drift apart.
        Assert.Contains(
            "StereoPriority =\n        StereoLadder.Where(profile => profile.Source != SoulseekSource).ToArray();",
            order.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            "SoulseekPriority =\n        StereoLadder.Where(profile => profile.Source == SoulseekSource).ToArray();",
            order.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);

        // KnownProfiles is the ladder plus the separate Atmos track.
        Assert.Contains(
            "KnownProfiles = StereoLadder.Concat(AtmosPriority).ToArray();",
            order.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);

        // A Soulseek code may never act as a target-quality seek, or a request for "flac" would skip every
        // catalogue step above Soulseek's own FLAC.
        Assert.Contains(
            "!string.Equals(step.Source, SoulseekSource, StringComparison.OrdinalIgnoreCase)",
            order,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NoPerEngineQueueBackgroundServiceWasAdded()
    {
        // Two guardrail tests forbid this pattern; a Soulseek-specific worker would break both.
        var program = ReadRepoFile("DeezSpoTag.Web", "Program.cs");
        var workers = ReadRepoFile("DeezSpoTag.Workers", "Program.cs");

        Assert.DoesNotContain("SoulseekQueueBackgroundService", program);
        Assert.DoesNotContain("SoulseekQueueBackgroundService", workers);
    }

    [Fact]
    public void ActivitiesShowsSoulseekLikeAnyOtherEngine()
    {
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "ActivitiesController.cs");
        var view = ReadRepoFile("DeezSpoTag.Web", "Views", "Activities", "Index.cshtml");

        Assert.Contains("\"soulseek\" => \"\"", controller);
        Assert.Contains("raw.includes('soulseek')", view);
    }

    [Fact]
    public void StaleCleanupIsExposedRatherThanRunByANewWorker()
    {
        var api = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs");

        // Cleanup is reachable on demand and also from the existing queue loop, so no new hosted service is
        // needed for it.
        Assert.Contains("[HttpPost(\"searches/cleanup\")]", api);
        Assert.Contains("[HttpPost(\"downloads/cleanup\")]", api);
        Assert.Contains("CleanupStaleSearchesAsync", api);
        Assert.Contains("CleanupStaleTransfersAsync", api);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var root = ResolveRepoRoot();
        return System.IO.File.ReadAllText(System.IO.Path.Join(new[] { root }.Concat(parts).ToArray()));
    }

    private static string ResolveRepoRoot()
    {
        var current = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (System.IO.Directory.Exists(System.IO.Path.Join(current.FullName, "DeezSpoTag.Services")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}

/// <summary>
///     Behaviour tests for the exact-peer browse action, run against a real controller instance.
/// </summary>
/// <remarks>
///     <para>
///         The reason these exist separately from the source-string guards is that the browse action's
///         whole contract is its HTTP status. A guardrail that finds the word <c>SoulseekBrowseFilePolicy
///         .Evaluate</c> in the method body passes just as happily while the action returns 500 for every
///         peer that does not answer, which is exactly the failure that stopped the album drawer and the
///         checkboxes behind it from ever appearing.
///     </para>
///     <para>
///         The only collaborator the action under test reaches is the browse service, so everything else is
///         either constructed for real or left unbuilt and documented. A fake here would be a second
///         statement about behaviour, which is the thing these tests are trying to remove.
///     </para>
/// </remarks>
[Collection("Settings Config Isolation")]
public sealed class SoulseekBrowseEndpointBehaviorTest : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _previousConfigDir;
    private readonly string _previousDataDir;
    private readonly StubSoulseekSearchService _search = new();

    public SoulseekBrowseEndpointBehaviorTest()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-soulseek-browse-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _previousConfigDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR");
        _previousDataDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR");
        Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", _tempRoot);
        Environment.SetEnvironmentVariable("DEEZSPOTAG_DATA_DIR", _tempRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", _previousConfigDir);
        Environment.SetEnvironmentVariable("DEEZSPOTAG_DATA_DIR", _previousDataDir);
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked temp file must not fail the run.
        }
    }

    [Fact]
    public async Task Browse_ProjectsTheExactDirectoryWithEligibleAndIneligibleFiles()
    {
        _search.Result =
        [
            new SlskdDirectory
            {
                Directory = "@@peer\\Music\\Artist\\Album",
                FileCount = 2,
                Files =
                [
                    Raw("@@peer\\Music\\Artist\\Album\\01 - One.flac"),
                    Raw("@@peer\\Music\\Artist\\Album\\cover.jpg")
                ]
            }
        ];

        var result = await Controller().BrowseDirectory("  peer  ", "@@peer\\Music\\Artist\\Album", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Serialize(ok.Value);

        // The trimmed peer is the one that is reported and the one that is asked for: slskd 404s on a
        // username with the surrounding whitespace still attached.
        Assert.Equal("peer", _search.RequestedUsername);

        // The peer's whole directory list is asked for rather than one folder. slskd's directory endpoint
        // answers with leaf names and only for a share root, so asking it for a nested folder returns an
        // empty listing that is indistinguishable from a folder that does not exist. The requested folder is
        // therefore found in the list by its real path, which is what lets the action tell an empty share from
        // an unpublished folder.
        Assert.Null(_search.RequestedDirectory);
        Assert.Equal("peer", body.GetProperty("username").GetString());
        Assert.Equal("@@peer/Music/Artist/Album", body.GetProperty("remoteDirectory").GetString());

        var directory = body.GetProperty("directories").EnumerateArray().Single();
        Assert.Equal("@@peer/Music/Artist/Album", directory.GetProperty("remoteDirectory").GetString());
        Assert.Equal(1, directory.GetProperty("eligibleFileCount").GetInt32());
        Assert.Equal(1, directory.GetProperty("nonAudioFileCount").GetInt32());

        var files = directory.GetProperty("files").EnumerateArray().ToArray();
        Assert.True(files[0].GetProperty("eligible").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, files[0].GetProperty("rejectedBecause").ValueKind);
        Assert.False(files[1].GetProperty("eligible").GetBoolean());
        Assert.Equal("non_audio_file", files[1].GetProperty("rejectedBecause").GetString());
    }

    /// <summary>
    ///     A folder the peer does not publish is reported as absent, not as an empty folder.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The drawer used to ask slskd for a folder named from a search result and render whatever came
    ///         back. slskd's directory endpoint answers with leaf names only and only for a share root, so a
    ///         real nested folder came back empty and so did a folder that never existed - the same answer for
    ///         both. Peers holding tens of thousands of files were reported as holding nothing, because the
    ///         folder in the search result was not one they publish.
    ///     </para>
    ///     <para>
    ///         The distinction matters to the reader: an empty folder is a fact about a peer, and a folder that
    ///         is not there means the path was wrong. Collapsing them into one is what made the app accuse
    ///         peers of having nothing they plainly had.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Browse_RefusesAFolderThePeerDoesNotPublishRatherThanReportingItEmpty()
    {
        _search.Result =
        [
            new SlskdDirectory { Directory = "@@peer\\Music\\Massive Attack\\Mezzanine", FileCount = 11, Files = [] },
            new SlskdDirectory { Directory = "@@peer\\Music\\Massive Attack\\Mezzanine (Remixes)", FileCount = 11, Files = [] }
        ];

        // Nearest to the two published folders, but published by neither. Resolving it to either would file
        // one release's tracks under the name of the other.
        var result = await Controller().BrowseDirectory("peer", "@@peer\\Music\\Massive Attack\\Mezzanine (1998)", CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var body = Serialize(notFound.Value);

        Assert.Equal("folder_not_published", body.GetProperty("reasonCode").GetString());

        // Not retryable: asking again cannot make the peer publish a folder it does not have, so offering
        // Retry here would be an invitation to press a button that cannot succeed.
        Assert.False(body.GetProperty("retryable").GetBoolean());
    }

    /// <summary>
    ///     A folder the peer does publish is served, whichever way the requester wrote its separators.
    /// </summary>
    [Fact]
    public async Task Browse_ServesAPublishedFolderWrittenWithEitherSeparator()
    {
        _search.Result =
        [
            new SlskdDirectory
            {
                Directory = "@@peer\\Music\\100 gecs\\(2020) Snake Oil",
                FileCount = 1,
                Files = [Raw("@@peer\\Music\\100 gecs\\(2020) Snake Oil\\01 - Track.flac")]
            }
        ];

        var result = await Controller().BrowseDirectory("peer", "@@peer/Music/100 gecs/(2020) Snake Oil", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var directory = Serialize(ok.Value).GetProperty("directories").EnumerateArray().Single();

        // The peer's own spelling comes back, so the drawer reports the folder the peer published rather than
        // the path the request happened to use.
        Assert.Equal("@@peer/Music/100 gecs/(2020) Snake Oil", directory.GetProperty("remoteDirectory").GetString());
        Assert.Single(directory.GetProperty("files").EnumerateArray());
    }

    [Fact]
    public async Task Browse_TreatsAnEmptyPeerFolderAsASuccessfulEmptyAnswer()
    {
        _search.Result = [new SlskdDirectory { Directory = "@@peer\\Music\\Empty", FileCount = 0, Files = [] }];

        var result = await Controller().BrowseDirectory("peer", "@@peer\\Music\\Empty", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var directory = Serialize(ok.Value).GetProperty("directories").EnumerateArray().Single();

        // An empty folder is a fact about the peer, not a fault. Reporting it as a failure is what made a
        // legitimate release look like a broken one.
        Assert.Equal(0, directory.GetProperty("fileCount").GetInt32());
        Assert.Empty(directory.GetProperty("files").EnumerateArray());
    }

    [Fact]
    public async Task Browse_ReturnsNoDirectoriesAtAllWhenThePeerSharedNothing()
    {
        _search.Result = [];

        var result = await Controller().BrowseDirectory("peer", "@@peer\\Music", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Empty(Serialize(ok.Value).GetProperty("directories").EnumerateArray());
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public async Task Browse_RefusesAnEmptyPeerWithoutAskingTheNetwork(string username)
    {
        var result = await Controller().BrowseDirectory(username, "@@peer\\Music", CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var body = Serialize(bad.Value);
        Assert.Equal("username_required", body.GetProperty("reasonCode").GetString());
        Assert.False(body.GetProperty("retryable").GetBoolean());
        Assert.Equal(0, _search.CallCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public async Task Browse_RefusesAnEmptyDirectoryWithoutAskingTheNetwork(string? path)
    {
        var result = await Controller().BrowseDirectory("peer", path, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var body = Serialize(bad.Value);
        Assert.Equal("remote_directory_required", body.GetProperty("reasonCode").GetString());
        Assert.False(body.GetProperty("retryable").GetBoolean());
        Assert.Equal(0, _search.CallCount);
    }

    [Fact]
    public async Task Browse_TranslatesAPeerTimeoutIntoARetryableGatewayFailureRatherThanAnApplicationError()
    {
        _search.Failure = new SlskdApiException(0, null, "slskd did not respond in time.");

        var result = await Controller().BrowseDirectory("peer", "@@peer\\Music", CancellationToken.None);

        var gateway = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, gateway.StatusCode);
        var body = Serialize(gateway.Value);
        Assert.Equal("peer_browse_failed", body.GetProperty("reasonCode").GetString());
        Assert.True(body.GetProperty("retryable").GetBoolean());
        Assert.Equal("slskd did not respond in time.", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Browse_TranslatesAnSlskdServerFailureIntoARetryableGatewayFailure()
    {
        _search.Failure = new SlskdApiException(500, "boom", "slskd returned HTTP 500.");

        var result = await Controller().BrowseDirectory("peer", "@@peer\\Music", CancellationToken.None);

        var gateway = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, gateway.StatusCode);
        Assert.Equal("peer_browse_failed", Serialize(gateway.Value).GetProperty("reasonCode").GetString());
    }

    /// <summary>
    ///     The regression for the reported HTTP 500.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="SoulseekUnavailableException" /> is raised by the browse service when Soulseek has
    ///         no stored slskd connection at all. It is a plain <see cref="Exception" /> and not a
    ///         <see cref="SlskdApiException" />, so the action's only catch - for slskd failures - never saw
    ///         it and the exception escaped as an unhandled application error. The drawer showed the generic
    ///         failure text, and because the checkboxes only exist once a browse has succeeded, neither the
    ///         listing nor a single checkbox ever appeared.
    ///     </para>
    ///     <para>
    ///         An expected, already-classified Soulseek failure must never surface as an application fault.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Browse_TranslatesAnUnconfiguredSoulseekIntoAServiceUnavailableInsteadOfAnUnhandledError()
    {
        _search.Failure = new SoulseekUnavailableException("Soulseek is not configured.");

        var result = await Controller().BrowseDirectory("peer", "@@peer\\Music", CancellationToken.None);

        var unavailable = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        var body = Serialize(unavailable.Value);
        Assert.Equal("soulseek_unavailable", body.GetProperty("reasonCode").GetString());
        Assert.True(body.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task Browse_ReportsACancelledRequestAsCancellationRatherThanAsAPeerFailure()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _search.Failure = new OperationCanceledException(cancellation.Token);

        // Rethrown rather than answered: the reader closed the drawer, and inventing a peer error for that
        // would put a false "this peer failed" message on the row they just dismissed.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Controller().BrowseDirectory("peer", "@@peer\\Music", cancellation.Token));
    }

    [Fact]
    public async Task Browse_LeavesAGenuinelyUnexpectedExceptionObservable()
    {
        _search.Failure = new InvalidOperationException("the projection is wrong");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Controller().BrowseDirectory("peer", "@@peer\\Music", CancellationToken.None));
    }

    /// <summary>
    ///     The cover is resolved once for the search and handed to the client with the search id, so the tab
    ///     has artwork to render without asking the catalogue once per candidate.
    /// </summary>
    [Fact]
    public async Task SearchStart_CarriesTheCatalogueCoverWithTheSearchId()
    {
        var slskd = new StubSlskdClient { StartedSearch = new SlskdSearch { Id = Guid.NewGuid() } };
        var controller = Controller(slskd, () => new StubCatalogueHandler(
            """{"data":[{"id":1,"title":"Roygbiv","artist":{"name":"Boards of Canada"},"album":{"cover_medium":"https://images.example.test/cover.jpg"}}]}"""));

        var cover = await SearchStartCoverAsync(controller);

        Assert.Equal("https://images.example.test/cover.jpg", cover);
    }

    /// <summary>
    ///     A catalogue that cannot be reached is not a search failure. The lookup is decoration, so the search
    ///     is still started and the cover simply comes back empty for the card's placeholder.
    /// </summary>
    [Fact]
    public async Task SearchStart_StillStartsTheSearchWhenTheCatalogueCannotBeReached()
    {
        var slskd = new StubSlskdClient { StartedSearch = new SlskdSearch { Id = Guid.NewGuid() } };
        var controller = Controller(slskd, () => new ThrowingHandler(new HttpRequestException("the catalogue is down")));

        var result = await controller.Search(
            new SoulseekSearchRequestModel("Boards of Canada", "Roygbiv"), CancellationToken.None);

        Assert.Equal(StatusCodes.Status202Accepted, Assert.IsType<AcceptedResult>(result).StatusCode);
        Assert.True(slskd.StartCalls > 0, "The search was not started at all.");
    }

    private static async Task<string?> SearchStartCoverAsync(SoulseekApiController controller)
    {
        var result = Assert.IsType<AcceptedResult>(await controller.Search(
            new SoulseekSearchRequestModel("Boards of Canada", "Roygbiv"), CancellationToken.None));

        Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);
        var body = Serialize(result.Value);
        Assert.True(body.TryGetProperty("searchId", out _), "The response no longer carries the search id.");
        Assert.True(body.TryGetProperty("coverUrl", out var cover), "The response no longer carries a cover url.");
        return cover.ValueKind == JsonValueKind.Null ? null : cover.GetString();
    }

    private static SlskdFile Raw(string filename) => new()
    {
        Filename = filename,
        Size = 1024,
        BitRate = 900,
        BitDepth = 16,
        SampleRate = 44100,
        Length = 180
    };

    private static JsonElement Serialize(object? value)
        => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>
    ///     Builds the controller with the browse path fully wired.
    /// </summary>
    /// <remarks>
    ///     <c>ISlskdClient</c> and <c>DownloadIntentService</c> are left unbuilt: the browse action never
    ///     reads either, and standing up the intent service means resolving the whole download stack, which
    ///     would test nothing this action does. The search-start test below supplies a real slskd stub,
    ///     because that action does call it.
    /// </remarks>
    [Theory]
    [InlineData("queued")]
    [InlineData("downloading")]
    [InlineData("pending")]
    [InlineData("completed")]
    [InlineData("downloaded")]
    public async Task RetryRefusesActiveOrCompletedQueueOwners(string status)
    {
        var (repository, queue, settings) = RetryDependencies();
        var id = await SeedRetryAsync(repository, queue, "retry-owner", status);
        var result = await Controller().RetryDownload(id, null!, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(status, (await queue.GetByUuidAsync("retry-owner"))!.Status);
    }

    [Fact]
    public async Task RetryRequeuesTheSamePinnedPayloadThroughTheSharedApplication()
    {
        var (repository, queue, settings) = RetryDependencies();
        var id = await SeedRetryAsync(repository, queue, "retry-pinned", "failed");
        var cancellation = new DownloadCancellationRegistry();
        var listener = new RetryListener();
        var scheduler = new DownloadRetryScheduler(queue, settings, null!, listener,
            NullLogger<DownloadRetryScheduler>.Instance, cancellation);
        var app = new DeezSpoTagApp(NullLogger<DeezSpoTagApp>.Instance,
            new DeezSpoTagApp.Dependencies(settings, listener, scheduler, queue, cancellation, new ClosedExecutionGate()),
            new ServiceCollection().BuildServiceProvider());

        var result = Assert.IsType<AcceptedResult>(await Controller().RetryDownload(id, app, CancellationToken.None));
        var response = Serialize(result.Value);
        Assert.Equal("retry-pinned", response.GetProperty("queueUuid").GetString());
        Assert.False(response.TryGetProperty("transferId", out _));
        var row = (await queue.GetByUuidAsync("retry-pinned"))!;
        Assert.Equal("queued", row.Status);
        var payload = JsonSerializer.Deserialize<SoulseekQueueItem>(row.PayloadJson!)!;
        Assert.Equal("chosen-peer", payload.SoulseekUsername);
        Assert.Equal("Album/track.flac", payload.SoulseekRemotePath);
        Assert.Equal("FLAC", payload.SoulseekQualityCode);
        Assert.Equal(7, row.DestinationFolderId);
        Assert.Equal("keep-enrichment-intent", JsonDocument.Parse(row.PayloadJson!).RootElement.GetProperty("EnrichmentIntentMarker").GetString());
        Assert.Single(await repository.GetTransfersForQueueAsync(row.QueueUuid));
    }

    [Theory]
    [InlineData("successful")]
    [InlineData("missing-owner")]
    [InlineData("wrong-engine")]
    [InlineData("newer-active")]
    [InlineData("no-plan")]
    [InlineData("retry-disabled")]
    [InlineData("running-transfer")]
    public async Task RetryRejectsInvalidOrStaleTransferWork(string reason)
    {
        var (repository, queue, settings) = RetryDependencies();
        var id = await SeedRetryAsync(repository, queue, "retry-invalid", "failed");
        var row = (await repository.GetTransferAsync(id))!;
        if (reason == "successful" || reason == "running-transfer")
            await repository.UpsertTransferAsync(new SoulseekTransferStatus(id, row.Username, row.Filename,
                reason == "successful" ? "completed" : "in_progress", reason != "running-transfer", reason == "successful",
                100, 100, 0, null, 100), row.QueueUuid, row.ExpectedPath, reason == "successful");
        if (reason == "missing-owner")
            await repository.UpsertTransferAsync(new SoulseekTransferStatus(id, row.Username, row.Filename, "failed", true, false,
                0, 100, 0, null, 0), "absent-queue", row.ExpectedPath, false);
        if (reason == "wrong-engine") await queue.UpdateEngineAsync("retry-invalid", "deezer");
        if (reason == "newer-active")
            await repository.UpsertTransferAsync(new SoulseekTransferStatus(Guid.NewGuid(), row.Username, row.Filename,
                "in_progress", false, false, 0, 100, 0, null, 0), row.QueueUuid, row.ExpectedPath, false);
        if (reason == "no-plan") await queue.UpdatePayloadAsync("retry-invalid", "{}");
        if (reason == "retry-disabled")
        {
            var config = settings.LoadSettings(); config.Soulseek.AutoRetryIncompleteTransfers = false; settings.SaveSettings(config);
        }
        var cancellation = new DownloadCancellationRegistry();
        var listener = new RetryListener();
        var app = new DeezSpoTagApp(NullLogger<DeezSpoTagApp>.Instance,
            new DeezSpoTagApp.Dependencies(settings, listener,
                new DownloadRetryScheduler(queue, settings, null!, listener, NullLogger<DownloadRetryScheduler>.Instance, cancellation),
                queue, cancellation, new ClosedExecutionGate()), new ServiceCollection().BuildServiceProvider());
        Assert.IsType<ConflictObjectResult>(await Controller().RetryDownload(id, app, CancellationToken.None));
        Assert.Equal("failed", (await queue.GetByUuidAsync("retry-invalid"))!.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(404)]
    public async Task DeleteRemovesLocalHistoryAfterRemoteRemoval(int remoteStatus)
    {
        var (repository, queue, settings) = RetryDependencies();
        var id = await SeedRetryAsync(repository, queue, "delete-terminal", "failed");
        var transport = new StubSoulseekTransferService { CancelError = remoteStatus == 0 ? null : new SlskdApiException(remoteStatus, null, "missing") };
        var otherId = await SeedRetryAsync(repository, queue, "delete-kept", "failed");
        var ownedPath = Path.Join(_tempRoot, "delivered.flac");
        var owner = Guid.NewGuid();
        Assert.True(await repository.TryClaimSourcePathsAsync(owner, "delete-terminal", "chosen-peer", "Album/track.flac", [ownedPath]));
        await repository.AttachSourceClaimsAsync(owner, id);
        File.WriteAllBytes(ownedPath, new byte[100]);
        var controller = Controller(transfer: transport);
        Assert.IsType<OkObjectResult>(await controller.DeleteDownload(id, CancellationToken.None));
        Assert.Empty(await repository.GetSourceClaimsForTransferAsync(id));
        Assert.True(File.Exists(ownedPath));
        Assert.NotNull(await repository.GetTransferAsync(otherId));
        Assert.NotNull(await queue.GetByUuidAsync("delete-terminal"));
        Assert.IsType<NotFoundObjectResult>(await controller.GetDownload(id, CancellationToken.None));
        Assert.Empty(await repository.GetTransfersForQueueAsync("delete-terminal"));
        Assert.IsType<NotFoundObjectResult>(await controller.DeleteDownload(id, CancellationToken.None));
        Assert.Equal(1, transport.CancelCalls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(500)]
    [InlineData(0)]
    public async Task DeletePreservesHistoryWhenRemoteRemovalFails(int remoteStatus)
    {
        var (repository, queue, settings) = RetryDependencies();
        var id = await SeedRetryAsync(repository, queue, "delete-failure", "failed");
        var transport = new StubSoulseekTransferService { CancelError = new SlskdApiException(remoteStatus, null, "unavailable") };
        await Assert.ThrowsAsync<SlskdApiException>(() => Controller(transfer: transport).DeleteDownload(id, CancellationToken.None));
        Assert.NotNull(await repository.GetTransferAsync(id));
    }

    [Fact]
    public async Task FailedSearchReadsExposeThePersistedErrorInsteadOfNoNetworkResponses()
    {
        var (repository, _, _) = RetryDependencies();
        var id = Guid.NewGuid();
        await repository.RecordSearchStartedAsync(id, "artist title", SoulseekSearchMode.Manual, null);
        await repository.RecordSearchFailedAsync(id, 42, 3, "response_retrieval_failed", SoulseekSearchService.ResponseRetrievalFailedMessage);
        foreach (var result in new[] { await Controller().GetSearch(id, CancellationToken.None), await Controller().GetSearchResults(id, CancellationToken.None) })
        {
            var json = Serialize(Assert.IsType<OkObjectResult>(result).Value);
            Assert.True(json.GetProperty("failed").GetBoolean());
            Assert.False(json.GetProperty("completed").GetBoolean());
            Assert.Equal("response_retrieval_failed", json.GetProperty("outcome").GetString());
            Assert.Equal(3, json.GetProperty("responseCount").GetInt32());
        }
    }

    private (SoulseekRepository, DownloadQueueRepository, DeezSpoTagSettingsService) RetryDependencies()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:Queue"] = $"Data Source={Path.Join(_tempRoot, "queue.db")}" }).Build();
        var settings = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var config = settings.LoadSettings(); config.Soulseek.AutoRetryIncompleteTransfers = true; settings.SaveSettings(config);
        return (new SoulseekRepository(configuration, NullLogger<SoulseekRepository>.Instance),
            new DownloadQueueRepository(configuration, NullLogger<DownloadQueueRepository>.Instance), settings);
    }

    private static async Task<Guid> SeedRetryAsync(SoulseekRepository repository, DownloadQueueRepository queue, string uuid, string status)
    {
        var payload = new SoulseekQueueItem { Id = uuid, SoulseekUsername = "chosen-peer", SoulseekRemotePath = "Album/track.flac",
            SoulseekQualityCode = "FLAC", SoulseekRemoteSizeBytes = 100, Title = "Track", Artist = "Artist" };
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(payload))!.AsObject();
        node["FallbackPlan"] = JsonSerializer.SerializeToNode(new[] { new { Engine = "soulseek", Quality = "FLAC" } });
        node["EnrichmentIntentMarker"] = "keep-enrichment-intent";
        var row = new DownloadQueueItem(0, uuid, "soulseek", "Artist", "Track", null, null, null, null, null, null, null,
            null, null, null, null, 7, null, null, uuid, null, "pending", status, node.ToJsonString(),
            0, 0, 1, "failed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.NotNull(await queue.EnqueueAsync(row));
        var id = Guid.NewGuid();
        await repository.UpsertTransferAsync(new SoulseekTransferStatus(id, "chosen-peer", "Album/track.flac", "failed",
            true, false, 0, 100, 0, null, 0), uuid, "staging.flac", false);
        return id;
    }

    private sealed class RetryListener : IDeezSpoTagListener
    {
        public void Send(string eventName, object? data = null) { }
    }

    private sealed class ClosedExecutionGate : IDownloadQueueExecutionGate
    {
        public Task<DownloadQueueExecutionDecision> EvaluateDownloadExecutionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DownloadQueueExecutionDecision(false, "test", "Keep the admitted item queued."));
    }

    private SoulseekApiController Controller(
        ISlskdClient? slskd = null,
        Func<HttpMessageHandler>? catalogueHandler = null,
        ISoulseekTransferService? transfer = null)
    {
        // The databases are supplied through configuration rather than the QUEUE_DB and LIBRARY_DB
        // environment variables. Those variables are process-wide and the repositories prefer them over
        // configuration, so setting them here would silently move every other test's database too.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Queue"] = $"Data Source={Path.Join(_tempRoot, "queue.db")}",
                ["ConnectionStrings:Library"] = $"Data Source={Path.Join(_tempRoot, "library.db")}"
            })
            .Build();
        var settings = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var library = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
        var soulseekSettings = new SoulseekSettingsService(settings, library, NullLogger<SoulseekSettingsService>.Instance);
        var session = new DeezerSessionManager(
            NullLogger<DeezerSessionManager>.Instance,
            () => new DeezSpoTagSettings(),
            catalogueHandler);

        return new SoulseekApiController(
            new StubSoulseekConnectionService(),
            _search,
            transfer ?? new StubSoulseekTransferService(),
            new StubSoulseekCredentialProvider(),
            slskd ?? new StubSlskdClient(),
            soulseekSettings,
            new StubSettingsService(),
            new SoulseekRepository(configuration, NullLogger<SoulseekRepository>.Instance),
            new DownloadQueueRepository(configuration, NullLogger<DownloadQueueRepository>.Instance),
            intentService: null!,
            new EnhancedPathTemplateProcessor(NullLogger<EnhancedPathTemplateProcessor>.Instance),
            new DeezerClient(NullLogger<DeezerClient>.Instance, session),
            NullLogger<SoulseekApiController>.Instance);
    }

    /// <summary>
    ///     A catalogue that answers with one track, so the cover lookup is deterministic and offline.
    /// </summary>
    private sealed class StubCatalogueHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StubCatalogueHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _failure;

        public ThrowingHandler(Exception failure) => _failure = failure;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(_failure);
    }

    private sealed class StubSoulseekSearchService : ISoulseekSearchService
    {
        public IReadOnlyList<SlskdDirectory> Result { get; set; } = [];

        public Exception? Failure { get; set; }

        public int CallCount { get; private set; }

        public string? RequestedUsername { get; private set; }

        public string? RequestedDirectory { get; private set; }

        public Task<IReadOnlyList<SlskdDirectory>> BrowseAsync(
            string username,
            string? directory = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            RequestedUsername = username;
            RequestedDirectory = directory;
            if (Failure is not null)
            {
                return Task.FromException<IReadOnlyList<SlskdDirectory>>(Failure);
            }

            // Mirrors the real service: asking for one directory returns only that directory, and asking for
            // the peer's list (a null directory) returns everything it publishes. The controller relies on this
            // to tell a folder that exists from one that does not, so a double that ignored the requested path
            // would make an unpublished folder indistinguishable from a published one.
            if (directory is null)
            {
                return Task.FromResult(Result);
            }

            var wanted = SoulseekRemotePath.Normalize(directory);
            var matched = Result
                .Where(entry => string.Equals(
                    SoulseekRemotePath.Normalize(entry.Directory),
                    wanted,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return Task.FromResult<IReadOnlyList<SlskdDirectory>>(matched);
        }

        public Task<SoulseekSearchOutcome> SearchAsync(
            SoulseekSearchTarget target,
            SoulseekSearchMode mode = SoulseekSearchMode.Manual,
            string? queueUuid = null,
            CancellationToken cancellationToken = default,
            string? requiredQualityCode = null)
            => throw new NotSupportedException();

        public Task<SoulseekSearchOutcome> ObserveAsync(
            SoulseekSearchTarget target,
            Guid searchId,
            string searchText,
            SoulseekSearchMode mode,
            string? queueUuid,
            CancellationToken cancellationToken = default,
            string? requiredQualityCode = null)
            => throw new NotSupportedException();

        public Task<int> CleanupStaleSearchesAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class StubSoulseekConnectionService : ISoulseekConnectionService
    {
        public Task<SoulseekConnectionStatus> GetStatusAsync(bool force = false, CancellationToken cancellationToken = default)
            => Task.FromResult(Connected());

        public Task<SoulseekConnectionStatus> EnsureAvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Connected());

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        /// <inheritdoc />
        public Task<SoulseekConnectionStatus> GetEligibilityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Connected());

        public void Invalidate()
        {
        }

        private static SoulseekConnectionStatus Connected()
            => new(SoulseekConnectionState.Connected, "slskd is connected to Soulseek.", null, null, DateTimeOffset.UtcNow, 12);
    }

    private sealed class StubSoulseekCredentialProvider : ISoulseekCredentialProvider
    {
        public Task<SlskdCredentials?> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<SlskdCredentials?>(new("http://127.0.0.1:5030", "test-key"));
    }

    private sealed class StubSettingsService : ISettingsService
    {
        public DeezSpoTagSettings LoadSettings() => new();

        public void SaveSettings(DeezSpoTagSettings settings)
        {
        }
    }

    private sealed class StubSoulseekTransferService : ISoulseekTransferService
    {
        public Exception? CancelError { get; set; }
        public int CancelCalls { get; private set; }

        public Task<Guid?> EnqueueAsync(string username, string filename, long size, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SoulseekTransferStatus?> FindTransferAsync(
            string username,
            string filename,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SoulseekTransferStatus?> GetStatusAsync(string username, Guid transferId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SoulseekCompletedFile?> WaitForCompletionAsync(
            string username,
            Guid transferId,
            string expectedPath,
            string queueUuid,
            string? completedDownloadsRoot = null,
            Func<double, double, Task>? progress = null,
            CancellationToken cancellationToken = default,
            CancellationToken hostStoppingToken = default)
            => throw new NotSupportedException();

        public Task CancelAsync(string username, Guid transferId, bool remove = false, CancellationToken cancellationToken = default)
        {
            CancelCalls++;
            return CancelError is null ? Task.CompletedTask : Task.FromException(CancelError);
        }

        public Task<int> CleanupStaleTransfersAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string>> FetchSidecarsAsync(
            string username,
            IReadOnlyList<string> remotePaths,
            string outputDirectory,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class StubSlskdClient : ISlskdClient
    {
        public SlskdSearch StartedSearch { get; set; } = new() { Id = Guid.NewGuid() };

        public int StartCalls { get; private set; }

        public Task<SlskdSearch> StartSearchAsync(SlskdCredentials credentials, SlskdSearchRequest request, CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return Task.FromResult(StartedSearch);
        }

        public Task<SlskdServerState> GetServerStateAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SlskdServerState> ConnectAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DisconnectAsync(SlskdCredentials credentials, string? message = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SlskdSearch?> GetSearchAsync(SlskdCredentials credentials, Guid searchId, bool includeResponses = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdSearch>> ListSearchesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdSearchResponse>> GetSearchResponsesAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CancelSearchAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteSearchAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SlskdUserStatus?> GetUserStatusAsync(SlskdCredentials credentials, string username, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdDirectory>> BrowseUserAsync(SlskdCredentials credentials, string username, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdDirectory>> BrowseUserDirectoryAsync(SlskdCredentials credentials, string username, string? directory, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdTransfer>> EnqueueDownloadsAsync(SlskdCredentials credentials, string username, IReadOnlyList<SlskdQueueDownload> downloads, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdTransfer>> ListDownloadsAsync(SlskdCredentials credentials, bool includeRemoved = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SlskdTransfer?> GetDownloadAsync(SlskdCredentials credentials, string username, Guid transferId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int?> GetDownloadPositionAsync(SlskdCredentials credentials, string username, Guid transferId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CancelDownloadAsync(SlskdCredentials credentials, string username, Guid transferId, bool remove = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ClearCompletedDownloadsAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdShare>> ListSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdDirectory>> BrowseSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RescanSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
