using System;
using System.IO;
using System.Linq;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Soulseek;
using DeezSpoTag.Services.Download.Shared;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Proves Soulseek uses the unified dedupe path and adds no second one of its own.
/// </summary>
/// <remarks>
///     The design forbids a Soulseek-specific dedupe. These tests check both halves of that: that the shared
///     <see cref="DownloadDedupeService"/> is what decides, and that nothing in the Soulseek code path tries to
///     decide for itself.
/// </remarks>
public sealed class SoulseekDedupeBeforeSearchTest : IDisposable
{
    public SoulseekDedupeBeforeSearchTest()
        => ResolveRepoRoot();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void SoulseekCodePathContainsNoDedupeImplementation()
    {
        // A duplicate check written for Soulseek would be a second, divergent dedupe path. The queue payload is
        // built from EngineQueueItemBase, which is what DownloadDedupeService.FromQueuePayload consumes, so
        // there is nothing Soulseek-specific to dedupe with.
        var downloadService = ReadSoulseekSource("SoulseekDownloadService.cs");
        var processor = ReadSoulseekSource("SoulseekEngineProcessor.cs");
        var queueItem = ReadSoulseekSource("SoulseekQueueItem.cs");

        foreach (var source in new[] { downloadService, processor, queueItem })
        {
            Assert.DoesNotContain("DownloadDedupeService", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CheckLibraryAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("queue_duplicate", source, StringComparison.Ordinal);
            Assert.DoesNotContain("library_duplicate", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SoulseekQueueItemInheritsTheShapeTheSharedDedupeReads()
    {
        // DownloadDedupeService.FromQueuePayload takes an EngineQueueItemBase, so inheriting from it is what
        // puts Soulseek on the unified path.
        Assert.True(typeof(EngineQueueItemBase).IsAssignableFrom(typeof(SoulseekQueueItem)));
    }

    [Fact]
    public void EngineIsAThinAdapterOntoTheSharedPipeline()
    {
        // The engine must delegate to the shared helper and to the download service. If it talked to the slskd
        // client directly, it would own decisions the shared pipeline is supposed to own.
        var processor = ReadSoulseekSource("SoulseekEngineProcessor.cs");

        Assert.Contains("EngineQueueProcessorHelper.ProcessQueueItemAsync", processor, StringComparison.Ordinal);
        Assert.Contains("ISoulseekDownloadService", processor, StringComparison.Ordinal);
        Assert.Contains(".DownloadAsync(", processor, StringComparison.Ordinal);
        Assert.DoesNotContain("ISlskdClient", processor, StringComparison.Ordinal);
    }

    [Fact]
    public void SoulseekDownloadFailsWhenNoCandidateMatches_RatherThanDownloadingSomethingElse()
    {
        // The corollary of dedupe-before-search: when dedupe admits an item but nothing matches on Soulseek, the
        // engine must report a no-match and not fall back to an arbitrary file.
        var downloadService = ReadSoulseekSource("SoulseekDownloadService.cs");

        // "Nothing acceptable" is now a filtered emptiness rather than a null best: peers that already failed
        // this item are excluded so the next attempt looks at a different peer, and an item with no acceptable
        // candidate left is a no-match.
        Assert.Contains("SoulseekNoMatchException", downloadService, StringComparison.Ordinal);
        Assert.Contains("var eligible = outcome.Candidates", downloadService, StringComparison.Ordinal);
        Assert.Contains("!excludedPeers.Contains(candidate.Username)", downloadService, StringComparison.Ordinal);
        Assert.Contains("_scoring.SelectBest(eligible)", downloadService, StringComparison.Ordinal);
        Assert.Contains("if (usable is null)", downloadService, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySoulseekServiceLivesUnderTheDownloadNamespace()
    {
        // A stray Soulseek download service outside the download pipeline would bypass the shared enqueue and
        // dedupe path entirely, so every file in the folder must sit in that namespace.
        var directory = Path.Join(ResolveRepoRoot(), "DeezSpoTag.Services", "Download", "Soulseek");
        Assert.True(Directory.Exists(directory), "Soulseek service directory is missing.");

        var files = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var wrongNamespace = files
            .Where(path => !File.ReadAllText(path).Contains(
                "namespace DeezSpoTag.Services.Download.Soulseek;",
                StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path))
            .ToArray();

        Assert.Empty(wrongNamespace);
    }

    private static string ReadSoulseekSource(string fileName)
        => File.ReadAllText(Path.Join(ResolveRepoRoot(), "DeezSpoTag.Services", "Download", "Soulseek", fileName));

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Services")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
