using System.Reflection;
using System;
using System.IO;
using DeezSpoTag.Services.Download.Amazon;
using DeezSpoTag.Services.Download.Shared;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadLifecycleCheckpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"download-checkpoint-{Guid.NewGuid():N}");

    public DownloadLifecycleCheckpointTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ExistingValidAudio_IsAdoptedAndResumedWithoutAcquisition()
    {
        var path = Path.Combine(_root, "retained.wav");
        WriteWave(path, 24);
        var payload = new AmazonQueueItem
        {
            Engine = "amazon",
            Quality = "HD_FLAC",
            FilePath = path
        };

        Assert.True(InvokeBool("TryAdoptExistingAudio", payload));
        Assert.True(payload.AudioAcquired);
        Assert.Equal(path, payload.AcquiredAudioPath);
        Assert.True(payload.AcquiredFileSizeBytes > 0);
        Assert.Equal("pending", payload.FinalizationStage);

        var arguments = new object?[] { payload, null };
        Assert.True(InvokeBool("TryResume", arguments));
        Assert.Equal(path, arguments[1]);
    }

    [Fact]
    public void MissingCheckpointAudio_ClearsAcquisitionAndReturnsToDownloader()
    {
        var payload = new AmazonQueueItem
        {
            Engine = "amazon",
            Quality = "HD_FLAC",
            AudioAcquired = true,
            AcquiredAudioPath = Path.Combine(_root, "missing.flac"),
            AcquiredFileSizeBytes = 100,
            FinalizationStage = "tag_writing"
        };
        var arguments = new object?[] { payload, null };

        Assert.False(InvokeBool("TryResume", arguments));
        Assert.False(payload.AudioAcquired);
        Assert.Empty(payload.AcquiredAudioPath);
        Assert.Empty(payload.FinalizationStage);
    }

    [Fact]
    public void TaggingSizeChange_DoesNotInvalidateOtherwiseValidAcquiredAudio()
    {
        var path = Path.Combine(_root, "retagged.wav");
        WriteWave(path, 24);
        var originalSize = new FileInfo(path).Length;
        var payload = new AmazonQueueItem
        {
            Engine = "amazon",
            Quality = "HD_FLAC",
            AudioAcquired = true,
            AcquiredAudioPath = path,
            AcquiredFileSizeBytes = originalSize - 128,
            FinalizationStage = "tag_writing"
        };
        var arguments = new object?[] { payload, null };

        Assert.True(InvokeBool("TryResume", arguments));
        Assert.Equal(originalSize, payload.AcquiredFileSizeBytes);
        Assert.Equal(path, arguments[1]);
    }

    [Fact]
    public void SharedAndSpecializedEngines_UseTheSameAcquiredAudioCheckpoint()
    {
        var shared = ReadSource("DeezSpoTag.Services", "Download", "Shared", "EngineQueueProcessorHelper.cs");
        var qobuz = ReadSource("DeezSpoTag.Services", "Download", "Qobuz", "QobuzEngineProcessor.cs");
        var apple = ReadSource("DeezSpoTag.Services", "Download", "Apple", "AppleEngineProcessor.cs");
        var deezer = ReadSource("DeezSpoTag.Services", "Download", "Deezer", "DeezerEngineProcessor.cs");

        Assert.Contains("DownloadLifecycleCheckpoint.TryResume", shared, StringComparison.Ordinal);
        Assert.Contains("DownloadLifecycleCheckpoint.TryResume", qobuz, StringComparison.Ordinal);
        Assert.Contains("DownloadLifecycleCheckpoint.TryResume", apple, StringComparison.Ordinal);
        Assert.Contains("DownloadLifecycleCheckpoint.TryResume", deezer, StringComparison.Ordinal);
        Assert.DoesNotContain("post-download settings failed", shared, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FinalizationFailurePath_DoesNotAdvanceEngineFallback()
    {
        var checkpoint = ReadSource("DeezSpoTag.Services", "Download", "Shared", "DownloadLifecycleCheckpoint.cs");
        var shared = ReadSource("DeezSpoTag.Services", "Download", "Shared", "EngineQueueProcessorHelper.cs");

        Assert.Contains("PersistFinalizationFailureAsync", checkpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("TryAdvanceAsync", checkpoint, StringComparison.Ordinal);
        var catchStart = shared.IndexOf("catch (DownloadFinalizationException ex)", StringComparison.Ordinal);
        Assert.True(catchStart >= 0);
        var catchBody = shared.Substring(catchStart, Math.Min(900, shared.Length - catchStart));
        Assert.Contains("PersistFinalizationFailureAsync", catchBody, StringComparison.Ordinal);
        Assert.DoesNotContain("TryAdvanceAsync", catchBody, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalizationFailure_RetainsAudioAndUsesConciseQueueMessages()
    {
        var checkpoint = ReadSource("DeezSpoTag.Services", "Download", "Shared", "DownloadLifecycleCheckpoint.cs");
        var postDownload = ReadSource("DeezSpoTag.Services", "Download", "Shared", "EngineAudioPostDownloadHelper.cs");
        var repository = ReadSource("DeezSpoTag.Services", "Download", "Queue", "DownloadQueueRepository.cs");

        Assert.Contains("HasRetainedAcquiredAudio(payloadJson)", repository, StringComparison.Ordinal);
        Assert.Contains("Audio downloaded; waiting to retry required album artwork.", postDownload, StringComparison.Ordinal);
        Assert.Contains("Audio downloaded; tag writing will be retried.", postDownload, StringComparison.Ordinal);
        Assert.Contains("Audio downloaded; embedded artwork verification will be retried.", postDownload, StringComparison.Ordinal);
        Assert.Contains("retry_waiting", checkpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete", checkpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("TryDelete", checkpoint, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private static bool InvokeBool(string methodName, params object?[] arguments)
    {
        var type = typeof(EngineQueueItemBase).Assembly.GetType(
            "DeezSpoTag.Services.Download.Shared.DownloadLifecycleCheckpoint",
            true)!;
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, arguments)!;
    }

    private static string ReadSource(params string[] parts)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../..", Path.Combine(parts)));
        return File.ReadAllText(path);
    }

    private static void WriteWave(string path, short bitsPerSample)
    {
        const short channels = 2;
        const int sampleRate = 44100;
        var blockAlign = (short)(channels * (bitsPerSample / 8));
        var byteRate = sampleRate * blockAlign;
        var dataSize = byteRate;
        using var writer = new BinaryWriter(File.Create(path));
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
