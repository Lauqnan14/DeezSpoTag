using DeezSpoTag.Services.Download.Shared;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SidecarFetchActivityTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoryArtworkRemainsAvailableAfterTheStagedCoverMoves(bool embedded)
    {
        var root = System.IO.Path.Join(System.IO.Path.GetTempPath(), "sidecar-cover-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var source = System.IO.Path.Join(root, "staging", "front.png");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(source)!);
            using (var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(8, 8))
                SixLabors.ImageSharp.ImageExtensions.SaveAsPng(image, source);
            if (embedded)
            {
                var audio = System.IO.Path.Join(root, "staging", "song.wav");
                DeliveredAudioQualityGuardTest.WriteWave(audio,16);
                using (var file = TagLib.File.Create(audio))
                {
                    file.Tag.Pictures = [new TagLib.Picture(System.IO.File.ReadAllBytes(source)) { Type=TagLib.PictureType.FrontCover }];
                    file.Save();
                }
                System.IO.File.Delete(source); source = audio;
            }
            var service = (DeezSpoTag.Web.Services.AutoTagService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DeezSpoTag.Web.Services.AutoTagService));
            typeof(DeezSpoTag.Web.Services.AutoTagService).GetField("_runtimeConfigDir", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(service, System.IO.Path.Join(root, "autotag", "runtime"));
            var method = typeof(DeezSpoTag.Web.Services.AutoTagService).GetMethod("BuildLyricsCoverUrl", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            var url = Assert.IsType<string>(method.Invoke(service, [source]));
            System.IO.File.Delete(source);
            var cached = System.Uri.UnescapeDataString(url.Split("path=")[1].Split('&')[0]);
            Assert.True(System.IO.File.Exists(cached), "History artwork must not point at a removed staging file.");
            Assert.StartsWith(System.IO.Path.Join(root, "library-thumbs"), cached);
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }

    public static TheoryData<SidecarFetchWork, string?> Cases => new()
    {
        { new(false, false, false, false), null },
        { new(true, false, false, false), "Fetching album artwork" },
        { new(false, true, false, false), "Fetching animated artwork" },
        { new(false, false, true, false), "Fetching artist artwork" },
        { new(false, false, false, true), "Fetching lyrics" },
        { new(true, true, false, false), "Fetching album artwork and animated artwork" },
        { new(true, false, true, true), "Fetching album artwork, artist artwork and lyrics" },
        { new(true, true, true, true), "Fetching album artwork, animated artwork, artist artwork and lyrics" }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Describe_UsesDownloadOrderingAndWording(SidecarFetchWork work, string? expected)
        => Assert.Equal(expected, SidecarFetchActivity.Describe(work));

    [Theory]
    [InlineData(LyricsSidecarWorkKind.None, null)]
    [InlineData(LyricsSidecarWorkKind.FetchMissing, "Fetching lyrics")]
    [InlineData(LyricsSidecarWorkKind.UpgradeLrcToWord, "Updating lyrics")]
    [InlineData(LyricsSidecarWorkKind.RewriteTtmlToWord, "Updating TTML")]
    [InlineData(LyricsSidecarWorkKind.RemoveLineSyncedTtml, "Removing line-synced TTML")]
    [InlineData(LyricsSidecarWorkKind.UpgradeLrcToWord | LyricsSidecarWorkKind.RewriteTtmlToWord, "Updating lyrics and TTML")]
    public void DescribeLyricsWork_UsesEnhancementWording(LyricsSidecarWorkKind work, string? expected)
        => Assert.Equal(expected, SidecarFetchActivity.DescribeLyricsWork(work));

    [Fact]
    public void DescribeEnhancement_CombinesArtworkWithLyricsAction()
    {
        Assert.Equal(
            "Fetching album artwork, animated artwork and lyrics",
            SidecarFetchActivity.DescribeEnhancement(true, true, LyricsSidecarWorkKind.FetchMissing));
        Assert.Equal(
            "Fetching album artwork, animated artwork and updating lyrics",
            SidecarFetchActivity.DescribeEnhancement(true, true, LyricsSidecarWorkKind.UpgradeLrcToWord));
        Assert.Equal(
            "Updating lyrics",
            SidecarFetchActivity.DescribeEnhancement(false, false, LyricsSidecarWorkKind.UpgradeLrcToWord));
        Assert.Equal(
            "Removing line-synced TTML",
            SidecarFetchActivity.DescribeEnhancement(false, false, LyricsSidecarWorkKind.RemoveLineSyncedTtml));
        Assert.Equal(
            "Fetching album artwork",
            SidecarFetchActivity.DescribeEnhancement(true, false, LyricsSidecarWorkKind.None));
    }
}
