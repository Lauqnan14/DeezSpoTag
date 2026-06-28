using System;
using System.IO;
using System.Reflection;
using DeezSpoTag.Services.Download;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ActualDownloadQualityLabelTests
{
    [Theory]
    [InlineData(24, 176400, "Max Hi-Res (24-bit/176.4kHz)")]
    [InlineData(24, 96000, "Hi-Res (24-bit/96kHz)")]
    [InlineData(24, 88200, "Hi-Res (24-bit/88.2kHz)")]
    [InlineData(16, 44100, "CD Lossless (16-bit/44.1kHz)")]
    [InlineData(16, 48000, "FLAC (16-bit/48kHz)")]
    public void LosslessLabelKeepsBucketAndUsesExactAudioProperties(
        int bitsPerSample,
        int sampleRate,
        string expected)
    {
        var method = GetFormatterMethod("TryBuildLosslessLabel");

        var actual = method.Invoke(null, [bitsPerSample, sampleRate, ".flac", "FLAC"]) as string;

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(320, ".mp3", "MPEG Audio", "MP3 320 kbps")]
    [InlineData(256, ".m4a", "AAC", "AAC-LC 256 kbps")]
    public void LossyLabelUsesExactBitrate(
        int bitrate,
        string extension,
        string codec,
        string expected)
    {
        var method = GetFormatterMethod("TryBuildLossyLabel");

        var actual = method.Invoke(null, [bitrate, extension, codec]) as string;

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FlacStreamInfoProvidesExactPropertiesWhenTagLibraryDoesNot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.flac");
        try
        {
            const int sampleRate = 44100;
            const int bitsPerSample = 16;
            const ulong totalSamples = 1000;
            var packed = ((ulong)sampleRate << 44)
                         | ((ulong)(2 - 1) << 41)
                         | ((ulong)(bitsPerSample - 1) << 36)
                         | totalSamples;
            var bytes = new byte[42];
            "fLaC"u8.CopyTo(bytes);
            bytes[4] = 0x80;
            bytes[7] = 34;
            for (var index = 0; index < 8; index++)
            {
                bytes[18 + index] = (byte)(packed >> ((7 - index) * 8));
            }

            File.WriteAllBytes(path, bytes);
            var method = GetFormatterMethod("TryReadFlacProperties");
            object?[] arguments = [path, 0, 0];

            var success = (bool)method.Invoke(null, arguments)!;

            Assert.True(success);
            Assert.Equal(bitsPerSample, arguments[1]);
            Assert.Equal(sampleRate, arguments[2]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static MethodInfo GetFormatterMethod(string name)
    {
        var formatterType = typeof(QualityCatalog).Assembly.GetType(
            "DeezSpoTag.Services.Download.Shared.ActualDownloadQualityLabel",
            throwOnError: true)!;
        return formatterType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
               ?? throw new InvalidOperationException($"Formatter method {name} was not found.");
    }
}
