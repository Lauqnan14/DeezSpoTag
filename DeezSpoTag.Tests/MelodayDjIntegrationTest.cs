using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Library.Dj;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// The DJ integration: a DJ is a personality applied inside Meloday's existing
/// generation, chosen per time slot, alongside Time and Mode rather than instead of
/// them.
/// </summary>
///
/// <para>Each test here pins one of the properties that make Random DJ usable, and each
/// corresponds to a way the obvious implementation goes wrong: an unseeded pick that
/// changes on retry, the DJ entering <c>mix_id</c> and minting a new playlist every
/// week, "both" resolving two different DJs and confounding the mode comparison, or
/// Random being written as a fourth strategy that picks a strategy.</para>
public sealed class MelodayDjIntegrationTest
{
    private static readonly DateOnly Monday = new(2026, 10, 5);
    private static readonly DateOnly NextTuesday = new(2026, 10, 13);

    // ------------------------------------------------- Random is a policy, not a DJ

    [Fact]
    public void RandomIsNotAStrategy()
    {
        // A strategy that picked another strategy would be a strategy with no behaviour
        // of its own, and would leave "which DJ ran" unanswerable. Random is a selection
        // step in MelodyDjResolver, and no strategy implements it.
        foreach (var strategy in Strategies())
        {
            Assert.NotEqual("random", strategy.Strategy);
        }

        Assert.Equal("random", MelodayDjSelections.Random);
    }

    [Fact]
    public void NoneSelectionUsesTheExistingNonDjPlaylistPath()
    {
        Assert.Equal("none", MelodayDjSelections.Normalize(" NONE "));
        Assert.True(MelodayDjSelections.IsNone(" NONE "));
        Assert.False(MelodayDjSelections.IsNone("random"));

        var context = Context();
        var resolution = MelodayDjResolver.Resolve("none", context, Catalog());
        Assert.Equal("none", resolution.ConfiguredDj);
        Assert.Equal(string.Empty, resolution.ResolvedDj);
        Assert.False(resolution.WasRandom);
        Assert.Equal(context.OccurrenceKey, resolution.OccurrenceKey);
        Assert.Null(resolution.FallbackDiagnostic);
    }

    [Fact]
    public void SettingsNormalisationPreservesNone()
    {
        var normalized = MelodayScheduleSlots.NormalizeLibraries(new[]
        {
            new MelodayLibrarySchedule(7, true, 4, "sonic", new List<string> { "evening" })
            {
                DjSelection = MelodayDjSelections.Normalize("none"),
            },
        });
        Assert.Equal("none", normalized[0].DjSelection);
    }

    [Fact]
    public void RandomResolvesToExactlyOneRegisteredDj()
    {
        var catalog = Catalog();
        var resolution = MelodayDjResolver.Resolve("random", Context(), catalog);

        Assert.True(resolution.WasRandom);
        Assert.NotEmpty(resolution.ResolvedDj);
        Assert.NotNull(catalog.GetById(resolution.ResolvedDj));
    }

    [Fact]
    public void ASpecificDjIsUsedWithoutAnyRandomness()
    {
        var catalog = Catalog();
        var resolution = MelodayDjResolver.Resolve("journey", Context(), catalog);

        Assert.False(resolution.WasRandom);
        Assert.Equal("journey", resolution.ResolvedDj);
        Assert.Equal("journey", resolution.ConfiguredDj);
    }

    [Fact]
    public void ANewlyRegisteredDjBecomesSelectableAndRandomEligibleWithoutCodeChanges()
    {
        // The correction to "there are three DJs". Adding one is a registration; the
        // catalogue, the settings validator and the UI option list all read from it.
        var catalog = new DjStrategyCatalog(new IMelodayDjStrategy[]
        {
            new AnchorDjStrategy(),
            new CompanionDjStrategy(),
            new JourneyDjStrategy(),
            new SunriseDjStrategy(),
        });

        Assert.Contains(catalog.GetAll(), entry => entry.Id == "sunrise");
        Assert.Contains(catalog.GetEligible(Context()), entry => entry.Id == "sunrise");
        Assert.Equal("sunrise", MelodayDjResolver.Resolve("sunrise", Context(), catalog).ResolvedDj);
    }

    // ------------------------------------------------------------- determinism

    [Fact]
    public void ARetryOfTheSameOccurrenceResolvesTheSameDj()
    {
        // The property that makes Random DJ safe at all. A run that fails part-way is
        // retried, and an unseeded pick would hand the retry a different DJ — so a
        // transient publish failure would visibly change the playlist.
        var catalog = Catalog();
        string? first = null;

        for (var attempt = 0; attempt < 25; attempt++)
        {
            var resolution = MelodayDjResolver.Resolve("random", Context(), catalog);
            first ??= resolution.ResolvedDj;
            Assert.Equal(first, resolution.ResolvedDj);
        }

        Assert.NotNull(first);
    }

    [Fact]
    public void RandomIsStableAcrossCatalogueRegistrationOrder()
    {
        // The catalogue's own order must not be a hidden input to the pick, or adding a
        // DJ would silently reshuffle which DJ every existing library gets.
        var forwards = new DjStrategyCatalog(new IMelodayDjStrategy[]
        {
            new AnchorDjStrategy(), new CompanionDjStrategy(), new JourneyDjStrategy(),
        });
        var backwards = new DjStrategyCatalog(new IMelodayDjStrategy[]
        {
            new JourneyDjStrategy(), new CompanionDjStrategy(), new AnchorDjStrategy(),
        });

        Assert.Equal(
            MelodayDjResolver.Resolve("random", Context(), forwards).ResolvedDj,
            MelodayDjResolver.Resolve("random", Context(), backwards).ResolvedDj);
    }

    [Fact]
    public void TheOccurrenceKeyVariesByDaySoALaterWeekMayChooseDifferently()
    {
        // Random must actually be random across the week, or "Random DJ" is a fixed DJ
        // with an unhelpful name. The keys differ, which is what permits it.
        var thisWeek = DjOccurrenceKey.ForOccurrence(7, "evening", Monday);
        var nextWeek = DjOccurrenceKey.ForOccurrence(7, "evening", NextTuesday);

        Assert.NotEqual(thisWeek, nextWeek);
    }

    [Fact]
    public void TheOccurrenceKeyIdentifiesTheTimeOccurrenceAndNothingElse()
    {
        var key = DjOccurrenceKey.ForOccurrence(7, "evening", Monday);

        Assert.Contains("7", key, StringComparison.Ordinal);
        Assert.Contains("evening", key, StringComparison.Ordinal);
        Assert.Contains("2026-10-05", key, StringComparison.Ordinal);
        Assert.Contains(DjOccurrenceKey.AlgorithmVersion, key, StringComparison.Ordinal);

        // No DJ and no weekday-of-identity: both would break something.
        Assert.DoesNotContain("anchor", key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("journey", key, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------- both + one DJ

    [Fact]
    public void BothModesOfOneTimeOccurrenceResolveTheSameDj()
    {
        // "Both" is two runs of one listening context. Resolving them separately would
        // confound the Direct/Sonic comparison with a second random variable, and would
        // make the two playlists incomparable for no reason.
        var catalog = Catalog();

        var direct = MelodayDjResolver.Resolve("random", Context(), catalog);
        var sonic = MelodayDjResolver.Resolve("random", Context(), catalog);

        Assert.Equal(direct.ResolvedDj, sonic.ResolvedDj);
        Assert.Equal(direct.OccurrenceKey, sonic.OccurrenceKey);
    }

    [Fact]
    public void TheSchedulerExpandsBothSoBothModesRunIndependently()
    {
        // The other half of the same requirement: the DJ being shared is only useful if
        // both modes actually run.
        Assert.Equal(new[] { "direct", "sonic" }, MelodayService.ResolveRunModes("both"));
    }

    // ------------------------------------------------------------- identity

    [Fact]
    public void TheDjIsNotPartOfPlaylistIdentity()
    {
        // The single most consequential decision in the integration. With the DJ in the
        // mix id, every change of Random would create a new mix_cache row and a new
        // playlist on the server, orphaning the old one — because nothing deletes remote
        // playlists.
        var mixId = MelodayScheduleSlots.SlotIdForMix(7, "evening", "sonic", "tuesday");

        Assert.Equal("meloday-7-evening-sonic-tuesday", mixId);
        Assert.DoesNotContain("anchor", mixId, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("journey", mixId, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("random", mixId, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlaylistNamesAreUnaffectedByTheDj()
    {
        // A Random DJ changes weekly. Naming it would rename the playlist weekly.
        var withAnyDj = MelodayScheduleSlots.PlaylistName("AltB", "Evening", "sonic", "tuesday");
        Assert.Equal("Tuesday Evening Sonic Playlist for AltB", withAnyDj);
    }

    // ------------------------------------------------- settings migration + safety

    [Fact]
    public void MissingDjSelectionMeansRandom()
    {
        // The required default has to come from the absence of the field, because that is
        // what every settings file written before DJs existed looks like.
        Assert.Equal("random", MelodayDjSelections.Normalize(null));
        Assert.Equal("random", MelodayDjSelections.Normalize(string.Empty));
        Assert.Equal("random", MelodayDjSelections.Normalize("   "));
        Assert.True(MelodayDjSelections.IsRandom(null));
    }

    [Fact]
    public void ANewLibraryDefaultsToRandomWithoutBeingAsked()
    {
        var library = new MelodayLibrarySchedule(7, true, 4, "sonic", new List<string> { "evening" });

        Assert.Equal("random", library.DjSelection);
    }

    [Fact]
    public void AnUnknownDjIsReportedRatherThanSilentlySwallowed()
    {
        // Two distinct failures are conflated if this is silent: a typo, and a DJ that
        // was removed. Either way the user must learn their setting is not in effect.
        var catalog = Catalog();
        var resolution = MelodayDjResolver.Resolve("no-such-dj", Context(), catalog);

        Assert.True(resolution.WasRandom);
        Assert.Equal("no-such-dj", resolution.ConfiguredDj);
        Assert.NotEqual("no-such-dj", resolution.ResolvedDj);
        Assert.False(string.IsNullOrWhiteSpace(resolution.FallbackDiagnostic));
        Assert.Contains(MelodayDjResolver.UnknownDjFallback, resolution.FallbackDiagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsNormalisationPreservesAnExplicitDjSelection()
    {
        // MelodyScheduleSlots.NormalizeLibraries rebuilds each record. An uninitialised
        // field here would quietly reset a library the user had pinned.
        var normalized = MelodayScheduleSlots.NormalizeLibraries(new[]
        {
            new MelodayLibrarySchedule(7, true, 4, "sonic", new List<string> { "evening" }) { DjSelection = "journey" },
        });

        Assert.Equal("journey", normalized[0].DjSelection);
    }

    [Fact]
    public void SettingsNormalisationDefaultsAMissingDjSelectionToRandom()
    {
        var normalized = MelodayScheduleSlots.NormalizeLibraries(new[]
        {
            new MelodayLibrarySchedule(8, true, 4, "sonic", new List<string> { "morning" })
            {
                DjSelection = MelodayDjSelections.Normalize(null),
            },
        });

        Assert.Equal("random", normalized[0].DjSelection);
    }

    // -------------------------------------------------------- Time as the context

    [Fact]
    public void ATimeSlotSuppliesTheDjSeedsAndSaysWhereTheyCameFrom()
    {
        var context = new MelodayDjContextBuilder().Build(
            libraryId: 7,
            libraryName: "AltB",
            slotId: "evening",
            slotName: "Evening",
            weekdayId: "tuesday",
            daypartHours: new[] { 19, 20, 21 },
            usedAllDayFallback: false,
            history: History((1, 5), (2, 3)),
            excludedTrackIds: new HashSet<long> { 2 },
            eligibleTrackIds: new long[] { 1, 3 },
            trackAnalyses: Analyses(1, 3),
            embeddedTrackIds: new HashSet<long> { 1 },
            sonicCoveragePercent: 50d,
            occurrenceKey: "key");

        // Track 2 is excluded because it was played recently, so it cannot be a seed.
        Assert.Equal(new long[] { 1 }, context.Seeds.Select(seed => seed.TrackId));
        Assert.Equal("daypart", context.ContextSource);
        Assert.False(context.UsedAllDayFallback);

        // Only track 1 had a vector, and the strategy has to be able to see that.
        Assert.True(context.Seeds[0].HasEmbedding);
    }

    [Fact]
    public void AnAllDayFallbackIsVisibleToTheDj()
    {
        // Without this the playlist would claim to be an evening selection while having
        // been built from every hour of the day, and nothing downstream could tell.
        var context = new MelodayDjContextBuilder().Build(
            libraryId: 7,
            libraryName: "AltB",
            slotId: "evening",
            slotName: "Evening",
            weekdayId: "tuesday",
            daypartHours: new[] { 19, 20, 21 },
            usedAllDayFallback: true,
            history: History((1, 5)),
            excludedTrackIds: new HashSet<long>(),
            eligibleTrackIds: new long[] { 1 },
            trackAnalyses: Analyses(1),
            embeddedTrackIds: new HashSet<long> { 1 },
            sonicCoveragePercent: 100d,
            occurrenceKey: "key");

        Assert.Equal("all-day-fallback", context.ContextSource);
        Assert.True(context.UsedAllDayFallback);
    }

    [Fact]
    public void SeedsAreTheSameEveryTimeTheSameSlotIsBuilt()
    {
        // A DJ whose seeds changed between runs would be a different DJ. Meloday's own
        // historical selection is deliberately random; the DJ's must not be.
        var builder = new MelodayDjContextBuilder();

        MelodayDjContext Build() => builder.Build(
            7, "AltB", "evening", "Evening", "tuesday", new[] { 19 },
            usedAllDayFallback: false,
            history: History((1, 5), (2, 5), (3, 5)),
            excludedTrackIds: new HashSet<long>(),
            eligibleTrackIds: new long[] { 1, 2, 3 },
            trackAnalyses: Analyses(1, 2, 3),
            embeddedTrackIds: new HashSet<long> { 1, 2, 3 },
            sonicCoveragePercent: 100d,
            occurrenceKey: "key");

        // Three tracks with identical play counts: only the id tie-break makes this
        // deterministic, and without it the order would follow dictionary iteration.
        for (var attempt = 0; attempt < 25; attempt++)
        {
            Assert.Equal(new long[] { 1, 2, 3 }, Build().Seeds.Select(seed => seed.TrackId));
        }
    }

    [Fact]
    public void NoAnalysedHistoryMeansNoDjRatherThanAnUnrelatedPlaylist()
    {
        var context = new MelodayDjContextBuilder().Build(
            7, "AltB", "evening", "Evening", "tuesday", new[] { 19 },
            usedAllDayFallback: false,
            history: History((1, 5)),
            excludedTrackIds: new HashSet<long>(),
            eligibleTrackIds: new long[] { 1 },
            trackAnalyses: new Dictionary<long, TrackAnalysisResultDto>(),
            embeddedTrackIds: new HashSet<long>(),
            sonicCoveragePercent: 0d,
            occurrenceKey: "key");

        Assert.Empty(context.Seeds);
        Assert.Empty(MelodayDjResolver.Resolve("random", context, Catalog()).ResolvedDj);
    }

    // ------------------------------------------------ the retired parallel product

    [Fact]
    public void ThereIsNoSeparateDjSchedulerOrDjProductSurface()
    {
        // A second cadence for the same playlist would mean two triggers disagreeing
        // about which DJ filled it. The scheduler is Meloday's, and was all along.
        Assert.Null(typeof(MelodayHostedService).Assembly.GetType("DeezSpoTag.Web.Services.MelodayDjHostedService"));

        foreach (var removed in new[]
        {
            "meloday-dj.js",
            "DjApiController.cs",
            "MelodayDjHostedService.cs",
            "MelodayDjService.cs",
        })
        {
            var path = Path.Join(ResolveRepoRoot(), "DeezSpoTag.Web", removed);
            var servicePath = Path.Join(ResolveRepoRoot(), "DeezSpoTag.Services", "Library", "Dj", removed);
            Assert.True(!File.Exists(path) && !File.Exists(servicePath), $"{removed} should no longer exist");
        }
    }

    [Fact]
    public void NoDjTableIsDefinedAnywhereInTheSchema()
    {
        foreach (var schemaFile in new[]
        {
            Path.Join("DeezSpoTag.Services", "Library", "Schema", "library.sql"),
            Path.Join("DeezSpoTag.Services", "Library", "LibraryDbService.cs"),
        })
        {
            var text = File.ReadAllText(Path.Join(ResolveRepoRoot(), schemaFile));
            foreach (var table in new[]
            {
                "dj_definition", "dj_seed_spec", "dj_schedule", "dj_run_state",
                "dj_generation", "dj_generation_item",
            })
            {
                Assert.DoesNotContain($"CREATE TABLE IF NOT EXISTS {table}", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void DjProvenanceIsStoredOnMelodayOwnedTables()
    {
        foreach (var schemaFile in new[]
        {
            Path.Join("DeezSpoTag.Services", "Library", "Schema", "library.sql"),
            Path.Join("DeezSpoTag.Services", "Library", "LibraryDbService.cs"),
        })
        {
            var text = File.ReadAllText(Path.Join(ResolveRepoRoot(), schemaFile));
            Assert.Contains("CREATE TABLE IF NOT EXISTS meloday_generation ", text, StringComparison.Ordinal);
            Assert.Contains("CREATE TABLE IF NOT EXISTS meloday_generation_item ", text, StringComparison.Ordinal);

            // Both the choice and the roll, because only one of them is a decision.
            Assert.Contains("configured_dj TEXT NOT NULL", text, StringComparison.Ordinal);
            Assert.Contains("resolved_dj TEXT NOT NULL", text, StringComparison.Ordinal);
            Assert.Contains("was_random INTEGER NOT NULL", text, StringComparison.Ordinal);
            Assert.Contains("context_source TEXT NOT NULL", text, StringComparison.Ordinal);
        }
    }

    // ----------------------------------------------------- the DJ owns the order

    [Fact]
    public void TheModeStillOwnsCandidateQualificationAndTheDjOwnsTheOrder()
    {
        var source = File.ReadAllText(Path.Join(
            ResolveRepoRoot(), "DeezSpoTag.Web", "Services", "MelodayService.cs"));

        // Mode builds the pool for the DJ, from the same selection code as before.
        Assert.Contains("TryBuildDjPlaylistAsync(mode, isDirect, context, cancellationToken)", source, StringComparison.Ordinal);
        Assert.Contains("BuildVibeDrivenTrackSelectionAsync(", source, StringComparison.Ordinal);

        // The mode's ordering is reachable only from the non-DJ branch, so a DJ's arc
        // cannot be handed straight back to a greedy similarity walk.
        var djBranch = source.IndexOf("if (djOutcome is not null)", StringComparison.Ordinal);
        var legacyBranch = source.IndexOf("OrderTracksDirect(selectedTrackIds", StringComparison.Ordinal);
        Assert.True(djBranch > 0 && legacyBranch > djBranch, "legacy ordering must sit in the non-DJ branch");

        var djTry = source.IndexOf("private async Task<DjPlaylistOutcome?> TryBuildDjPlaylistAsync(", StringComparison.Ordinal);
        var djTryEnd = source.IndexOf("Builds what a DJ knows about this time slot", djTry, StringComparison.Ordinal);
        Assert.True(djTry > 0 && djTryEnd > djTry, "expected the DJ build to precede the context builder");
        Assert.DoesNotContain("OrderTracksDirect", source[djTry..djTryEnd], StringComparison.Ordinal);
        Assert.DoesNotContain("OrderTracksSonicAsync", source[djTry..djTryEnd], StringComparison.Ordinal);
    }

    [Fact]
    public void TheLegacyPathIsStillPresentRatherThanReplaced()
    {
        // Both mode orderings survive untouched, and the selection builders keep their
        // original signatures so every existing guardrail still holds.
        var source = File.ReadAllText(Path.Join(
            ResolveRepoRoot(), "DeezSpoTag.Web", "Services", "MelodayService.cs"));

        Assert.Contains("private static List<long> OrderTracksDirect(", source, StringComparison.Ordinal);
        Assert.Contains("private Task<IReadOnlyList<long>> OrderTracksSonicAsync(", source, StringComparison.Ordinal);
        Assert.Contains("private async Task<List<long>> BuildDirectTrackSelectionAsync(", source, StringComparison.Ordinal);
        Assert.Contains("private async Task<List<long>> BuildSonicTrackSelectionAsync(", source, StringComparison.Ordinal);

        // The pool limit defaults to playlist size, so a library with no DJ behaves
        // exactly as it did before any of this existed.
        Assert.Contains("int trackLimit = 0)", source, StringComparison.Ordinal);
        Assert.Contains("trackLimit > 0 ? trackLimit : context.Options.MaxTracks", source, StringComparison.Ordinal);
        Assert.Contains("trackLimit > 0 ? trackLimit : options.MaxTracks", source, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- the UI surface

    [Fact]
    public void TheDjSelectorLivesOnTheLibraryCardAndItsOptionsComeFromTheServer()
    {
        var script = File.ReadAllText(Path.Join(ResolveRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "meloday.js"));

        Assert.Contains("data-meloday-library-dj", script, StringComparison.Ordinal);
        Assert.Contains("'Random DJ'", script, StringComparison.Ordinal);
        Assert.Contains("/api/meloday/settings/djs", script, StringComparison.Ordinal);

        // The option list is built from melodayState.djs, which came from the server.
        // A hard-coded list of DJs here is exactly what the plan forbids.
        Assert.DoesNotContain("melodayDjOptions = [", script, StringComparison.Ordinal);
        Assert.DoesNotContain("'anchor', 'companion', 'journey'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("meloday-library-dj-label", script, StringComparison.Ordinal);
        Assert.Contains("djSelect.appendChild(noneOption)", script, StringComparison.Ordinal);
        Assert.Contains("'None'", script, StringComparison.Ordinal);
        Assert.Contains("normal Meloday track selection", script, StringComparison.Ordinal);
        Assert.Contains("DJ ${dj.displayName}", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMaximumControlNoLongerClaimsToCapPlaylists()
    {
        // It never capped playlists: it only limits how many slot checkboxes may be
        // ticked, and "Both" alone can exceed it twofold. The label was the lie.
        var script = File.ReadAllText(Path.Join(ResolveRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "meloday.js"));

        Assert.Contains("Maximum time slots", script, StringComparison.Ordinal);
        Assert.DoesNotContain("'Maximum playlists'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendSyncModeIsWarnedAboutRatherThanHidden()
    {
        // Under append, last week's tracks are kept, so the playlist becomes a union of
        // several DJs. That is a real consequence and the user has to be told.
        var script = File.ReadAllText(Path.Join(ResolveRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "meloday.js"));

        Assert.Contains("Sync mode is set to add only", script, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static IMelodayDjStrategy[] Strategies() =>
    [
        new AnchorDjStrategy(), new CompanionDjStrategy(), new JourneyDjStrategy(),
    ];

    private static DjStrategyCatalog Catalog() => new(Strategies());

    private static MelodayDjContext Context() => new MelodayDjContextBuilder().Build(
        libraryId: 7,
        libraryName: "AltB",
        slotId: "evening",
        slotName: "Evening",
        weekdayId: "tuesday",
        daypartHours: new[] { 19, 20, 21 },
        usedAllDayFallback: false,
        history: History((1, 9), (2, 4), (3, 2)),
        excludedTrackIds: new HashSet<long>(),
        eligibleTrackIds: new long[] { 1, 2, 3, 4, 5 },
        trackAnalyses: Analyses(1, 2, 3),
        embeddedTrackIds: new HashSet<long> { 1, 2, 3 },
        sonicCoveragePercent: 60d,
        occurrenceKey: DjOccurrenceKey.ForOccurrence(7, "evening", Monday));

    private static IReadOnlyList<PlayHistoryEntryDto> History(params (long TrackId, int Count)[] entries)
        => entries
            .Select(entry => new PlayHistoryEntryDto(entry.TrackId, DateTimeOffset.UtcNow, entry.Count))
            .ToList();

    private static Dictionary<long, TrackAnalysisResultDto> Analyses(params long[] trackIds)
        => trackIds.ToDictionary(trackId => trackId, Analysis);

    private static TrackAnalysisResultDto Analysis(long trackId) => new(
        TrackId: trackId,
        LibraryId: 7,
        Status: "complete",
        Energy: 0.5,
        Rms: null,
        ZeroCrossing: null,
        SpectralCentroid: null,
        Bpm: 120,
        AnalyzedAtUtc: DateTimeOffset.UtcNow,
        Error: null,
        AnalysisMode: "enhanced",
        AnalysisVersion: "test",
        MoodTags: new[] { "calm" },
        MoodHappy: 0.5,
        MoodSad: 0.5,
        MoodRelaxed: 0.5,
        MoodAggressive: 0.5,
        MoodParty: 0.5,
        MoodAcoustic: 0.5,
        MoodElectronic: 0.5,
        Valence: 0.5,
        Arousal: 0.5,
        BeatsCount: null,
        Key: null,
        KeyScale: null,
        KeyStrength: null,
        Loudness: null,
        DynamicRange: null,
        Danceability: 0.5,
        Instrumentalness: 0.5,
        Acousticness: 0.5,
        Speechiness: 0.5,
        DanceabilityMl: 0.5,
        EssentiaGenres: null,
        LastfmTags: null,
        Approachability: null,
        Engagement: null,
        VoiceInstrumental: null,
        TonalAtonal: null,
        ValenceMl: 0.5,
        ArousalMl: 0.5,
        DynamicComplexity: null,
        LoudnessMl: null);

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Join(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test output path.");
    }

    /// <summary>A fourth DJ, used only to prove the catalogue is not three-deep.</summary>
    private sealed class SunriseDjStrategy : IMelodayDjStrategy
    {
        public string Strategy => "sunrise";

        public DjStrategyResult BuildPlaylist(DjStrategyRequest request)
            => new()
            {
                Candidates = request.CandidateTrackIds.Take(request.TrackCount)
                    .Select(trackId => new DjCandidate(trackId, 0d, DjItemReasons.Seed))
                    .ToList(),
            };
    }
}
