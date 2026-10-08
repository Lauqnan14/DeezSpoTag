using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Pins the tiered capability resolution: rounds gate each other in a fixed order, the configured
/// lyrics fallback order only sequences providers <em>within</em> a round, and a round stops at
/// its first success. These replace the previous flat "walk the fallback order" behaviour where a
/// line-only provider (deezer) could be queried before a word-capable one (betterlyrics).
/// </summary>
public class LyricsCapabilityRoundTest
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    [Fact]
    public void DefaultOrder_PlacesProvidersInCapabilityRounds()
    {
        var requirements = CreateRequirements(wantsLrc: true, wantsTtml: true, wantsPlain: false);

        var rounds = BuildRounds(requirements, DefaultOrder);

        Assert.Equal(
            new[]
            {
                LyricsCapabilityRound.NativeTtml,
                LyricsCapabilityRound.WordSynchronizedLrc,
                LyricsCapabilityRound.LineSynchronizedLrc,
                LyricsCapabilityRound.PlainText
            },
            rounds.Select(round => round.Round).ToArray());

        // Only providers that serve a real TTML document belong in the native word-timed round.
        Assert.Equal(new[] { "apple", "betterlyrics" }, Providers(rounds, LyricsCapabilityRound.NativeTtml));

        // Apple and BetterLyrics also serve word-timed LRC, so they qualify here too. They are
        // filtered out at execution time when an earlier round already attempted them.
        Assert.Equal(
            new[] { "apple", "musixmatch", "youlyplus", "betterlyrics" },
            Providers(rounds, LyricsCapabilityRound.WordSynchronizedLrc));

        // Deezer, spotify and lrclib are line-synced only, so they can never satisfy word timings.
        Assert.Equal(
            new[] { "apple", "deezer", "spotify", "lrclib", "musixmatch", "youlyplus", "betterlyrics" },
            Providers(rounds, LyricsCapabilityRound.LineSynchronizedLrc));

        // Plain text is a capability every registered provider has, so it forms a final round.
        Assert.Equal(
            new[] { "apple", "deezer", "spotify", "lrclib", "musixmatch", "youlyplus", "betterlyrics" },
            Providers(rounds, LyricsCapabilityRound.PlainText));
    }

    [Fact]
    public void FilterRoundProviders_RemovesProvidersAnEarlierRoundAlreadyAttempted()
    {
        var requirements = CreateRequirements(wantsLrc: true, wantsTtml: true, wantsPlain: false);
        var rounds = BuildRounds(requirements, DefaultOrder);

        var nativeTtml = rounds.First(round => round.Round == LyricsCapabilityRound.NativeTtml);
        var wordLrc = rounds.First(round => round.Round == LyricsCapabilityRound.WordSynchronizedLrc);

        // After round 1 attempted both native-TTML providers, round 2 must not ask them again.
        var remaining = FilterRoundProviders(wordLrc, new[] { "apple", "betterlyrics" });

        Assert.Equal(new[] { "musixmatch", "youlyplus" }, remaining.ToArray());
        Assert.Equal(nativeTtml.Providers, FilterRoundProviders(nativeTtml, Array.Empty<string>()).ToArray());
    }

    [Fact]
    public void FallbackOrder_SequencesWithinRoundsWithoutReorderingThem()
    {
        var requirements = CreateRequirements(wantsLrc: true, wantsTtml: true, wantsPlain: false);

        // betterlyrics ahead of apple, and lrclib ahead of deezer/spotify.
        var rounds = BuildRounds(
            requirements,
            new[] { "betterlyrics", "apple", "youlyplus", "musixmatch", "lrclib", "spotify", "deezer" });

        Assert.Equal(LyricsCapabilityRound.NativeTtml, rounds[0].Round);
        Assert.Equal(new[] { "betterlyrics", "apple" }, Providers(rounds, LyricsCapabilityRound.NativeTtml));

        Assert.Equal(LyricsCapabilityRound.WordSynchronizedLrc, rounds[1].Round);
        Assert.Equal(
            new[] { "betterlyrics", "apple", "youlyplus", "musixmatch" },
            Providers(rounds, LyricsCapabilityRound.WordSynchronizedLrc));

        // Still the third round regardless of the configured order.
        Assert.Equal(LyricsCapabilityRound.LineSynchronizedLrc, rounds[2].Round);
        Assert.Equal(
            new[] { "betterlyrics", "apple", "youlyplus", "musixmatch", "lrclib", "spotify", "deezer" },
            Providers(rounds, LyricsCapabilityRound.LineSynchronizedLrc));

        // Plain text is a capability every registered provider has.
        Assert.Equal(LyricsCapabilityRound.PlainText, rounds[3].Round);
    }

    [Fact]
    public void SingleProviderOrder_NeverQueriesTheSameProviderTwice()
    {
        var requirements = CreateRequirements(wantsLrc: true, wantsTtml: true, wantsPlain: true);

        // Mirrors LyricsFallbackEnabled = false, where ProviderOrderResolver keeps providers[0].
        var rounds = BuildRounds(requirements, new[] { "apple" });
        var attempted = new List<string>();

        foreach (var round in rounds)
        {
            foreach (var provider in FilterRoundProviders(round, attempted))
            {
                attempted.Add(provider);
            }
        }

        // Every round qualifies on capability, but the attempted filter guarantees apple is the
        // only provider ever contacted, and only once.
        Assert.Equal(new[] { "apple" }, attempted);
        Assert.All(rounds, round => Assert.Equal(new[] { "apple" }, round.Providers.ToArray()));
    }

    [Fact]
    public void WordTimedRound_IsNotSatisfiedByLineSyncedLrc()
    {
        var lineOnly = new LyricsSource
        {
            SyncedLyrics = new List<SynchronizedLyric>
            {
                new() { Text = "line one", LrcTimestamp = "[00:01.00]", Milliseconds = 1000 }
            },
            SyncedLyricsSourceFormat = LyricsSourceFormat.DownloadedLrc
        };

        // CanSaveLrcSidecar() is true, so the line round is satisfied...
        Assert.True(IsRoundSatisfied(LyricsCapabilityRound.LineSynchronizedLrc, lineOnly));

        // ...but the word-timed round must stay unsatisfied so the line fallback still happens.
        Assert.False(IsRoundSatisfied(LyricsCapabilityRound.WordSynchronizedLrc, lineOnly));
    }

    [Fact]
    public void WordTimedRound_IsSatisfiedByWordTimings()
    {
        var enhanced = new LyricsSource
        {
            SyncedLyrics = new List<SynchronizedLyric>
            {
                new()
                {
                    Text = "karaoke line",
                    LrcTimestamp = "[00:01.00]",
                    Milliseconds = 1000,
                    Words = new List<SynchronizedLyricWord>
                    {
                        new() { Text = "karaoke", StartMilliseconds = 1000, EndMilliseconds = 1500 }
                    }
                }
            },
            SyncedLyricsSourceFormat = LyricsSourceFormat.ProviderSyncedJson
        };

        Assert.True(IsRoundSatisfied(LyricsCapabilityRound.WordSynchronizedLrc, enhanced));
        Assert.True(IsRoundSatisfied(LyricsCapabilityRound.LineSynchronizedLrc, enhanced));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LineRound_IsSkippedWhenWordTimingIsRequired(bool requiresWordTiming)
    {
        var requirements = CreateRequirements(wantsLrc: true, wantsTtml: false, wantsPlain: false, requiresWordTiming: requiresWordTiming);

        Assert.Equal(!requiresWordTiming, ShouldEnterRound(LyricsCapabilityRound.LineSynchronizedLrc, requirements));
        // The word-timed round is always entered when an LRC is requested.
        Assert.True(ShouldEnterRound(LyricsCapabilityRound.WordSynchronizedLrc, requirements));
    }

    [Fact]
    public void Rounds_AreOnlyEnteredForRequestedOutputs()
    {
        var ttmlOnly = CreateRequirements(wantsLrc: false, wantsTtml: true, wantsPlain: false);
        Assert.True(ShouldEnterRound(LyricsCapabilityRound.NativeTtml, ttmlOnly));
        Assert.False(ShouldEnterRound(LyricsCapabilityRound.WordSynchronizedLrc, ttmlOnly));
        Assert.False(ShouldEnterRound(LyricsCapabilityRound.LineSynchronizedLrc, ttmlOnly));
        Assert.False(ShouldEnterRound(LyricsCapabilityRound.PlainText, ttmlOnly));

        var plainOnly = CreateRequirements(wantsLrc: false, wantsTtml: false, wantsPlain: true);
        Assert.False(ShouldEnterRound(LyricsCapabilityRound.NativeTtml, plainOnly));
        Assert.True(ShouldEnterRound(LyricsCapabilityRound.PlainText, plainOnly));
    }

    [Fact]
    public void LineOnlyMode_DoesNotEnterWordRound()
    {
        var settings = new DeezSpoTagSettings
        {
            SyncedLyrics = true, LrcFormat = "lrc", LrcType = "lyrics,syllable-lyrics",
            LrcTimingPreference = LrcTimingModes.Line
        };
        var requirements = Invoke<object>("ResolveOutputRequirements", settings);

        Assert.False(ShouldEnterRound(LyricsCapabilityRound.WordSynchronizedLrc, requirements));
        Assert.True(ShouldEnterRound(LyricsCapabilityRound.LineSynchronizedLrc, requirements));
    }

    [Fact]
    public void NativeTtmlRound_RequiresWordSyncedTtml()
    {
        Assert.True(IsRoundSatisfied(LyricsCapabilityRound.NativeTtml, CreateWordTtmlLyrics()));
        Assert.False(IsRoundSatisfied(LyricsCapabilityRound.NativeTtml, new LyricsSource()));
        Assert.False(IsRoundSatisfied(LyricsCapabilityRound.NativeTtml, null));
    }

    [Fact]
    public void PublicLyricsTimeout_IsClampedToASaneBand()
    {
        Assert.Equal(10, ResolveTimeout(new DeezSpoTagSettings()).TotalSeconds);
        Assert.Equal(3, ResolveTimeout(new DeezSpoTagSettings { AppleMusic = new AppleMusicSettings { PublicLyricsTimeoutSeconds = 1 } }).TotalSeconds);
        Assert.Equal(60, ResolveTimeout(new DeezSpoTagSettings { AppleMusic = new AppleMusicSettings { PublicLyricsTimeoutSeconds = 999 } }).TotalSeconds);
        Assert.Equal(25, ResolveTimeout(new DeezSpoTagSettings { AppleMusic = new AppleMusicSettings { PublicLyricsTimeoutSeconds = 25 } }).TotalSeconds);
    }

    /// <summary>
    /// Mirrors the shape the public Apple Music lyrics API actually returns, captured from a live
    /// response: word timing is declared with the namespaced itunes:timing attribute and each
    /// paragraph carries begin/end timed spans.
    /// </summary>
    private static LyricsBase CreateWordTtmlLyrics()
    {
        const string ttml = "<tt xmlns=\"http://www.w3.org/ns/ttml\" "
            + "xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" "
            + "xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\" itunes:timing=\"Word\" xml:lang=\"en\">"
            + "<head><metadata><ttm:agent type=\"person\" xml:id=\"v1\">"
            + "<ttm:name type=\"full\">Artist</ttm:name></ttm:agent></metadata></head>"
            + "<body dur=\"0:12.000\"><div begin=\"1.000\" end=\"3.000\" itunes:songPart=\"Verse\">"
            + "<p begin=\"1.000\" end=\"2.000\" itunes:key=\"L1\" ttm:agent=\"v1\">"
            + "<span begin=\"1.000\" end=\"1.500\">hello</span> <span begin=\"1.500\" end=\"2.000\">world</span>"
            + "</p></div></body></tt>";
        return new LyricsSource
        {
            TtmlLyrics = ttml,
            TtmlLyricsSourceFormat = LyricsSourceFormat.DownloadedTtml
        };
    }

    private static string[] DefaultOrder =>
        new[] { "apple", "deezer", "spotify", "lrclib", "musixmatch", "youlyplus", "betterlyrics" };

    private static IReadOnlyList<LyricsRoundPlan> BuildRounds(object requirements, IReadOnlyList<string> providers)
        => Invoke<IReadOnlyList<LyricsRoundPlan>>("BuildCapabilityRounds", requirements, providers);

    private static bool ShouldEnterRound(LyricsCapabilityRound round, object requirements)
        => Invoke<bool>("ShouldEnterRound", round, requirements);

    private static bool IsRoundSatisfied(LyricsCapabilityRound round, LyricsBase? lyrics)
        => Invoke<bool>("IsRoundSatisfied", round, lyrics);

    private static IReadOnlyList<string> FilterRoundProviders(LyricsRoundPlan round, IReadOnlyCollection<string> attempted)
        => Invoke<IReadOnlyList<string>>("FilterRoundProviders", round, attempted);

    private static TimeSpan ResolveTimeout(DeezSpoTagSettings settings)
        => Invoke<TimeSpan>("ResolvePublicLyricsTimeout", settings);

    private static T Invoke<T>(string name, params object?[] args)
    {
        var method = typeof(LyricsService).GetMethod(name, PrivateStatic)
            ?? throw new InvalidOperationException($"{name} was not found.");
        return (T)(method.Invoke(null, args)
            ?? throw new InvalidOperationException($"{name} returned null."));
    }

    private static string[] Providers(IReadOnlyList<LyricsRoundPlan> rounds, LyricsCapabilityRound round)
        => rounds.First(candidate => candidate.Round == round).Providers.ToArray();

    /// <summary>
    /// <see cref="LyricsService"/> keeps its output requirements struct private, so it is built and
    /// passed around as an opaque object and only ever handed back to the resolver under test.
    /// </summary>
    private static object CreateRequirements(
        bool wantsLrc,
        bool wantsTtml,
        bool wantsPlain,
        bool requiresWordTiming = false)
    {
        var type = typeof(LyricsService).GetNestedType("LyricsOutputRequirements", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LyricsOutputRequirements was not found.");
        return Activator.CreateInstance(
            type,
            wantsLrc,
            wantsLrc,
            wantsLrc && requiresWordTiming,
            wantsTtml,
            wantsPlain)
            ?? throw new InvalidOperationException("Could not create LyricsOutputRequirements.");
    }
}
