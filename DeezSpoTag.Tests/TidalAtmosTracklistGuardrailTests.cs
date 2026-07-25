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
        Assert.Contains("const isSourceUnmatchedDownloadableRow = playbackSource !== 'apple'", view, StringComparison.Ordinal);
        Assert.Contains("&& isDirectSourceDownloadRow", view, StringComparison.Ordinal);
        Assert.Contains("&& !deezerId;", view, StringComparison.Ordinal);
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

    [Fact]
    public void TidalTracklistQueueing_UsesInternalTidalIdentityInsteadOfPublicUrl()
    {
        var view = ReadSource("DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml");
        var client = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "download-client.js");
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "TidalDownloadApiController.cs");
        var requestBuilder = ReadSource("DeezSpoTag.Services", "Download", "Tidal", "TidalRequestBuilder.cs");
        var processor = ReadSource("DeezSpoTag.Services", "Download", "Tidal", "TidalEngineProcessor.cs");

        Assert.Contains("const queueSourceUrl = source === 'tidal' && tidalId", view, StringComparison.Ordinal);
        Assert.Contains("? `tidal:track:${tidalId}`", view, StringComparison.Ordinal);
        Assert.Contains("url: queueSourceUrl", view, StringComparison.Ordinal);
        Assert.Contains("displaySourceUrl: resolvedSourceUrl || undefined", view, StringComparison.Ordinal);

        Assert.Contains("tidalId: ctx.options?.metadata?.tidalId || this.extractInternalTidalTrackId(ctx.url) || undefined", client, StringComparison.Ordinal);
        Assert.Contains("extractInternalTidalTrackId(url)", client, StringComparison.Ordinal);
        Assert.Contains("/^tidal:track:(\\d+)$/i", client, StringComparison.Ordinal);
        Assert.DoesNotContain("const webMatch = /\\/track\\/(\\d+)/i.exec(trimmed);", client, StringComparison.Ordinal);
        Assert.DoesNotContain("this.hostMatches(parsed.hostname, ['tidal.com'])", client, StringComparison.Ordinal);

        Assert.Contains("return BuildInternalTidalTrackIdentity(trackId);", controller, StringComparison.Ordinal);
        Assert.Contains("var internalPrefix = $\"tidal:{entityType}:\";", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("TryExtractTidalEntityId(sourceUrl, \"track\")", controller, StringComparison.Ordinal);
        Assert.Contains("private static string BuildInternalTidalTrackIdentity(string trackId)", controller, StringComparison.Ordinal);

        Assert.Contains("request.ServiceUrl.StartsWith(\"tidal:track:\", StringComparison.OrdinalIgnoreCase)", requestBuilder, StringComparison.Ordinal);
        Assert.Contains("request.ServiceUrl = string.Empty;", requestBuilder, StringComparison.Ordinal);

        Assert.Contains("return persistedId ?? string.Empty;", processor, StringComparison.Ordinal);
        Assert.DoesNotContain("TryExtractTrackId(payload.SourceUrl)", processor, StringComparison.Ordinal);
        Assert.DoesNotContain("TryExtractTrackId(payload.Url)", processor, StringComparison.Ordinal);
    }

    [Fact]
    public void TidalTracklistRouting_UsesNativeIdWithoutExternalUrl()
    {
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "ExternalPlaylistTracklistApiController.cs");
        var tracklistView = ReadSource("DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml");
        var home = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "home-index.js");
        var search = ReadSource("DeezSpoTag.Web", "Views", "Search", "Index.cshtml");

        Assert.Contains("&& string.IsNullOrWhiteSpace(playlistUrl))", controller, StringComparison.Ordinal);
        Assert.Contains("Tidal ID is required.", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("if (string.IsNullOrWhiteSpace(playlistUrl))\n        {\n            return BadRequest(new { available = false, error = \"External URL is required.\" });\n        }", controller, StringComparison.Ordinal);

        Assert.Contains("if (!sourceUrl && normalizedSource !== 'tidal')", tracklistView, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceUrl = `https://tidal.com/browse/${encodeURIComponent(normalizedType)}/${encodeURIComponent(tracklistId)}`;", tracklistView, StringComparison.Ordinal);

        Assert.Contains("if (source === 'tidal')", home, StringComparison.Ordinal);
        Assert.Contains("return `/Tracklist?id=${encodeURIComponent(collectionId)}&type=playlist&source=tidal`;", home, StringComparison.Ordinal);
        Assert.DoesNotContain("const genericPlaylistSources = new Set(['soundcloud', 'tidal', 'qobuz', 'bandcamp', 'pandora']);", home, StringComparison.Ordinal);

        Assert.Contains("function parseTidalTracklistRoute(url)", search, StringComparison.Ordinal);
        Assert.Contains("navigateToTracklist(tidalRoute.id, tidalRoute.type || 'playlist', 'tidal');", search, StringComparison.Ordinal);
        Assert.Contains("navigateToTracklist(id, normalizedType || 'track', 'tidal');", search, StringComparison.Ordinal);
        Assert.DoesNotContain("navigateToTracklist(id, normalizedType || 'track', 'tidal', { externalUrl: normalizedUrl });", search, StringComparison.Ordinal);
    }

    [Fact]
    public void TidalTracklistDeezerMatching_UsesNativeIdAndIsrc()
    {
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "ExternalPlaylistTracklistApiController.cs");
        var view = ReadSource("DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml");

        Assert.Contains("GetAnyString(trackNode, \"isrc\", \"ISRC\")", controller, StringComparison.Ordinal);
        Assert.Contains("private static string GetAnyString(JsonElement element, params string[] propertyNames)", controller, StringComparison.Ordinal);

        Assert.Contains("const platformIds = resolveTrackPlatformIds(track);", view, StringComparison.Ordinal);
        Assert.Contains("link = `tidal:track:${platformIds.tidalId}`;", view, StringComparison.Ordinal);
        Assert.Contains("link = `tidal:track:${tidalId}`;", view, StringComparison.Ordinal);
        Assert.Contains("link = `isrc:${isrc}`;", view, StringComparison.Ordinal);
        Assert.Contains("tidalId: platformIds.tidalId || ''", view, StringComparison.Ordinal);
        Assert.Contains("tidalId,", view, StringComparison.Ordinal);
        Assert.Contains("qs.set('tidalId', current.tidalId);", view, StringComparison.Ordinal);
        Assert.Contains("qs.set('isrc', current.isrc);", view, StringComparison.Ordinal);
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
