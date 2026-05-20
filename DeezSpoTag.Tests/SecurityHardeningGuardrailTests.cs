using System;
using System.IO;
using System.Linq;
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
}
