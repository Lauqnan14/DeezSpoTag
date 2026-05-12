using System;
using System.IO;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LibraryScanTriggerGuardrailTests
{
    private static readonly long[] ExpectedChangedFolderIds = [5L];

    [Fact]
    public void DownloadOrchestration_UsesScopedChangedFolderScans()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs");

        Assert.Contains("RunChangedFoldersAsync", source);
        Assert.DoesNotContain("_scanRunner.RunAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagPostMoveScan_UsesScopedChangedFolderScans()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");

        Assert.Contains("ResolveChangedLibraryFolderIdsAsync", source);
        Assert.Contains("_libraryScanRunner.RunChangedFoldersAsync", source);
        Assert.DoesNotContain("_libraryScanRunner.EnqueueAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RealtimeLibraryScanService_IsNotRegisteredAsHostedService()
    {
        var source = ReadSource("DeezSpoTag.Web", "Program.cs");

        Assert.DoesNotContain("LibraryRealtimeScanService", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagMoveSummary_TracksChangedFolderIds()
    {
        var summary = new AutoTagMoveSummary();

        summary.MarkChangedFolder(5);
        summary.MarkChangedFolder(5);
        summary.MarkChangedFolder(0);
        var clone = summary.Clone();

        Assert.Equal(ExpectedChangedFolderIds, summary.ChangedFolderIds);
        Assert.Equal(ExpectedChangedFolderIds, clone.ChangedFolderIds);
    }

    private static string ReadSource(params string[] pathParts)
    {
        var path = Path.Combine(GetRepositoryRoot(), Path.Combine(pathParts));
        return File.ReadAllText(path);
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
    }
}
