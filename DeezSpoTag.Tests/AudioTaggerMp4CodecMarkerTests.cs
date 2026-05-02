using System;
using System.IO;
using System.Reflection;
using System.Text;
using DeezSpoTag.Services.Download.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AudioTaggerMp4CodecMarkerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"deezspotag-audio-tagger-{Guid.NewGuid():N}");

    public AudioTaggerMp4CodecMarkerTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Theory]
    [InlineData("ec-3")]
    [InlineData("ac-3")]
    [InlineData("dec3")]
    [InlineData("dac3")]
    public void HasMp4AudioCodecMarker_DetectsAtmosCompatibleMp4Markers(string marker)
    {
        var path = Path.Combine(_tempDir, $"{marker}.m4a");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes($"ftypisom....moov....stsd....{marker}....mdat"));

        Assert.True(InvokeHasMp4AudioCodecMarker(path, marker));
    }

    [Fact]
    public void HasMp4AudioCodecMarker_ReturnsFalseForAacOnlyContainer()
    {
        var path = Path.Combine(_tempDir, "aac.m4a");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("ftypM4A ....moov....stsd....mp4a....mdat"));

        Assert.False(InvokeHasMp4AudioCodecMarker(path, "ec-3"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception)
        {
            // Best effort cleanup for temp files created by the test.
        }
    }

    private static bool InvokeHasMp4AudioCodecMarker(string path, string marker)
    {
        var method = typeof(AudioTagger).GetMethod(
            "HasMp4AudioCodecMarker",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var result = method!.Invoke(null, [path, new[] { Encoding.ASCII.GetBytes(marker) }]);
        Assert.IsType<bool>(result);
        return (bool)result;
    }
}
