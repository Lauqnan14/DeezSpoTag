using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class EngineQueueProcessorHelperPostDownloadSettingsTests
{
    [Fact]
    public async Task ApplyPostDownloadSettingsWithFallbackAsync_ReturnsAppliedPath_WhenNoException()
    {
        var result = await InvokeApplyPostDownloadSettingsWithFallbackAsync(
            "apple",
            "queue-1",
            "/tmp/original.m4a",
            () => Task.FromResult("/tmp/tagged.m4a"));

        Assert.Equal("/tmp/tagged.m4a", result);
    }

    [Fact]
    public async Task ApplyPostDownloadSettingsWithFallbackAsync_Rethrows_WhenTaggingThrows()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeApplyPostDownloadSettingsWithFallbackAsync(
                "apple",
                "queue-2",
                "/tmp/original.m4a",
                static () => Task.FromException<string>(new InvalidOperationException("tag write failed"))));
    }

    [Fact]
    public async Task ApplyPostDownloadSettingsWithFallbackAsync_DoesNotSwallowOperationCanceled()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await InvokeApplyPostDownloadSettingsWithFallbackAsync(
                "apple",
                "queue-3",
                "/tmp/original.m4a",
                static () => Task.FromException<string>(new OperationCanceledException("canceled"))));
    }

    private static async Task<string> InvokeApplyPostDownloadSettingsWithFallbackAsync(
        string engine,
        string queueUuid,
        string outputPath,
        Func<Task<string>> applySettingsAsync)
    {
        var helperType = Type.GetType(
            "DeezSpoTag.Services.Download.Shared.EngineQueueProcessorHelper, DeezSpoTag.Services",
            throwOnError: true)!;
        var method = helperType.GetMethod(
            "ApplyPostDownloadSettingsWithFallbackAsync",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(method);

        var task = (Task<string>)method!.Invoke(
            null,
            new object[] { engine, queueUuid, outputPath, NullLogger.Instance, applySettingsAsync })!;
        return await task;
    }
}
