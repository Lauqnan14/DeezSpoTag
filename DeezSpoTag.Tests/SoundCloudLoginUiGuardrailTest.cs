using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Pins the SoundCloud login pane's connected-state behaviour.
/// </summary>
/// <remarks>
///     <para>
///         Every assertion here corresponds to a defect that shipped in the first version of this pane:
///         it had no status card, nothing populated it on page load, its disconnect button and endpoint did
///         not exist, and its save renderer was handed Soulseek's payload because the platform was missing
///         from the payload-routing chain. The pane looked like it had no logged-in state at all, which is
///         exactly how it was reported.
///     </para>
///     <para>
///         These are source-shape assertions rather than browser tests, matching
///         <see cref="BoomplayLoginUiGuardrailTest" /> and <see cref="SoulseekUiGuardrailTest" />, which is
///         how this codebase guards the login page.
///     </para>
/// </remarks>
[Collection("DataRoot Environment")]
public sealed class SoundCloudLoginUiGuardrailTest
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    private static string LoginPage => ReadSource("DeezSpoTag.Web/Views/Login/Index.cshtml");

    private static string SoundCloudPane => ExtractBetween(
        LoginPage,
        "<div class=\"tab-pane\" id=\"soundcloud-login\"",
        "<div class=\"tab-pane\" id=\"discogs-login\"");

    /// <summary>
    ///     One button, not two, for what is a single endpoint.
    /// </summary>
    /// <remarks>
    ///     Saving and re-checking both POST to <c>/api/platform-auth/soundcloud</c>; the endpoint validates
    ///     whatever it is handed and treats a blank submission as a re-check. Two buttons therefore gave the
    ///     reader two controls for one operation.
    /// </remarks>
    [Fact]
    public void SoundCloud_HasOneButton_NotSeparateSaveAndCheck()
    {
        var pane = SoundCloudPane;

        Assert.DoesNotContain("Check saved token", pane, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"soundcloudCheckBtn\"", pane, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"soundcloudCheckBtn\"", LoginPage, StringComparison.Ordinal);

        // The pane holds exactly one button, and it is the form's submit button.
        Assert.Equal(1, CountOccurrences(pane, "<button"));
        Assert.Contains("<button class=\"btn btn-primary action-btn\" type=\"submit\">Save token</button>", pane, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The pane uses the shared connected-state card, like every other platform.
    /// </summary>
    [Fact]
    public void SoundCloud_UsesTheSharedConnectedStateCard()
    {
        var pane = SoundCloudPane;

        Assert.Contains("ContainerId = \"soundcloudLoggedInInfo\"", pane, StringComparison.Ordinal);
        Assert.Contains("ButtonId = \"soundcloudDisconnectBtn\"", pane, StringComparison.Ordinal);
        Assert.Contains("NameId = \"soundcloudLoggedInName\"", pane, StringComparison.Ordinal);
        Assert.Contains("DetailId = \"soundcloudLoggedInStatus\"", pane, StringComparison.Ordinal);
        Assert.Contains("/images/icons/soundcloud.png", pane, StringComparison.Ordinal);

        // The card is what the disconnect binding targets, so the two must exist together.
        Assert.Contains(
            "bindPlatformDisconnect('soundcloudDisconnectBtn', '/api/platform-auth/soundcloud/disconnect', 'soundcloudStatus')",
            LoginPage,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     The form and the card swap on one flag, which is what makes the pane look logged in.
    /// </summary>
    [Fact]
    public void SoundCloud_FormAndCardAreSwappedByTheConnectedFlag()
    {
        var pane = SoundCloudPane;

        Assert.Contains(
            "id=\"soundcloudLoginFormSection\" class=\"@(initialSoundCloudConnected ? \"hidden\" : \"\")\"",
            pane,
            StringComparison.Ordinal);
        Assert.Contains(
            "WrapperCssClass = initialSoundCloudConnected ? \"settings-group mt-6\" : \"settings-group mt-6 hidden\"",
            pane,
            StringComparison.Ordinal);

        // The wrapper must come before the card, or both are briefly visible.
        Assert.True(
            pane.IndexOf("id=\"soundcloudLoginFormSection\"", StringComparison.Ordinal)
            < pane.IndexOf("ContainerId = \"soundcloudLoggedInInfo\"", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The state is rendered server-side, so it is correct on first paint.
    /// </summary>
    /// <remarks>
    ///     Previously the pane emitted an empty status element and nothing else, so a saved-but-rejected
    ///     token was indistinguishable from no token until the reader pressed the check button.
    /// </remarks>
    [Fact]
    public void SoundCloud_InitialStateIsServerRendered()
    {
        var login = LoginPage;

        Assert.Contains("var initialSoundCloudAuth = initialPlatformAuthState.SoundCloud;", login, StringComparison.Ordinal);
        Assert.Contains("var initialSoundCloudConnected = initialSoundCloudAuth?.CredentialsValid == true;", login, StringComparison.Ordinal);
        Assert.Contains("ResolveInitialSoundCloudStatus(initialSoundCloudAuth, initialSoundCloudConnected)", login, StringComparison.Ordinal);
        Assert.Contains("static string ResolveInitialSoundCloudStatus(SoundCloudAuth? auth, bool connected)", login, StringComparison.Ordinal);

        // A stored-but-unverified token must say so rather than showing nothing.
        Assert.Contains("A token is saved but has not been checked yet.", login, StringComparison.Ordinal);
        Assert.Contains("No token saved. Public tracks work without one.", login, StringComparison.Ordinal);

        // The status element is seeded, not left blank.
        Assert.Contains("id=\"soundcloudStatus\">@(!initialSoundCloudConnected ? initialSoundCloudStatus : \"\")", SoundCloudPane, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Loading the page refreshes the state instead of leaving the pane blank.
    /// </summary>
    [Fact]
    public void SoundCloud_PageLoadPopulatesTheState()
    {
        var loader = ExtractBetween(
            LoginPage,
            "async function loadPlatformAuthState()",
            "async function refreshBeatportAuthState()");

        Assert.Contains("if (data.soundcloud) {", loader, StringComparison.Ordinal);
        Assert.Contains("renderSoundCloudInfo(data.soundcloud);", loader, StringComparison.Ordinal);
        Assert.Contains(
            "togglePlatformLoginState('soundcloud', data.soundcloud.connected === true);",
            loader,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     The save renderer is handed SoundCloud's payload, not another platform's.
    /// </summary>
    /// <remarks>
    ///     <c>soundcloud</c> was missing from the payload-routing chain, so it fell through to
    ///     <c>data?.soulseek</c>. The SoundCloud renderer was therefore reading Soulseek's
    ///     <c>connected</c> flag and reporting the wrong platform's state.
    /// </remarks>
    [Fact]
    public void SoundCloud_SaveRendererReceivesItsOwnPayload()
    {
        var apply = ExtractBetween(
            LoginPage,
            "function applyPlatformAuthState(url, data, payload, statusElementId, isConnected)",
            "function applyPlatformDisconnectState(url, statusElementId, bindings)");

        Assert.Contains("platform === 'soundcloud' ? data?.soundcloud || payload :", apply, StringComparison.Ordinal);

        // The chain must not be able to fall through to another platform's payload.
        Assert.True(
            apply.IndexOf("platform === 'soundcloud' ? data?.soundcloud || payload :", StringComparison.Ordinal)
            < apply.LastIndexOf("data?.soulseek || payload;", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The disconnect route the button points at actually exists.
    /// </summary>
    /// <remarks>
    ///     The button was bound to <c>/api/platform-auth/soundcloud/disconnect</c> while no such route
    ///     existed, so the control could only ever have been dead. Asserted by reflection so the test fails
    ///     if the route is renamed rather than only if the markup drifts.
    /// </remarks>
    [Fact]
    public void SoundCloud_DisconnectRouteExists()
    {
        var disconnect = typeof(PlatformAuthApiController).GetMethod("DisconnectSoundCloud");

        Assert.NotNull(disconnect);
        var routes = disconnect!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPostAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Mvc.HttpPostAttribute>()
            .Select(a => a.Template)
            .ToArray();
        Assert.Contains("soundcloud/disconnect", routes);
    }

    /// <summary>
    ///     Clearing removes the stored token, not just the connection flag.
    /// </summary>
    /// <remarks>
    ///     The credential provider sends any non-empty token as a cookie on every SoundCloud page request, so
    ///     a token left on disk while marked disconnected would keep degrading every public download.
    /// </remarks>
    [Fact]
    public async Task ClearingSoundCloud_RemovesTheStoredToken()
    {
        var root = Path.Join(Path.GetTempPath(), "deezspotag-soundcloud-disconnect-" + Path.GetRandomFileName());
        var dataRoot = Path.Join(root, "DeezSpoTag.Workers", "Data");
        Directory.CreateDirectory(Path.Join(dataRoot, "autotag"));

        try
        {
            var auth = new PlatformAuthService(
                new OverrideWebHostEnvironment(dataRoot),
                NullLogger<PlatformAuthService>.Instance,
                DataProtectionProvider.Create(new DirectoryInfo(Path.Join(root, "keys"))));

            await auth.UpdateAsync(state =>
            {
                state.SoundCloud = new SoundCloudAuth { OAuthToken = "a-token", CredentialsValid = true };
                return state.SoundCloud;
            });

            Assert.False(string.IsNullOrWhiteSpace((await auth.LoadAsync()).SoundCloud?.OAuthToken));

            await auth.UpdateAsync(state =>
            {
                state.SoundCloud = null;
                return state.SoundCloud;
            });

            var after = await auth.LoadAsync();
            Assert.Null(after.SoundCloud);
            Assert.False(File.Exists(Path.Join(dataRoot, "autotag", "soundcloud.json")));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private static int CountOccurrences(string source, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>
///     A rejected save paints the reason instead of leaving the pane silently unchanged.
/// </summary>
/// <remarks>
///     <para>
///         This was the defect behind "the page does not change to logged in state". SoundCloud answers a
///         refused token with a 400 that carries the new redacted view, and the shared save helper returned
///         on any non-OK status before touching the DOM - so a refused token left the pane showing exactly
///         what it had before, which is indistinguishable from the button doing nothing.
///     </para>
///     <para>
///         The helper is shared by every platform, so it is guarded on the body carrying this platform's own
///         key; an error that merely echoes the submitted payload must not be able to repaint the form.
///     </para>
/// </remarks>
[Fact]
public void SoundCloud_ARejectedSaveRendersTheReason()
{
    var helper = ExtractBetween(
        LoginPage,
        "async function savePlatformAuth(url, payload, statusElementId, isConnected)",
        "function applyRejectedPlatformState(url, failure, payload, statusElementId)");

    Assert.Contains("let failure = null;", helper, StringComparison.Ordinal);
    Assert.Contains("failure = await response.json();", helper, StringComparison.Ordinal);
    Assert.Contains(
        "applyRejectedPlatformState(url, failure, payload, statusElementId);",
        helper,
        StringComparison.Ordinal);

    // The apply must come before the early return that used to swallow it.
    Assert.True(
        helper.IndexOf("applyRejectedPlatformState(url, failure, payload, statusElementId);", StringComparison.Ordinal)
        < helper.LastIndexOf("return;", StringComparison.Ordinal));

    var guard = ExtractBetween(
        LoginPage,
        "function applyRejectedPlatformState(url, failure, payload, statusElementId)",
        "function applyPlatformAuthState(url, data, payload, statusElementId, isConnected)");

    Assert.Contains("resolvePlatformAuthBinding(url, PLATFORM_AUTH_BINDINGS)", guard, StringComparison.Ordinal);
    Assert.Contains("const state = failure[platform];", guard, StringComparison.Ordinal);
    Assert.Contains("if (!state || typeof state !== 'object') {", guard, StringComparison.Ordinal);
    Assert.Contains(
        "applyPlatformAuthState(url, failure, payload, statusElementId, false);",
        guard,
        StringComparison.Ordinal);
}

/// <summary>
///     The bindings table is shared, so the success and rejection paths cannot drift apart.
/// </summary>
[Fact]
public void PlatformAuthBindings_AreSharedBetweenTheSuccessAndRejectionPaths()
{
    Assert.Contains("const PLATFORM_AUTH_BINDINGS = {", LoginPage, StringComparison.Ordinal);
    Assert.Contains("const bindings = PLATFORM_AUTH_BINDINGS;", LoginPage, StringComparison.Ordinal);
}

/// <summary>
///     SoundCloud is registered in the save table as well as the disconnect table.
/// </summary>
/// <remarks>
///     <para>
///         This is the defect behind "the page does not change to logged in state". SoundCloud was present
///         only in <c>platformAuthBindings</c>, which the disconnect path uses. The save path resolves its
///         platform from a different table, so the lookup failed, <c>applyPlatformAuthState</c> returned right
///         after writing the generic "Saved", and the card was never rendered or swapped in.
///     </para>
///     <para>
///         Asserted per table rather than by counting occurrences. An earlier version of this test counted
///         occurrences across the whole file, found exactly one, and passed - while that one sat in the wrong
///         table. Same class of mistake as the original bug: the assertion confirmed itself.
///     </para>
/// </remarks>
[Fact]
public void SoundCloud_IsRegisteredInBothTheSaveAndDisconnectTables()
{
    var saveTable = ExtractBetween(
        LoginPage,
        "const PLATFORM_AUTH_BINDINGS = {",
        "function applyPlatformAuthState(url, data, payload, statusElementId, isConnected)");

    var disconnectTable = ExtractBetween(
        LoginPage,
        "const platformAuthBindings = {",
        "await disconnectPlatform(url, statusElementId, platformAuthBindings);");

    Assert.Contains("matches: '/soundcloud'", saveTable, StringComparison.Ordinal);
    Assert.Contains("matches: '/soundcloud'", disconnectTable, StringComparison.Ordinal);

    // The save entry must do the whole job, not merely match the URL.
    Assert.Contains("renderSoundCloudInfo(info || {});", saveTable, StringComparison.Ordinal);
    Assert.Contains("setSoundCloudFormStatus(info || {}, elementId);", saveTable, StringComparison.Ordinal);
}

/// <summary>
///     The connected copy claims only what a token was measured to deliver.
/// </summary>
/// <remarks>
///     <para>
///         The card previously said a connected account also gets "the HQ stream". That was disproved against
///         live SoundCloud: a valid token authenticated and changed the page hydration
///         (<c>track_authorization</c> grew from 192 to 205 characters), yet both test tracks still resolved
///         to <c>sq</c>. <c>hq</c> ranks above <c>sq</c> in the ladder, so its absence from the selection
///         proves it was not advertised - Go+ is an entitled tier, and authentication alone does not reach it.
///     </para>
///     <para>
///         Pinned so the wording cannot drift back into promising a quality the engine has not been shown to
///         deliver.
///     </para>
/// </remarks>
[Fact]
public void SoundCloud_ConnectedCopyDoesNotPromiseTheHQStream()
{
    var ladder = ReadSource("DeezSpoTag.Services/Download/SoundCloud/SoundCloudStereoQuality.cs");

    Assert.DoesNotContain("HQ stream", LoginPage, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("higher-quality MP3 stream", LoginPage, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("Private tracks your account has access to are available.", LoginPage, StringComparison.Ordinal);

    // hq must still rank above sq, or the tier could never be selected even when it is advertised.
    Assert.Contains("\"hq\" => 2,", ladder, StringComparison.Ordinal);
    Assert.Contains("\"sq\" => 1,", ladder, StringComparison.Ordinal);
}

private sealed class OverrideWebHostEnvironment(string rootPath) : IWebHostEnvironment, IAppDataRootOverride
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public string WebRootPath { get; set; } = rootPath;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = rootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string? AppDataRoot { get; } = rootPath;
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
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DeezSpoTag.Web", "Views", "Login", "Index.cshtml")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}