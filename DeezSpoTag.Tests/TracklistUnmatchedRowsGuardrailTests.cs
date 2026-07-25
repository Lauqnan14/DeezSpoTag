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
        Assert.Contains("const isDeadExternalRow = isDeadSpotifyRow || isDeadBoomplayRow || isDeadGenericExternalRow || isSourceUnmatchedDownloadableRow;", view, StringComparison.Ordinal);
        Assert.DoesNotContain("const isDeadExternalRow = (isDeadSpotifyRow || isDeadBoomplayRow || isDeadGenericExternalRow)", view, StringComparison.Ordinal);
        Assert.Contains("function isPlayableTracklistPreviewControl(control)", view, StringComparison.Ordinal);
        Assert.Contains("row.classList.contains('track-row-dead')", view, StringComparison.Ordinal);
        Assert.Contains("!isPlayableTracklistPreviewControl(nextControl)", view, StringComparison.Ordinal);
        Assert.Contains("function normalizeTrackRowPlaybackSource(source)", view, StringComparison.Ordinal);
        Assert.Contains("if (normalized === 'recommendations')", view, StringComparison.Ordinal);
        Assert.Contains("return normalized || 'deezer';", view, StringComparison.Ordinal);
        Assert.Contains("const playbackSource = normalizeTrackRowPlaybackSource(requestSource || tracklistSource || 'deezer');", view, StringComparison.Ordinal);
        Assert.Contains("'recommendations'", view, StringComparison.Ordinal);
        Assert.Contains("if (!isDeezerMatchedExternalSource(normalizedSource))", view, StringComparison.Ordinal);
        Assert.Contains("scheduleExternalTracklistMatches(tracks, tracklistSource);", view, StringComparison.Ordinal);
        Assert.DoesNotContain("externalSource !== 'deezer'", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitored_playlist_ignore_control_uses_dedicated_column()
    {
        var view = ReadSource("DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml");

        Assert.Contains("function shouldRenderIgnoreColumn(isPlaylistLike, isLocalSource)", view, StringComparison.Ordinal);
        Assert.Contains("+ (shouldRenderIgnoreColumn(isPlaylistLike, isLocalSource) ? 1 : 0)", view, StringComparison.Ordinal);
        Assert.Contains("${shouldShowIgnore ? '<col class=\"col-ignore\">' : ''}", view, StringComparison.Ordinal);
        Assert.Contains("${shouldShowIgnore ? `<th class=\"track-ignore\"><span class=\"track-ignore-toggle ${ignoreToggleClass}\" title=\"${ignoreToggleTitle}\">Ignore</span></th>` : ''}", view, StringComparison.Ordinal);
        Assert.Contains("${shouldShowIgnore ? `<td class=\"track-ignore\">", view, StringComparison.Ordinal);
        Assert.Contains("setIgnoreVisibility(true);", view, StringComparison.Ordinal);
        Assert.DoesNotContain("setIgnoreVisibility(false);\n        } else {\n            applyDefaultMonitorState(false);", view, StringComparison.Ordinal);
        Assert.DoesNotContain("tracks-table--with-ignore col.col-actions { width: 168px; }", view, StringComparison.Ordinal);
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
