using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TidalAtmosTracklistGuardrailTests
{
    [Fact]
    public void TidalAlbumTracklist_UsesItemsEndpointAndCarriesAtmosMetadata()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "ExternalPlaylistTracklistApiController.cs");

        Assert.Contains("FetchTidalAlbumTracksAsync(albumId, token, cancellationToken)", source, StringComparison.Ordinal);
        Assert.Contains("$\"albums/{Uri.EscapeDataString(albumId)}/items\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$\"albums/{Uri.EscapeDataString(albumId)}/tracks\"", source, StringComparison.Ordinal);
        Assert.Contains("var hasAtmos = HasTidalAtmos(albumNode.Value) || TracksContainAtmos(tracks);", source, StringComparison.Ordinal);
        Assert.Contains("audioQuality = GetString(albumNode.Value, \"audioQuality\")", source, StringComparison.Ordinal);
        Assert.Contains("tidalId = trackId", source, StringComparison.Ordinal);
        Assert.Contains("hasAtmos,", source, StringComparison.Ordinal);
        Assert.Contains("quality = hasAtmos ? \"DOLBY_ATMOS\" : audioQuality", source, StringComparison.Ordinal);
        Assert.Contains("private static bool HasTidalAtmos(JsonElement item)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AtmosTracklists_ShowQualityAndUseDualRoutingForAllAtmosCapableRows()
    {
        var view = ReadSource("DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml");

        Assert.Contains("return tracklistIsAtmosView;", view, StringComparison.Ordinal);
        Assert.Contains("data-track-has-atmos=\"${isAtmosTrackRow(track) ? 'true' : 'false'}\"", view, StringComparison.Ordinal);
        Assert.Contains("const hasSelectedAtmosAudioTracks = Array.isArray(trackElements)", view, StringComparison.Ordinal);
        Assert.Contains("const useDualAtmosRouting = tracklistMultiQualityEnabled", view, StringComparison.Ordinal);
        Assert.DoesNotContain("const hasSelectedAtmosAudioTracks = tracklistSource === 'apple'", view, StringComparison.Ordinal);
        Assert.DoesNotContain("const useDualAtmosRouting = tracklistSource === 'apple'", view, StringComparison.Ordinal);
        Assert.Contains("const isDirectAtmosTrack = (source === 'tidal' || source === 'amazon')", view, StringComparison.Ordinal);
        Assert.Contains("secondaryDestinationFolderId: useDualRoutingForTrack ? atmosDestinationFolderId : null", view, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceDownloadRowsRemainGreyedButSelectableWithoutDeezerMatch()
    {
        var view = ReadSource("DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml");

        Assert.Contains("function isDirectSourceDownloadSource(source)", view, StringComparison.Ordinal);
        Assert.Contains("normalized === 'spotify'", view, StringComparison.Ordinal);
        Assert.Contains("normalized === 'apple'", view, StringComparison.Ordinal);
        Assert.Contains("normalized === 'tidal'", view, StringComparison.Ordinal);
        Assert.Contains("normalized === 'qobuz'", view, StringComparison.Ordinal);
        Assert.Contains("normalized === 'amazon'", view, StringComparison.Ordinal);
        Assert.Contains("function isDirectSourceDownloadCheckbox(checkbox)", view, StringComparison.Ordinal);
        Assert.Contains("function resolveDownloadSourceForQueue(source, trackElements = [])", view, StringComparison.Ordinal);
        Assert.Contains("function hasDirectSourceDownloadIdentity(track, platformIds, source, spotifyId = '')", view, StringComparison.Ordinal);
        Assert.Contains("const isDirectDownloadExternalSource = playbackSource === 'tidal'", view, StringComparison.Ordinal);
        Assert.Contains("|| playbackSource === 'qobuz'", view, StringComparison.Ordinal);
        Assert.Contains("|| playbackSource === 'amazon';", view, StringComparison.Ordinal);
        Assert.Contains("const isDirectSourceDownloadRow = hasDirectSourceDownloadIdentity(track, platformIds, playbackSource, spotifyId);", view, StringComparison.Ordinal);
        Assert.Contains("const isSourceUnmatchedDownloadableRow = isDirectSourceDownloadRow && !deezerId;", view, StringComparison.Ordinal);
        Assert.Contains("const isDeadExternalRow = isDeadSpotifyRow || isDeadBoomplayRow || isDeadGenericExternalRow || isSourceUnmatchedDownloadableRow;", view, StringComparison.Ordinal);
        Assert.Contains("const shouldDisableCheckbox = isDeadExternalRow && !isSourceUnmatchedDownloadableRow;", view, StringComparison.Ordinal);
        Assert.Contains("track-row-source-downloadable", view, StringComparison.Ordinal);
        Assert.Contains("return !!deezerId || isDirectSourceDownloadCheckbox(checkbox);", view, StringComparison.Ordinal);
        Assert.Contains("const source = resolveDownloadSourceForQueue(tracklistSource, trackElements);", view, StringComparison.Ordinal);
        Assert.Contains("row.classList.toggle('track-row-source-downloadable', !deezerId && sourceDownloadable);", view, StringComparison.Ordinal);
        Assert.Contains("row.classList.toggle('track-row-source-downloadable', isDirectSourceDownloadCheckbox(row.querySelector('.track-checkbox')));", view, StringComparison.Ordinal);
        Assert.Contains("if (isDirectSourceDownloadCheckbox(checkbox))", view, StringComparison.Ordinal);
        Assert.Contains("checkbox.setAttribute('title', 'Select source track');", view, StringComparison.Ordinal);
        Assert.Contains("sourceService: source", view, StringComparison.Ordinal);
        Assert.Contains("const isDeadGenericExternalRow = isGenericExternalSource && !isDirectDownloadExternalSource && !deezerId;", view, StringComparison.Ordinal);
        Assert.DoesNotContain("const isDeadGenericExternalRow = isGenericExternalSource && !deezerId;", view, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] relativeParts)
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Combine(new[] { repoRoot }.Concat(relativeParts).ToArray()));
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root.");
    }
}
