using System;
using System.IO;
using System.Linq;
using DeezSpoTag.Services.Download.SoundCloud;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Guards the three defects found by driving the real tracklist page with a SoundCloud
///     <c>/discover/sets/</c> URL.
/// </summary>
/// <remarks>
///     <para>
///         Each test here failed against the working implementation. They are kept as source-shape guardrails
///         because all three defects were shape mismatches between the API response, the shared view, and the
///         Search router - none of which the behavioural tests exercised, since the parser was correct long
///         before the album column could display anything.
///     </para>
/// </remarks>
public sealed class SoundCloudTracklistPresentationTest
{
    [Fact]
    public void TracklistApi_EmitsAlbumTitleBecauseTheSharedViewReadsAlbumTitle()
    {
        var source = ReadSource(
            "DeezSpoTag.Web", "Controllers", "Api", "ExternalPlaylistTracklistApiController.cs");

        // The shared renderer reads album.title (Index.cshtml) and falls back to the literal "Unknown" when it
        // is absent. The SoundCloud response used to carry album.name only, so every row rendered "Unknown"
        // even though the value was visibly present in the payload.
        Assert.Contains("title = albumTitle,", source, System.StringComparison.Ordinal);
        Assert.Contains("name = albumTitle,", source, System.StringComparison.Ordinal);
        Assert.Contains("private static string ResolveSoundCloudAlbumTitle(SoundCloudTrack track)", source, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TracklistApi_DoesNotStampTheSetTitleOntoEveryAlbum()
    {
        var source = ReadSource(
            "DeezSpoTag.Web", "Controllers", "Api", "ExternalPlaylistTracklistApiController.cs");

        // A /discover page's set title is a genre ("Trap"), not a release. Falling back to it labelled every
        // row with the genre, which is a false album that would later be written to the file as TALB.
        Assert.DoesNotContain("name = track.Album ?? set.Title", source, System.StringComparison.Ordinal);
        Assert.DoesNotContain("title = track.Album ?? set.Title", source, System.StringComparison.Ordinal);
    }

    [Fact]
    public void AlbumTitle_PrefersTheDistributorDeclarationAndOtherwiseReturnsNothing()
    {
        var method = typeof(DeezSpoTag.Web.Controllers.Api.ExternalPlaylistTracklistApiController)
            .GetMethod("ResolveSoundCloudAlbumTitle",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var withPublisherTitle = new DeezSpoTag.Services.Download.SoundCloud.SoundCloudTrack
        {
            Id = 1,
            PublisherAlbumTitle = "  Answer Me  "
        };
        var withNothing = new DeezSpoTag.Services.Download.SoundCloud.SoundCloudTrack { Id = 2 };

        Assert.Equal("Answer Me", method!.Invoke(null, [withPublisherTitle]));
        Assert.Equal(string.Empty, method.Invoke(null, [withNothing]));
    }

    [Fact]
    public void StubExpansion_CarriesTheAlbumTitleAndTheOtherExtendedMetadata()
    {
        var source = ReadSource(
            "DeezSpoTag.Services", "Download", "SoundCloud", "SoundCloudClient.cs");

        // A set's tracks arrive as id-only stubs, so every extended field is recovered by the hydrate call.
        // The merge copied only five of them; the album title was parsed and then thrown away, so no row
        // could ever show a real album.
        Assert.Contains("PublisherAlbumTitle = Has(track.PublisherAlbumTitle)",
            source, System.StringComparison.Ordinal);
        Assert.Contains("PublisherArtist = Has(track.PublisherArtist)",
            source, System.StringComparison.Ordinal);
        Assert.Contains("UploaderUsername = Has(track.UploaderUsername)",
            source, System.StringComparison.Ordinal);
        Assert.Contains("Bpm = track.Bpm ?? hydrated.Bpm", source, System.StringComparison.Ordinal);
        Assert.Contains("KeySignature = Has(track.KeySignature)",
            source, System.StringComparison.Ordinal);
    }

    [Fact]
    public void SearchRouter_OpensTheSharedTracklistForAPastedSoundCloudPlaylist()
    {
        var search = ReadSource("DeezSpoTag.Web", "Views", "Search", "Index.cshtml");

        // classifyExternalSource, shouldUseLinkMap and isPlaylistLikeExternalLink all already handled
        // SoundCloud, but deezerMappedPlaylistSources did not, so routeSearchUrlDirectly failed its gate and
        // threw instead of navigating. A pasted SoundCloud playlist URL could not reach the tracklist at all.
        Assert.Contains("deezerMappedPlaylistSources", search, System.StringComparison.Ordinal);
        var line = search.Split('\n')
            .FirstOrDefault(l => l.Contains("const deezerMappedPlaylistSources", System.StringComparison.Ordinal));
        Assert.NotNull(line);
        Assert.Contains("'soundcloud'", line, System.StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistArtwork_FallsBackToTheCalculatedUrl()
    {
        // A /discover page sets artwork_url to null and keeps the only usable image in
        // calculated_artwork_url. Reading the plain field alone left every algorithmic playlist cover-less.
        var html = SoundCloudFixtures.BuildDiscoverPageHtml(
            stubIds: ["1"],
            title: "Trap",
            description: "Trending tracks in Trap",
            shortDescription: "Trending",
            calculatedArtworkUrl: "https://i1.sndcdn.com/artworks-b6wx7MgEyX7M7Ywc-IhPusw-large.jpg");

        var set = SoundCloudHydrationParser.ParseSet(html);

        Assert.Equal("https://i1.sndcdn.com/artworks-b6wx7MgEyX7M7Ywc-IhPusw-large.jpg", set.ArtworkUrl);
    }

    [Fact]
    public void PlaylistArtwork_PrefersTheExplicitUrlWhenThePageHasOne()
    {
        // The calculated url is a fallback, not an override: a user-owned set carries only artwork_url and a
        // test that only checked the calculated path would not notice the precedence being inverted.
        var html = SoundCloudFixtures.BuildDiscoverPageHtml(
            stubIds: ["1"],
            title: "Trap",
            description: "Trending",
            shortDescription: "Trending",
            artworkUrl: "https://i1.sndcdn.com/artworks-explicit-large.jpg",
            calculatedArtworkUrl: "https://i1.sndcdn.com/artworks-calculated-large.jpg");

        var set = SoundCloudHydrationParser.ParseSet(html);

        Assert.Equal("https://i1.sndcdn.com/artworks-explicit-large.jpg", set.ArtworkUrl);
    }

    [Fact]
    public void StubExpansion_KeepsTheHydratedArtworkInsteadOfTheStubsEmptyString()
    {
        // The regression behind every row sharing one cover. A set's tracks arrive as id-only stubs, and the
        // readers return "" for an absent field - so "track.ArtworkUrl ?? hydrated.ArtworkUrl" kept the stub's
        // blank and discarded the artwork the hydrate call had just recovered. Coalescing on null instead of
        // on "has a value" is what let one playlist cover be stamped across all 49 rows.
        var source = ReadSource(
            "DeezSpoTag.Services", "Download", "SoundCloud", "SoundCloudClient.cs");

        Assert.Contains("private static bool Has(string? value)", source, System.StringComparison.Ordinal);
        Assert.Contains(
            "ArtworkUrl = Has(track.ArtworkUrl) ? track.ArtworkUrl : hydrated.ArtworkUrl",
            source, System.StringComparison.Ordinal);
        Assert.DoesNotContain("ArtworkUrl = track.ArtworkUrl ?? hydrated.ArtworkUrl",
            source, System.StringComparison.Ordinal);
    }

    [Fact]
    public void StubExpansion_CoalescesEveryTextFieldOnValueRatherThanOnNull()
    {
        // Same defect, every field: the readers return "" for an absent key, so every "?? hydrated.X" in the
        // merge silently kept the stub's blank rather than the recovered value.
        var source = ReadSource(
            "DeezSpoTag.Services", "Download", "SoundCloud", "SoundCloudClient.cs");

        foreach (var field in new[]
                 {
                     "Isrc", "Genre", "Label", "PublisherArtist", "PublisherAlbumTitle",
                     "MetadataArtist", "UploaderUsername", "KeySignature"
                 })
        {
            Assert.DoesNotContain($"{field} = track.{field} ?? hydrated.{field}",
                source, System.StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryRowCarriesACoverForTheRowThumbnail()
    {
        // The row renderer draws a thumbnail from album.cover_medium, not from cover_big. Emitting only the
        // header's four spellings left every SoundCloud row with an empty gutter, which is what made this
        // list look unlike every other platform's in the app.
        var source = ReadSource(
            "DeezSpoTag.Web", "Controllers", "Api", "ExternalPlaylistTracklistApiController.cs");

        Assert.Contains("cover_medium = rowCover,", source, System.StringComparison.Ordinal);
    }

    [Fact]
    public void RowArtworkFallsBackToThePlaylistCoverSoDiscoverRowsAreNotBlank()
    {
        // A /discover stub carries no artwork of its own. Without falling back to the set's calculated cover
        // every row would still be empty even after cover_medium was emitted.
        var source = ReadSource(
            "DeezSpoTag.Web", "Controllers", "Api", "ExternalPlaylistTracklistApiController.cs");

        Assert.Contains("var rowCover = !string.IsNullOrWhiteSpace(track.ArtworkUrl)", source,
            System.StringComparison.Ordinal);
        Assert.Contains(": set.ArtworkUrl ?? string.Empty;", source, System.StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistDescription_IsCarriedThroughToTheTracklistResponse()
    {
        // The response hardcoded an empty description, so an algorithmic set - whose only description is the
        // curator's blurb, "Trending tracks in Trap" - rendered a blank header.
        var html = SoundCloudFixtures.BuildDiscoverPageHtml(
            stubIds: ["1"],
            title: "Trap",
            description: "Trending tracks in Trap",
            shortDescription: "Trending");

        var set = SoundCloudHydrationParser.ParseSet(html);

        Assert.Equal("Trending tracks in Trap", set.Description);
    }

    [Fact]
    public void PlaylistDescription_FallsBackToTheAbbreviatedForm()
    {
        var html = SoundCloudFixtures.BuildDiscoverPageHtml(
            stubIds: ["1"],
            title: "Trap",
            description: null,
            shortDescription: "Trending");

        var set = SoundCloudHydrationParser.ParseSet(html);

        Assert.Equal("Trending", set.Description);
    }

    [Fact]
    public void TheTracklistApi_SendsTheDescriptionRatherThanAnEmptyString()
    {
        var source = ReadSource(
            "DeezSpoTag.Web", "Controllers", "Api", "ExternalPlaylistTracklistApiController.cs");

        // Scoped to the SoundCloud set builder: other sources in this controller legitimately send an empty
        // description, so the assertion is that SoundCloud's is the one reading the set's own blurb.
        Assert.Contains("description = set.Description ?? string.Empty", source, System.StringComparison.Ordinal);
    }

    [Fact]
    public void SoundCloudIsRecognisedAsAPlaylistShapeByTheSearchRouter()
    {
        var search = ReadSource("DeezSpoTag.Web", "Views", "Search", "Index.cshtml");

        Assert.Contains("if (source === 'soundcloud')", search, System.StringComparison.Ordinal);
        Assert.Contains("return path.includes('/sets/');", search, System.StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] parts)
    {
        var root = FindRepoRoot();
        var path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "DeezSpoTag.Web")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}