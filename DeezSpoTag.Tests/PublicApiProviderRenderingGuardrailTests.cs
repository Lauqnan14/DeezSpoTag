using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PublicApiProviderRenderingGuardrailTests
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
    public void VerificationButton_RendersOnlyAsTheFinalControlInTheZarzRow()
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
