using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AmazonMusicMetadataApiGuardrailTests
{
    [Fact]
    public void AmazonMetadata_UsesRegionalSkillEndpointsWithLegacyFallback()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AmazonMusicMetadataService.cs");

        Assert.Contains("RegionConfigs", source, StringComparison.Ordinal);
        Assert.Contains("https://na.web.skill.music.a2z.com/api", source, StringComparison.Ordinal);
        Assert.Contains("https://eu.web.skill.music.a2z.com/api", source, StringComparison.Ordinal);
        Assert.Contains("https://fe.web.skill.music.a2z.com/api", source, StringComparison.Ordinal);
        Assert.Contains("https://na.mesk.skill.music.a2z.com/api", source, StringComparison.Ordinal);
        Assert.Contains("https://eu.mesk.skill.music.a2z.com/api", source, StringComparison.Ordinal);
        Assert.Contains("https://fe.mesk.skill.music.a2z.com/api", source, StringComparison.Ordinal);
        Assert.Contains("ResolveRegionConfig(host)", source, StringComparison.Ordinal);
        Assert.Contains("session.SkillApiBaseUrls", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AmazonMetadata_UsesTypedSearchBeforeShowSearchFallback()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AmazonMusicMetadataService.cs");

        Assert.Contains("SearchTracksPath = \"/searchCatalogTracks\"", source, StringComparison.Ordinal);
        Assert.Contains("SearchAlbumsPath = \"/searchCatalogAlbums\"", source, StringComparison.Ordinal);
        Assert.Contains("SearchArtistsPath = \"/searchCatalogArtists\"", source, StringComparison.Ordinal);
        Assert.Contains("SearchPlaylistsPath = \"/searchCatalogPlaylists\"", source, StringComparison.Ordinal);
        Assert.Contains("SearchCommunityPlaylistsPath = \"/searchCommunityPlaylists\"", source, StringComparison.Ordinal);
        Assert.Contains("TrySearchTypedAsync", source, StringComparison.Ordinal);
        Assert.Contains("PostSkillJsonAsync(session, SearchAllPath", source, StringComparison.Ordinal);
        Assert.Contains("TrySearchCommunityPlaylistsAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("falling back to showSearch", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AmazonMetadata_UsesTypedCatalogFetchWithoutDeeplinkFallback()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AmazonMusicMetadataService.cs");

        Assert.Contains("TrackInfoPath = \"/cosmicTrack/displayCatalogTrack\"", source, StringComparison.Ordinal);
        Assert.Contains("AlbumInfoPath = \"/showCatalogAlbum\"", source, StringComparison.Ordinal);
        Assert.Contains("PlaylistInfoPath = \"/showCatalogPlaylist\"", source, StringComparison.Ordinal);
        Assert.Contains("CommunityPlaylistInfoPath = \"/showLibraryPlaylist\"", source, StringComparison.Ordinal);
        Assert.Contains("FetchCatalogDocumentAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FetchCatalogDocumentOrHomeAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FetchHomeDocumentAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("showHome", source, StringComparison.Ordinal);
        Assert.DoesNotContain("deeplink home fetch", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AmazonTrackResolution_UsesCatalogAlbumExpansionWhenTypedTrackSearchMisses()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AmazonMusicMetadataService.cs");

        Assert.Contains("SearchAsync(session, trackQuery, \"track\"", source, StringComparison.Ordinal);
        Assert.Contains("ResolveTrackFromCatalogAsync", source, StringComparison.Ordinal);
        Assert.Contains("ResolveTrackCatalogCandidatesAsync", source, StringComparison.Ordinal);
        Assert.Contains("BuildAlbumSearchQuery", source, StringComparison.Ordinal);
        Assert.Contains("ExpandAlbumTracksForSearchAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetTracklistAsync(session, album.Id, \"album\", album.Url", source, StringComparison.Ordinal);
        Assert.Contains("ResolveAtmosTrackAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AmazonMetadata_WiresRemainingReferenceMetadataEndpoints()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AmazonMusicMetadataService.cs");

        Assert.Contains("SearchCommunityPlaylistsPath = \"/searchCommunityPlaylists\"", source, StringComparison.Ordinal);
        Assert.Contains("CommunityPlaylistInfoPath = \"/showLibraryPlaylist\"", source, StringComparison.Ordinal);
        Assert.Contains("ArtistTopTracksPath = \"/showCatalogTracks\"", source, StringComparison.Ordinal);
        Assert.Contains("ExtractCommunityPlaylistSearchItems", source, StringComparison.Ordinal);
        Assert.Contains("FetchArtistTopTracksAsync", source, StringComparison.Ordinal);
        Assert.Contains("IsCommunityPlaylistUrl", source, StringComparison.Ordinal);
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
