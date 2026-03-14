using System;
using DeezSpoTag.Services.Download.Apple;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AppleHlsManifestParserTests
{
    [Fact]
    public void ParseMedia_ByterangeWithoutOffset_ResolvesPerResourceUri()
    {
        var manifest = """
            #EXTM3U
            #EXT-X-VERSION:7
            #EXT-X-MAP:URI="init.mp4",BYTERANGE="100@0"
            #EXTINF:5.0,
            #EXT-X-BYTERANGE:200
            seg.mp4
            #EXTINF:5.0,
            #EXT-X-BYTERANGE:300
            seg.mp4
            """;

        var parsed = AppleHlsManifestParser.ParseMedia(manifest, new Uri("https://cdn.example.com/path/master.m3u8"));

        Assert.Equal("https://cdn.example.com/path/init.mp4", parsed.InitSegment);
        Assert.NotNull(parsed.InitRange);
        Assert.Equal(0, parsed.InitRange!.Offset);
        Assert.Equal(100, parsed.InitRange.Length);

        Assert.Equal(2, parsed.Segments.Count);
        Assert.NotNull(parsed.Segments[0].Range);
        var firstRange = parsed.Segments[0].Range!;
        Assert.Equal(0, firstRange.Offset);
        Assert.Equal(200, firstRange.Length);

        Assert.NotNull(parsed.Segments[1].Range);
        var secondRange = parsed.Segments[1].Range!;
        Assert.Equal(200, secondRange.Offset);
        Assert.Equal(300, secondRange.Length);
    }

    [Fact]
    public void ParseMedia_ByterangeWithoutOffset_DoesNotCarryOffsetAcrossDifferentUris()
    {
        var manifest = """
            #EXTM3U
            #EXT-X-VERSION:7
            #EXT-X-BYTERANGE:50@10
            a.bin
            #EXT-X-BYTERANGE:20
            a.bin
            #EXT-X-BYTERANGE:30
            b.bin
            #EXT-X-BYTERANGE:40
            b.bin
            """;

        var parsed = AppleHlsManifestParser.ParseMedia(manifest, new Uri("https://cdn.example.com/path/master.m3u8"));

        Assert.Equal(4, parsed.Segments.Count);

        Assert.NotNull(parsed.Segments[0].Range);
        var aFirstRange = parsed.Segments[0].Range!;
        Assert.Equal(10, aFirstRange.Offset);
        Assert.Equal(50, aFirstRange.Length);

        Assert.NotNull(parsed.Segments[1].Range);
        var aSecondRange = parsed.Segments[1].Range!;
        Assert.Equal(60, aSecondRange.Offset);
        Assert.Equal(20, aSecondRange.Length);

        Assert.NotNull(parsed.Segments[2].Range);
        var bFirstRange = parsed.Segments[2].Range!;
        Assert.Equal(0, bFirstRange.Offset);
        Assert.Equal(30, bFirstRange.Length);

        Assert.NotNull(parsed.Segments[3].Range);
        var bSecondRange = parsed.Segments[3].Range!;
        Assert.Equal(30, bSecondRange.Offset);
        Assert.Equal(40, bSecondRange.Length);
    }

    [Fact]
    public void ParseMedia_ByterangeWithoutOffset_ContinuesAcrossSignedQueryVariants()
    {
        var manifest = """
            #EXTM3U
            #EXT-X-VERSION:7
            #EXT-X-BYTERANGE:100
            chunk.mp4?token=aaa
            #EXT-X-BYTERANGE:120
            chunk.mp4?token=bbb
            #EXT-X-BYTERANGE:140
            chunk.mp4?token=ccc
            """;

        var parsed = AppleHlsManifestParser.ParseMedia(manifest, new Uri("https://cdn.example.com/path/master.m3u8"));
        Assert.Equal(3, parsed.Segments.Count);

        var firstRange = parsed.Segments[0].Range!;
        Assert.Equal(0, firstRange.Offset);
        Assert.Equal(100, firstRange.Length);

        var secondRange = parsed.Segments[1].Range!;
        Assert.Equal(100, secondRange.Offset);
        Assert.Equal(120, secondRange.Length);

        var thirdRange = parsed.Segments[2].Range!;
        Assert.Equal(220, thirdRange.Offset);
        Assert.Equal(140, thirdRange.Length);
    }
}
