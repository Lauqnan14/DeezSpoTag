using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TracklistUnmatchedRowsGuardrailTests
{
    [Fact]
    public void Unmatched_row_style_and_playback_skip_are_shared_across_sources()
    {
        var view = ReadSource("DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml");

        Assert.Contains("const deezerMatchedExternalSources = new Set([", view, StringComparison.Ordinal);
        Assert.Contains("'spotify'", view, StringComparison.Ordinal);
        Assert.Contains("'boomplay'", view, StringComparison.Ordinal);
        Assert.Contains("'tidal'", view, StringComparison.Ordinal);
        Assert.Contains("'qobuz'", view, StringComparison.Ordinal);
        Assert.Contains("'amazon'", view, StringComparison.Ordinal);

        Assert.Contains("applyUnmatchedRowState(row);", view, StringComparison.Ordinal);
        Assert.Contains("function markUnresolvedExternalRowsAsUnmatched(indices)", view, StringComparison.Ordinal);
        Assert.Contains("markUnresolvedExternalRowsAsUnmatched(processedIndices);", view, StringComparison.Ordinal);
        Assert.Contains("const isDeadExternalRow = isDeadSpotifyRow || isDeadBoomplayRow || isDeadGenericExternalRow;", view, StringComparison.Ordinal);
        Assert.DoesNotContain("const isDeadExternalRow = (isDeadSpotifyRow || isDeadBoomplayRow || isDeadGenericExternalRow)", view, StringComparison.Ordinal);
        Assert.Contains("function isPlayableTracklistPreviewControl(control)", view, StringComparison.Ordinal);
        Assert.Contains("row.classList.contains('track-row-dead')", view, StringComparison.Ordinal);
        Assert.Contains("!isPlayableTracklistPreviewControl(nextControl)", view, StringComparison.Ordinal);
        Assert.Contains("function normalizeTrackRowPlaybackSource(source)", view, StringComparison.Ordinal);
        Assert.Contains("return normalized === 'recommendations' ? 'deezer' : (normalized || 'deezer');", view, StringComparison.Ordinal);
        Assert.Contains("const playbackSource = normalizeTrackRowPlaybackSource(requestSource || tracklistSource || 'deezer');", view, StringComparison.Ordinal);
        Assert.Contains("'recommendations'", view, StringComparison.Ordinal);
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
