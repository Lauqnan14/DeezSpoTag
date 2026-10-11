using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Guards the two PWA regressions that shipped before: the install prompt reappearing
/// while the app was already running as an installed web app, and the installed/shortcut
/// icon not being the app logo.
/// </summary>
public sealed class PwaInstallSupportGuardrailTest
{
    [Fact]
    public async Task ManifestServesAnAbsoluteSelfApplicationIdForTheCurrentHost()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            WebRootPath = Path.Combine(RepositoryRoot(), "DeezSpoTag.Web", "wwwroot")
        });
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        typeof(DeezSpoTag.Web.Program).GetMethod("ConfigureStaticFiles", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, new object[] { app });
        await app.StartAsync();
        try
        {
            using var client = new HttpClient();
            var origin = app.Urls.Single();
            using var response = await client.GetAsync(origin + "/manifest.webmanifest");
            response.EnsureSuccessStatusCode();
            Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);
            using var manifest = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var self = manifest.RootElement.GetProperty("related_applications")[0];
            Assert.Equal("webapp", self.GetProperty("platform").GetString());
            Assert.Equal(origin + "/", self.GetProperty("id").GetString());
            Assert.Equal("/manifest.webmanifest", self.GetProperty("url").GetString());
            Assert.Equal("/", manifest.RootElement.GetProperty("id").GetString());
            Assert.True(response.Headers.CacheControl?.NoStore);
            foreach (var publicOrigin in new[] { "https://nas.example:8443", "http://nas.example:8668" })
            {
                using var publicResponse = await client.GetAsync(origin + "/manifest.webmanifest?origin=" + Uri.EscapeDataString(publicOrigin));
                publicResponse.EnsureSuccessStatusCode();
                using var publicManifest = JsonDocument.Parse(await publicResponse.Content.ReadAsStringAsync());
                var applications = publicManifest.RootElement.GetProperty("related_applications");
                Assert.Equal(publicOrigin + "/", applications[0].GetProperty("id").GetString());
                Assert.Equal("/manifest.webmanifest", applications[0].GetProperty("url").GetString());
                Assert.Equal(publicOrigin + "/", applications[1].GetProperty("id").GetString());
                Assert.StartsWith("/manifest.webmanifest?origin=", applications[1].GetProperty("url").GetString());
            }
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public void InstallPromptChecksCurrentInstallationOnEveryPageLoad()
    {
        var script = """
            const fs = require('node:fs'), vm = require('node:vm'), assert = require('node:assert/strict');
            const source = fs.readFileSync(`${process.argv[1]}/DeezSpoTag.Web/wwwroot/js/site.js`, 'utf8');
            const manifest = JSON.parse(fs.readFileSync(`${process.argv[1]}/DeezSpoTag.Web/wwwroot/manifest.webmanifest`, 'utf8'));
            const start = source.indexOf('    pwaAppDisplayModes:');
            const end = source.indexOf('\n    },', source.indexOf('    prepareIosPwaPrompt(', start)) + 7;
            const head = fs.readFileSync(`${process.argv[1]}/DeezSpoTag.Web/Views/Shared/_CommonHeadAssets.cshtml`, 'utf8');
            const manifestLink = { href: 'https://nas.example/manifest.webmanifest' };
            const headScript = head.slice(head.indexOf('<script>') + 8, head.indexOf('</script>'));
            vm.runInNewContext(headScript, { URL, document: { querySelector: () => manifestLink }, location: { origin: 'https://nas.example' } });
            const manifestUrl = manifestLink.href;
            assert.equal(new URL(manifestUrl).searchParams.get('origin'), 'https://nas.example');
            async function scenario(apps, recorded = true, dismissed = false, standalone = false, fail = false, duringQuery = null, userAgent = 'Chrome/140.0 Linux') {
                const storage = new Map(recorded ? [['pwa-installed', String(Date.now())]] : []);
                if (dismissed) storage.set('pwa-prompt-dismissed', String(Date.now()));
                const listeners = {}, timers = [], classes = new Set(['hidden']);
                const button = { addEventListener() {}, classList: { add() {} } };
                const prompt = {
                    classList: { contains: key => classes.has(key), add: key => classes.add(key), remove: key => classes.delete(key) },
                    querySelector: () => button
                };
                let queries = 0;
                const navigator = { userAgent };
                if (apps !== null) navigator.getInstalledRelatedApps = async () => {
                    queries++;
                    if (duringQuery) duringQuery(listeners);
                    if (fail) throw Error('Installation check failed');
                    // The browser only reports applications declared in the manifest.
                    return manifest.related_applications?.some(app => app.platform === 'webapp'
                        && new URL(app.url, manifestUrl).pathname === '/manifest.webmanifest') ? apps : [];
                };
                const context = vm.createContext({ URL, navigator,
                    document: { getElementById: () => prompt, querySelector: () => ({ href: manifestUrl }) },
                    matchMedia: () => ({ matches: standalone }),
                    localStorage: { getItem: key => storage.get(key) || null, setItem: (key, value) => storage.set(key, value) },
                    addEventListener: (name, handler) => listeners[name] = handler,
                    setTimeout: fn => timers.push(fn)
                });
                const app = vm.runInContext('({' + source.slice(start, end) + '})', context);
                app.initializePwaInstallPrompt();
                await new Promise(resolve => setImmediate(resolve));
                timers.forEach(fn => fn());
                return { visible: !classes.has('hidden'), recorded: app.hasRecordedPwaInstall(), queries };
            }
            (async () => {
                const installed = [{ platform: 'webapp', url: manifestUrl, id: 'https://nas.example/' }];
                assert.equal((await scenario(installed)).visible, false);
                assert.equal((await scenario([])).visible, true, 'Confirmed uninstall must reveal the existing banner');
                assert.equal((await scenario([])).recorded, false);
                assert.equal((await scenario([], true, true)).visible, true, 'An old dismissal must not hide a confirmed uninstall');
                assert.equal((await scenario([], false, true)).visible, false, 'Respect a dismissal after the uninstall was detected');
                assert.equal((await scenario(installed, false)).visible, false);
                assert.equal((await scenario([{ platform: 'webapp', url: 'https://other.example/manifest.webmanifest' }], false)).visible, true);
                const standalone = await scenario([], true, false, true);
                assert.equal(standalone.visible, false);
                assert.equal(standalone.queries, 0);
                assert.equal((await scenario(null)).visible, false, 'Unsupported detection is not proof of uninstall');
                const olderDesktop = await scenario([], true, false, false, false, null, 'Chrome/139.0 Linux');
                assert.equal(olderDesktop.visible, false, 'Older desktop implementations cannot detect installed PWAs');
                assert.equal(olderDesktop.queries, 0);
                assert.equal((await scenario([], true, false, false, true)).recorded, true);
                const installedDuringQuery = await scenario([], true, false, false, false, listeners => listeners.appinstalled());
                assert.equal(installedDuringQuery.visible, false);
                assert.equal(installedDuringQuery.recorded, true);
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;
        var start = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardError = true };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(RepositoryRoot());
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public void Manifest_DeclaresAStableIdAndNoDisplayOverride()
    {
        using var manifest = JsonDocument.Parse(ReadManifest());

        var root = manifest.RootElement;
        Assert.Equal("/", root.GetProperty("id").GetString());
        Assert.Equal("/", root.GetProperty("start_url").GetString());
        Assert.Equal("/", root.GetProperty("scope").GetString());
        Assert.Equal("standalone", root.GetProperty("display").GetString());

        // display_override can select a display mode the install prompt does not treat
        // as "installed", which is how the app ended up nagging inside its own window.
        Assert.False(root.TryGetProperty("display_override", out _));
    }

    [Theory]
    [InlineData("192x192", "any")]
    [InlineData("512x512", "any")]
    [InlineData("512x512", "maskable")]
    [InlineData("512x512", "monochrome")]
    public void Manifest_DeclaresEveryInstallableIconVariant(string sizes, string purpose)
    {
        var matches = ReadIcons()
            .Where(candidate => string.Equals(candidate.Sizes, sizes, StringComparison.Ordinal)
                && string.Equals(candidate.Purpose, purpose, StringComparison.Ordinal))
            .ToList();

        Assert.True(matches.Count > 0, $"Manifest is missing a {sizes} '{purpose}' icon.");

        foreach (var icon in matches)
        {
            Assert.EndsWith(".png", icon.Src, StringComparison.OrdinalIgnoreCase);

            // Browsers ignore SVG for install, and the shipped manifest used to point
            // the install icon set at an animated wordmark that cannot render as an app icon.
            Assert.DoesNotContain(".svg", icon.Src, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Manifest_IconSourcesAllExistOnDisk()
    {
        var icons = ReadIcons();
        Assert.NotEmpty(icons);

        foreach (var icon in icons)
        {
            var relativePath = icon.Src.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.Combine(RepositoryRoot(), "DeezSpoTag.Web", "wwwroot", relativePath);
            Assert.True(File.Exists(fullPath), $"Manifest icon is missing on disk: {icon.Src}");
        }
    }

    [Theory]
    [InlineData("icon-180.png", 180)]
    [InlineData("icon-192.png", 192)]
    [InlineData("icon-512.png", 512)]
    [InlineData("icon-maskable-512.png", 512)]
    [InlineData("icon-mono-512.png", 512)]
    public void PwaIcons_AreRealSquarePngsOfTheDeclaredSize(string fileName, int expectedSize)
    {
        var bytes = File.ReadAllBytes(Path.Combine(PwaIconDirectory(), fileName));
        var (width, height) = ReadPngSize(bytes);

        Assert.Equal(expectedSize, width);
        Assert.Equal(expectedSize, height);
    }

    [Fact]
    public void MaskableIcon_KeepsArtworkInsideTheEightyPercentSafeCircle()
    {
        // Android crops maskable icons to a circle covering 80% of the icon, so the mark
        // must not be authored at the same scale as the plain "any" icon.
        var any = ReadSource("DeezSpoTag.Web", "wwwroot", "images", "pwa", "icon-source.svg");
        var maskable = ReadSource("DeezSpoTag.Web", "wwwroot", "images", "pwa", "icon-source-maskable.svg");

        var anyScale = ReadScale(any);
        var maskableScale = ReadScale(maskable);

        Assert.True(
            anyScale > maskableScale,
            "The maskable icon must scale the mark down relative to the plain icon.");
    }

    [Fact]
    public void Head_Assets_LinkTheManifestAndThePwaIconSet()
    {
        var head = ReadSource("DeezSpoTag.Web", "Views", "Shared", "_CommonHeadAssets.cshtml");

        Assert.Contains("rel=\"manifest\"", head, StringComparison.Ordinal);
        Assert.Contains("manifest.webmanifest", head, StringComparison.Ordinal);
        Assert.Contains("~/images/pwa/icon-180.png", head, StringComparison.Ordinal);
        Assert.Contains("rel=\"apple-touch-icon\"", head, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DeezSpoTag.Web/Views/Shared/_CommonHeadAssets.cshtml")]
    [InlineData("DeezSpoTag.Web/Areas/Identity/Pages/_Layout.cshtml")]
    [InlineData("DeezSpoTag.Web/Views/Shared/Error.cshtml")]
    public void EveryStandaloneHead_UsesTheDedicatedFaviconSet(string headPath)
    {
        var head = ReadSource(headPath.Split('/'));

        Assert.Contains("~/favicon.ico", head, StringComparison.Ordinal);
        Assert.Contains("~/images/favicon/favicon.svg", head, StringComparison.Ordinal);
        Assert.Contains("sizes=\"32x32\"", head, StringComparison.Ordinal);
        Assert.Contains("sizes=\"16x16\"", head, StringComparison.Ordinal);

        // The 400x180 animated wordmark cannot survive a 16px tab and used to be
        // declared as the page icon. It survives only as the discography placeholder.
        Assert.DoesNotContain("~/images/logo.svg", head, StringComparison.Ordinal);
    }

    [Fact]
    public void FaviconAssets_AreRealSquareImagesAtEveryDeclaredSize()
    {
        var webRoot = Path.Combine(RepositoryRoot(), "DeezSpoTag.Web", "wwwroot");
        var faviconDirectory = Path.Combine(webRoot, "images", "favicon");

        var icoPath = Path.Combine(webRoot, "favicon.ico");
        Assert.True(File.Exists(icoPath), "favicon.ico is missing, so /favicon.ico does not resolve.");

        // An SVG body served as image/x-icon cannot be parsed, so the conventional
        // path has to stay a real multi-frame ICO.
        var ico = File.ReadAllBytes(icoPath);
        Assert.True(ico.Length > 6, "favicon.ico is empty.");
        Assert.Equal(0x00, ico[0]);
        Assert.Equal(0x00, ico[1]);
        Assert.Equal(0x01, ico[2]);
        Assert.Equal(0x00, ico[3]);
        Assert.True(
            BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4, 2)) >= 4,
            "favicon.ico carries too few frames for a usable tab icon.");

        foreach (var size in new[] { 16, 24, 32, 48, 64, 128, 256 })
        {
            var bytes = File.ReadAllBytes(Path.Combine(faviconDirectory, $"favicon-{size}.png"));
            var (width, height) = ReadPngSize(bytes);

            Assert.Equal(size, width);
            Assert.Equal(size, height);
        }

        // A downscaled copy of the 512 app icon resolves to roughly a quarter-pixel
        // stroke at 16px, so the favicon is authored at a native 32x32 instead.
        var source = File.ReadAllText(Path.Combine(faviconDirectory, "favicon-source.svg"));
        Assert.Contains("viewBox=\"0 0 32 32\"", source, StringComparison.Ordinal);
        Assert.Contains("stroke-width=\"2\"", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DeezSpoTag.Web/Views/Shared/_CommonHeadAssets.cshtml")]
    [InlineData("DeezSpoTag.Web/Areas/Identity/Pages/_Layout.cshtml")]
    [InlineData("DeezSpoTag.Web/Views/Shared/Error.cshtml")]
    public void EveryStandaloneHead_OffersTheVectorAndTheRasterLadder(string headPath)
    {
        var head = ReadSource(headPath.Split('/'));

        // Capable browsers take the SVG, so it must be declared; everyone else falls
        // back to the ladder. Without the high-resolution rungs a HiDPI tab ends up
        // with a 16px bitmap stretched, which is what made the tab icon look poor.
        Assert.Contains("~/images/favicon/favicon.svg", head, StringComparison.Ordinal);
        Assert.Contains("~/favicon.ico", head, StringComparison.Ordinal);

        foreach (var size in new[] { 16, 32, 48, 64, 128, 256 })
        {
            Assert.Contains($"favicon-{size}.png", head, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InstallPrompt_TreatsEveryAppDisplayModeAsInstalled()
    {
        var site = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "site.js");

        Assert.DoesNotContain("isPwaStandalone()", site, StringComparison.Ordinal);

        foreach (var displayMode in new[] { "standalone", "minimal-ui", "fullscreen", "window-controls-overlay", "tabbed" })
        {
            Assert.Contains($"'{displayMode}'", site, StringComparison.Ordinal);
        }

        Assert.Contains("navigator.standalone === true", site, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallPrompt_RecordsAnInstalledRunAndCanClearAStaleLatch()
    {
        var site = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "site.js");

        // A latch that is only ever written would silence the prompt forever, so the
        // browser stays the authority: an installed run is recorded and beforeinstallprompt
        // clears it again.
        Assert.Contains("if (this.isRunningAsInstalledApp()) {\n            this.markPwaInstalled();\n            return;\n        }", site, StringComparison.Ordinal);
        Assert.Contains("globalThis.addEventListener('appinstalled'", site, StringComparison.Ordinal);
        Assert.Contains("this.markPwaInstalled();", site, StringComparison.Ordinal);
        Assert.Contains("this.clearRecordedPwaInstall();", site, StringComparison.Ordinal);

        // The listener must be registered before the gate returns early, otherwise a
        // recorded install can never be cleared.
        var listenerIndex = site.IndexOf("globalThis.addEventListener('beforeinstallprompt'", StringComparison.Ordinal);
        var gateIndex = site.IndexOf("if (this.isRunningAsInstalledApp()) {\n            this.markPwaInstalled();\n            return;\n        }", StringComparison.Ordinal);
        Assert.True(listenerIndex >= 0, "beforeinstallprompt listener was not registered.");
        Assert.True(gateIndex >= 0, "The recorded-install gate was not found.");
        Assert.True(
            listenerIndex < gateIndex,
            "beforeinstallprompt must be registered before the recorded-install gate returns.");
    }

    [Fact]
    public void InstallPrompt_RecordedInstallCannotDisableThePrompt()
    {
        var site = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "site.js");

        // Regression: the recorded install used to return from initializePwaInstallPrompt
        // outright, which left the buttons and the manual instructions unwired. The latch
        // is cleared only by beforeinstallprompt, which Chrome does not guarantee to fire,
        // so an account-wide latch could lock a user out of installing permanently.
        // Scoped to the initialiser: markPwaInstalled keeps a legitimate idempotency guard
        // with the same text, which must not trip this check.
        var initIndex = site.IndexOf("initializePwaInstallPrompt() {", StringComparison.Ordinal);
        Assert.True(initIndex >= 0, "initializePwaInstallPrompt was not found.");
        var initializer = site[initIndex..];
        initializer = initializer[..initializer.IndexOf("isRunningAsInstalledApp() {", StringComparison.Ordinal)];
        Assert.DoesNotContain("if (this.hasRecordedPwaInstall()) {", initializer, StringComparison.Ordinal);

        // It may still suppress the automatic banner, but only that.
        Assert.Contains("if (!this.hasRecordedPwaInstall() && !this.hasRecentPwaDismissal()) {", site, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallPrompt_InstallFallsBackToManualInstructions()
    {
        var site = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "site.js");

        // Chrome gates beforeinstallprompt behind engagement heuristics and stops firing
        // it once the user dismisses its native banner, so the button must not be a silent
        // no-op when no deferred event exists.
        Assert.DoesNotContain("if (!deferredPrompt) {\n                    return;\n                }", site, StringComparison.Ordinal);
        Assert.Contains("showManualInstallInstructions();", site, StringComparison.Ordinal);
        Assert.Contains("getManualPwaInstallInstructions()", site, StringComparison.Ordinal);

        // Every supported platform needs its own menu path.
        Assert.Contains("/iPad|iPhone|iPod/", site, StringComparison.Ordinal);
        Assert.Contains("/Android/i", site, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallPrompt_DismissalStillUsesTheSevenDayValve()
    {
        var site = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "site.js");

        Assert.Contains("pwa-prompt-dismissed", site, StringComparison.Ordinal);
        Assert.Contains("const sevenDays = 7 * 24 * 60 * 60 * 1000;", site, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallPrompt_TheInstallLatchIsDeviceLocalNotAccountSynced()
    {
        var preferences = ReadSource("DeezSpoTag.Web", "Services", "UserPreferencesStore.cs");
        var layout = ReadSource("DeezSpoTag.Web", "Views", "Shared", "_Layout.cshtml");
        var site = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "site.js");

        Assert.Contains("public long? PwaPromptDismissedAt", preferences, StringComparison.Ordinal);

        // Whether the app is installed is a property of one browser on one device. Syncing
        // it to the account meant an install on any device suppressed the prompt on every
        // other device signed in, and uninstalling could not clear it because an install
        // is never reported to the server.
        Assert.DoesNotContain("'pwa-installed': 'pwaInstalledAt'", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("UserPrefs.set('pwaInstalledAt'", site, StringComparison.Ordinal);

        // It must not be wiped by the per-load legacy cleanup either, or it could never persist.
        var legacyKeys = layout[layout.IndexOf("var legacyLocalStorageKeys", StringComparison.Ordinal)..];
        var legacyBlock = legacyKeys[..legacyKeys.IndexOf("];", StringComparison.Ordinal)];
        Assert.DoesNotContain("'pwa-installed'", legacyBlock, StringComparison.Ordinal);

        // Device-local keys fall through to the real localStorage so they survive a reload.
        Assert.Contains("var deviceLocalKeys = {", layout, StringComparison.Ordinal);
        Assert.Contains("'pwa-installed': true", layout, StringComparison.Ordinal);
        Assert.Contains("nativeGetItem.call(localRef, normalizedKey)", layout, StringComparison.Ordinal);
        Assert.Contains("nativeSetItem.call(localRef, normalizedKey, normalizedValue)", layout, StringComparison.Ordinal);
        Assert.Contains("pwaInstalledStorageKey: 'pwa-installed'", site, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallPrompt_WorkerRegistersWithoutTheMediaSessionApi()
    {
        var footer = ReadSource("DeezSpoTag.Web", "Views", "Shared", "_CommonFooterScripts.cshtml");

        // Regression: worker registration was gated on mediaSession, an unrelated feature,
        // so a browser without it never registered the worker at all.
        Assert.DoesNotContain("!('serviceWorker' in navigator) || !('mediaSession' in navigator)", footer, StringComparison.Ordinal);
        Assert.Contains("var canRegisterWorker = 'serviceWorker' in navigator;", footer, StringComparison.Ordinal);
        Assert.Contains("if (canRegisterWorker) {", footer, StringComparison.Ordinal);
        Assert.Contains("if (canUseMediaSession && globalThis.DeezSpoTagMediaSession)", footer, StringComparison.Ordinal);
    }

    private static string ReadManifest() => ReadSource("DeezSpoTag.Web", "wwwroot", "manifest.webmanifest");

    private static List<(string Src, string Sizes, string Purpose)> ReadIcons()
    {
        using var manifest = JsonDocument.Parse(ReadManifest());
        return manifest.RootElement
            .GetProperty("icons")
            .EnumerateArray()
            .Select(icon => (
                icon.GetProperty("src").GetString() ?? string.Empty,
                icon.GetProperty("sizes").GetString() ?? string.Empty,
                icon.GetProperty("purpose").GetString() ?? string.Empty))
            .ToList();
    }

    private static string PwaIconDirectory() => Path.Combine(RepositoryRoot(), "DeezSpoTag.Web", "wwwroot", "images", "pwa");

    private static string ReadSource(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot() }.Concat(segments).ToArray()));

    private static double ReadScale(string svg)
    {
        const string marker = "scale(";
        var start = svg.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "The icon source does not scale its artwork.");
        start += marker.Length;
        var end = svg.IndexOf(')', start);
        Assert.True(end > start, "The icon source has a malformed scale transform.");

        return double.Parse(
            svg[start..end],
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (int Width, int Height) ReadPngSize(byte[] bytes)
    {
        // Layout: 8-byte signature, 4-byte chunk length, 4-byte "IHDR" type, then width/height.
        Assert.True(bytes.Length > 24, "PWA icon is not a readable PNG.");
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'N', bytes[2]);
        Assert.Equal((byte)'G', bytes[3]);
        Assert.Equal((byte)'I', bytes[12]);
        Assert.Equal((byte)'H', bytes[13]);
        Assert.Equal((byte)'D', bytes[14]);
        Assert.Equal((byte)'R', bytes[15]);

        return (
            (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)),
            (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeezSpoTag.Web", "DeezSpoTag.Web.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
