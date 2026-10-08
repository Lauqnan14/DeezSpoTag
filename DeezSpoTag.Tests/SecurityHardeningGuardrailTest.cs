using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Web.Controllers.Api;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SecurityHardeningGuardrailTest
{
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
    ///     threads. Both production entries below need it for the same reason: the work is genuinely
    ///     detached, there is no caller left to await it, and the HTTP request has already been
    ///     answered. Both already log inside the delegate, and both filter cancellation, so the thing
    ///     the rule protects against - a silent failure - is handled at the site.
    ///     <para>
    ///         The three test entries wrap a delegate they want to observe from outside the test's own
    ///         async flow.
    ///     </para>
    ///     <para>
    ///         A new entry must carry its reason here rather than being added silently. Anything not
    ///         listed has to await its work or hand it to a service that does.
    ///     </para>
    /// </remarks>
    private static readonly string[] DetachedWorkAllowlist =
    {
        "LibraryRecommendationService.cs",
        "PlaylistPlatformSnapshotTest.cs",
        "SoulseekApiController.cs",
        "SoulseekConnectionServiceTest.cs",
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
