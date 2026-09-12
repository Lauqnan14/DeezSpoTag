using System;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Guards the BetterLyrics response check and the TTML time parsing it depends on.
///
/// BetterLyrics is the only lyrics provider that resolves by song + artist and then trusts the
/// answer, so a radio edit, a live take or a same-titled song would otherwise be written to the
/// file unchecked. The API exposes no usable match score, so the timed document's length against
/// the track's own duration is the signal.
///
/// The parsing half matters on its own: real payloads express times past the tenth minute as
/// <c>M:SS.mmm</c>, which <see cref="TimeSpan"/>-based parsing silently rejects.
/// </summary>
public sealed class BetterLyricsTimelineTest
{
    private const string TtmlNs = "http://www.w3.org/ns/ttml";
    private const string ItunesNs = "http://music.apple.com/lyric-ttml-internal";

    private static MethodInfo GetTimelineMatcher()
    {
        return typeof(LyricsService).GetMethod(
                   "BetterLyricsTimelineMatches",
                   BindingFlags.NonPublic | BindingFlags.Static)
               ?? throw new InvalidOperationException("LyricsService.BetterLyricsTimelineMatches not found.");
    }

    private static bool TimelineMatches(string ttml, int trackDurationSeconds, int toleranceSeconds)
    {
        var track = new Track
        {
            Id = "betterlyrics-timeline-track",
            Title = "Track",
            ArtistString = "Artist",
            Duration = trackDurationSeconds
        };
        var settings = new DeezSpoTagSettings
        {
            BetterLyrics = new BetterLyricsOptions { DurationToleranceSeconds = toleranceSeconds }
        };

        return (bool)GetTimelineMatcher().Invoke(null, [ttml, track, settings])!;
    }

    private static string BuildTtml(params string[] lastLineEnds)
    {
        var lines = string.Empty;
        var offset = 0;
        foreach (var end in lastLineEnds)
        {
            lines += $"<p begin=\"{offset}.000\" end=\"{end}\"><span begin=\"{offset}.000\" end=\"{end}\">Line</span></p>";
            offset += 5;
        }

        return $"<tt xmlns=\"{TtmlNs}\" xmlns:itunes=\"{ItunesNs}\" itunes:timing=\"Word\" xml:lang=\"en\">"
               + $"<body dur=\"9:59.999\"><div>{lines}</div></body></tt>";
    }

    // ---------------------------------------------------------------- time parsing

    [Theory]
    [InlineData("9.731", 9731)]          // SS.mmm
    [InlineData("99.999", 99999)]
    [InlineData("0.000", 0)]
    [InlineData("5:47.085", 347085)]     // M:SS.mmm  <- the format that used to fail
    [InlineData("1:16.656", 76656)]
    [InlineData("3:53.713", 233713)]
    [InlineData("00:01:30.500", 90500)]  // H:MM:SS.mmm
    [InlineData("1:02:03.004", 3723004)]
    public void TryReadTtmlEndMilliseconds_ParsesEveryDocumentedFormat(string end, int expectedMs)
    {
        var ttml = BuildTtml(end);

        Assert.True(AppleLyricsService.TryReadTtmlEndMilliseconds(ttml, out var milliseconds));
        Assert.Equal(expectedMs, milliseconds);
    }

    [Fact]
    public void TryReadTtmlEndMilliseconds_TakesTheLatestLineEnd()
    {
        var ttml = BuildTtml("12.105", "3:47.085", "5:47.085");

        Assert.True(AppleLyricsService.TryReadTtmlEndMilliseconds(ttml, out var milliseconds));
        Assert.Equal(347085, milliseconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<tt><body/></tt>")]
    [InlineData("not xml at all")]
    [InlineData("<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=\"0.000\" end=\"\">x</p></div></body></tt>")]
    public void TryReadTtmlEndMilliseconds_ReturnsFalseWhenNothingIsMeasurable(string ttml)
    {
        Assert.False(AppleLyricsService.TryReadTtmlEndMilliseconds(ttml, out var milliseconds));
        Assert.Equal(0, milliseconds);
    }

    [Fact]
    public void TryReadTtmlEndMilliseconds_IgnoresNegativeLines()
    {
        var ttml = $"<tt xmlns=\"{TtmlNs}\" xmlns:itunes=\"{ItunesNs}\" itunes:timing=\"Word\">"
                   + "<body><div><p begin=\"-1.000\" end=\"-1.000\"><span>x</span></p>"
                   + "<p begin=\"1.000\" end=\"2.000\"><span>y</span></p></div></body></tt>";

        Assert.True(AppleLyricsService.TryReadTtmlEndMilliseconds(ttml, out var milliseconds));
        Assert.Equal(2000, milliseconds);
    }

    // ---------------------------------------------------------------- the gate

    [Fact]
    public void TimelineMatches_AcceptsDocumentThatEndsNearTheTrack()
    {
        // 200s track, document ends at 205s: 5s long, inside a 15s tolerance.
        Assert.True(TimelineMatches(BuildTtml("3:25.000"), 200, 15));
    }

    [Fact]
    public void TimelineMatches_AcceptsTrailingSilenceWithinTolerance()
    {
        // 233s track (Shape of You), document ends at 245s: 12s of outro, inside 15s.
        Assert.True(TimelineMatches(BuildTtml("4:05.000"), 233, 15));
    }

    [Fact]
    public void TimelineMatches_RejectsTrailingSilenceBeyondTolerance()
    {
        // 233s track, document ends at 260s: 27s long, outside 15s.
        Assert.False(TimelineMatches(BuildTtml("4:20.000"), 233, 15));
    }

    [Fact]
    public void TimelineMatches_RejectsDocumentMissingVerses()
    {
        // 354s track (Bohemian Rhapsody), document stops at 200s: a truncated or wrong recording.
        Assert.False(TimelineMatches(BuildTtml("3:20.000"), 354, 15));
    }

    [Fact]
    public void TimelineMatches_RejectsJustPastTheShorterAllowance()
    {
        // Shorter allowance is 5s: 6s short is rejected even with a generous tolerance.
        Assert.False(TimelineMatches(BuildTtml("3:14.000"), 200, 60));
    }

    [Fact]
    public void TimelineMatches_AcceptsExactlyAtTheShorterAllowance()
    {
        Assert.True(TimelineMatches(BuildTtml("3:15.000"), 200, 15));
    }

    [Fact]
    public void TimelineMatches_AcceptsWhenTrackDurationIsUnknown()
    {
        // No known duration means no evidence, so the response must not be discarded.
        Assert.True(TimelineMatches(BuildTtml("3:20.000"), 0, 15));
    }

    [Fact]
    public void TimelineMatches_AcceptsWhenDocumentHasNoMeasurableLength()
    {
        const string untimed = "<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p>x</p></div></body></tt>";
        Assert.True(TimelineMatches(untimed, 200, 15));
    }

    [Fact]
    public void TimelineMatches_ClampsAbsurdToleranceInsteadOfRejectingEverything()
    {
        // A stored tolerance beyond the documented range is clamped, not treated as a rejection.
        Assert.True(TimelineMatches(BuildTtml("3:25.000"), 200, 5000));
    }

    [Fact]
    public void TimelineMatches_UsesDefaultsWhenSettingsAreMissing()
    {
        var track = new Track { Id = "t", Title = "Track", ArtistString = "Artist", Duration = 200 };
        var settings = new DeezSpoTagSettings();

        // Defaults to 15s and always has a BetterLyricsOptions instance.
        Assert.True((bool)GetTimelineMatcher()
            .Invoke(null, [BuildTtml("3:25.000"), track, settings])!);
    }

    // ---------------------------------------------------------------- card / backend agreement

    /// <summary>
    /// The backend is the source of truth for defaults. The card's declared default must equal what
    /// the pipeline actually uses when the profile carries no stored value, otherwise the UI shows a
    /// number the runner does not honour.
    /// </summary>
    [Fact]
    public void DurationToleranceOption_MatchesBackendDefault()
    {
        var backendDefault = new BetterLyricsOptions().DurationToleranceSeconds;

        var option = new DeezSpoTag.Web.Services.AutoTag.BetterLyricsPlatform(
                new StubWebHostEnvironment())
            .Describe()
            .Platform
            .CustomOptions
            .Options
            .Single(o => o.Id == "duration_tolerance_seconds");

        var declared = Assert.IsType<DeezSpoTag.Web.Services.AutoTag.PlatformCustomOptionNumber>(option.Value);
        Assert.Equal(backendDefault, declared.Value);
    }

    /// <summary>
    /// The card's slider bounds must not exceed the range the overlay clamps reads to, or a value the
    /// UI allows would be silently pulled back to a different number on the next load.
    /// </summary>
    [Fact]
    public void DurationToleranceOption_BoundsWithinBackendClamp()
    {
        var option = new DeezSpoTag.Web.Services.AutoTag.BetterLyricsPlatform(
                new StubWebHostEnvironment())
            .Describe()
            .Platform
            .CustomOptions
            .Options
            .Single(o => o.Id == "duration_tolerance_seconds");

        var declared = Assert.IsType<DeezSpoTag.Web.Services.AutoTag.PlatformCustomOptionNumber>(option.Value);
        Assert.True(declared.Min >= 0, $"Min {declared.Min} is below the backend clamp of 0.");
        Assert.True(declared.Max <= 120, $"Max {declared.Max} exceeds the backend clamp of 120.");
    }

    private sealed class StubWebHostEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public string EnvironmentName { get; set; } = "Development";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
