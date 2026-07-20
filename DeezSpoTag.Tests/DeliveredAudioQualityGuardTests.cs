using System;
using System.IO;
using System.Reflection;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Amazon;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Tidal;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DeliveredAudioQualityGuardTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"delivered-quality-{Guid.NewGuid():N}");

    public DeliveredAudioQualityGuardTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void TwentyFourBitPlanStep_RejectsDeliveredSixteenBitAudio()
    {
        var path = Path.Combine(_tempDirectory, "sixteen-bit.wav");
        WriteWave(path, bitsPerSample: 16);

        var result = Validate(CreatePayload("27"), path);

        Assert.False(ReadBool(result, "Success"));
        Assert.Contains("16-bit", ReadString(result, "DeliveredQuality"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("24-bit", ReadString(result, "Message"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TwentyFourBitPlanStep_AcceptsDeliveredTwentyFourBitAudio()
    {
        var path = Path.Combine(_tempDirectory, "twenty-four-bit.wav");
        WriteWave(path, bitsPerSample: 24, sampleRate: 192000);

        var result = Validate(CreatePayload("27"), path);

        Assert.True(ReadBool(result, "Success"));
        Assert.Contains("24-bit", ReadString(result, "DeliveredQuality"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaxHiResPlanStep_RejectsDeliveredNinetySixKhzAudio()
    {
        var path = Path.Combine(_tempDirectory, "twenty-four-bit-96.wav");
        WriteWave(path, bitsPerSample: 24, sampleRate: 96000);

        var result = Validate(CreatePayload("27"), path);

        Assert.False(ReadBool(result, "Success"));
        Assert.Contains("96kHz", ReadString(result, "DeliveredQuality"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HiResPlanStep_AcceptsDeliveredNinetySixKhzAudio()
    {
        var path = Path.Combine(_tempDirectory, "twenty-four-bit-96-hires.wav");
        WriteWave(path, bitsPerSample: 24, sampleRate: 96000);

        var result = Validate(CreatePayload("7"), path);

        Assert.True(ReadBool(result, "Success"));
        Assert.Contains("96kHz", ReadString(result, "DeliveredQuality"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AmazonHdFlacPlanStep_AcceptsDeliveredSixteenBitLosslessAudio()
    {
        var path = Path.Combine(_tempDirectory, "amazon-hd-sixteen-bit.wav");
        WriteWave(path, bitsPerSample: 16);

        var result = Validate(CreateAmazonPayload("HD_FLAC"), path);

        Assert.True(ReadBool(result, "Success"));
        Assert.Contains("16-bit", ReadString(result, "DeliveredQuality"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AmazonHdFlacPlanStep_AcceptsDeliveredTwentyFourBitLosslessAudio()
    {
        var path = Path.Combine(_tempDirectory, "amazon-hd-twenty-four-bit.wav");
        WriteWave(path, bitsPerSample: 24);

        var result = Validate(CreateAmazonPayload("HD_FLAC"), path);

        Assert.True(ReadBool(result, "Success"));
        Assert.Contains("24-bit", ReadString(result, "DeliveredQuality"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AmazonHdFlacPlanStep_RejectsDeliveredLossyAudio()
    {
        Assert.False(IsDeliveredQualityAccepted(
            "amazon",
            "HD_FLAC",
            label: "MP3 320 kbps",
            bitsPerSample: 0,
            sampleRate: 44100,
            bitrateKbps: 320,
            isLossless: false));
    }

    [Fact]
    public void AmazonUltraHdFlacPlanStep_RequiresDeliveredTwentyFourBitLosslessAudio()
    {
        var sixteenBitPath = Path.Combine(_tempDirectory, "amazon-ultra-sixteen-bit.wav");
        WriteWave(sixteenBitPath, bitsPerSample: 16);

        var rejected = Validate(CreateAmazonPayload("ULTRA_HD_FLAC"), sixteenBitPath);

        Assert.False(ReadBool(rejected, "Success"));

        var twentyFourBitPath = Path.Combine(_tempDirectory, "amazon-ultra-twenty-four-bit.wav");
        WriteWave(twentyFourBitPath, bitsPerSample: 24);

        var accepted = Validate(CreateAmazonPayload("ULTRA_HD_FLAC"), twentyFourBitPath);

        Assert.True(ReadBool(accepted, "Success"));
    }

    [Fact]
    public void TidalMaxHiResPlanStep_RejectsDeliveredNinetySixKhzAudio()
    {
        var path = Path.Combine(_tempDirectory, "tidal-max-delivered-96.wav");
        WriteWave(path, bitsPerSample: 24, sampleRate: 96000);

        var result = Validate(CreateTidalPayload("HI_RES_LOSSLESS"), path);

        Assert.False(ReadBool(result, "Success"));
        Assert.Contains("96kHz", ReadString(result, "DeliveredQuality"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tidal Max Hi-Res", ReadString(result, "Message"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TidalHiResPlanStep_AcceptsDeliveredNinetySixKhzAudio()
    {
        var path = Path.Combine(_tempDirectory, "tidal-hires-delivered-96.wav");
        WriteWave(path, bitsPerSample: 24, sampleRate: 96000);

        var result = Validate(CreateTidalPayload("HI_RES"), path);

        Assert.True(ReadBool(result, "Success"));
    }

    [Fact]
    public void TidalHiResPlanStep_RejectsDeliveredMaxHiResAudio()
    {
        var path = Path.Combine(_tempDirectory, "tidal-hires-delivered-192.wav");
        WriteWave(path, bitsPerSample: 24, sampleRate: 192000);

        var result = Validate(CreateTidalPayload("HI_RES"), path);

        Assert.False(ReadBool(result, "Success"));
        Assert.Contains("192kHz", ReadString(result, "DeliveredQuality"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TidalCdLosslessPlanStep_RejectsDeliveredTwentyFourBitAudio()
    {
        var path = Path.Combine(_tempDirectory, "tidal-cd-delivered-24.wav");
        WriteWave(path, bitsPerSample: 24, sampleRate: 44100);

        var result = Validate(CreateTidalPayload("LOSSLESS"), path);

        Assert.False(ReadBool(result, "Success"));
        Assert.Contains("24-bit", ReadString(result, "DeliveredQuality"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TidalHighPlanStep_RejectsDeliveredLosslessAudio()
    {
        Assert.False(IsDeliveredQualityAccepted(
            "tidal",
            "HIGH",
            label: "CD Lossless (16-bit/44.1kHz)",
            bitsPerSample: 16,
            sampleRate: 44100,
            bitrateKbps: 800,
            isLossless: true));
    }

    [Theory]
    [InlineData("LOW", "AAC-LC 96 kbps", 96, true)]
    [InlineData("LOW", "AAC-LC 320 kbps", 320, false)]
    [InlineData("HIGH", "AAC-LC 320 kbps", 320, true)]
    [InlineData("HIGH", "AAC-LC 96 kbps", 96, false)]
    public void TidalLossyPlanSteps_AcceptOnlyTheirOwnLossyBuckets(
        string requestedQuality,
        string label,
        int bitrateKbps,
        bool expected)
    {
        var accepted = IsDeliveredQualityAccepted(
            "tidal",
            requestedQuality,
            label,
            bitsPerSample: 0,
            sampleRate: 44100,
            bitrateKbps,
            isLossless: false);

        Assert.Equal(expected, accepted);
    }

    [Fact]
    public void QualityRejectedAudio_IsNotDeletedByGuard()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Shared/DeliveredAudioQualityGuard.cs"));
        var source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("TryDeleteFile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CancelPrefetchAndWaitAsync", source, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDirectory, recursive: true);
    }

    private static QobuzQueueItem CreatePayload(string quality) => new()
    {
        Engine = "qobuz",
        Quality = quality,
        AutoIndex = 0,
        FallbackPlan =
        [
            new FallbackPlanStep("step-0", "qobuz", quality, [], "direct_url"),
            new FallbackPlanStep("step-1", "tidal", "HI_RES_LOSSLESS", [], "direct_url")
        ]
    };

    private static AmazonQueueItem CreateAmazonPayload(string quality) => new()
    {
        Engine = "amazon",
        Quality = quality,
        AutoIndex = 0,
        FallbackPlan =
        [
            new FallbackPlanStep("step-0", "amazon", quality, [], "direct_url"),
            new FallbackPlanStep("step-1", "tidal", "LOSSLESS", [], "direct_url")
        ]
    };

    private static TidalQueueItem CreateTidalPayload(string quality) => new()
    {
        Engine = "tidal",
        Quality = quality,
        AutoIndex = 0,
        FallbackPlan =
        [
            new FallbackPlanStep("step-0", "tidal", quality, [], "direct_url")
        ]
    };

    private static object Validate(EngineQueueItemBase payload, string path)
    {
        var type = typeof(QualityCatalog).Assembly.GetType(
            "DeezSpoTag.Services.Download.Shared.DeliveredAudioQualityGuard",
            throwOnError: true)!;
        var method = type.GetMethod("Validate", BindingFlags.Public | BindingFlags.Static)!;
        return method.Invoke(null, [payload, path])!;
    }

    private static bool IsDeliveredQualityAccepted(
        string engine,
        string requestedQuality,
        string label,
        int bitsPerSample,
        int sampleRate,
        int bitrateKbps,
        bool isLossless)
    {
        var assembly = typeof(QualityCatalog).Assembly;
        var guardType = assembly.GetType(
            "DeezSpoTag.Services.Download.Shared.DeliveredAudioQualityGuard",
            throwOnError: true)!;
        var actualType = assembly.GetType(
            "DeezSpoTag.Services.Download.Shared.ActualAudioQuality",
            throwOnError: true)!;
        var actual = Activator.CreateInstance(
            actualType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [label, bitsPerSample, sampleRate, bitrateKbps, isLossless],
            culture: null)!;
        var method = guardType.GetMethod(
            "IsDeliveredQualityAccepted",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, [engine, requestedQuality, actual])!;
    }

    private static bool ReadBool(object value, string property)
        => (bool)value.GetType().GetProperty(property)!.GetValue(value)!;

    private static string ReadString(object value, string property)
        => (string)value.GetType().GetProperty(property)!.GetValue(value)!;

    private static void WriteWave(string path, short bitsPerSample, int sampleRate = 44100)
    {
        const short channels = 2;
        var blockAlign = (short)(channels * (bitsPerSample / 8));
        var byteRate = sampleRate * blockAlign;
        var dataSize = byteRate;
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
        writer.Write(new byte[dataSize]);
    }
}
