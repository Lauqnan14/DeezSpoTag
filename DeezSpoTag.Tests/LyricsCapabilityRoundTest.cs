using System;
using System.Collections.Generic;
using System.IO;
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

    [Theory]
    [InlineData("ttml-lyrics,syllable-lyrics,lyrics", LyricsCapabilityRound.NativeTtml, LyricsCapabilityRound.WordSynchronizedLrc, LyricsCapabilityRound.LineSynchronizedLrc)]
    [InlineData("ttml-lyrics,lyrics,syllable-lyrics", LyricsCapabilityRound.NativeTtml, LyricsCapabilityRound.LineSynchronizedLrc, LyricsCapabilityRound.WordSynchronizedLrc)]
    [InlineData("syllable-lyrics,ttml-lyrics,lyrics", LyricsCapabilityRound.WordSynchronizedLrc, LyricsCapabilityRound.NativeTtml, LyricsCapabilityRound.LineSynchronizedLrc)]
    [InlineData("syllable-lyrics,lyrics,ttml-lyrics", LyricsCapabilityRound.WordSynchronizedLrc, LyricsCapabilityRound.LineSynchronizedLrc, LyricsCapabilityRound.NativeTtml)]
    [InlineData("lyrics,ttml-lyrics,syllable-lyrics", LyricsCapabilityRound.LineSynchronizedLrc, LyricsCapabilityRound.NativeTtml, LyricsCapabilityRound.WordSynchronizedLrc)]
    [InlineData("lyrics,syllable-lyrics,ttml-lyrics", LyricsCapabilityRound.LineSynchronizedLrc, LyricsCapabilityRound.WordSynchronizedLrc, LyricsCapabilityRound.NativeTtml)]
    public void TypeOrder_ReordersRichRoundsWhilePlainStaysLast(string typeOrder,
        LyricsCapabilityRound first, LyricsCapabilityRound second, LyricsCapabilityRound third)
    {
        var requirements = CreateRequirements(wantsLrc: true, wantsTtml: true, wantsPlain: true);

        var rounds = BuildRounds(requirements, DefaultOrder, typeOrder);

        Assert.Equal(
            new[] { first, second, third, LyricsCapabilityRound.PlainText },
            rounds.Select(round => round.Round).ToArray());
    }

    [Fact]
    public void TypeOrder_PlainIsPinnedLast_EvenWhenSuppliedFirst()
    {
        var requirements = CreateRequirements(wantsLrc: true, wantsTtml: true, wantsPlain: true);

        var rounds = BuildRounds(requirements, DefaultOrder, "unsynced-lyrics,lyrics,ttml-lyrics,syllable-lyrics");

        Assert.Equal(
            new[]
            {
                LyricsCapabilityRound.LineSynchronizedLrc,
                LyricsCapabilityRound.NativeTtml,
                LyricsCapabilityRound.WordSynchronizedLrc,
                LyricsCapabilityRound.PlainText
            },
            rounds.Select(round => round.Round).ToArray());
    }

    [Fact]
    public void TypeOrder_AbsentOrder_KeepsLegacyRoundDeclaration()
    {
        var requirements = CreateRequirements(wantsLrc: true, wantsTtml: true, wantsPlain: true);

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
    }

    [Fact]
    public void TypeOrder_RoundProviders_FollowConfiguredSourceOrderWithinEachRound()
    {
        var requirements = CreateRequirements(wantsLrc: true, wantsTtml: true, wantsPlain: false);

        var forward = BuildRounds(
            requirements,
            new[] { "apple", "betterlyrics", "deezer", "lrclib" },
            "lyrics,ttml-lyrics,syllable-lyrics");
        var reversed = BuildRounds(
            requirements,
            new[] { "lrclib", "deezer", "betterlyrics", "apple" },
            "lyrics,ttml-lyrics,syllable-lyrics");

        // The type order is the outer loop; the configured source order is the inner loop.
        // The plain round is still planned (every provider has the capability) but the
        // execution gates keep it closed unless plain output is requested.
        Assert.Equal(
            new[] { LyricsCapabilityRound.LineSynchronizedLrc, LyricsCapabilityRound.NativeTtml, LyricsCapabilityRound.WordSynchronizedLrc, LyricsCapabilityRound.PlainText },
            forward.Select(round => round.Round).ToArray());
        Assert.Equal(new[] { "apple", "betterlyrics", "deezer", "lrclib" }, Providers(forward, LyricsCapabilityRound.LineSynchronizedLrc));
        Assert.Equal(new[] { "apple", "betterlyrics" }, Providers(forward, LyricsCapabilityRound.NativeTtml));
        Assert.Equal(new[] { "lrclib", "deezer", "betterlyrics", "apple" }, Providers(reversed, LyricsCapabilityRound.LineSynchronizedLrc));
        Assert.Equal(new[] { "betterlyrics", "apple" }, Providers(reversed, LyricsCapabilityRound.NativeTtml));
    }

    [Fact]
    public void TypeOrder_RoundsOmitDisabledOrIncapableSources()
    {
        var requirements = CreateRequirements(wantsLrc: true, wantsTtml: true, wantsPlain: false);

        // deezer/spotify/lrclib cannot serve native TTML or word timings; they must not
        // appear in either round even when placed first in the configured source order.
        var rounds = BuildRounds(
            requirements,
            new[] { "lrclib", "deezer", "spotify", "apple", "musixmatch" },
            "ttml-lyrics,syllable-lyrics,lyrics");

        Assert.Equal(new[] { "apple" }, Providers(rounds, LyricsCapabilityRound.NativeTtml));
        Assert.Equal(new[] { "apple", "musixmatch" }, Providers(rounds, LyricsCapabilityRound.WordSynchronizedLrc));
        Assert.Equal(new[] { "lrclib", "deezer", "spotify", "apple", "musixmatch" }, Providers(rounds, LyricsCapabilityRound.LineSynchronizedLrc));
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

    [Theory]
    [InlineData("Line", false)]
    [InlineData("None", false)]
    [InlineData("Word", true)]
    public void PublicApplePayload_PreservesWordTimingAndExplainsLineOnlyResponse(string timing, bool word)
    {
        var text = "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"" + timing
            + "\"><body><div><p begin=\"1.0\" end=\"2.0\">" + (word ? "<span begin=\"1.0\" end=\"2.0\">hello</span>" : "hello") + "</p></div></body></tt>";
        using var payload = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new { type = "TTML", content = text, source = "api" }));
        var result = Invoke<LyricsBase>("ParsePaxsenixLyricsPayload", payload.RootElement, new DeezSpoTagSettings { SynthesizeLrcFromTtml = false });
        Assert.Equal(word, DeezSpoTag.Services.Apple.AppleLyricsService.IsWordSyncedTtml(result.TtmlLyrics));
        if (word)
        {
            Assert.Equal(text, result.TtmlLyrics);
            Assert.Equal(LyricsSourceFormat.DownloadedTtml, result.TtmlLyricsSourceFormat);
            Assert.True(string.IsNullOrEmpty(result.ErrorMessage));
        }
        else if (timing == "Line")
        {
            Assert.Contains("line", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
            Assert.False(result.IsLoaded());
        }
    }

    private static string[] DefaultOrder =>
        new[] { "apple", "deezer", "spotify", "lrclib", "musixmatch", "youlyplus", "betterlyrics" };

    private static IReadOnlyList<LyricsRoundPlan> BuildRounds(object requirements, IReadOnlyList<string> providers, string? typeOrder = null)
        => Invoke<IReadOnlyList<LyricsRoundPlan>>("BuildCapabilityRounds", requirements, providers, typeOrder);

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

    // ---- lyrics type priority: progress messages reflect the actual active round ----

    [Theory]
    [InlineData("ttml-lyrics,syllable-lyrics,lyrics", "word-synced TTML", "word-timed LRC", "line-synced LRC")]
    [InlineData("lyrics,syllable-lyrics,ttml-lyrics", "line-synced LRC", "word-timed LRC", "word-synced TTML")]
    [InlineData("syllable-lyrics,lyrics,ttml-lyrics", "word-timed LRC", "line-synced LRC", "word-synced TTML")]
    [InlineData("unsynced-lyrics,lyrics,ttml-lyrics,syllable-lyrics", "line-synced LRC", "word-synced TTML", "word-timed LRC")]
    public void TypeOrder_RoundGoalsFollowTheReorderedRounds(string typeOrder, string firstGoal, string secondGoal, string thirdGoal)
    {
        var requirements = CreateRequirements(wantsLrc: true, wantsTtml: true, wantsPlain: true);

        var rounds = BuildRounds(requirements, DefaultOrder, typeOrder);

        // The progress pipeline surfaces DescribeRoundGoal of the active round as
        // LookupGoal, so these strings are what the Activities feed displays.
        Assert.Equal(
            new[] { firstGoal, secondGoal, thirdGoal, "plain lyrics" },
            rounds.Select(round => round.Goal).ToArray());
    }

    // ---- lyrics type priority: AutoTag ordering widget (Node harness) ----

    [Fact]
    public async System.Threading.Tasks.Task LyricsTypeOrderUi_ReordersRichRowsAndKeepsPlainPinned()
    {
        var result = await RunLyricsTypeOrderWidgetAsync("""
            const ui = buildUi('lyrics,ttml-lyrics,syllable-lyrics,unsynced-lyrics');
            setupLyricsTypeOrderDropdown();
            assert.equal(ui.orderInput.value, 'ttml-lyrics,syllable-lyrics,lyrics,unsynced-lyrics',
                'The default row order becomes the saved order when nothing was customized yet.');

            // Mouse reorder: drag TTML below the line-synced row.
            const ttml = ui.row('ttml-lyrics');
            ui.dragstart(ttml);
            ui.dragover(ui.row('lyrics'), { clientY: 999 });
            ui.dragend();
            assert.equal(ui.orderInput.value, 'syllable-lyrics,lyrics,ttml-lyrics,unsynced-lyrics');

            // Plain row cannot be moved: dragging it changes nothing.
            const plain = ui.row('unsynced-lyrics');
            ui.dragstart(plain);
            ui.dragover(ui.row('lyrics'), { clientY: 0 });
            ui.dragend();
            assert.equal(ui.orderInput.value, 'syllable-lyrics,lyrics,ttml-lyrics,unsynced-lyrics');
            assert.equal(ui.list.children[3].dataset.value, 'unsynced-lyrics');

            // Keyboard reorder: ArrowDown moves TTML back to last rich position.
            ui.keydown(ui.row('ttml-lyrics'), 'ArrowDown');
            assert.equal(ui.orderInput.value, 'syllable-lyrics,lyrics,ttml-lyrics,unsynced-lyrics',
                'TTML is already last among rich rows; the order is unchanged.');
            ui.keydown(ui.row('syllable-lyrics'), 'ArrowDown');
            assert.equal(ui.orderInput.value, 'lyrics,syllable-lyrics,ttml-lyrics,unsynced-lyrics');
            process.stdout.write('OK');
            """);
        Assert.Equal("OK", result);
    }

    [Fact]
    public async System.Threading.Tasks.Task LyricsTypeOrderUi_DisablingTypePreservesSavedOrder()
    {
        var result = await RunLyricsTypeOrderWidgetAsync("""
            const ui = buildUi('lyrics,ttml-lyrics,syllable-lyrics,unsynced-lyrics');
            setupLyricsTypeOrderDropdown();
            ui.dragstart(ui.row('ttml-lyrics'));
            ui.dragover(ui.row('lyrics'), { clientY: 999 });
            ui.dragend();
            assert.equal(ui.orderInput.value, 'syllable-lyrics,lyrics,ttml-lyrics,unsynced-lyrics');

            // Disable TTML and word-synced LRC through the membership checkboxes.
            ui.setTypeSelection('lyrics,unsynced-lyrics');
            ui.changeSelection();
            assert.equal(ui.orderInput.value, 'syllable-lyrics,lyrics,ttml-lyrics,unsynced-lyrics',
                'Disabling a type must not erase its saved position.');
            assert.deepEqual(ui.disabledValues(), ['syllable-lyrics', 'ttml-lyrics'],
                'Disabled rows are dimmed in their preserved displayed order.');

            // Disabled types remain reorderable preferences while never looked up.
            assert.equal(ui.row('lyrics').draggable, true);
            assert.equal(ui.row('ttml-lyrics').draggable, true);
            assert.equal(ui.row('syllable-lyrics').draggable, true);
            ui.dragstart(ui.row('syllable-lyrics'));
            ui.dragover(ui.row('ttml-lyrics'), { clientY: 999 });
            ui.dragend();
            assert.equal(ui.orderInput.value, 'lyrics,ttml-lyrics,syllable-lyrics,unsynced-lyrics',
                'A disabled type can still be arranged for its future priority.');

            // Keyboard reordering works for disabled rows too.
            ui.keydown(ui.row('ttml-lyrics'), 'ArrowUp');
            assert.equal(ui.orderInput.value, 'ttml-lyrics,lyrics,syllable-lyrics,unsynced-lyrics');

            // Re-enabling keeps the same saved order.
            ui.setTypeSelection('lyrics,ttml-lyrics,syllable-lyrics,unsynced-lyrics');
            ui.changeSelection();
            assert.equal(ui.orderInput.value, 'ttml-lyrics,lyrics,syllable-lyrics,unsynced-lyrics');
            assert.deepEqual(ui.disabledValues(), []);
            process.stdout.write('OK');
            """);
        Assert.Equal("OK", result);
    }

    [Fact]
    public async System.Threading.Tasks.Task LyricsTypeOrderUi_LegacyOmittedFieldFallsBackToDefaultOrder()
    {
        var result = await RunLyricsTypeOrderWidgetAsync("""
            const ui = buildUi('lyrics,ttml-lyrics,syllable-lyrics,unsynced-lyrics');
            ui.orderInput.value = '';
            setupLyricsTypeOrderDropdown();
            assert.equal(ui.orderInput.value, 'ttml-lyrics,syllable-lyrics,lyrics,unsynced-lyrics',
                'A legacy client that never saved an order gets the default priority.');

            // A profile without the field resolves through the same normalizer.
            assert.equal(
                normalizeLyricsTypeOrderSetting(''),
                'ttml-lyrics,syllable-lyrics,lyrics');
            assert.equal(
                normalizeLyricsTypeOrderSetting('unsynced-lyrics,ttml,synced-lyrics'),
                'ttml-lyrics,lyrics,syllable-lyrics,unsynced-lyrics');
            assert.equal(
                normalizeLyricsTypeOrderSetting('lyrics'),
                'lyrics,ttml-lyrics,syllable-lyrics');
            process.stdout.write('OK');
            """);
        Assert.Equal("OK", result);
    }

    [Fact]
    public void LyricsTypeOrderUi_ProfileLoadSaveAndSettingsPayloadAreWired()
    {
        var script = File.ReadAllText(Path.Join(
            AppContext.BaseDirectory, "../../../../DeezSpoTag.Web/wwwroot/js/autotag.js"));
        // Profile load: restore the saved order and re-sync the widget.
        Assert.Contains("applyFieldValueIfPresent(\"lyricsTypeOrder\"", script, StringComparison.Ordinal);
        Assert.Contains("if (state.syncLyricsTypeOrder)", script, StringComparison.Ordinal);
        // Profile save: persist the widget's order.
        Assert.Contains("technical.lyricsTypeOrder = normalizeLyricsTypeOrderSetting(", script, StringComparison.Ordinal);

        // Global settings save must round-trip the stored order instead of resetting it.
        var settingsView = File.ReadAllText(Path.Join(
            AppContext.BaseDirectory, "../../../../DeezSpoTag.Web/Views/Settings/Index.cshtml"));
        Assert.Contains(
            "lyricsTypeOrder: getValue('lyricsTypeOrder', baseSettings.lyricsTypeOrder ?? 'ttml-lyrics,syllable-lyrics,lyrics')",
            settingsView, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs the AutoTag lyrics type order widget under Node against a minimal DOM stub,
    /// mirroring the harness used by <see cref="AutoTagCreditRoleTest"/>.
    /// </summary>
    private static async System.Threading.Tasks.Task<string> RunLyricsTypeOrderWidgetAsync(string testScript)
    {
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DeezSpoTag.Web", "wwwroot", "js", "autotag.js")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);

        var harness = """
            const fs = require('node:fs');
            const vm = require('node:vm');
            const assert = require('node:assert/strict');
            const source = fs.readFileSync(process.argv[1], 'utf8');
            const boundary = source.indexOf('\n    document.addEventListener("change"');
            assert(boundary > 0, 'AutoTag startup boundary not found');

            function makeClassList(element) {
                const set = new Set();
                return {
                    add: (c) => set.add(c),
                    remove: (c) => set.delete(c),
                    contains: (c) => set.has(c),
                    toggle: (c, force) => {
                        if (force === undefined) { set.has(c) ? set.delete(c) : set.add(c); return; }
                        if (force) set.add(c); else set.delete(c);
                    },
                    _set: set
                };
            }

            function makeElement(tag) {
                const listeners = {};
                const element = {
                    tagName: tag,
                    children: [],
                    parentElement: null,
                    dataset: {},
                    textContent: '',
                    value: '',
                    draggable: false,
                    disabled: false,
                    classList: null,
                    addEventListener(type, fn) { (listeners[type] = listeners[type] || []).push(fn); },
                    dispatch(type, event) {
                        (listeners[type] || []).forEach((fn) => fn(Object.assign({
                            preventDefault() {}, stopPropagation() {}, target: element
                        }, event || {})));
                    },
                    appendChild(child) { element.children.push(child); child.parentElement = element; return child; },
                    insertBefore(node, reference) {
                        const current = element.children.indexOf(node);
                        if (current >= 0) element.children.splice(current, 1);
                        let at = reference ? element.children.indexOf(reference) : element.children.length;
                        if (at < 0) at = element.children.length;
                        element.children.splice(at, 0, node);
                        node.parentElement = element;
                        return node;
                    },
                    querySelector(selector) { return matchChildren(element, selector)[0] || null; },
                    querySelectorAll(selector) { return matchChildren(element, selector); },
                    getBoundingClientRect: () => ({ top: 0, height: 100 }),
                    get nextSibling() {
                        const p = element.parentElement;
                        if (!p) return null;
                        const at = p.children.indexOf(element);
                        return at >= 0 ? p.children[at + 1] || null : null;
                    }
                };
                element.classList = makeClassList(element);
                return element;
            }

            // The widget only ever queries these three selectors.
            function matchChildren(root, selector) {
                const out = [];
                const walk = (node) => {
                    (node.children || []).forEach((child) => {
                        if (matchesSelector(child, selector)) out.push(child);
                        walk(child);
                    });
                };
                walk(root);
                return out;
            }

            function matchesSelector(element, selector) {
                if (selector === '.drag-handle') return element.classList.contains('drag-handle');
                const plainSelector = '.lyrics-type-order-item-plain[data-value="unsynced-lyrics"]';
                if (selector === plainSelector) {
                    return element.classList.contains('lyrics-type-order-item-plain')
                        && element.dataset.value === 'unsynced-lyrics';
                }
                if (selector === '.lyrics-type-order-item:not(.lyrics-type-order-item-plain)') {
                    return element.classList.contains('lyrics-type-order-item')
                        && !element.classList.contains('lyrics-type-order-item-plain');
                }
                return false;
            }

            function buildUi(lrcTypeValue) {
                const list = makeElement('ul');
                ['ttml-lyrics', 'syllable-lyrics', 'lyrics', 'unsynced-lyrics'].forEach((value) => {
                    const row = makeElement('li');
                    row.dataset.value = value;
                    row.classList.add('lyrics-type-order-item');
                    if (value === 'unsynced-lyrics') row.classList.add('lyrics-type-order-item-plain');
                    const label = makeElement('span');
                    row.appendChild(label);
                    const handle = makeElement('span');
                    handle.classList.add('drag-handle');
                    row.appendChild(handle);
                    list.appendChild(row);
                });
                const orderInput = makeElement('input');
                const lrcType = makeElement('input');
                lrcType.value = lrcTypeValue;
                const typeOptions = makeElement('div');
                typeOptions.appendChild(lrcType);
                const trigger = makeElement('button');
                const menu = makeElement('div');
                const dropdown = makeElement('div');
                const document = {
                    getElementById: (id) => ({
                        lyricsTypeOrderDropdown: dropdown,
                        lyricsTypeOrderTrigger: trigger,
                        lyricsTypeOrderMenu: menu,
                        lyricsTypeOrderList: list,
                        lyricsTypeOrder: orderInput,
                        lrcType,
                        lyricsTypeOptions: typeOptions
                    })[id] || null,
                    addEventListener() {}
                };
                const context = { document, console, structuredClone, setTimeout, clearTimeout };
                vm.createContext(context);
                vm.runInContext(source.slice(0, boundary) + '\n globalThis.typeOrderTest = {setupLyricsTypeOrderDropdown, normalizeLyricsTypeOrderSetting, state, parseLyricsTypeSetting};})();', context);
                const api = context.typeOrderTest;
                globalThis.setupLyricsTypeOrderDropdown = api.setupLyricsTypeOrderDropdown;
                globalThis.normalizeLyricsTypeOrderSetting = api.normalizeLyricsTypeOrderSetting;
                globalThis.parseLyricsTypeSetting = api.parseLyricsTypeSetting;
                return {
                    get orderInput() { return orderInput; },
                    get list() { return list; },
                    row: (value) => list.children.find((child) => child.dataset.value === value),
                    dragstart: (row) => list.dispatch('dragstart', { target: row, dataTransfer: { setData() {}, setDragImage() {} } }),
                    dragover: (row, event) => list.dispatch('dragover', Object.assign({ target: row }, event)),
                    dragend: () => list.dispatch('dragend'),
                    keydown: (row, key) => list.dispatch('keydown', { key, target: row }),
                    setTypeSelection: (value) => { lrcType.value = value; },
                    changeSelection: () => typeOptions.dispatch('change', { target: lrcType }),
                    disabledValues: () => list.children
                        .filter((child) => child.classList.contains('lyrics-type-order-item-disabled'))
                        .map((child) => child.dataset.value)
                };
            }
            """ + "\nconst ui = buildUi; process.argv[2] = 'unused';\n";

        var full = harness + "\n(async () => {\n" + testScript + "\n})().catch((error) => { console.error(error); process.exit(1); });\n";
        var start = new System.Diagnostics.ProcessStartInfo("node")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(full);
        start.ArgumentList.Add(Path.Combine(directory!.FullName, "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stderr);
        return (await stdout).Trim();
    }

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
