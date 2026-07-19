using System;
using System.IO;
using DeezSpoTag.Services.Download.Shared.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AudioDurationGuardTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"duration-guard-{Guid.NewGuid():N}");

    public AudioDurationGuardTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ValidateAgainstPreview_RejectsThirtySecondOutputForFullLengthTrack()
    {
        var path = Path.Combine(_tempDir, "preview.wav");
        WriteSilentWav(path, TimeSpan.FromSeconds(30));

        var result = AudioDurationGuard.ValidateAgainstPreview(path, expectedDurationSeconds: 145);

        Assert.False(result.Success);
        Assert.Contains("Refusing likely preview", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateAgainstPreview_AllowsOutputNearExpectedDuration()
    {
        var path = Path.Combine(_tempDir, "full.wav");
        WriteSilentWav(path, TimeSpan.FromSeconds(140));

        var result = AudioDurationGuard.ValidateAgainstPreview(path, expectedDurationSeconds: 145);

        Assert.True(result.Success);
    }

    [Fact]
    public void ValidateAgainstPreview_RejectsOneMinuteSampleForLongerTrack()
    {
        var path = Path.Combine(_tempDir, "one-minute-sample.wav");
        WriteSilentWav(path, TimeSpan.FromSeconds(60));

        var result = AudioDurationGuard.ValidateAgainstPreview(path, expectedDurationSeconds: 100);

        Assert.False(result.Success);
        Assert.Contains("Refusing likely preview", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateAgainstPreview_AllowsLegitimateOneMinuteTrack()
    {
        var path = Path.Combine(_tempDir, "one-minute-track.wav");
        WriteSilentWav(path, TimeSpan.FromSeconds(60));

        var result = AudioDurationGuard.ValidateAgainstPreview(path, expectedDurationSeconds: 61);

        Assert.True(result.Success);
    }

    [Fact]
    public void ValidateAgainstPreview_RejectsMissingFileEvenWithoutExpectedDuration()
    {
        var result = AudioDurationGuard.ValidateAgainstPreview(
            Path.Combine(_tempDir, "missing.wav"),
            expectedDurationSeconds: 0);

        Assert.False(result.Success);
    }

    [Fact]
    public void ValidateAgainstPreview_RejectsThirtySecondOutputWithoutExpectedDuration()
    {
        var path = Path.Combine(_tempDir, "unknown-duration-preview.wav");
        WriteSilentWav(path, TimeSpan.FromSeconds(30));

        var result = AudioDurationGuard.ValidateAgainstPreview(path, expectedDurationSeconds: 0);

        Assert.False(result.Success);
        Assert.Contains("Refusing likely preview", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateAgainstPreview_AllowsOneMinuteOutputWithoutExpectedDuration()
    {
        var path = Path.Combine(_tempDir, "unknown-duration-one-minute.wav");
        WriteSilentWav(path, TimeSpan.FromSeconds(60));

        var result = AudioDurationGuard.ValidateAgainstPreview(path, expectedDurationSeconds: 0);

        Assert.True(result.Success);
    }

    [Fact]
    public void ValidateAgainstPreview_AllowsLongerLegitimateVersion()
    {
        var path = Path.Combine(_tempDir, "extended.wav");
        WriteSilentWav(path, TimeSpan.FromSeconds(180));

        var result = AudioDurationGuard.ValidateAgainstPreview(path, expectedDurationSeconds: 145);

        Assert.True(result.Success);
        Assert.True(result.Conclusive);
    }

    [Fact]
    public void ValidateAgainstPreview_UnreadableNonEmptyFileIsInconclusive()
    {
        var path = Path.Combine(_tempDir, "audio.flac");
        File.WriteAllBytes(path, [1, 2, 3, 4]);

        var result = AudioDurationGuard.ValidateAgainstPreview(path, expectedDurationSeconds: 145);

        Assert.True(result.Success);
        Assert.False(result.Conclusive);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static void WriteSilentWav(string path, TimeSpan duration)
    {
        const int sampleRate = 8000;
        const short channels = 1;
        const short bitsPerSample = 8;
        var bytesPerSample = bitsPerSample / 8;
        var sampleCount = (int)Math.Round(duration.TotalSeconds * sampleRate);
        var dataSize = sampleCount * channels * bytesPerSample;
        var byteRate = sampleRate * channels * bytesPerSample;
        var blockAlign = (short)(channels * bytesPerSample);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);

        var silence = new byte[Math.Min(dataSize, 4096)];
        Array.Fill<byte>(silence, 128);
        var remaining = dataSize;
        while (remaining > 0)
        {
            var toWrite = Math.Min(remaining, silence.Length);
            writer.Write(silence, 0, toWrite);
            remaining -= toWrite;
        }
    }
}
