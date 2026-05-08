using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TabPreferenceGuardrailTests
{
    [Fact]
    public void CommonFooter_LoadsGlobalTabPreferenceScriptAfterBootstrap()
    {
        var root = ResolveRepoRoot();
        var footerPath = Path.Join(root, "DeezSpoTag.Web", "Views", "Shared", "_CommonFooterScripts.cshtml");
        Assert.True(File.Exists(footerPath), $"Missing common footer: {footerPath}");

        var source = File.ReadAllText(footerPath);
        var bootstrapIndex = source.IndexOf("bootstrap.bundle.min.js", StringComparison.Ordinal);
        var tabPreferenceIndex = source.IndexOf("~/js/tab-preferences.js", StringComparison.Ordinal);

        Assert.True(bootstrapIndex >= 0, "Bootstrap script was not found in the common footer.");
        Assert.True(tabPreferenceIndex >= 0, "Global tab preference script was not loaded in the common footer.");
        Assert.True(
            bootstrapIndex < tabPreferenceIndex,
            "Global tab preference script must load after Bootstrap so it can restore tabs with bootstrap.Tab.");
    }

    [Fact]
    public void GlobalTabPreferenceScript_PersistsAndRestoresBootstrapTabs()
    {
        var root = ResolveRepoRoot();
        var scriptPath = Path.Join(root, "DeezSpoTag.Web", "wwwroot", "js", "tab-preferences.js");
        Assert.True(File.Exists(scriptPath), $"Missing tab preference script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);

        Assert.Contains("shown.bs.tab", source, StringComparison.Ordinal);
        Assert.Contains("tabs-preference-enabled", source, StringComparison.Ordinal);
        Assert.Contains("tabs:last:", source, StringComparison.Ordinal);
        Assert.Contains("globalThis.bootstrap.Tab.getOrCreateInstance(trigger).show();", source, StringComparison.Ordinal);
        Assert.Contains("document.addEventListener(\"DOMContentLoaded\", restoreAllTabs);", source, StringComparison.Ordinal);
        Assert.Contains("data-no-global-tab-fallback", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LoginTabs_RemainOptedOutOfGlobalTabPersistence()
    {
        var root = ResolveRepoRoot();
        var loginPath = Path.Join(root, "DeezSpoTag.Web", "Views", "Login", "Index.cshtml");
        Assert.True(File.Exists(loginPath), $"Missing login view: {loginPath}");

        var source = File.ReadAllText(loginPath);

        Assert.Contains("id=\"platformLoginTabs\"", source, StringComparison.Ordinal);
        Assert.Contains("data-no-global-tab-fallback=\"true\"", source, StringComparison.Ordinal);
        Assert.Contains("getLoginTabPreferenceKey()", source, StringComparison.Ordinal);
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Join(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test output path.");
    }
}
