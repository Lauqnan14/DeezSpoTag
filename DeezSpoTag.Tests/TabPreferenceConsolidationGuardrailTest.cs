using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Guards the "remember last opened tab" contract. Every tab group must reach the
/// setting, the storage key and the server-side mirror through tab-preferences.js, so
/// the behaviour cannot drift apart per page again.
/// </summary>
public sealed class TabPreferenceConsolidationGuardrailTest
{
    private static readonly string[] TabConsumers =
    {
        "Views/Login/Index.cshtml",
        "Views/Search/Index.cshtml",
        "Views/Artist/Index.cshtml",
        "Views/MediaManagement/Index.cshtml",
        "wwwroot/js/library-soundtracks.js",
        "wwwroot/js/autotag.js",
        "wwwroot/js/home-index.js"
    };

    [Fact]
    public void TabPreferences_ExposesTheSharedContract()
    {
        var module = ReadWebRoot("tab-preferences.js");

        Assert.Contains("globalThis.TabPreferences = {", module, StringComparison.Ordinal);
        Assert.Contains("isEnabled: isRememberEnabled", module, StringComparison.Ordinal);
        Assert.Contains("keyFor: buildStorageKeyForId", module, StringComparison.Ordinal);
        Assert.Contains("read: readTabPreference", module, StringComparison.Ordinal);
        Assert.Contains("write: writeTabPreference", module, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Views/Login/Index.cshtml")]
    [InlineData("Views/Search/Index.cshtml")]
    [InlineData("Views/Artist/Index.cshtml")]
    [InlineData("wwwroot/js/library-soundtracks.js")]
    [InlineData("wwwroot/js/autotag.js")]
    [InlineData("wwwroot/js/home-index.js")]
    public void Consumers_DelegateToTheSharedApiInsteadOfReimplementingStorage(string relativePath)
    {
        var source = Read(relativePath);

        // A consumer that re-reads the raw setting or rebuilds the key has forked the
        // contract, which is exactly the duplication this guards against.
        Assert.DoesNotContain("'tabs-preference-enabled'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tabs-preference-enabled\"", source, StringComparison.Ordinal);

        if (relativePath == "wwwroot/js/home-index.js")
        {
            // Home reads the Search page's key from a different path, so it keeps a
            // local key list but still delegates the setting.
            Assert.Contains("SEARCH_TAB_STORAGE_PREFIX", source, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("tabs:last:", source, StringComparison.Ordinal);
        }

        Assert.Contains("TabPreferences", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheSharedModuleBuildsKeysAndGatesOnTheSetting()
    {
        // The settings page owns writing the preference itself, and the layout plus
        // user-preferences.js own the server-side mirror of it. Nobody else may gate
        // on the raw setting or build a remembered-tab key.
        var allowed = new[]
        {
            "Views/Settings/Index.cshtml",
            "Views/Shared/_Layout.cshtml",
            "wwwroot/js/user-preferences.js",
            "wwwroot/js/tab-preferences.js"
        };

        foreach (var relativePath in TabConsumers)
        {
            if (allowed.Contains(relativePath))
            {
                continue;
            }

            var source = Read(relativePath);
            Assert.DoesNotContain("tabs-preference-enabled", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SharedReadAndWrite_HonourTheSettingAndClearWhenEmpty()
    {
        var module = ReadWebRoot("tab-preferences.js");

        var read = ExtractFunction(module, "function readTabPreference(");
        Assert.Contains("!isRememberEnabled()", read, StringComparison.Ordinal);
        Assert.Contains("return null;", read, StringComparison.Ordinal);

        var write = ExtractFunction(module, "function writeTabPreference(");
        // Disabled or empty clears the entry, so a stale tab cannot come back.
        Assert.Contains("!normalized || !isRememberEnabled()", write, StringComparison.Ordinal);
        Assert.Contains("removeItem(storageKey)", write, StringComparison.Ordinal);
        Assert.Contains("setTabSelection?.(storageKey, \"\")", write, StringComparison.Ordinal);
        Assert.Contains("setItem(storageKey, normalized)", write, StringComparison.Ordinal);
        Assert.Contains("setTabSelection?.(storageKey, normalized)", write, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKeyFormatIsProducedInExactlyOnePlace()
    {
        var module = ReadWebRoot("tab-preferences.js");

        Assert.Contains("return `${STORAGE_PREFIX}${globalThis.location.pathname}:${id}`;", module, StringComparison.Ordinal);

        // The generic handler must route through the shared builder too.
        Assert.Contains("return buildStorageKeyForId(getTabListId(tabList));", module, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtistDiscography_KeepsItsPerArtistKeySoSavedTabsSurvive()
    {
        var artist = Read("Views/Artist/Index.cshtml");

        // The Artist page remembers a tab within one artist, not a page-level tab, so
        // its key is intentionally different. Migrating it would orphan saved values.
        Assert.Contains("artist-page-tab:", artist, StringComparison.Ordinal);
        Assert.DoesNotContain("TabPreferences?.keyFor?.(\"artist", artist, StringComparison.Ordinal);
    }

    [Fact]
    public void LoginKeepsSessionStorageForTheInFlightOauthTab()
    {
        var login = Read("Views/Login/Index.cshtml");

        // sessionStorage tracks the OAuth round trip, which is not a user preference and
        // must not be folded into the shared server-backed store.
        Assert.Contains("LOGIN_TAB_STORAGE_KEY", login, StringComparison.Ordinal);
        Assert.Contains("sessionStorage.setItem(LOGIN_TAB_STORAGE_KEY, tabId)", login, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDeliberateDeepLinkOverridesStillExist()
    {
        // Arriving from a finished download should land on Downloads, not on whichever
        // tab was last used, so the ?tab= override is intentional.
        Assert.Contains("/Activities?tab=downloads-content", ReadWebRoot("download-client.js"), StringComparison.Ordinal);
        Assert.Contains("/Activities?tab=autotag-status-content", Read("Views/AutoTag/Index.cshtml"), StringComparison.Ordinal);
    }

    /// <summary>
    ///     Reads a script from wwwroot. The app's scripts now live under <c>wwwroot/js</c>, but the resolver
    ///     falls back to the wwwroot root so this keeps working if a script is ever served from the top level.
    ///     Only the lookup path changed; the assertions are unchanged.
    /// </summary>
    private static string ReadWebRoot(string fileName)
    {
        var webRoot = Path.Combine(RepositoryRoot(), "DeezSpoTag.Web", "wwwroot");
        var inJsFolder = Path.Combine(webRoot, "js", fileName);
        if (File.Exists(inJsFolder))
        {
            return File.ReadAllText(inJsFolder);
        }

        return File.ReadAllText(Path.Combine(webRoot, fileName));
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(
            new[] { RepositoryRoot(), "DeezSpoTag.Web" }.Concat(relativePath.Split('/')).ToArray()));

    private static string ExtractFunction(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Function not found: {signature}");

        var bodyStart = source.IndexOf('{', start);
        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }

            if (source[index] != '}')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return source[start..(index + 1)];
            }
        }

        throw new InvalidOperationException($"Unbalanced braces in: {signature}");
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
