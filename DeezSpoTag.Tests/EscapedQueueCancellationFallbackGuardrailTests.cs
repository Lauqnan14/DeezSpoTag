using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class EscapedQueueCancellationFallbackGuardrailTests
{
    [Fact]
    public void EscapedProcessorCancellation_AttemptsFallbackBeforeFinalFailure()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../",
            "DeezSpoTag.Services",
            "Download",
            "Shared",
            "DeezSpoTagApp.cs"));

        var handlerStart = source.IndexOf(
            "private async Task HandleUnhandledProcessorCancellationAsync",
            StringComparison.Ordinal);
        var fallbackIndex = source.IndexOf(
            "await TryAdvanceFallbackAsync(item)",
            handlerStart,
            StringComparison.Ordinal);
        var failureIndex = source.IndexOf(
            "await MarkQueueItemAsFailedAndRetryAsync(item, timeoutException.Message)",
            handlerStart,
            StringComparison.Ordinal);

        Assert.True(handlerStart >= 0);
        Assert.True(fallbackIndex > handlerStart);
        Assert.True(failureIndex > fallbackIndex);
    }

    [Fact]
    public void EscapedProcessorCancellation_FallbackSupportsEveryQueueEngine()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../",
            "DeezSpoTag.Services",
            "Download",
            "Shared",
            "DeezSpoTagApp.cs"));

        Assert.Contains("TryAdvanceFallbackAsync<QobuzQueueItem>", source, StringComparison.Ordinal);
        Assert.Contains("TryAdvanceFallbackAsync<TidalQueueItem>", source, StringComparison.Ordinal);
        Assert.Contains("TryAdvanceFallbackAsync<AmazonQueueItem>", source, StringComparison.Ordinal);
        Assert.Contains("TryAdvanceFallbackAsync<AppleQueueItem>", source, StringComparison.Ordinal);
        Assert.Contains("TryAdvanceFallbackAsync<DeezerQueueItem>", source, StringComparison.Ordinal);
        Assert.Contains("CancellationToken.None", source, StringComparison.Ordinal);
    }
}
