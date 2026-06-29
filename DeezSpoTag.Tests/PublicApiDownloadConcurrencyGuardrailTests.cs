using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PublicApiDownloadConcurrencyGuardrailTests
{
    [Fact]
    public void SharedQueueProcessor_ReservesOnePublicApiDownloadSlotBeforeDequeue()
    {
        var source = ReadSource(
            "DeezSpoTag.Services",
            "Download",
            "Shared",
            "DeezSpoTagApp.cs");

        Assert.Contains("private readonly SemaphoreSlim _publicApiDownloadGate = new(1, 1);", source, StringComparison.Ordinal);
        Assert.Contains("TryReservePublicApiDownloadSlotAsync", source, StringComparison.Ordinal);
        Assert.Contains("WaitAsync(0, cancellationToken)", source, StringComparison.Ordinal);
        Assert.Contains("DequeueNextAnyExceptAsync(", source, StringComparison.Ordinal);
        Assert.Contains("PublicApiDownloadEngines", source, StringComparison.Ordinal);
        Assert.Contains("\"qobuz\"", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"tidal\"", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SharedQueueProcessor_DoesNotMarkSecondPublicApiRowRunningWhileGateIsOccupied()
    {
        var source = ReadSource(
            "DeezSpoTag.Services",
            "Download",
            "Shared",
            "DeezSpoTagApp.cs");

        var reservationIndex = source.IndexOf(
            "var publicApiSlot = await TryReservePublicApiDownloadSlotAsync",
            StringComparison.Ordinal);
        var dequeueIndex = source.IndexOf(
            "var nextItem = publicApiSlot.Reserved",
            StringComparison.Ordinal);
        var processIndex = source.IndexOf(
            "await ProcessQueueItemAsync(nextItem, CancellationToken.None);",
            StringComparison.Ordinal);

        Assert.True(reservationIndex >= 0);
        Assert.True(dequeueIndex > reservationIndex);
        Assert.True(processIndex > dequeueIndex);
    }

    [Fact]
    public void LegacyPerEngineQueueServices_AreNotRegisteredAsHostedDownloadWorkers()
    {
        var programSource = ReadSource("DeezSpoTag.Web", "Program.cs");

        Assert.DoesNotContain("QobuzQueueBackgroundService", programSource, StringComparison.Ordinal);
        Assert.DoesNotContain("TidalQueueBackgroundService", programSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AppleQueueBackgroundService", programSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AmazonQueueBackgroundService", programSource, StringComparison.Ordinal);
        Assert.Contains("DeezSpoTagQueueBackgroundService", programSource, StringComparison.Ordinal);
    }

    [Fact]
    public void QueueRepository_ExcludesPublicApiEnginesCaseInsensitively()
    {
        var source = ReadSource(
            "DeezSpoTag.Services",
            "Download",
            "Queue",
            "DownloadQueueRepository.cs");

        Assert.Contains("engine.Trim().ToLowerInvariant()", source, StringComparison.Ordinal);
        Assert.Contains("AND lower(engine) NOT IN", source, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] pathParts)
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../",
            Path.Combine(pathParts)));
}
