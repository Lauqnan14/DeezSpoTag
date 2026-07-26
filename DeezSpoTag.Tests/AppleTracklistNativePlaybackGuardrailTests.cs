using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Web.Controllers;
using DeezSpoTag.Web.Controllers.Api;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AppleTracklistNativePlaybackGuardrailTests
{
    [Theory]
    [InlineData("apple", "apple")]
    [InlineData("applemusic", "apple")]
    [InlineData("apple-music", "apple")]
    [InlineData("apple_music", "apple")]
    [InlineData("itunes", "apple")]
    public void Tracklist_controller_canonicalizes_Apple_source_aliases(string source, string expected)
    {
        var method = typeof(TracklistController).GetMethod(
            "NormalizeTracklistSource",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal(expected, method!.Invoke(null, new object?[] { source }));
    }

    [Fact]
    public void Apple_catalog_track_entry_preserves_native_identity_metadata_and_preview()
    {
        using var trackDocument = JsonDocument.Parse("""{"id":"123456789"}""");
        using var attributesDocument = JsonDocument.Parse(
            """
            {
              "url":"https://music.apple.com/us/song/example/123456789",
              "isrc":"USABC2400001",
              "durationInMillis":211000,
              "previews":[{"url":"https://audio-ssl.itunes.apple.com/example.m4a"}],
              "artwork":{"url":"https://is1-ssl.mzstatic.com/image/thumb/example/{w}x{h}bb.jpg"}
            }
            """);
        var method = typeof(AppleTracklistApiController).GetMethod(
            "BuildTrackEntry",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var entry = method!.Invoke(
            null,
            new object?[]
            {
                trackDocument.RootElement,
                attributesDocument.RootElement,
                "Example",
                "Example Artist",
                "Example Album",
                1
            });
        var json = JsonSerializer.Serialize(entry);
        using var result = JsonDocument.Parse(json);
        var root = result.RootElement;

        Assert.Equal("123456789", root.GetProperty("id").GetString());
        Assert.Equal("apple", root.GetProperty("source").GetString());
        Assert.Equal("USABC2400001", root.GetProperty("isrc").GetString());
        Assert.Equal("https://music.apple.com/us/song/example/123456789", root.GetProperty("sourceUrl").GetString());
        Assert.Equal("https://audio-ssl.itunes.apple.com/example.m4a", root.GetProperty("preview").GetString());
    }

    [Fact]
    public void Apple_rows_are_native_and_never_depend_on_Deezer_matching()
    {
        var view = ReadSource("DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml");
        var sourceSetStart = view.IndexOf("const deezerMatchedExternalSources = new Set([", StringComparison.Ordinal);
        var sourceSetEnd = view.IndexOf("]);", sourceSetStart, StringComparison.Ordinal);
        Assert.True(sourceSetStart >= 0 && sourceSetEnd > sourceSetStart);
        var matchedSources = view[sourceSetStart..sourceSetEnd];

        Assert.DoesNotContain("'apple'", matchedSources, StringComparison.Ordinal);
        Assert.Contains("if (!isDeezerMatchedExternalSource(normalizedSource))", view, StringComparison.Ordinal);
        Assert.DoesNotContain("const isAppleAudioTracklist", view, StringComparison.Ordinal);
        Assert.Contains("const isSourceUnmatchedDownloadableRow = playbackSource !== 'apple'", view, StringComparison.Ordinal);
        Assert.Contains("if (!row || !isDeezerMatchedExternalSource(getRowTrackSource(row)))", view, StringComparison.Ordinal);
        Assert.Contains("if (!isDeezerMatchedExternalSource(source))", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Apple_rows_use_only_native_preview_and_retain_Apple_id()
    {
        var view = ReadSource("DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml");

        Assert.Contains(
            "? String(track.preview || track.previewUrl || track.preview_url || '').trim()",
            view,
            StringComparison.Ordinal);
        Assert.Contains("? 'Preview unavailable'", view, StringComparison.Ordinal);
        Assert.Contains("data-apple-id=\"${escapeHtml(platformIds.appleId || '')}\"", view, StringComparison.Ordinal);
        Assert.Contains("return String(checkbox?.dataset.appleId || '').trim();", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Apple_playlist_animation_uses_the_shared_cached_visual_pipeline_progressively()
    {
        var controller = ReadSource(
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "AppleTracklistApiController.cs");
        var view = ReadSource("DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml");

        Assert.Contains("[HttpGet(\"animated-artwork\")]", controller, StringComparison.Ordinal);
        Assert.Contains(
            "_playlistVisualService.ResolveApplePlaylistAnimatedVisualAsync(",
            controller,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SaveAnimatedArtworkAsync(", controller, StringComparison.Ordinal);

        var renderIndex = view.IndexOf("renderTracklist(payload.tracklist);", StringComparison.Ordinal);
        var animationIndex = view.IndexOf("void loadApplePlaylistAnimatedArtwork();", StringComparison.Ordinal);
        Assert.True(renderIndex >= 0 && animationIndex > renderIndex);
        Assert.Contains(
            "/api/apple/tracklist/animated-artwork?id=${encodeURIComponent(tracklistId)}",
            view,
            StringComparison.Ordinal);
        Assert.Contains("coverImage.src = animatedUrl;", view, StringComparison.Ordinal);
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
