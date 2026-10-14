using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Web.Controllers.Api;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using System.Security.Claims;
using System.Threading.Tasks;
using DeezSpoTag.Core.Security;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Web.Filters;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DeezSpoTag.Tests;

public sealed class SecurityHardeningGuardrailTest
{
    [Theory]
    [InlineData("DeezSpoTag.Integrations/YouTube/YouTubeDataApiClient.cs", "YouTube playlistItems for {PlaylistId} returned a repeating", "playlistId")]
    [InlineData("DeezSpoTag.Integrations/YouTube/YouTubeDataApiClient.cs", "YouTube playlistItems for {PlaylistId} exceeded", "playlistId")]
    [InlineData("DeezSpoTag.Integrations/YouTube/YouTubeDataApiClient.cs", "YouTube failed to add a video to", "playlistId")]
    [InlineData("DeezSpoTag.Services/Download/Shared/ZarzSignedSessionCoordinator.cs", "{Provider} signed-session peek failed.", "provider")]
    [InlineData("DeezSpoTag.Services/Download/Shared/ZarzSignedSessionCoordinator.cs", "Failed to load {Provider} Zarz signed session.", "provider")]
    [InlineData("DeezSpoTag.Integrations/Soulseek/SlskdClient.cs", "slskd request to {ApiPath}", "apiPath")]
    [InlineData("DeezSpoTag.Services/Download/Utils/EnhancedPathTemplateProcessor.cs", "Creating artist folder - Track:", "track.Title", "track.Album?.MainArtist?.Name ?? \"NULL\"", "track.MainArtist?.Name ?? \"NULL\"", "artistToUse?.Name ?? \"NULL\"")]
    [InlineData("DeezSpoTag.Services/Download/Utils/EnhancedPathTemplateProcessor.cs", "GenerateArtistName called with artist:", "artist?.Name ?? \"NULL\"", "normalizedArtistName", "artist?.Id ?? \"NULL\"", "template")]
    [InlineData("DeezSpoTag.Services/Download/Utils/EnhancedPathTemplateProcessor.cs", "Artist name is empty or 'Unknown'", "normalizedArtistName")]
    [InlineData("DeezSpoTag.Web/Controllers/Api/SoulseekApiController.cs", "Soulseek browse of {RemoteDirectory} from {Peer} was refused:", "remoteDirectory", "peer", "ex.Message")]
    [InlineData("DeezSpoTag.Web/Controllers/Api/SoulseekApiController.cs", "Soulseek browse of {RemoteDirectory} from {Peer} failed with slskd status", "remoteDirectory", "peer", "ex.Message")]
    [InlineData("DeezSpoTag.Web/Controllers/Api/SoulseekApiController.cs", "Could not resolve a catalogue cover for the Soulseek search", "query")]
    [InlineData("DeezSpoTag.Web/Services/ArtistLocation/MusicBrainzArtistLocationService.cs", "MusicBrainz artist search failed for", "artistName")]
    [InlineData("DeezSpoTag.Web/Services/ArtistLocation/MusicBrainzArtistLocationService.cs", "MusicBrainz artist search returned nothing for", "artistName")]
    [InlineData("DeezSpoTag.Web/Services/ArtistLocation/MusicBrainzArtistLocationService.cs", "MusicBrainz artist search for {ArtistName} not corroborated", "artistName")]
    [InlineData("DeezSpoTag.Web/Services/ArtistLocation/MusicBrainzArtistLocationService.cs", "MusicBrainz candidate {Mbid} for {ArtistName} shares no album", "candidate.Id", "artistName")]
    [InlineData("DeezSpoTag.Web/Services/ArtistLocation/MusicBrainzArtistLocationService.cs", "No MusicBrainz candidate for", "artistName")]
    [InlineData("DeezSpoTag.Web/Services/ArtistLocation/MusicBrainzArtistLocationService.cs", "MusicBrainz matched {ArtistName} to {Mbid}", "artistName", "best.Mbid")]
    [InlineData("DeezSpoTag.Web/Services/AutoTag/LocalAutoTagRunner.ArtistMetadata.cs", "Explicit language observations unavailable for", "filePath")]
    [InlineData("DeezSpoTag.Web/Services/LibraryRecommendationService.cs", "Recommendation maintenance failed for", "jobKey")]
    [InlineData("DeezSpoTag.Web/Services/PlatformTrackIdentityResolver.cs", "No identity search is wired for", "service")]
    [InlineData("DeezSpoTag.Web/Services/PlatformTrackIdentityResolver.cs", "Searching {Service} for {Track} failed.", "service", "track.Name")]
    [InlineData("DeezSpoTag.Web/Services/TrackIdentityResolver.cs", "Hydrated SoundCloud source", "SoundCloudUrlRedactor.Redact(sourceUrl)", "track.Urn", "track.Title", "track.PreferredArtist")]
    [InlineData("DeezSpoTag.Web/Controllers/Api/AppleBrowseApiController.cs", "Apple browse preview unavailable for {Id}", "id")]
    [InlineData("DeezSpoTag.Web/Controllers/Api/AppleBrowseApiController.cs", "Apple Music browse request failed for {Path}", "path")]
    [InlineData("DeezSpoTag.Web/Controllers/Api/YouTubeBrowseApiController.cs", "YouTube non-music segments unavailable for {VideoId}", "id")]
    public void SecurityFlaggedLogCall_SanitizesEachUntrustedArgument(string path, string message, params string[] arguments)
    {
        var source = File.ReadAllText(Path.Combine(ResolveSrcRoot(), path));
        var start = source.IndexOf(message, StringComparison.Ordinal);
        Assert.True(start >= 0, "Flagged logging call must remain covered.");
        var end = source.IndexOf(");", start, StringComparison.Ordinal);
        var call = source[start..(end + 2)];
        foreach (var argument in arguments)
            Assert.Contains("LogSanitizer.OneLine(" + argument + ")", call, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("ordinary artist", "ordinary artist")]
    [InlineData("first\r\nsecond\tthird", "first  second third")]
    public void SecurityLogSanitizer_PreservesTextWithoutRecordSeparators(string? input, string expected)
        => Assert.Equal(expected, LogSanitizer.OneLine(input));

    [Fact]
    public void SecurityLogSanitizer_LimitsUntrustedText()
        => Assert.Equal("1234...", LogSanitizer.OneLine("123456", 4));

    [Fact]
    public void SecurityPathTemplateLog_SanitizesArtistAndTemplateWithoutChangingPath()
    {
        var logger = new SecurityCaptureLogger<EnhancedPathTemplateProcessor>();
        var processor = new EnhancedPathTemplateProcessor(logger);
        var artist = new Artist("first\r\nFORGED\tartist") { Id = "id\r\nFORGED" };
        const string template = "%artist%\r\nFORGED";
        var settings = new DeezSpoTagSettings();
        var result = processor.GenerateArtistName(template, artist, settings, null);
        var expected = new EnhancedPathTemplateProcessor(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EnhancedPathTemplateProcessor>.Instance)
            .GenerateArtistName(template, artist, settings, null);
        Assert.Equal(expected, result);
        Assert.Equal("first\r\nFORGED\tartist", artist.Name);
        Assert.NotEmpty(logger.Messages);
        foreach (var message in logger.Messages)
        {
            Assert.DoesNotContain("\r", message);
            Assert.DoesNotContain("\n", message);
            Assert.DoesNotContain("\t", message);
        }
    }

    [Fact]
    public void SecurityFlaggedActions_ExplicitlyDeclareAntiforgery()
    {
        AssertPostRequiresAntiforgery(typeof(AutoPlaylistsApiController), nameof(AutoPlaylistsApiController.SyncPlaylist));
        foreach (var name in new[] { nameof(PlatformAuthApiController.CheckSoundCloud),
                     nameof(PlatformAuthApiController.SaveSoundCloud), nameof(PlatformAuthApiController.DisconnectSoundCloud) })
            AssertPostRequiresAntiforgery(typeof(PlatformAuthApiController), name);
    }

    [Theory]
    [InlineData("Cookies", "POST", false, false, 1, true)]
    [InlineData("Cookies", "POST", true, false, 1, false)]
    [InlineData("ApiToken", "POST", false, false, 0, false)]
    [InlineData("Cookies", "GET", false, false, 0, false)]
    [InlineData("Cookies", "POST", false, true, 0, false)]
    public async Task SecurityAntiforgeryPolicies_PreserveBrowserAndApiTokenBehavior(
        string authenticationType, string method, bool valid, bool ignored, int validations, bool rejected)
    {
        var antiforgery = new SecurityFakeAntiforgery(valid);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllersWithViews();
        services.AddSingleton<IAntiforgery>(antiforgery);
        using var provider = services.BuildServiceProvider();
        var standard = (IAsyncAuthorizationFilter)new ValidateAntiForgeryTokenAttribute().CreateInstance(provider);
        var aware = new ApiTokenAwareAntiforgeryFilter(antiforgery);
        var filters = new List<IFilterMetadata> { (IFilterMetadata)standard, aware };
        if (ignored) filters.Add(new IgnoreAntiforgeryTokenAttribute());
        var http = new DefaultHttpContext { RequestServices = provider };
        http.Request.Method = method;
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test-user")], authenticationType));
        var context = new AuthorizationFilterContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()), filters);
        // MVC executes filters in this order; the higher-order aware policy must
        // override the standard policy rather than forcing cookie tokens on API clients.
        await standard.OnAuthorizationAsync(context);
        if (context.Result is null) await aware.OnAuthorizationAsync(context);
        Assert.Equal(validations, antiforgery.Validations);
        if (rejected)
            Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<BadRequestObjectResult>(context.Result).StatusCode);
        else Assert.Null(context.Result);
    }

    private sealed class SecurityFakeAntiforgery(bool valid) : IAntiforgery
    {
        public int Validations { get; private set; }
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext context) => new("request", "cookie", "__RequestVerificationToken", "X-CSRF-TOKEN");
        public AntiforgeryTokenSet GetTokens(HttpContext context) => GetAndStoreTokens(context);
        public Task<bool> IsRequestValidAsync(HttpContext context) => Task.FromResult(valid);
        public Task ValidateRequestAsync(HttpContext context)
        {
            Validations++;
            return valid ? Task.CompletedTask : Task.FromException(new AntiforgeryValidationException("Invalid token."));
        }
        public void SetCookieTokenAndHeader(HttpContext context) { }
    }

    private sealed class SecurityCaptureLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
    /// <summary>
    ///     Files permitted to block a thread on another, each with the reason it cannot be async.
    /// </summary>
    /// <remarks>
    ///     This list was two entries while seven files were breaking the rule, so it had stopped
    ///     being maintained and the guardrail could not tell a deliberate exception from an oversight.
    ///     Every entry below is now accounted for:
    ///     <list type="bullet">
    ///         <item>LibraryConfigStore.cs - synchronous compatibility shims over async store methods, kept for callers not yet migrated.</item>
    ///         <item>ShazamRecognitionService.cs - a ported recognizer and one discovery lookup on a synchronous entry point.</item>
    ///         <item>SoulseekConnectionService.cs - a lock deliberately held across a region, never across the network call.</item>
    ///         <item>The remaining four are tests reading a value their own synchronous assertion needs.</item>
    ///     </list>
    ///     A new entry should come with its reason here rather than being added silently, and anything
    ///     not listed must be made async.
    /// </remarks>

    private static readonly string[] BlockingWaitAllowlist =
    {
        "EngineProcessorResolutionTest.cs",
        "GenreRegionUiGuardrailTest.cs",
        "LibraryConfigStore.cs",
        "ShazamRecognitionService.cs",
        "SoulseekConnectionService.cs",
        "SoulseekConnectionServiceTest.cs",
        "SoulseekPeerSearchResolutionGuardrailTest.cs"
    };

    /// <summary>
    ///     Files permitted to start detached work, each with the reason it cannot await it.
    /// </summary>
    /// <remarks>
    ///     The rule bans <c>Task.Run</c> because fire-and-forget hides failures and eats thread-pool
    ///     threads. The production entry below needs it because the work is genuinely
    ///     detached, there is no caller left to await it, and the HTTP request has already been
    ///     answered. It logs inside the delegate and filters cancellation, so the thing
    ///     the rule protects against - a silent failure - is handled at the site.
    ///     <para>
    ///         PlaylistPlatformSnapshotTest, SoulseekConnectionServiceTest and SoundCloudAutoTagProviderTest
    ///         wrap delegates they observe from outside the test's own async flow.
    ///         SoulseekRepositoryTest awaits both Task.Run calls through Task.WhenAll to exercise
    ///         competing source-path claims from separate repository instances.
    ///     </para>
    ///     <para>
    ///         A new entry must carry its reason here rather than being added silently. Anything not
    ///         listed has to await its work or hand it to a service that does.
    ///     </para>
    /// </remarks>
    private static readonly string[] DetachedWorkAllowlist =
    {
        "PlaylistPlatformSnapshotTest.cs",
        "SoulseekApiController.cs",
        "SoulseekConnectionServiceTest.cs",
        "SoulseekRepositoryTest.cs",
        "SoundCloudAutoTagProviderTest.cs"
    };

    [Fact]
    public void SourceCode_MustNotUseTaskRunWrappers()
    {
        var srcRoot = ResolveSrcRoot();
        var taskRunPattern = "Task" + ".Run(";
        // Select the file name here, as the blocking-wait rule does. Path.GetFileName returns null for a
        // rooted path, so asking for it inside the failure message only would have reported full
        // paths and left the comparison comparing names against paths.
        var offenders = EnumerateTrackedFiles(srcRoot, "*.cs")
            .Where(path => !path.EndsWith("SecurityHardeningGuardrailTest.cs", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(taskRunPattern, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        // Both sides sorted with the same comparer, so the comparison is about membership rather than
        // about how either list happens to be ordered.
        Assert.True(
            offenders
                .OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(DetachedWorkAllowlist.OrderBy(name => name, StringComparer.Ordinal)),
            "Detached work allowlist changed. Current: " + string.Join(", ", offenders));
    }

    [Fact]
    public void SourceCode_MustNotUseBlockingWaitPrimitives()
    {
        var srcRoot = ResolveSrcRoot();
        var offenders = EnumerateTrackedFiles(srcRoot, "*.cs")
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                var waitPattern = ".Wait(" + ")";
                var getResultPattern = "GetAwaiter()." + "GetResult()";
                return source.Contains(waitPattern, StringComparison.Ordinal)
                    || source.Contains(getResultPattern, StringComparison.Ordinal);
            })
            .Where(path => !path.EndsWith("SecurityHardeningGuardrailTest.cs", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.SequenceEqual(BlockingWaitAllowlist.OrderBy(name => name, StringComparer.Ordinal)),
            "Blocking wait allowlist changed. Current: " + string.Join(", ", offenders));
    }

    [Fact]
    public void ProjectFiles_MustNotReferenceLegacyTagLibSharpPackage()
    {
        var srcRoot = ResolveSrcRoot();
        var offenders = Directory
            .EnumerateFiles(srcRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.dsh-worktrees{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("Include=\"TagLibSharp\"", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Legacy TagLibSharp package references found in: " + string.Join(", ", offenders.Select(Path.GetFileName)));
    }

    [Fact]
    public void ApiCorsPolicy_MustNotUseAllowAnyMethodOrAllowAnyHeader()
    {
        var apiProgramPath = Path.Combine(ResolveSrcRoot(), "DeezSpoTag.API", "Program.cs");
        var source = File.ReadAllText(apiProgramPath);

        Assert.DoesNotContain(".AllowAnyMethod()", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".AllowAnyHeader()", source, StringComparison.Ordinal);
        Assert.Contains(".WithMethods(", source, StringComparison.Ordinal);
        Assert.Contains(".WithHeaders(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalApiAuthorizeAttribute_MustNotImplementAllowAnonymous()
    {
        var sourcePath = Path.Combine(ResolveSrcRoot(), "DeezSpoTag.Web", "Controllers", "Api", "LocalApiAuthorizeAttribute.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("IAllowAnonymous", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SecuritySensitivePostEndpoints_MustRequireAntiforgeryTokens()
    {
        foreach (var methodName in new[]
                 {
                     nameof(PlatformAuthApiController.CheckAmazonMusicProviders),
                     nameof(PlatformAuthApiController.CheckTidalProviders),
                     nameof(PlatformAuthApiController.CheckQobuzProviders),
                     nameof(PlatformAuthApiController.SaveSpotify),
                     nameof(PlatformAuthApiController.SaveDiscogs),
                     nameof(PlatformAuthApiController.SaveQobuz),
                     nameof(PlatformAuthApiController.SaveTidal),
                     nameof(PlatformAuthApiController.SaveSoulseek),
                     nameof(PlatformAuthApiController.SaveBoomplay),
                     nameof(PlatformAuthApiController.SaveAmazonMusic),
                     nameof(PlatformAuthApiController.SaveLastFm),
                     nameof(PlatformAuthApiController.SaveBpmSupreme),
                     nameof(PlatformAuthApiController.SavePlex),
                     nameof(PlatformAuthApiController.LoginPlex),
                     nameof(PlatformAuthApiController.SaveJellyfin),
                     nameof(PlatformAuthApiController.LoginJellyfin),
                     nameof(PlatformAuthApiController.LoginNavidrome),
                     nameof(PlatformAuthApiController.Disconnect)
                 })
        {
            AssertPostRequiresAntiforgery(typeof(PlatformAuthApiController), methodName);
        }

        AssertPostRequiresAntiforgery(
            typeof(SpotifyDiscoveryTracklistApiController),
            nameof(SpotifyDiscoveryTracklistApiController.SyncRecommendationPlaylist));
    }

    [Fact]
    public void PlaylistSyncWarnings_MustSanitizePlaylistIdentifiersBeforeLogging()
    {
        var source = File.ReadAllText(Path.Combine(ResolveSrcRoot(), "DeezSpoTag.Web", "Services", "PlaylistSyncService.cs"));

        Assert.Contains("No Plex matches found for playlist {Source}:{SourceId}", source, StringComparison.Ordinal);
        Assert.Contains("SafeLog(playlist.Source),\n                SafeLog(playlist.SourceId),", source, StringComparison.Ordinal);
        Assert.Contains("No Jellyfin matches found for playlist {Source}:{SourceId}.", source, StringComparison.Ordinal);
        Assert.Contains("SafeLog(playlist.Source),\n                SafeLog(playlist.SourceId));", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SecuritySensitiveLogArguments_MustBeSanitizedAtTheFlaggedSinks()
    {
        var root = ResolveSrcRoot();
        var navidromeSource = File.ReadAllText(Path.Combine(
            root,
            "DeezSpoTag.Integrations",
            "Navidrome",
            "NavidromeApiClient.cs"));
        var resumeSource = File.ReadAllText(Path.Combine(
            root,
            "DeezSpoTag.Web",
            "Services",
            "AutoTagService.Resume.cs"));

        Assert.Contains("LogSanitizer.OneLine(playlistName)", navidromeSource, StringComparison.Ordinal);
        Assert.Equal(
            2,
            resumeSource.Split("LogSanitizer.OneLine(id)", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void WebProgram_MustApplyDefaultApiRateLimitToControllersWithoutLimitingLibraryBrowsing()
    {
        var webProgramPath = Path.Combine(ResolveSrcRoot(), "DeezSpoTag.Web", "Program.cs");
        var webProgramSource = File.ReadAllText(webProgramPath);
        var libraryControllerSource = File.ReadAllText(Path.Combine(ResolveSrcRoot(), "DeezSpoTag.Web", "Controllers", "LibraryController.cs"));
        var libraryImagesControllerSource = File.ReadAllText(Path.Combine(ResolveSrcRoot(), "DeezSpoTag.Web", "Controllers", "Api", "LibraryImagesApiController.cs"));

        Assert.Contains("options.AddPolicy(\"DefaultApi\"", webProgramSource, StringComparison.Ordinal);
        Assert.Contains("app.MapControllers().RequireRateLimiting(\"DefaultApi\")", webProgramSource, StringComparison.Ordinal);
        Assert.Contains("[DisableRateLimiting]", libraryControllerSource, StringComparison.Ordinal);
        Assert.Contains("[DisableRateLimiting]", libraryImagesControllerSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecurityBrowserTextEscaping_ProtectsAttributeBoundaries()
    {
        await RunSecurityNodeTest("""
        for (const file of ['home-index.js', 'library.js', 'personal-genre.js']) {
            const escape = readFunction(file, 'escapeHtml');
            const escaped = escape(`<img src=x onerror=alert(1)>"'&`);
            assert(!/[<>"']/.test(escaped), file + ': HTML attribute boundary remains unescaped');
            assert(escaped.includes('&quot;') && escaped.includes('&#39;'));
        }
        """);
    }

    [Fact]
    public async Task SecurityBrowserApiRequests_RejectExternalAndUnexpectedEndpoints()
    {
        await RunSecurityNodeTest("""
        const api = readFunction('library.js', 'resolveLibraryApiUrl');
        for (const value of ['https://evil.example/api/library', '//evil.example/api/library', 'javascript:alert(1)', '/Identity/Account/Logout'])
            assert.throws(() => api(value));
        assert(api('/api/library/artists?id=1').endsWith('/api/library/artists?id=1'));
        const lyrics = readFunction('lrc-editor.js', 'resolveLyricsFileUrl');
        for (const value of ['https://evil.example/api/lrc/track/1/lrc', '//evil.example/api/lrc/file/lrc', '/api/library/artists'])
            assert.throws(() => lyrics(value, 'lrc'));
        assert(lyrics('/api/lrc/track/42/lrc', 'lrc').endsWith('/api/lrc/track/42/lrc'));
        assert(lyrics('/api/lrc/file/txt?path=abc%2Fdef', 'txt').includes('path=abc%2Fdef'));
        """);
    }

    [Fact]
    public async Task SecurityBrowserMediaUrls_RejectExecutableSchemes()
    {
        await RunSecurityNodeTest("""
        const media = readFunction('library-artist-hero.js', 'sanitizeMediaUrl');
        for (const value of ['javascript:alert(1)', 'data:image/svg+xml,<svg onload=alert(1)>', 'file:///etc/passwd', '/images/x\n'])
            assert.strictEqual(media(value), '');
        assert(media('/images/avatar.png').endsWith('/images/avatar.png'));
        assert(media('https://cdn.example/cover.jpg').startsWith('https://cdn.example/'));
        """);
    }

    [Fact]
    public async Task SecurityHomeClickBuilders_KeepUntrustedValuesAsData()
    {
        await RunSecurityNodeTest("""
        let executed = 0;
        const context = {URLSearchParams, URL, encodeURIComponent, pwn: () => executed++,
            openTracklist(){}, openHomeChannel(){}, openArtist(){}, openSpotifyTracklist(){}, openSpotifyArtist(){}};
        vm.createContext(context);
        vm.runInContext(fs.readFileSync(path.join(root, 'DeezSpoTag.Web/wwwroot/js/home-view-helpers.js'), 'utf8'), context);
        for (const item of [{id:"'-pwn()-'",type:'playlist'}, {id:'x',target:"/channels/'-pwn()-'"},
            {id:'x',source:'spotify',type:'artist',name:"'-pwn()-'"},
            {id:'x',source:'spotify',type:"'-pwn()-'"}]) {
            const code = context.HomeViewHelpers.buildHomeClick(item);
            vm.runInContext(code, context);
            assert.equal(executed, 0, 'Untrusted catalogue value executed as JavaScript');
        }
        """);
    }

    [Fact]
    public async Task SecurityRegionSvg_PreservesGeometryWithoutActiveContent()
    {
        await RunSecurityNodeTest("""
        const namespace = 'http://www.w3.org/2000/svg';
        class Element {
            constructor(name, attributes = {}, children = []) {this.localName=name; this.namespaceURI=namespace;
                this.attributes=Object.entries(attributes).map(([name,value]) => ({name,value})); this.children=children;}
            setAttribute(name,value) {this.attributes.push({name,value});}
            appendChild(child) {this.children.push(child);}
        }
        const document = {createElementNS(ns,name) {assert.equal(ns, namespace); return new Element(name);}};
        const sanitize = readFunction('personal-genre.js', 'sanitizeRegionSvg', {document});
        const result = sanitize('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10" onload="pwn()"><script>pwn()</script><foreignObject/><path d="M 0.001 2.002 L 3.003 4.004" data-country="KE" data-regions="east-africa" onclick="pwn()" style="fill:url(https://evil.example)"/></svg>');
        assert(result);
        assert.equal(result.children.length, 1);
        assert.equal(result.children[0].localName, 'path');
        assert(result.children[0].attributes.some(a => a.name==='d' && a.value==='M 0.001 2.002 L 3.003 4.004'));
        assert(result.children[0].attributes.some(a => a.name==='data-regions' && a.value==='east-africa'));
        for (const node of [result,...result.children])
            assert(node.attributes.every(a => !a.name.startsWith('on') && a.name!=='style'));
        """);
    }

    [Fact]
    public async Task SecurityManagedArtworkAndIcons_PreserveExistingRoutes()
    {
        await RunSecurityNodeTest("""
        const managed = readFunction('library-watchlists.js', 'isManagedPlaylistVisualUrl');
        assert(managed('/api/library/playlists/spotify/playlist-123/visual?v=1'));
        assert(!managed('https://evil.example/api/library/playlists/spotify/playlist-123/visual'));
        assert(!managed('/api/library/playlists/spotify/playlist-123/visual/other'));
        const source = fs.readFileSync(path.join(root, 'DeezSpoTag.Web/wwwroot/js/site.js'), 'utf8');
        const method = source.match(/    resolvePlatformIcon\([^]*?\n    },/)[0];
        const resolve = vm.runInNewContext('({' + method + '})').resolvePlatformIcon;
        const instance = {normalizePlatformId: x => x, platformIconMap:{}, isCurrentThemeLightSurface:()=>false};
        assert.equal(resolve.call(instance, 'youly', '/images/icons/youly+.png'), '/images/icons/youly+.png');
        assert.equal(resolve.call(instance, 'other', 'javascript:alert(1)'), '');
        """);
    }

    [Fact]
    public async Task SecurityHomeCategoryMarkup_PreservesQuotedHandlersAndRejectsActiveArtwork()
    {
        await RunSecurityNodeTest("""
        const context = {URL, URLSearchParams, encodeURIComponent, location:{origin:'https://app.example'},
            escapeHtml:readFunction('home-index.js','escapeHtml'),
            upgradeSpotifyImageUrl:x=>x, extractFirstUrl:x=>x, buildDeezerImageUrlWithSize:()=>'', buildDeezerImageUrl:()=>''};
        vm.createContext(context);
        vm.runInContext(fs.readFileSync(path.join(root, 'DeezSpoTag.Web/wwwroot/js/home-view-helpers.js'), 'utf8'), context);
        const markup = context.HomeViewHelpers.renderTopGenresItem({id:'x',type:'channel',target:'/channels/valid',image:'javascript:alert(1)'});
        const handler = markup.match(/onclick="([^"]*)"/)[1];
        assert(handler.includes('&quot;channels/valid&quot;'), 'Handler literal broke its HTML attribute');
        assert(!markup.includes('javascript:'), 'Executable image scheme survived');
        """);
    }

    [Fact]
    public async Task SecurityBiographyText_StripsFormattingBeforeDecodingEntities()
    {
        await RunSecurityNodeTest("""
        const document = {createElement(name) {
            assert.equal(name, 'textarea');
            return {set innerHTML(value) {assert(!/[<>]/.test(value)); this.encoded=value;},
                get value() {return this.encoded.replace(/&lt;/g,'<').replace(/&gt;/g,'>').replace(/&amp;/g,'&');}};
        }};
        const normalize = readFunction('library.js', 'normalizeArtistBiographyForSync', {document});
        assert.equal(normalize('<a title="a>b">Rock &amp; Roll</a>'), 'Rock & Roll');
        assert.equal(normalize('<!-- x > y -->Music'), 'Music');
        assert.equal(normalize('<img src=x onerror=alert(1)>Music'), 'Music');
        assert.equal(normalize('1 < 2'), '1 < 2');
        """);
    }

    private static async Task RunSecurityNodeTest(string body)
    {
        var script = """
        const fs = require('node:fs'), vm = require('node:vm'), path = require('node:path'), assert = require('node:assert/strict');
        const root = process.argv[1];
        function readFunction(file, name, extra = {}) {
            const source = fs.readFileSync(path.join(root, 'DeezSpoTag.Web/wwwroot/js', file), 'utf8');
            const re = new RegExp(String.raw`(?:^|\n)([ \t]*)function ${name}\([^]*?\n\1}`, 'm');
            const match = source.match(re);
            assert(match, 'Missing production function ' + name);
            const document = {createElement() { return {textContent:'', get innerHTML() {return this.textContent.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');}};}};
            return vm.runInNewContext('(' + match[0].trim() + ')', {URL, URLSearchParams, document, location:{origin:'https://app.example'}, ...extra});
        }
        """ + Environment.NewLine + body;
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(ResolveSrcRoot());
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error + await output);
    }

    private static string ResolveSrcRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var candidate = Path.GetFullPath(Path.Combine(current, "..", "..", "..", ".."));
            if (Directory.Exists(Path.Combine(candidate, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Combine(candidate, "DeezSpoTag.Tests")))
            {
                return candidate;
            }

            var parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }

            current = parent.FullName;
        }

        throw new InvalidOperationException("Could not resolve src root.");
    }

    private static IEnumerable<string> EnumerateTrackedFiles(string srcRoot, string pattern)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = srcRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("ls-files");
        startInfo.ArgumentList.Add("-z");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(pattern);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git to enumerate tracked files.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "git ls-files failed: " + error);

        return output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.GetFullPath(Path.Combine(srcRoot, path)))
            .Where(File.Exists);
    }

    private static void AssertPostRequiresAntiforgery(Type controllerType, string methodName)
    {
        var method = controllerType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        Assert.Contains(method.GetCustomAttributes(inherit: true), attribute => attribute is ValidateAntiForgeryTokenAttribute);
    }
}
