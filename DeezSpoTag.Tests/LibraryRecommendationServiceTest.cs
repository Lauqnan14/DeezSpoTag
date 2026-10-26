using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Download.Identity;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LibraryRecommendationServiceTest
{
    [Theory]
    [InlineData("no_data", true)]
    [InlineData("not_found", true)]
    [InlineData("quota", false)]
    [InlineData("network", false)]
    [InlineData("cancel", false)]
    public async Task StoredSeedLookupDistinguishesMissingRecordingsFromProviderFailures(string kind, bool missing)
    {
        var seed = new LibraryRecommendationSeedTrackDto(42, "Song", "Artist", "Album", 180000, "ISRC", "12345");
        Exception failure = kind switch
        {
            "no_data" => new InvalidOperationException("API Error: 800 - no data"),
            "not_found" => new System.Net.Http.HttpRequestException("not found", null, System.Net.HttpStatusCode.NotFound),
            "quota" => new InvalidOperationException("API Error: 4 - quota exceeded"),
            "network" => new System.Net.Http.HttpRequestException("unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable),
            _ => new OperationCanceledException()
        };
        Func<string, CancellationToken, Task<DeezSpoTag.Core.Models.Deezer.ApiTrack>> fetch = (_, _) => Task.FromException<DeezSpoTag.Core.Models.Deezer.ApiTrack>(failure);
        var method = typeof(LibraryRecommendationService).GetMethod("ValidateStoredDeezerSeedAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var task = (Task<string>)method.Invoke(null, [seed, fetch, CancellationToken.None])!;
        if (missing) Assert.Equal("", await task);
        else Assert.Same(failure, await Record.ExceptionAsync(async () => await task));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StoredDailySeedIdsMustIdentifyTheFoldersActualRecording(bool matches)
    {
        var seed = new LibraryRecommendationSeedTrackDto(42, "Like Dat", "Darkoo", "Album", 180000, "GBMA22510404", "134522904");
        var candidate = new DeezSpoTag.Core.Models.Deezer.ApiTrack { Id = seed.DeezerTrackId!,
            Title = matches ? "Like Dat" : "You Don't Know What Love Is", Duration = matches ? 180 : 391,
            Isrc = matches ? seed.Isrc! : "USFI85600002",
            Artist = new DeezSpoTag.Core.Models.Deezer.ApiArtist { Name = matches ? "Darkoo" : "Sonny Rollins" } };
        var calls = 0;
        Func<string, CancellationToken, Task<DeezSpoTag.Core.Models.Deezer.ApiTrack>> fetch = (id, token) =>
        { Assert.Equal(seed.DeezerTrackId, id); calls++; return Task.FromResult(candidate); };
        var method = typeof(LibraryRecommendationService).GetMethod("ValidateStoredDeezerSeedAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = await (Task<string>)method.Invoke(null, [seed, fetch, CancellationToken.None])!;
        Assert.Equal(matches ? seed.DeezerTrackId : "", result);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("daily-rotation:l1:f2", 3)]
    [InlineData("daily-rotation:l1:f2:extra", 2)]
    [InlineData("daily-rotation:l1:f999", 2)]
    [InlineData("not-a-station", 2)]
    public void DailyScopeNeverFallsBackToAnotherFolder(string stationId, long folderId)
    {
        var folders = new[] {
            new FolderDto(2, "/music", "Music", true, 1, "Library", "flac", null, false, false, null, null),
            new FolderDto(3, "/gold", "Gold", true, 1, "Library", "flac", null, false, false, null, null) };
        var method = typeof(LibraryRecommendationService).GetMethod("ResolveScope", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Null(method.Invoke(null, [1L, folders, stationId, folderId]));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public void DailyAllocationSurvivesDuplicatesAndRejections(int total)
    {
        var quotas = new Dictionary<string, int> { ["shazam"] = total / 2, ["deezer"] = total / 2 };
        var tracks = CreateTracks("shazam", total + 1, 1).Select(t => t with { RecommendationOrigin = "shazam" })
            .Concat(CreateTracks("deezer", total + 1, 1).Select(t => t with { RecommendationOrigin = "deezer" })).ToList();
        var excluded = new HashSet<string> { "1" };
        var result = LibraryRecommendationService.BuildAllocatedDailySelection(tracks, quotas, total, new DateOnly(2026, 10, 10), excluded);
        Assert.Equal(total, result.Count);
        Assert.Equal(total / 2, result.Count(t => t.RecommendationOrigin == "shazam"));
        Assert.Equal(total / 2, result.Count(t => t.RecommendationOrigin == "deezer"));
        Assert.Equal(total, result.Select(t => t.Id).Distinct().Count());
        Assert.DoesNotContain(result, t => excluded.Contains(t.Id));
        Assert.Equal(Enumerable.Range(1, total), result.Select(t => t.TrackPosition));
    }

    private static Task<List<RecommendationTrackDto>> CollectDailySourcesAsync(
        DeezSpoTag.Core.Models.Settings.DailyRecommendationSettings configuration,
        Func<string,int,CancellationToken,Task<IReadOnlyList<RecommendationTrackDto>>> fetch,CancellationToken token)
        => LibraryRecommendationService.CollectDailySeedBatchesAsync(
            [new(1,"Seed","Artist","Album",180000,null,"1")],configuration.Allocations(),new(2026,10,24),[],
            new(),new(),(source,batch,count,ct)=>fetch(source,count,ct),_=>Task.CompletedTask,token);

    [Fact]
    public void DailyAllocationCannotReplaceAnUnavailableSource()
    {
        var tracks = CreateTracks("deezer", 100, 1).Select(t => t with { RecommendationOrigin = "deezer" }).ToList();
        var quotas = new Dictionary<string, int> { ["shazam"] = 25, ["deezer"] = 25 };
        var result = LibraryRecommendationService.BuildAllocatedDailySelection(tracks, quotas, 50, new DateOnly(2026, 10, 10));
        Assert.Equal(25, result.Count);
        Assert.False(LibraryRecommendationService.HasCompleteDailyAllocation(result, quotas));
    }

    [Fact]
    public void FourSourceAndSingleSourceAllocationsAreExact()
    {
        var quotas = new Dictionary<string, int> { ["shazam"] = 20, ["deezer"] = 20, ["spotify"] = 30, ["tidal"] = 30 };
        var tracks = quotas.SelectMany((pair, index) => CreateTracks(pair.Key, 100, index * 1000 + 1).Select(t => t with { RecommendationOrigin = pair.Key })).ToList();
        var result = LibraryRecommendationService.BuildAllocatedDailySelection(tracks, quotas, 100, new DateOnly(2026, 10, 10));
        Assert.True(LibraryRecommendationService.HasCompleteDailyAllocation(result, quotas));
        foreach (var pair in quotas) Assert.Equal(pair.Value, result.Count(t => t.RecommendationOrigin == pair.Key));
        var single = LibraryRecommendationService.BuildAllocatedDailySelection(tracks, new Dictionary<string, int> { ["tidal"] = 100 }, 100, new DateOnly(2026, 10, 10));
        Assert.Equal(100, single.Count);
        Assert.All(single, t => Assert.Equal("tidal", t.RecommendationOrigin));
    }

    [Fact]
    public async Task DailyCollectionNeverCallsDisabledSources()
    {
        var calls = new List<string>();
        var configuration = new DeezSpoTag.Core.Models.Settings.DailyRecommendationSettings();
        await CollectDailySourcesAsync(configuration, (source, count, token) =>
        {
            calls.Add(source);
            Assert.Equal(60, count);
            return Task.FromResult<IReadOnlyList<RecommendationTrackDto>>(Array.Empty<RecommendationTrackDto>());
        }, CancellationToken.None);
        Assert.Equal(new[] { "deezer", "spotify", "tidal" }, calls);
    }

    [Fact]
    public async Task DailyCollectionNeverCallsUnavailableShazamEvenWithStaleSettings()
    {
        var calls = new List<string>();
        var configuration = new DeezSpoTag.Core.Models.Settings.DailyRecommendationSettings
        {
            ShazamEnabled = true, ShazamTrackCount = 25
        };
        await CollectDailySourcesAsync(configuration, (source, _, _) =>
        {
            calls.Add(source);
            return Task.FromResult<IReadOnlyList<RecommendationTrackDto>>(Array.Empty<RecommendationTrackDto>());
        }, CancellationToken.None);
        Assert.Equal(new[] { "deezer", "spotify", "tidal" }, calls);
    }
    [Fact]
    public void SharedDailyRecordingRetainsAllSeedAndProviderAttribution()
    {
        var rows = new[] { "deezer", "spotify", "tidal" }.SelectMany(provider => new[] { 1L, 2L }.Select(seed =>
            CreateTracks(provider, 1, 101)[0] with { RecommendationOrigin = provider,
                Provenance = new[] { new RecommendationProvenanceDto(provider, "provider101", seed, "seed" + seed, 3) } })).ToList();
        var merged = LibraryRecommendationService.MergeDailyRecommendationProvenance(rows);
        var track = Assert.Single(merged);
        Assert.Equal(6, track.Provenance!.Count);
        var selected = LibraryRecommendationService.BuildAllocatedDailySelection(merged,
            new Dictionary<string, int> { ["tidal"] = 1 }, 20, new DateOnly(2026, 10, 11));
        Assert.Equal("tidal", Assert.Single(selected).RecommendationOrigin);
        Assert.Equal(6, selected[0].Provenance!.Count);
    }

    [Fact]
    public async Task AllDisabledMakesNoDailyProviderRequests()
    {
        var paused = new DeezSpoTag.Core.Models.Settings.DailyRecommendationSettings { ShazamEnabled = false, ShazamTrackCount = 0,
            DeezerEnabled = false, DeezerTrackCount = 0, SpotifyEnabled = false, SpotifyTrackCount = 0, TidalEnabled = false, TidalTrackCount = 0 };
        var rows = await CollectDailySourcesAsync(paused,
            (_, _, _) => throw new InvalidOperationException("Disabled source called"), CancellationToken.None);
        Assert.Empty(rows);
    }

    [Theory]
    [InlineData(DeezSpoTag.Core.Models.Settings.DailyRecommendationAiFilterMode.Allow, 6)]
    [InlineData(DeezSpoTag.Core.Models.Settings.DailyRecommendationAiFilterMode.BlockAiArtistsAndFullyGenerated, 5)]
    [InlineData(DeezSpoTag.Core.Models.Settings.DailyRecommendationAiFilterMode.Strict, 4)]
    public async Task DailyAdmissionMergesTwoSeedsAcrossProvidersAndAppliesExplicitAiPolicy(DeezSpoTag.Core.Models.Settings.DailyRecommendationAiFilterMode mode, int expectedCount)
    {
        var rows = new List<RecommendationTrackDto>();
        var sources = new Dictionary<string, string[]> { ["deezer"] = new[] { "B", "C", "D" }, ["spotify"] = new[] { "B", "E", "F" }, ["tidal"] = new[] { "B", "C", "G" } };
        foreach (var source in sources)
            foreach (var seed in new[] { 1L, 2L })
                for (var rank = 0; rank < source.Value.Length; rank++)
                {
                    var title = source.Value[rank];
                    var status = title == "F" ? RecommendationAiStatus.AiArtist : title == "E" ? RecommendationAiStatus.PartialAiInvolvement : RecommendationAiStatus.Unknown;
                    var candidate = new DailyRecommendationCandidateDto(source.Key, "native" + title, seed, "seed" + seed, rank + 1,
                        title, "Artist " + title, "Album", 180000, null,
                        new[] { new RecommendationAiEvidenceDto(status, "fixture", "explicit_disclosure", "verified_provider_disclosure", DateTimeOffset.UnixEpoch) });
                    var row = await LibraryRecommendationService.ResolveDailyDeezerCandidateCoreAsync(candidate, mode,
                        (track, _) => Task.FromResult<string?>((2000 + track.Title[0]).ToString()), _ => { }, CancellationToken.None);
                    if (row is not null) rows.Add(row);
                }
        var merged = LibraryRecommendationService.MergeDailyRecommendationProvenance(rows);
        Assert.Equal(expectedCount, merged.Count);
        Assert.Equal(6, merged.Single(t => t.Title == "B").Provenance!.Count);
        Assert.Equal(4, merged.Single(t => t.Title == "C").Provenance!.Count);
        Assert.All(merged, t => Assert.Equal("deezer", t.Source));
        if (mode != DeezSpoTag.Core.Models.Settings.DailyRecommendationAiFilterMode.Allow) Assert.DoesNotContain(merged, t => t.Title == "F");
        if (mode == DeezSpoTag.Core.Models.Settings.DailyRecommendationAiFilterMode.Strict) Assert.DoesNotContain(merged, t => t.Title == "E");
    }

    [Fact]
    public async Task DailyAdmissionRejectsInvalidMetadataAndUnmatchedRecording()
    {
        var rejected = new List<string>();
        var candidate = new DailyRecommendationCandidateDto("spotify", "native", 1, "seed", 1, "", "Artist", "Album", 180000, null);
        Assert.Null(await LibraryRecommendationService.ResolveDailyDeezerCandidateCoreAsync(candidate,
            DeezSpoTag.Core.Models.Settings.DailyRecommendationAiFilterMode.Strict,
            (_, _) => throw new InvalidOperationException("Invalid metadata must not be mapped"), rejected.Add, CancellationToken.None));
        Assert.Equal("INVALID_METADATA", Assert.Single(rejected));
        rejected.Clear();
        Assert.Null(await LibraryRecommendationService.ResolveDailyDeezerCandidateCoreAsync(candidate with { Title = "Song" },
            DeezSpoTag.Core.Models.Settings.DailyRecommendationAiFilterMode.Strict,
            (_, _) => Task.FromResult<string?>(null), rejected.Add, CancellationToken.None));
        Assert.Equal("NO_DEEZER_MATCH", Assert.Single(rejected));
    }

    [Fact]
    public void ShazamSharedRecommendationPreservesEachSeedRank()
    {
        var track = CreateTracks("deezer", 1, 201)[0];
        var cache = new Dictionary<long, ShazamTrackCacheDto>
        {
            [1] = new(1, "matched", "shazamseed1", "Seed 1", "Artist", "", new[] { track }, DateTimeOffset.UtcNow, null),
            [2] = new(2, "matched", "shazamseed2", "Seed 2", "Artist", "", new[] { track }, DateTimeOffset.UtcNow, null)
        };
        var method = typeof(LibraryRecommendationService).GetMethod("BuildRecommendationsFromShazamCache", BindingFlags.NonPublic | BindingFlags.Static)!;
        var rows = (List<RecommendationTrackDto>)method.Invoke(null, [new long[] { 1, 2 }, cache, CancellationToken.None, 20])!;
        var selected = Assert.Single(rows);
        Assert.Equal(new long[] { 1, 2 }, selected.Provenance!.Select(p => p.LocalSeedTrackId).OrderBy(n => n));
        Assert.All(selected.Provenance!, p => { Assert.Equal("shazam", p.Provider); Assert.Equal(1, p.ProviderRank); });
    }

    [Fact]
    public void DeezerAccumulatorRetainsDuplicateDiscoveryProvenance()
    {
        var type = typeof(LibraryRecommendationService).GetNestedType("RecommendationAccumulator", BindingFlags.NonPublic)!;
        var accumulator = Activator.CreateInstance(type, 20)!;
        var method = typeof(LibraryRecommendationService).GetMethod("TryAddRecommendationTrack", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var seed in new long[] { 1, 2 })
        {
            var track = CreateTracks("deezer", 1, 201)[0] with { Provenance = new[] { new RecommendationProvenanceDto("deezer", "201", seed, "seed" + seed, 1) } };
            method.Invoke(null, [track, new HashSet<string>(), accumulator]);
        }
        var rows = (List<RecommendationTrackDto>)type.GetProperty("DestinationTracks")!.GetValue(accumulator)!;
        Assert.Equal(2, Assert.Single(rows).Provenance!.Count);
    }

    [Theory]
    [InlineData(20, 0, 0)]
    [InlineData(0, 20, 0)]
    [InlineData(0, 0, 20)]
    [InlineData(10, 10, 0)]
    [InlineData(10, 0, 10)]
    [InlineData(0, 10, 10)]
    [InlineData(6, 6, 8)]
    public async Task EnabledDailySourceCombinationsRetainIndependentAllocations(int deezer, int spotify, int tidal)
    {
        var config = new DeezSpoTag.Core.Models.Settings.DailyRecommendationSettings { TrackCount = 20, ShazamEnabled = false,
            ShazamTrackCount = 0, DeezerEnabled = deezer > 0, DeezerTrackCount = deezer,
            SpotifyEnabled = spotify > 0, SpotifyTrackCount = spotify, TidalEnabled = tidal > 0, TidalTrackCount = tidal };
        var raw = await CollectDailySourcesAsync(config, (source, count, _) =>
            Task.FromResult<IReadOnlyList<RecommendationTrackDto>>(CreateTracks(source, count, source == "deezer" ? 1 : source == "spotify" ? 1001 : 2001)
                .Select(t => t with { RecommendationOrigin = source }).ToList()), CancellationToken.None);
        var selected = LibraryRecommendationService.BuildAllocatedDailySelection(raw, config.Allocations(), 20, new DateOnly(2026, 10, 11));
        Assert.Equal(20, selected.Count);
        Assert.Equal(deezer, selected.Count(t => t.RecommendationOrigin == "deezer"));
        Assert.Equal(spotify, selected.Count(t => t.RecommendationOrigin == "spotify"));
        Assert.Equal(tidal, selected.Count(t => t.RecommendationOrigin == "tidal"));
    }

    private static readonly MethodInfo MergeRotatingMethod = typeof(LibraryRecommendationService).GetMethod(
        "MergeRotating",
        BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo TopUpRecommendationSelectionMethod = typeof(LibraryRecommendationService).GetMethod(
        "TopUpRecommendationSelection",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo ResolveFolderContentTypeMethod = typeof(LibraryRecommendationService).GetMethod(
        "ResolveFolderContentType",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo BuildVisibleDailySelectionMethod = typeof(LibraryRecommendationService).GetMethod(
        "BuildVisibleDailySelection",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo BuildRecommendationArtworkAssignmentsMethod = typeof(LibraryRecommendationService).GetMethod(
        "BuildRecommendationArtworkAssignments",
        BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo BuildRecommendationUnavailableMessageMethod = typeof(LibraryRecommendationService).GetMethod(
        "BuildRecommendationUnavailableMessage",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo CreateUnavailableRecommendationDetailMethod = typeof(LibraryRecommendationService).GetMethod(
        "CreateUnavailableRecommendationDetail",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo FilterDerivativeRecommendationCandidatesMethod = typeof(LibraryRecommendationService).GetMethod(
        "FilterDerivativeRecommendationCandidates",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly string[] GenerationQueuedReasonCodes = ["generation_queued"];

    [Fact]
    public async System.Threading.Tasks.Task PreviousDailySelectionIsExpiredInsteadOfServedForToday()
    {
        var fixture = new RecommendationCacheServingTest();
        await fixture.InitializeAsync();
        try
        {
            var savedDay = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);
            var id = $"daily-rotation:l{fixture.Folder.LibraryId}:f{fixture.Folder.Id}";
            var tracks = Enumerable.Range(1, 80).Select(i => new RecommendationTrackDto(i.ToString(), $"Track {i}", 180, "", i,
                new((i % 8).ToString(), $"Artist {i % 8}"), new("1", "Album", ""))).ToList();
            var generated = new DateTimeOffset(savedDay.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
            await fixture.Repository.UpsertPlaylistTrackCandidateCacheAsync("recommendations-daily-pool", id, $"v1:{savedDay:yyyyMMdd}",
                System.Text.Json.JsonSerializer.Serialize(new { GeneratedAtUtc = generated, Tracks = tracks }), 0, null, null, true);
            var response = await fixture.Service().GetRecommendationsAsync(fixture.Folder.LibraryId!.Value, id);
            Assert.Equal("generating", response!.Status);
            Assert.Empty(response.Tracks);
            Assert.Equal(savedDay.AddDays(1), response.DailyDateLocal);
            Assert.Equal(0, response.Station.TrackCount);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public void MergeRotating_UsesRecommendationPoolLimit()
    {
        var deezerTracks = CreateTracks("deezer", 240, 1);
        var shazamTracks = CreateTracks("shazam", 240, 10001);

        var result = (List<RecommendationTrackDto>)MergeRotatingMethod.Invoke(
            null,
            [deezerTracks, shazamTracks, 150, new DateOnly(2026, 4, 12)])!;

        Assert.Equal(150, result.Count);
        Assert.Equal(Enumerable.Range(1, 150), result.Select(track => track.TrackPosition));
    }

    [Fact]
    public void TopUpRecommendationSelection_FillsToRequestedLimit()
    {
        var primarySelection = CreateTracks("primary", 34, 1);
        var fallbackCandidates = CreateTracks("fallback", 180, 1001);

        var result = (List<RecommendationTrackDto>)TopUpRecommendationSelectionMethod.Invoke(
            null,
            [primarySelection, fallbackCandidates, 50, new DateOnly(2026, 4, 12)])!;

        Assert.Equal(50, result.Count);
        Assert.Equal(50, result.Select(track => track.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Enumerable.Range(1, 50), result.Select(track => track.TrackPosition));
    }

    [Fact]
    public void ResolveFolderContentType_TreatsLegacyZeroAsMusicByDefault()
    {
        var folder = new FolderDto(
            Id: 1,
            RootPath: "/music/library",
            DisplayName: "Main Music",
            Enabled: true,
            LibraryId: 1,
            LibraryName: "Library",
            DesiredQuality: "0",
            AutoTagProfileId: null,
            AutoTagEnabled: false,
            ConvertEnabled: false,
            ConvertFormat: null,
            ConvertBitrate: null);

        var contentType = (string)ResolveFolderContentTypeMethod.Invoke(null, [folder])!;
        Assert.Equal("music", contentType);
    }

    [Fact]
    public void ResolveFolderContentType_TreatsLegacyAtmosRankAsAtmos()
    {
        var folder = new FolderDto(
            Id: 1,
            RootPath: "/music/atmos",
            DisplayName: "Atmos",
            Enabled: true,
            LibraryId: 1,
            LibraryName: "Library",
            DesiredQuality: "5",
            AutoTagProfileId: null,
            AutoTagEnabled: false,
            ConvertEnabled: false,
            ConvertFormat: null,
            ConvertBitrate: null);

        var contentType = (string)ResolveFolderContentTypeMethod.Invoke(null, [folder])!;
        Assert.Equal("atmos", contentType);
    }

    [Fact]
    public void BuildVisibleDailySelection_BackfillsIgnoredTracks()
    {
        var day = new DateOnly(2026, 4, 12);
        var tracks = CreateTracks("daily", 120, 1);
        var baseline = (List<RecommendationTrackDto>)BuildVisibleDailySelectionMethod.Invoke(
            null,
            [tracks, new HashSet<string>(StringComparer.Ordinal), 50, day])!;
        var ignored = new HashSet<string>(
            baseline.Take(10).Select(track => track.Id),
            StringComparer.Ordinal);

        var result = (List<RecommendationTrackDto>)BuildVisibleDailySelectionMethod.Invoke(
            null,
            [tracks, ignored, 50, day])!;

        Assert.Equal(50, result.Count);
        Assert.DoesNotContain(result, track => ignored.Contains(track.Id));
        Assert.Equal(Enumerable.Range(1, 50), result.Select(track => track.TrackPosition));
    }

    [Fact]
    public void DailyRefillPreservesEligibleSongsAndReplacesExcludedSong()
    {
        var tracks = CreateTracks("daily", 50, 1);
        var replacement = CreateTracks("refill", 1, 51);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { tracks[0].Id };
        var merged = LibraryRecommendationService.MergeDailyRecommendationProvenance(
            tracks.Concat(replacement).Where(t => !excluded.Contains(t.Id)));
        Assert.Equal(50, merged.Count);
        Assert.DoesNotContain(merged, t => excluded.Contains(t.Id));
        Assert.Equal(tracks[1].Id, merged[0].Id);
        Assert.Contains(merged, t => t.Id == replacement[0].Id);
    }

    [Fact]
    public void FilterDerivativeRecommendationCandidates_RemovesInstrumentalVariants()
    {
        var tracks = new List<RecommendationTrackDto>
        {
            CreateTrack("1", "Real Song", "Main Artist", "Main Album"),
            CreateTrack("2", "Real Song - Instrumental", "Main Artist", "Main Album"),
            CreateTrack("3", "Real Song", "Karaoke Band", "Main Album")
        };

        var result = (List<RecommendationTrackDto>)FilterDerivativeRecommendationCandidatesMethod.Invoke(
            null,
            [tracks])!;

        Assert.Single(result);
        Assert.Equal("1", result[0].Id);
        Assert.Equal(1, result[0].TrackPosition);
    }

    [Fact]
    public void BuildRecommendationArtworkAssignments_IsStableAcrossSameLocalDay()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-recommendations-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(webRoot, "images", "recommendations", "set-a"));
            Directory.CreateDirectory(Path.Combine(webRoot, "images", "recommendations", "set-b"));
            File.WriteAllText(Path.Combine(webRoot, "images", "recommendations", "set-a", "sunday.jpg"), string.Empty);
            File.WriteAllText(Path.Combine(webRoot, "images", "recommendations", "set-b", "sunday.jpg"), string.Empty);
            var service = CreateRecommendationService(webRoot);
            var folders = new List<FolderDto>
            {
                CreateFolder(1, "Main Music"),
                CreateFolder(2, "Second Music")
            };

            var early = (Dictionary<string, string>)BuildRecommendationArtworkAssignmentsMethod.Invoke(
                service,
                [folders, new DateTimeOffset(2026, 4, 12, 1, 0, 0, TimeSpan.Zero)])!;
            var late = (Dictionary<string, string>)BuildRecommendationArtworkAssignmentsMethod.Invoke(
                service,
                [folders, new DateTimeOffset(2026, 4, 12, 23, 0, 0, TimeSpan.Zero)])!;

            Assert.Equal(early, late);
        }
        finally
        {
            if (Directory.Exists(webRoot))
            {
                Directory.Delete(webRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void BuildRecommendationUnavailableMessage_GenerationQueuedMessageIsExplicit()
    {
        var message = (string)BuildRecommendationUnavailableMessageMethod.Invoke(
            null,
            [new List<string> { "generation_queued" }])!;

        Assert.Equal("Recommendation generation is running in the background. Refresh this tracklist shortly.", message);
    }

    [Fact]
    public void CreateUnavailableRecommendationDetail_UsesGeneratingStatusWhenQueued()
    {
        var scopeType = typeof(LibraryRecommendationService).GetNestedType("RecommendationScope", BindingFlags.NonPublic)!;
        var scope = Activator.CreateInstance(scopeType, 1L, 2L, "Main", "daily-rotation:l1:f2", "l1:f2");
        var detail = (RecommendationDetailDto)CreateUnavailableRecommendationDetailMethod.Invoke(
            null,
            [scope!, "/images/recommendations/V1/Sunday.jpg", new DateOnly(2026, 5, 24), GenerationQueuedReasonCodes])!;

        Assert.Equal("generating", detail.Status);
        Assert.Equal("generating", detail.Station.Status);
    }

    private static List<RecommendationTrackDto> CreateTracks(string prefix, int count, int idStart)
    {
        var tracks = new List<RecommendationTrackDto>(count);
        for (var index = 0; index < count; index++)
        {
            var id = (idStart + index).ToString();
            tracks.Add(new RecommendationTrackDto(
                id,
                $"{prefix}-title-{id}",
                180 + (index % 60),
                $"{prefix}-isrc-{id}",
                index + 1,
                new RecommendationArtistDto(
                    (100000 + (index % 24)).ToString(),
                    $"{prefix}-artist-{index % 24}"),
                new RecommendationAlbumDto(
                    (200000 + (index % 36)).ToString(),
                    $"{prefix}-album-{index % 36}",
                    $"https://example.com/{prefix}/{id}.jpg")));
        }

        return tracks;
    }

    private static RecommendationTrackDto CreateTrack(string id, string title, string artist, string album)
    {
        return new RecommendationTrackDto(
            id,
            title,
            180,
            $"ISRC{id.PadLeft(8, '0')}",
            int.Parse(id),
            new RecommendationArtistDto(id, artist),
            new RecommendationAlbumDto(id, album, $"https://example.com/{id}.jpg"));
    }

    private static FolderDto CreateFolder(long id, string name)
    {
        return new FolderDto(
            Id: id,
            RootPath: $"/music/{id}",
            DisplayName: name,
            Enabled: true,
            LibraryId: 1,
            LibraryName: "Library",
            DesiredQuality: "0",
            AutoTagProfileId: null,
            AutoTagEnabled: false,
            ConvertEnabled: false,
            ConvertFormat: null,
            ConvertBitrate: null);
    }

    private static LibraryRecommendationService CreateRecommendationService(string webRootPath)
    {
        return new LibraryRecommendationService(
            new LibraryRecommendationService.LibraryRecommendationCollaborators
            {
                Repository = null!,
                ShazamRecognitionService = null!,
                ShazamDiscoveryService = null!,
                DeezerClient = null!,
                DeezerGatewayService = null!,
                TrackIdentityResolver = new NullTrackIdentityResolver(),
                DedupeService = null!
            },
            new TestWebHostEnvironment(webRootPath),
            NullLogger<LibraryRecommendationService>.Instance);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public TestWebHostEnvironment(string webRootPath)
        {
            WebRootPath = webRootPath;
            ContentRootPath = webRootPath;
            WebRootFileProvider = new PhysicalFileProvider(webRootPath);
            ContentRootFileProvider = WebRootFileProvider;
        }

        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public IFileProvider ContentRootFileProvider { get; set; }
        public string ContentRootPath { get; set; }
        public string EnvironmentName { get; set; } = "Development";
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
    }
}
