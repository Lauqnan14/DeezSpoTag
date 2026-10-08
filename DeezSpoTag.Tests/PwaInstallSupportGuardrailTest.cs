using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

        // A latch that is only ever written would silence the prompt forever, even
        // after the app is uninstalled, so the browser stays the authority.
        Assert.Contains("if (this.isRunningAsInstalledApp()) {\n            this.markPwaInstalled();\n            return;\n        }", site, StringComparison.Ordinal);
        Assert.Contains("globalThis.addEventListener('appinstalled'", site, StringComparison.Ordinal);
        Assert.Contains("this.markPwaInstalled();", site, StringComparison.Ordinal);
        Assert.Contains("this.clearRecordedPwaInstall();", site, StringComparison.Ordinal);

        // The listener must be registered before the gates return early, otherwise a
        // recorded install can never be cleared.
        var listenerIndex = site.IndexOf("globalThis.addEventListener('beforeinstallprompt'", StringComparison.Ordinal);
        var gateIndex = site.IndexOf("if (this.hasRecordedPwaInstall()) {\n            return;\n        }", StringComparison.Ordinal);
        Assert.True(listenerIndex >= 0, "beforeinstallprompt listener was not registered.");
        Assert.True(gateIndex >= 0, "The recorded-install gate was not found.");
        Assert.True(
            listenerIndex < gateIndex,
            "beforeinstallprompt must be registered before the recorded-install gate returns.");
    }

    [Fact]
    public void InstallPrompt_DismissalStillUsesTheSevenDayValve()
    {
        var site = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "site.js");

        Assert.Contains("pwa-prompt-dismissed", site, StringComparison.Ordinal);
        Assert.Contains("const sevenDays = 7 * 24 * 60 * 60 * 1000;", site, StringComparison.Ordinal);
    }

    [Fact]
    public void Preferences_CarryTheRecordedPwaInstallThroughTheSyncPipeline()
    {
        var preferences = ReadSource("DeezSpoTag.Web", "Services", "UserPreferencesStore.cs");
        var layout = ReadSource("DeezSpoTag.Web", "Views", "Shared", "_Layout.cshtml");
        var userPreferencesJs = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "user-preferences.js");
        var site = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "site.js");

        Assert.Contains("public long? PwaPromptDismissedAt", preferences, StringComparison.Ordinal);
        Assert.Contains("public long? PwaInstalledAt", preferences, StringComparison.Ordinal);

        // The layout shadows localStorage, so these keys only persist when they are
        // mirrored into the server-side preference store.
        Assert.Contains("'pwa-installed': 'pwaInstalledAt'", layout, StringComparison.Ordinal);
        Assert.Contains("'pwa-installed':                         'pwaInstalledAt'", userPreferencesJs, StringComparison.Ordinal);

        Assert.Contains("'pwa-installed'", layout, StringComparison.Ordinal);
        Assert.Contains("pwaInstalledStorageKey: 'pwa-installed'", site, StringComparison.Ordinal);
        Assert.Contains("UserPrefs.set('pwaInstalledAt', installedAt)", site, StringComparison.Ordinal);
        Assert.Contains("UserPrefs.set('pwaInstalledAt', 0)", site, StringComparison.Ordinal);
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
