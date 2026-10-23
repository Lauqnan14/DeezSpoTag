using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Soulseek;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     The item's error is one line under its title in the queue list.
/// </summary>
/// <remarks>
///     <para>
///         It is read at a glance, in a column that has to fit, by someone deciding whether to retry. A
///         paragraph there is worse than no message: it pushes the title out of view, and the sentences a person
///         does not need - a remote path, a peer name, a transport's error text - are the ones that bury the
///         sentence they do.
///     </para>
///     <para>
///         The diagnosis is not lost by shortening it. It is written to the log and kept on the item's fallback
///         history, so this test also holds the log line in place.
///     </para>
/// </remarks>
public sealed class QueueItemErrorLineTest
{
    /// <summary>
    ///     The longest a line may be. The CSS gives the error its own line under the title, so a message longer
    ///     than this wraps and becomes the paragraph this test exists to prevent.
    /// </summary>
    private const int MaxLineLength = 60;

    public static IEnumerable<object[]> EveryFailureClass()
    {
        // The whole vocabulary, not the classes seen in one failure: a message that is short for the common
        // case and a paragraph for the rare one is a message that is a paragraph. The classifier is internal, so
        // the vocabulary is read from the file that defines it - which also fails the test if a class is added
        // there without a clause here, which is the point.
        var classes = Source("DeezSpoTag.Services/Download/Fallback/FallbackFailureClassifier.cs")
            .Split('\n')
            .Select(line => System.Text.RegularExpressions.Regex.Match(
                line.Trim(),
                "^public const string (?<name>[A-Za-z]+) = \"(?<value>[^\"]+)\";$"))
            .Where(match => match.Success)
            .Select(match => match.Groups["value"].Value)
            .ToArray();

        Assert.NotEmpty(classes);

        return classes
            .Select(constant => new object[] { constant })
            .Concat([new object[] { string.Empty }])
            .Concat([new object[] { "some_class_nobody_has_seen" }]);
    }

    [Theory]
    [MemberData(nameof(EveryFailureClass))]
    public void TheExhaustionLineIsOneShortSentenceForEveryFailureClass(string errorClass)
    {
        var line = Line(errorClass);

        Assert.False(line.Contains('\n'), $"The item's error must be one line, got: {line}");
        Assert.True(
            line.Length <= MaxLineLength,
            $"The item's error must fit one line of at most {MaxLineLength} characters, got {line.Length}: {line}");
        Assert.Equal('.', line[^1]);
        Assert.DoesNotContain(". ", line, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EveryFailureClass))]
    public void TheExhaustionLineNamesTheSourceAndNothingInternal(string errorClass)
    {
        var line = Line(errorClass);

        Assert.StartsWith("Soulseek ", line, StringComparison.Ordinal);
        Assert.DoesNotContain("slskd", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\\\", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ExhaustedFallback_ShowsEachAttemptedEngineAndQuality()
    {
        var payload = new SoulseekQueueItem
        {
            FallbackPlan = [new("step-0", "qobuz", "6", [], "isrc"), new("step-1", "amazon", "HD_FLAC", [], "isrc")],
            FallbackHistory = [new("step-0", "failed", "provider_verification_required", "403"), new("step-1", "skipped", "unresolved", "missing URL")]
        };
        var method = typeof(EngineFallbackCoordinator).GetMethod("BuildExhaustionMessage", BindingFlags.NonPublic | BindingFlags.Static)!;
        var line = (string)method.Invoke(null, new object[] { payload, "tidal" })!;
        Assert.Equal("Qobuz (6) download API session needs verification; Amazon Music (HD_FLAC) had no copy of this track.", line);
    }

    [Fact]
    public void ExhaustedFallback_UsesSavedSelectionAndLatestOutcomeForEachQuality()
    {
        var settings = new DeezSpoTagSettings { Service = "custom" };
        settings.DownloadEngineOrder.Enabled = true;
        settings.DownloadEngineOrder.Engines.Single(engine => engine.Engine == "amazon")
            .Qualities.Single(quality => quality.Quality == "OPUS").Enabled = false;
        settings.DownloadEngineOrder.Engines.Single(engine => engine.Engine == "apple").Enabled = false;
        var payload = new SoulseekQueueItem
        {
            SourceSettingsSnapshot = QueueSourceSettingsSnapshot.Capture(settings),
            FallbackPlan =
            [
                new("step-0", "tidal", "HI_RES", [], "isrc"),
                new("step-1", "tidal", "LOSSLESS", [], "isrc"),
                new("step-2", "qobuz", "6", [], "isrc"),
                new("step-3", "amazon", "OPUS", [], "isrc"),
                new("step-4", "apple", "ALAC", [], "isrc")
            ],
            FallbackHistory =
            [
                new("step-0", "failed", "provider_verification_required", "old failure"),
                new("step-0", "failed", "provider_rate_limited", "latest failure"),
                new("step-1", "failed", "provider_rate_limited", "429"),
                new("step-2", "skipped", "quality_below_requested", "wrong quality"),
                new("step-3", "failed", "provider_verification_required", "disabled quality"),
                new("step-4", "failed", "provider_verification_required", "disabled engine")
            ]
        };
        var method = typeof(EngineFallbackCoordinator).GetMethod("BuildExhaustionMessage", BindingFlags.NonPublic | BindingFlags.Static)!;
        var line = (string)method.Invoke(null, [payload, "amazon"])!;

        Assert.Equal("Tidal (HI_RES, LOSSLESS) rate-limited the request; Qobuz (6) cannot offer that quality.", line);
        Assert.DoesNotContain("OPUS", line);
        Assert.DoesNotContain("Apple", line);
        Assert.DoesNotContain("verification", line);
    }

    [Fact]
    public void AnEmptyHistoryStillGivesOneShortLine()
    {
        // The line is built when a walk ends, which is also when the history may not have been written. It still
        // has to be a line.
        var line = Exhaust("", "Soulseek");

        Assert.Equal("Soulseek did not deliver the file.", line);
    }

    [Fact]
    public void TheDetailIsWrittenToTheLogSoShorteningTheLineLosesNothing()
    {
        var coordinator = Source("DeezSpoTag.Services/Download/Fallback/EngineFallbackCoordinator.cs");

        Assert.Contains("Fallback exhausted detail for {request.QueueUuid}: {exhausted.FallbackHistory[^1].Detail}", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void SoulseekFailureLinesAreOneShortSentenceEach()
    {
        // Every line the Soulseek path can put on an item, checked by hand against the same rule. The transfer
        // wording is asserted by SoulseekTransferRecoveryGuardrailTest; this holds the length and the shape.
        var lines = new[]
        {
            "Soulseek could not deliver the file.",
            "Soulseek started no transfer.",
            "Soulseek had no matching copy.",
            "Soulseek lost track of the transfer.",
            "Soulseek finished but left no file.",
            "Soulseek peer sent no data.",
            "Soulseek peer stopped at 35.8%."
        };

        foreach (var line in lines)
        {
            Assert.False(line.Contains('\n'));
            Assert.True(line.Length <= MaxLineLength, $"Too long ({line.Length}): {line}");
            Assert.DoesNotContain(". ", line, StringComparison.Ordinal);
        }
    }

    /// <summary>The line the item's error column will actually hold.</summary>
    private static string Line(string errorClass)
        => Exhaust(errorClass, "Soulseek");

    private static string Exhaust(string errorClass, string engine)
    {
        var payload = new SoulseekQueueItem();
        if (!string.IsNullOrEmpty(errorClass))
        {
            payload.FallbackHistory.Add(new FallbackAttempt(
                "step-0",
                "failed",
                errorClass,
                "the detail, which the log keeps"));
        }

        var build = typeof(EngineFallbackCoordinator)
            .GetMethod("BuildExhaustionMessage", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("BuildExhaustionMessage is gone; the copy it holds has moved.");

        return (string)build.Invoke(null, [payload, engine])!;
    }

    private static string Source(string relativePath)
        => File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../..",
            relativePath)));
}
