using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// The Downloads tab Pause action is driven by the user-pause marker in the cancellation registry.
/// Every engine processor has to honour it; otherwise the cancelled branch runs instead and the
/// item flips to "cancelled" and auto-schedules a retry, which makes Pause look like it did nothing.
/// </summary>
public sealed class QobuzPauseGuardrailTest
{
    [Fact]
    public void QobuzHandleCancellation_HonoursUserPauseBeforeTreatingItAsCancellation()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Qobuz/QobuzEngineProcessor.cs"));

        var handlerStart = source.IndexOf(
            "private async Task HandleCancellationAsync(string queueUuid",
            StringComparison.Ordinal);
        Assert.True(handlerStart >= 0, "QobuzEngineProcessor.HandleCancellationAsync was not found.");

        var handlerEnd = source.IndexOf(
            "private async Task HandleFailureAsync(",
            handlerStart,
            StringComparison.Ordinal);
        Assert.True(handlerEnd > handlerStart, "The cancellation handler body could not be delimited.");

        var handler = source[handlerStart..handlerEnd];
        var pauseIndex = handler.IndexOf("WasUserPaused", StringComparison.Ordinal);
        var cancelIndex = handler.IndexOf("WasUserCanceled", StringComparison.Ordinal);

        Assert.True(pauseIndex >= 0, "Qobuz cancellation handling must check for a user pause.");
        Assert.True(cancelIndex >= 0, "Qobuz cancellation handling must check for a user cancel.");
        Assert.True(
            pauseIndex < cancelIndex,
            "The user-pause check must run before the user-cancel check.");
        Assert.Contains("PausedStatus", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryEngineProcessorThatHandlesCancellation_HonoursUserPause()
    {
        var processors = new[]
        {
            "../../../../DeezSpoTag.Services/Download/Deezer/DeezerEngineProcessor.cs",
            "../../../../DeezSpoTag.Services/Download/Apple/AppleEngineProcessor.cs",
            "../../../../DeezSpoTag.Services/Download/Qobuz/QobuzEngineProcessor.cs"
        };

        foreach (var relativePath in processors)
        {
            var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, relativePath));
            Assert.True(
                source.Contains("WasUserPaused", StringComparison.Ordinal),
                $"{Path.GetFileName(relativePath)} must honour a user pause during cancellation.");
        }

        // Tidal, Amazon and Soulseek share the common handler, which is covered by its own use of
        // the same marker. Keep it asserted here so a regression in the shared helper is visible.
        var shared = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Shared/EngineAudioPostDownloadHelper.cs"));
        Assert.Contains("WasUserPaused", shared, StringComparison.Ordinal);
    }
}
