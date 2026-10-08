using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PublicApiProviderRenderingGuardrailTest
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    [Fact]
    public void ProviderPanels_UseOnePlatformScopedRenderingPath()
    {
        var login = ReadSource("DeezSpoTag.Web/Views/Login/Index.cshtml");

        Assert.Contains("const PUBLIC_API_PROVIDER_CONFIG = Object.freeze", login, StringComparison.Ordinal);
        Assert.Contains("endpoint: '/api/platform-auth/qobuz/providers'", login, StringComparison.Ordinal);
        Assert.Contains("endpoint: '/api/platform-auth/tidal/providers'", login, StringComparison.Ordinal);
        Assert.Contains("endpoint: '/api/platform-auth/amazonmusic/providers'", login, StringComparison.Ordinal);
        Assert.Contains("function renderPublicApiProviderPanel(platformId", login, StringComparison.Ordinal);
        Assert.Contains("function createPublicApiProviderRow(config", login, StringComparison.Ordinal);
        Assert.Contains("data-public-api-provider-id", login, StringComparison.Ordinal);

        Assert.DoesNotContain("renderQobuzProviders", login, StringComparison.Ordinal);
        Assert.DoesNotContain("renderTidalProviders", login, StringComparison.Ordinal);
        Assert.DoesNotContain("renderAmazonMusicProviders", login, StringComparison.Ordinal);
        Assert.DoesNotContain("qobuz-provider-", login, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderPanelBinder_SharesTheReadyCallbackScopeWithAsyncClickBinder()
    {
        var login = ReadSource("DeezSpoTag.Web/Views/Login/Index.cshtml");
        var readyCallback = ExtractBetween(
            login,
            "document.addEventListener('DOMContentLoaded', function()",
            "\n});\n\nfunction bindProviderPanelToggle");

        Assert.Contains("function bindAsyncClick(elementId, handler)", readyCallback, StringComparison.Ordinal);
        Assert.Contains("function bindPublicApiProviderPanel(platformId)", readyCallback, StringComparison.Ordinal);
        Assert.Contains("Object.keys(PUBLIC_API_PROVIDER_CONFIG).forEach(bindPublicApiProviderPanel);", readyCallback, StringComparison.Ordinal);

        var afterReadyCallback = login[(login.IndexOf("\n});\n\nfunction bindProviderPanelToggle", StringComparison.Ordinal) + 1)..];
        Assert.DoesNotContain("function bindPublicApiProviderPanel(platformId)", afterReadyCallback, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderToggle_PatchesReturnedProviderWithoutGlobalAuthReload()
    {
        var login = ReadSource("DeezSpoTag.Web/Views/Login/Index.cshtml");
        var toggleBody = ExtractBetween(
            login,
            "async function setPublicApiProviderEnabled",
            "async function checkPublicApiProviders");

        Assert.Contains("const updatedProvider = await response.json();", toggleBody, StringComparison.Ordinal);
        Assert.Contains("state.pendingMutations.add(providerId);", toggleBody, StringComparison.Ordinal);
        Assert.Contains("provider?.id === providerId ? updatedProvider : provider", toggleBody, StringComparison.Ordinal);
        Assert.Contains("renderPublicApiProviderPanel(platformId);", toggleBody, StringComparison.Ordinal);
        Assert.Contains("void checkPublicApiProviders(platformId, { background: true });", toggleBody, StringComparison.Ordinal);
        Assert.DoesNotContain("loadPlatformAuthState", toggleBody, StringComparison.Ordinal);
        Assert.DoesNotContain("refreshConnectedPlatformsSidebar", toggleBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderLoading_IsRetryableAndRejectsStaleResponses()
    {
        var login = ReadSource("DeezSpoTag.Web/Views/Login/Index.cshtml");

        Assert.Contains("loadState: 'idle'", login, StringComparison.Ordinal);
        Assert.Contains("state.loadState = 'failed';", login, StringComparison.Ordinal);
        Assert.Contains("data-public-api-provider-retry", login, StringComparison.Ordinal);
        Assert.Contains("state.requestController = new AbortController();", login, StringComparison.Ordinal);
        Assert.Contains("state.requestSequence += 1;", login, StringComparison.Ordinal);
        Assert.Contains("isCurrentPublicApiProviderRequest(state, request.sequence)", login, StringComparison.Ordinal);
        Assert.Contains("state.mutationSequences.get(providerId) !== mutationSequence", login, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderPresentation_SeparatesConfigurationHealthAndSession()
    {
        var login = ReadSource("DeezSpoTag.Web/Views/Login/Index.cshtml");

        Assert.Contains("createPublicApiProviderBadge('configuration'", login, StringComparison.Ordinal);
        Assert.Contains("createPublicApiProviderBadge('health'", login, StringComparison.Ordinal);
        Assert.Contains("createPublicApiProviderBadge('session'", login, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"qobuzProviderSummary\"", login, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"tidalProviderSummary\"", login, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"amazonMusicProviderSummary\"", login, StringComparison.Ordinal);
        Assert.DoesNotContain("summaryId", login, StringComparison.Ordinal);
        Assert.DoesNotContain("Configuration: ${enabledProviders.length}", login, StringComparison.Ordinal);
        Assert.DoesNotContain("Health: ${healthText}", login, StringComparison.Ordinal);
        Assert.DoesNotContain("Download session: ${sessionText}", login, StringComparison.Ordinal);
    }

    [Fact]
    public void VerificationButtons_RenderInTheZarzRowWithVerifyFirstThenTheSharedVerifyAllControl()
    {
        var login = ReadSource("DeezSpoTag.Web/Views/Login/Index.cshtml");
        var rowFactory = ExtractBetween(
            login,
            "function createPublicApiProviderRow",
            "function createPublicApiProviderBadge");

        Assert.Contains("verificationProviderId: 'zarz-v2'", login, StringComparison.Ordinal);
        Assert.Contains("verificationProviderId: 'zarz'", login, StringComparison.Ordinal);
        Assert.Contains("verificationProviderId: 'zarz-api'", login, StringComparison.Ordinal);
        Assert.Contains("if (!sessionValid && provider?.id === config.verificationProviderId)", rowFactory, StringComparison.Ordinal);
        Assert.Contains("verify.textContent = 'Verify';", rowFactory, StringComparison.Ordinal);
        Assert.True(
            rowFactory.IndexOf("controls.append(badges, switchControl);", StringComparison.Ordinal)
            < rowFactory.IndexOf("controls.append(verify);", StringComparison.Ordinal));
        Assert.Contains("data-public-api-provider-verify", login, StringComparison.Ordinal);
        Assert.DoesNotContain("Verify public downloads", login, StringComparison.Ordinal);

        // The shared control is an addition beside the per-platform one, not a replacement for it: it is
        // appended after Verify, shares Verify's exact class list so the two read as the same action at two
        // scopes, and stays inside the same unverified Zarz-only gate.
        Assert.Contains("verifyAll.textContent = '(Verify all)';", rowFactory, StringComparison.Ordinal);
        Assert.Contains("verifyAll.dataset.publicApiProviderVerifyAll = config.platformId;", rowFactory, StringComparison.Ordinal);
        Assert.Equal(
            ExtractAssignedString(rowFactory, "verify.className"),
            ExtractAssignedString(rowFactory, "verifyAll.className"));
        Assert.True(
            rowFactory.IndexOf("controls.append(verify);", StringComparison.Ordinal)
            < rowFactory.IndexOf("controls.append(verifyAll);", StringComparison.Ordinal));
        Assert.True(
            rowFactory.LastIndexOf("controls.append(verifyAll);", StringComparison.Ordinal)
            < rowFactory.IndexOf("row.append(identity, controls);", StringComparison.Ordinal));

        // The shared control is gated identically to the per-platform one: the gate is evaluated at row-factory
        // body level and every verifyAll statement lives inside that gate's block (one level deeper), so the
        // control can never be hoisted above the gate or rendered unconditionally after it.
        Assert.Equal(
            1,
            BraceDepthAt(rowFactory, rowFactory.IndexOf("if (!sessionValid && provider?.id === config.verificationProviderId)", StringComparison.Ordinal)));
        Assert.Equal(
            2,
            BraceDepthAt(rowFactory, rowFactory.IndexOf("verifyAll = document.createElement('button');", StringComparison.Ordinal)));
        Assert.Equal(
            2,
            BraceDepthAt(rowFactory, rowFactory.IndexOf("controls.append(verifyAll);", StringComparison.Ordinal)));
        Assert.Equal(
            1,
            BraceDepthAt(rowFactory, rowFactory.IndexOf("row.append(identity, controls);", StringComparison.Ordinal)));
    }

    [Fact]
    public void VerifyAllControl_ReusesTheSinglePlatformVerificationPerConfiguredPlatform()
    {
        var login = ReadSource("DeezSpoTag.Web/Views/Login/Index.cshtml");
        var singlePlatform = ExtractBetween(
            login,
            "async function startPublicDownloadVerification(config)",
            "async function startAllPublicDownloadVerifications()");
        var allPlatforms = ExtractBetween(
            login,
            "async function startAllPublicDownloadVerifications()",
            "function extractPublicDownloadGrant");

        // One shared implementation. The per-platform entry point keeps only button handling around it, and
        // the all-platforms entry point calls that same implementation rather than repeating the fetch,
        // popup and grant handshake.
        Assert.Contains("await runPublicDownloadVerification(config);", singlePlatform, StringComparison.Ordinal);
        Assert.Contains("async function runPublicDownloadVerification(config)", login, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(login, "const response = await fetch(`${config.sessionEndpoint}/start`"));
        Assert.Equal(1, CountOccurrences(login, "config.popupName,"));
        Assert.Equal(1, CountOccurrences(login, "await waitForPublicDownloadGrant(verificationWindow, config);"));
        Assert.Contains("await runPublicDownloadVerification(config);", allPlatforms, StringComparison.Ordinal);

        // Every configured platform is covered, in configuration order, strictly sequentially.
        Assert.Contains(
            "for (const config of Object.values(PUBLIC_API_PROVIDER_CONFIG))",
            allPlatforms,
            StringComparison.Ordinal);

        // One platform failing must not strand the ones after it, and the reader has to be told which failed.
        Assert.Contains("failures.push(", allPlatforms, StringComparison.Ordinal);
        Assert.Contains("showToast(failures.join(' '), 'error');", allPlatforms, StringComparison.Ordinal);

        // The existing per-platform control keeps its own disabling behaviour and is never routed through
        // the all-platforms path, and a second click cannot start a competing run.
        Assert.Contains("if (button) button.disabled = true;", singlePlatform, StringComparison.Ordinal);
        Assert.Contains("if (button) button.disabled = false;", singlePlatform, StringComparison.Ordinal);
        Assert.DoesNotContain("startAllPublicDownloadVerifications", singlePlatform, StringComparison.Ordinal);
        Assert.Contains("if (startAllPublicDownloadVerifications.running) {", allPlatforms, StringComparison.Ordinal);
        Assert.Contains("document.querySelectorAll('[data-public-api-provider-verify-all]')", allPlatforms, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalAuthEndpoint_DoesNotLoadOrEmbedProviderState()
    {
        var controller = ReadSource("DeezSpoTag.Web/Controllers/Api/PlatformAuthApiController.cs");
        var getBody = ExtractBetween(
            controller,
            "public async Task<IActionResult> Get()",
            "[HttpGet(\"amazonmusic/providers\")]");

        Assert.DoesNotContain("GetPublicAmazonProvidersAsync", getBody, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPublicQobuzProvidersAsync", getBody, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPublicTidalProvidersAsync", getBody, StringComparison.Ordinal);
        Assert.DoesNotContain("publicApiOnline", getBody, StringComparison.Ordinal);
        Assert.DoesNotContain("providers =", getBody, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh", getBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LiveAccountChecks_AreScopedToTheirOwnTabs()
    {
        var controller = ReadSource("DeezSpoTag.Web/Controllers/Api/PlatformAuthApiController.cs");
        var login = ReadSource("DeezSpoTag.Web/Views/Login/Index.cshtml");

        Assert.Contains("[HttpGet(\"qobuz/account\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"soulseek/connection\")]", controller, StringComparison.Ordinal);
        Assert.Contains("platformAuthTask.then(refreshQobuzAccountState)", login, StringComparison.Ordinal);
        Assert.Contains("platformAuthTask.then(refreshSoulseekConnectionState)", login, StringComparison.Ordinal);
        Assert.Contains("function applyQobuzAccountState(auth)", login, StringComparison.Ordinal);
        Assert.Contains("function applySoulseekConnectionState(auth)", login, StringComparison.Ordinal);
    }

    [Fact]
    public void SidebarPublicApiStatus_UsesDedicatedStatusEndpointOnly()
    {
        var site = ReadSource("DeezSpoTag.Web/wwwroot/js/site.js");

        Assert.Contains("/api/platform-auth/public-providers/status", site, StringComparison.Ordinal);
        Assert.Contains("/api/platform-auth/public-providers/status?check=true", site, StringComparison.Ordinal);
        Assert.Contains("const [authResult, publicApiResult] = await Promise.allSettled", site, StringComparison.Ordinal);
        Assert.Contains("await this.applyPublicApiStatus(", site, StringComparison.Ordinal);
        Assert.Contains("publicApiStatus: ['qobuz', 'tidal', 'amazonmusic'].includes(id) ? 'unknown' : null", site, StringComparison.Ordinal);
        Assert.Contains("publicApiCheckedAt: Number(parsed.publicApiCheckedAt || 0) || null", site, StringComparison.Ordinal);
        Assert.Contains("const publicApiCheckDue = options?.checkPublicApis === true", site, StringComparison.Ordinal);
        Assert.DoesNotContain("refreshPublicApiSidebarStatus", site, StringComparison.Ordinal);
        Assert.DoesNotContain("tryAcquireConnectedPlatformsPollingLease", site, StringComparison.Ordinal);
        Assert.DoesNotContain("authData.qobuz?.publicApiStatus", site, StringComparison.Ordinal);
        Assert.DoesNotContain("authData.tidal?.publicApiStatus", site, StringComparison.Ordinal);
        Assert.DoesNotContain("authData.amazonMusic?.publicApiStatus", site, StringComparison.Ordinal);
    }

    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }

    /// <summary>Net brace nesting of <paramref name="source"/> at <paramref name="index"/>, ignoring braces inside
    /// quoted strings and template literals so that `${...}` substitutions do not shift the count.</summary>
    private static int BraceDepthAt(string source, int index)
    {
        Assert.True(index >= 0, "BraceDepthAt requires a located marker.");
        var depth = 0;
        var quote = '\0';
        for (var i = 0; i < index; i++)
        {
            var c = source[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '\'':
                case '"':
                case '`':
                    quote = c;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    break;
            }
        }

        return depth;
    }

    private static string ExtractAssignedString(string source, string variable)
    {
        var marker = $"{variable} = '";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing assignment for {variable}");
        start += marker.Length;
        var end = source.IndexOf('\'', start);
        Assert.True(end > start, $"Unterminated assignment for {variable}");
        return source[start..end];
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = source.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath));

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DeezSpoTag.Web", "Views", "Login", "Index.cshtml")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not resolve repository root.");
    }
}
