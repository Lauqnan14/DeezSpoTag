using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Guardrails for the Shazam signature window and the capture duration that feeds it.
///
/// Measured against the live Shazam API, N=60 paired trials on an identical capture where
/// only the window length varied: a 10s window matched 33/60 and a 12s window matched
/// 56/60, with 12s never losing a track that 10s won. Windows of 16s and above matched
/// 0/17 while still returning well-formed, larger signatures, so the band is ~12-14s.
/// The recognizer needs a full 12s of contiguous audio to sit inside it.
/// </summary>
public sealed class ShazamSignatureWindowTest
{
    private const int ExpectedWindowSeconds = 12;

    public static IEnumerable<object[]> SupportedCaptureLengths()
    {
        // Every length the settings service accepts, plus the upper clamp boundary.
        return new[] { 12, 13, 16, 19, 20 }.Select(v => new object[] { v });
    }

    [Theory]
    [MemberData(nameof(SupportedCaptureLengths))]
    public void MicSignatureWindow_IsTwelveSecondsForEverySupportedCaptureLength(int captureDurationSeconds)
    {
        Assert.Equal(ExpectedWindowSeconds, ResolveWindow(captureDurationSeconds));
    }

    [Fact]
    public void MicSignatureWindow_NeverExceedsTheAudioItWasGiven()
    {
        // Only reachable if a capture below the floor slips past normalization; the
        // recognizer then uses all of that audio rather than padding it out.
        Assert.Equal(3, ResolveWindow(3));
        Assert.Equal(7, ResolveWindow(7));
    }

    private static int ResolveWindow(int captureDurationSeconds)
    {
        var method = typeof(ShazamRecognitionService).Assembly
            .GetType("DeezSpoTag.Web.Controllers.Api.ShazamRecognitionApiController")!
            .GetMethod("ResolveMicSignatureWindowSeconds", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (int)method!.Invoke(null, [captureDurationSeconds])!;
    }

    [Fact]
    public void CaptureDuration_DefaultsHighEnoughToHoldAFullWindow()
    {
        var defaults = new DeezSpoTagSettings();

        // A fresh install must start unmigrated so the versioned lift runs on first load.
        Assert.Equal(0, defaults.ShazamCaptureSettingsVersion);
        Assert.True(
            defaults.ShazamCaptureDurationSeconds >= ExpectedWindowSeconds,
            $"Default capture {defaults.ShazamCaptureDurationSeconds}s cannot hold a {ExpectedWindowSeconds}s window.");
    }

    [Fact]
    public void SettingsNormalization_KeepsTheCaptureDurationInsideTheUsableBand()
    {
        var source = ResolveRepoFile("DeezSpoTag.Services", "Settings", "DeezSpoTagSettingsService.cs");

        // The floor has to be the signature window, not the old 3: a shorter capture cannot
        // fingerprint, so allowing it only offers a setting that cannot work.
        Assert.Contains("MinShazamCaptureDurationSeconds = 12", source, StringComparison.Ordinal);
        Assert.Contains("MaxShazamCaptureDurationSeconds = 20", source, StringComparison.Ordinal);
        Assert.Contains("settings.ShazamCaptureDurationSeconds < MinShazamCaptureDurationSeconds", source, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.ShazamCaptureDurationSeconds < 3", source, StringComparison.Ordinal);

        // Persisted values below the floor are lifted once, not left in place.
        Assert.Contains("settings.ShazamCaptureSettingsVersion < 1", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsView_OffersTheSameFloorAsTheService()
    {
        var view = ResolveRepoFile("DeezSpoTag.Web", "Views", "Settings", "Index.cshtml");

        // The old floor was min="3", which let a capture be configured that cannot match.
        Assert.Contains("id=\"shazamCaptureDurationSeconds\" min=\"12\" max=\"20\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureClient_CannotConfigureACaptureShorterThanTheWindow()
    {
        var source = ResolveRepoFile("DeezSpoTag.Web", "wwwroot", "js", "shazam-listen.js");

        // Both the speculative early attempt and the final upload now carry a full window,
        // so a short window must not creep back in.
        Assert.Contains("const EARLY_ATTEMPT_SECONDS = 12;", source, StringComparison.Ordinal);
        Assert.Contains("const MIN_CAPTURE_SECONDS = 12;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.max(3, Math.min(20", source, StringComparison.Ordinal);

        // The recognizer needs ~2.7s per attempt, so the early attempt must be fired with
        // enough slack to answer before the capture ends, or it is aborted and wasted.
        Assert.Contains("EARLY_ATTEMPT_FIRE_SLOP_MS", source, StringComparison.Ordinal);
        Assert.Contains("EARLY_ATTEMPT_SECONDS * 1000 + EARLY_ATTEMPT_FIRE_SLOP_MS", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureClient_RequestTimeoutOutlivesTheServerProcessTimeout()
    {
        var source = ResolveRepoFile("DeezSpoTag.Web", "wwwroot", "js", "shazam-listen.js");
        var match = System.Text.RegularExpressions.Regex.Match(source, @"const RECOGNITION_REQUEST_TIMEOUT_MS = (\d+);");
        Assert.True(match.Success, "RECOGNITION_REQUEST_TIMEOUT_MS not found");

        var clientTimeoutSeconds = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var processTimeout = (TimeSpan)typeof(ShazamRecognitionService)
            .GetField("RecognizerProcessTimeout", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        // Otherwise the client abandons the request first and reports a generic timeout in
        // place of the server's precise reason.
        Assert.True(
            clientTimeoutSeconds > processTimeout.TotalSeconds,
            $"Client timeout {clientTimeoutSeconds}s must exceed the {processTimeout.TotalSeconds}s server process timeout.");
    }

    [Fact]
    public void RetryWindows_AreDocumentedAsOutsideTheUsableBand()
    {
        var source = ResolveRepoFile("DeezSpoTag.Web", "Services", "ShazamRecognitionService.cs");

        // The file/auto-tag path retries [10, 18]: one weak window and one past the cliff.
        // Left as-is pending its own validation, so record why rather than let the next
        // reader assume it is deliberate tuning.
        Assert.Contains("AudioOnlySignatureRetryWindowsSeconds = [10, 18]", source, StringComparison.Ordinal);
        Assert.Contains("16s and above matched 0/17", source, StringComparison.Ordinal);
    }

    private static string ResolveRepoFile(params string[] segments)
    {
        var path = Path.Join(new[] { ResolveRepoRoot() }.Concat(segments).ToArray());
        Assert.True(File.Exists(path), $"Missing file: {path}");
        return File.ReadAllText(path);
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Join(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test output path.");
    }
}
