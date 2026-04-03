using System;
using System.IO;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Web.Controllers.Api;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagStatusRefreshGuardrailTests
{
    [Fact]
    public void GetJob_DisablesResponseCaching()
    {
        var method = typeof(AutoTagJobsController).GetMethod(nameof(AutoTagJobsController.GetJob));
        Assert.NotNull(method);

        var responseCache = method!.GetCustomAttributes(typeof(ResponseCacheAttribute), inherit: true)
            .OfType<ResponseCacheAttribute>()
            .SingleOrDefault();

        Assert.NotNull(responseCache);
        Assert.True(responseCache!.NoStore);
        Assert.Equal(ResponseCacheLocation.None, responseCache.Location);
    }

    [Fact]
    public void GetLatestJob_DisablesResponseCaching()
    {
        var method = typeof(AutoTagJobsController).GetMethod(nameof(AutoTagJobsController.GetLatestJob));
        Assert.NotNull(method);

        var responseCache = method!.GetCustomAttributes(typeof(ResponseCacheAttribute), inherit: true)
            .OfType<ResponseCacheAttribute>()
            .SingleOrDefault();

        Assert.NotNull(responseCache);
        Assert.True(responseCache!.NoStore);
        Assert.Equal(ResponseCacheLocation.None, responseCache.Location);
    }

    [Fact]
    public void AutoTagStatusScript_UsesNoStoreFetchAndResumeRefreshHooks()
    {
        var repoRoot = ResolveRepoRoot();
        var scriptPath = Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js");
        Assert.True(File.Exists(scriptPath), $"Missing status script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);
        Assert.Contains("cache: \"no-store\"", source, StringComparison.Ordinal);
        Assert.Contains("bindPageResumeRefresh", source, StringComparison.Ordinal);
        Assert.Contains("visibilitychange", source, StringComparison.Ordinal);
        Assert.Contains("pageshow", source, StringComparison.Ordinal);
        Assert.Contains("\"focus\"", source, StringComparison.Ordinal);
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
