using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class BoomplayDeezerMatchConsolidationTests
{
    [Fact]
    public void Boomplay_matching_is_owned_by_shared_service()
    {
        var boomplayController = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "BoomplayApiController.cs");
        var resolveController = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "ResolveDeezerApiController.cs");
        var matchService = ReadSource("DeezSpoTag.Web", "Services", "BoomplayDeezerMatchService.cs");
        var tracklistView = ReadSource("DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml");

        Assert.DoesNotContain("BoomplayDeezerMatchService _boomplayDeezerMatchService", boomplayController, StringComparison.Ordinal);
        Assert.DoesNotContain("_boomplayDeezerMatchService.ResolveTrackAsync", boomplayController, StringComparison.Ordinal);
        Assert.Contains("BoomplayDeezerMatchService _boomplayDeezerMatchService", resolveController, StringComparison.Ordinal);
        Assert.Contains("_boomplayDeezerMatchService.ResolveAsync", resolveController, StringComparison.Ordinal);

        Assert.Contains("TryResolveIsrcFirstAsync", matchService, StringComparison.Ordinal);
        Assert.Contains("TryResolveDirectMetadataAsync", matchService, StringComparison.Ordinal);
        Assert.Contains("TryResolveSearchFallbackAsync", matchService, StringComparison.Ordinal);
        Assert.Contains("TryResolveByEnrichedIsrcAsync", matchService, StringComparison.Ordinal);
        Assert.Contains("if (!HasAnySourceMetadata(context))", matchService, StringComparison.Ordinal);
        Assert.Contains("await EnrichBoomplayMetadataAsync(context, cancellationToken);", matchService, StringComparison.Ordinal);
        Assert.DoesNotContain("TryResolveBySongLinkAsync", matchService, StringComparison.Ordinal);
        Assert.DoesNotContain("SongLinkResolver", matchService, StringComparison.Ordinal);
        Assert.True(
            matchService.IndexOf("TryResolveSearchFallbackAsync(context", StringComparison.Ordinal)
            < matchService.IndexOf("TryResolveByEnrichedIsrcAsync(context", StringComparison.Ordinal),
            "Boomplay stream-tag ISRC enrichment must only run after normal metadata matching fails.");

        Assert.DoesNotContain("TryResolveBoomplay", boomplayController, StringComparison.Ordinal);
        Assert.DoesNotContain("TryResolveBoomplay", resolveController, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveDeezerIdFromCoreMetadataAsync", boomplayController, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveDeezerIdViaDirectMetadataAsync", boomplayController, StringComparison.Ordinal);
        Assert.DoesNotContain("DeezerResolvedMetadata", boomplayController, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildDeezerResolutionCacheKey", boomplayController, StringComparison.Ordinal);
        Assert.DoesNotContain("includeMeta', 'true'", tracklistView, StringComparison.Ordinal);
        Assert.DoesNotContain("shouldHydrateDeezerMetadata", tracklistView, StringComparison.Ordinal);
        Assert.DoesNotContain("applyDeezerMetadataToRow", tracklistView, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist/tracks/stream", boomplayController, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist/metadata", boomplayController, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist/tracks", boomplayController, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist/tracks/stream", tracklistView, StringComparison.Ordinal);
        Assert.DoesNotContain("loadBoomplayPlaylistTracksStream", tracklistView, StringComparison.Ordinal);
        Assert.DoesNotContain("EventSource", tracklistView, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] relativeParts)
    {
        var root = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(root))
        {
            var candidate = Path.Combine(new[] { root }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            root = Directory.GetParent(root)?.FullName;
        }

        throw new FileNotFoundException("Unable to locate source file.", Path.Combine(relativeParts));
    }
}
