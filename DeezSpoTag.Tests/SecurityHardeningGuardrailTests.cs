using System;
using System.IO;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Web.Controllers.Api;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SecurityHardeningGuardrailTests
{
    private static readonly string[] BlockingWaitAllowlist =
    {
        "LibraryConfigStore.cs",
        "ShazamRecognitionService.cs"
    };

    [Fact]
    public void SourceCode_MustNotUseTaskRunWrappers()
    {
        var srcRoot = ResolveSrcRoot();
        var taskRunPattern = "Task" + ".Run(";
        var offenders = Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.EndsWith("SecurityHardeningGuardrailTests.cs", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(taskRunPattern, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Task.Run wrappers found in: " + string.Join(", ", offenders.Select(Path.GetFileName)));
    }

    [Fact]
    public void SourceCode_MustNotUseBlockingWaitPrimitives()
    {
        var srcRoot = ResolveSrcRoot();
        var offenders = Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                var waitPattern = ".Wait(" + ")";
                var getResultPattern = "GetAwaiter()." + "GetResult()";
                return source.Contains(waitPattern, StringComparison.Ordinal)
                    || source.Contains(getResultPattern, StringComparison.Ordinal);
            })
            .Where(path => !path.EndsWith("SecurityHardeningGuardrailTests.cs", StringComparison.Ordinal))
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

    private static void AssertPostRequiresAntiforgery(Type controllerType, string methodName)
    {
        var method = controllerType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        Assert.Contains(method.GetCustomAttributes(inherit: true), attribute => attribute is ValidateAntiForgeryTokenAttribute);
    }
}
