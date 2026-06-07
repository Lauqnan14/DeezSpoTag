using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadDedupeServiceGuardrailTests
{
    [Fact]
    public void DownloadIntentService_UsesSingleDedupeService()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "DownloadIntentService.cs");

        Assert.Contains("private readonly DownloadDedupeService _dedupeService;", source, StringComparison.Ordinal);
        Assert.Contains("await _dedupeService.CheckAsync(BuildDedupeRequest(context), cancellationToken)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryValidateLibraryDuplicateStateAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveQueueDuplicateAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryBlockByGlobalBlocklistAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryBlockByRuleSet", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectEngineEnqueueHelper_UsesSingleDedupeService()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "DownloadQueueEnqueueHelper.cs");

        Assert.Contains("DownloadDedupeService dedupeService", source, StringComparison.Ordinal);
        Assert.Contains("await dedupeService.CheckAsync", source, StringComparison.Ordinal);
        Assert.Contains("DownloadDedupeService.FromQueuePayload", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDuplicateAsync(duplicateRequest", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RequeueAsync(existing.QueueUuid", source, StringComparison.Ordinal);
    }

    [Fact]
    public void QueueDuplicateLookup_TreatsAllRowsAsAuthoritativeUntilCleared()
    {
        var source = ReadSource("DeezSpoTag.Services", "Download", "Queue", "DownloadQueueRepository.cs");

        Assert.Contains("Math.Abs(item.DurationMs.Value - request.DurationMs.Value) <= 2000", source, StringComparison.Ordinal);
        Assert.Contains("AddFinalDestinationPaths(item.FinalDestinationsJson, paths);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsCompletedStatus(item.Status) && !HasExistingMaterializedFile(item)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalBlocklist_CoversTrackArtistAlbumAndGenre()
    {
        var source = ReadSource("DeezSpoTag.Services", "Library", "LibraryRepository.cs");
        var service = ReadSource("DeezSpoTag.Web", "Services", "DownloadDedupeService.cs");

        Assert.Contains("private const string GenreType = \"genre\";", source, StringComparison.Ordinal);
        Assert.Contains("field = 'genre'", source, StringComparison.Ordinal);
        Assert.Contains("request.Genres", service, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, Path.Combine(relativeParts));
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate source file.", Path.Combine(relativeParts));
    }
}
