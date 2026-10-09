using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class WeeklyPathfinderRecommendationTest
{
    [Theory]
    [InlineData("2026-10-04", "2026-09-28")]
    [InlineData("2026-10-05", "2026-10-05")]
    [InlineData("2027-01-01", "2026-12-28")]
    public void WeekStartsOnMonday(string day, string expected)
    {
        var method = typeof(LibraryRecommendationService).GetMethod("GetRecommendationWeekStart", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(DateOnly.Parse(expected), method.Invoke(null, [DateOnly.Parse(day)]));
    }

    [Fact]
    public async Task WeeklyMembershipUsesOnlyArtistIdsFromTheRequestedFolder()
    {
        var fixture = new RecommendationCacheServingTest();
        await fixture.InitializeAsync();
        try
        {
            var other = await fixture.Repository.AddFolderAsync(new DeezSpoTag.Services.Library.LibraryRepository.FolderUpsertInput(
                RootPath: fixture.Folder.RootPath + "-other", DisplayName: "Other", Enabled: true,
                LibraryName: "Music", DesiredQuality: "flac", ConvertEnabled: false,
                ConvertFormat: null, ConvertBitrate: null, AutoTagProfileId: "test-profile"));
            Assert.Equal(fixture.Folder.LibraryId, other.LibraryId);
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + fixture.DatabasePath);
            await connection.OpenAsync();
            foreach (var folder in new[] { fixture.Folder, other })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO artist (id,name) VALUES (@id,@name);
                    INSERT INTO album (id,artist_id,title) VALUES (@id,@id,'Album');
                    INSERT INTO track (id,album_id,title) VALUES (@id,@id,'Song');
                    INSERT INTO audio_file (id,path,folder_id) VALUES (@id,@path,@id);
                    INSERT INTO track_local (track_id,audio_file_id) VALUES (@id,@id);
                    """;
                command.Parameters.AddWithValue("id", folder.Id);
                command.Parameters.AddWithValue("name", folder.DisplayName + " artist");
                command.Parameters.AddWithValue("path", System.IO.Path.Combine(folder.RootPath, "song.flac"));
                await command.ExecuteNonQueryAsync();
                await fixture.Repository.UpsertArtistSourceIdAsync(folder.Id, "spotify", "artist-" + folder.Id);
            }
            var method = typeof(LibraryRecommendationService).GetMethod("GetWeeklyLibraryArtistMembershipAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            foreach (var folder in new[] { fixture.Folder, other })
            {
                var membership = await (Task<(HashSet<string> Ids, HashSet<string> Names, Dictionary<string, string> DisplayNames)>)method.Invoke(
                    fixture.Service(), [folder.LibraryId!.Value, folder.Id, false, CancellationToken.None])!;
                Assert.Equal("artist-" + folder.Id, Assert.Single(membership.Ids));
                Assert.Equal(folder.DisplayName + " artist", Assert.Single(membership.DisplayNames).Value);
            }
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public void SelectionMaximizesArtistsAndIsStable()
    {
        var tracks = Enumerable.Range(0, 60).SelectMany(artist => Enumerable.Range(0, 3).Select(song =>
            new RecommendationTrackDto($"{artist}-{song}", $"Song {song}", 180, "", 0,
                new RecommendationArtistDto(artist.ToString(), $"Artist {artist}"), new RecommendationAlbumDto("", "Album", "")))).ToList();
        var method = typeof(LibraryRecommendationService).GetMethod("SelectWeeklyArtistTracks", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var day = new DateOnly(2026, 9, 28);
        var selected = (IReadOnlyList<RecommendationTrackDto>)method.Invoke(null, [tracks, 50, day, "missing"] )!;
        var repeated = (IReadOnlyList<RecommendationTrackDto>)method.Invoke(null, [tracks, 50, day, "missing"] )!;
        Assert.Equal(50, selected.Count);
        Assert.Equal(50, selected.Select(t => t.Artist.Id).Distinct().Count());
        Assert.Equal(selected.Select(t => t.Id), repeated.Select(t => t.Id));
    }
    [Fact]
    public void SelectionUsesEveryArtistBeforeTakingSecondsAndDeduplicatesIsrc()
    {
        var tracks = Enumerable.Range(0, 3).SelectMany(artist => Enumerable.Range(0, 4).Select(song =>
            new RecommendationTrackDto($"{artist}-{song}", "Song", 180, song == 0 ? "shared-isrc" : $"{artist}-{song}", 0,
                new(artist.ToString(), $"Artist {artist}"), new("", "Album", "")))).ToList();
        var method = typeof(LibraryRecommendationService).GetMethod("SelectWeeklyArtistTracks", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (IReadOnlyList<RecommendationTrackDto>)method.Invoke(null, [tracks, 50, new DateOnly(2026, 9, 28), "missing"] )!;
        Assert.Equal(3, result.Take(3).Select(t => t.Artist.Id).Distinct().Count());
        Assert.Equal(result.Count, result.Select(t => t.Isrc).Distinct().Count());
        Assert.Equal(10, result.Count);
    }

    [Fact]
    public void WeeklyOrderRotatesAcrossWeeks()
    {
        var tracks = Enumerable.Range(0, 60).Select(artist => new RecommendationTrackDto(artist.ToString(), "Song", 180, "", 0,
            new(artist.ToString(), "Artist"), new("", "Album", ""))).ToList();
        var method = typeof(LibraryRecommendationService).GetMethod("SelectWeeklyArtistTracks", BindingFlags.NonPublic | BindingFlags.Static)!;
        var first = (IReadOnlyList<RecommendationTrackDto>)method.Invoke(null, [tracks, 50, new DateOnly(2026, 9, 28), "missing"] )!;
        var second = (IReadOnlyList<RecommendationTrackDto>)method.Invoke(null, [tracks, 50, new DateOnly(2026, 10, 5), "missing"] )!;
        Assert.False(first.Select(t => t.Id).SequenceEqual(second.Select(t => t.Id)));
    }

    [Fact]
    public void SharedTopTrackDoesNotReduceArtistVariety()
    {
        var shared = new RecommendationTrackDto("x", "Shared", 180, "", 0, new("b", "Artist B"), new("", "Album", ""));
        var candidates = new[] { shared, shared with { Id = "w", Title = "Alternative" }, shared with { Artist = new("a", "Artist A") } };
        var method = typeof(LibraryRecommendationService).GetMethod("SelectWeeklyArtistTracks", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (IReadOnlyList<RecommendationTrackDto>)method.Invoke(null, [candidates, 50, new DateOnly(2026, 9, 28), "missing"] )!;
        Assert.Equal(2, result.Select(t => t.Artist.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WeeklyAutomationWaitsForDailyCompletion(bool complete)
    {
        var method = typeof(LibraryRecommendationService).GetMethod("RunRecommendationSequenceAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var daily = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var weeklyStarted = false;
        Func<CancellationToken, Task> dailyWork = _ => daily.Task;
        Func<CancellationToken, Task<bool>> barrier = _ => Task.FromResult(complete);
        Func<CancellationToken, Task> weeklyWork = _ => { weeklyStarted = true; return Task.CompletedTask; };
        var run = (Task)method.Invoke(null, [dailyWork, barrier, weeklyWork, CancellationToken.None])!;
        Assert.False(weeklyStarted);
        daily.SetResult();
        await run;
        Assert.Equal(complete, weeklyStarted);
    }

    [Theory]
    [InlineData("failed", 16, true)]
    [InlineData("failed", 1, false)]
    [InlineData("running", 31, true)]
    [InlineData("running", 1, false)]
    [InlineData("completed", 60, false)]
    public void WeeklyRetryRespectsBackoffAndRecoversExpiredLease(string status, int minutes, bool expected)
    {
        var method = typeof(LibraryRecommendationService).GetMethod("ShouldQueueWeeklyGeneration", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var now = DateTimeOffset.UtcNow;
        var then = now.AddMinutes(-minutes);
        var state = new RecommendationGenerationStateDto(1, -1, "library:1:weekly-missing-favourites", new DateOnly(2026,9,28), status, null, then, null, null, 1, then);
        Assert.Equal(expected, method.Invoke(null, [state, now]));
    }

    [Fact]
    public async Task OneArtistTimeoutDoesNotDiscardOtherArtists()
    {
        var method = typeof(LibraryRecommendationService).GetMethod("FetchWeeklyArtistMetadataAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var fetch = method.MakeGenericMethod(typeof(string));
        var failures = 0;
        Action<Exception> report = _ => failures++;
        Func<Task<List<string>>> broken = () => Task.FromException<List<string>>(new InvalidOperationException("metadata unavailable"));
        var skipped = await (Task<List<string>>)fetch.Invoke(null, [broken, report, CancellationToken.None])!;
        Assert.Empty(skipped);
        Func<Task<List<string>>> healthy = () => Task.FromResult(new List<string> { "track" });
        var tracks = await (Task<List<string>>)fetch.Invoke(null, [healthy, report, CancellationToken.None])!;
        Assert.Single(tracks);
        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task WeeklyDeezerMatchSurvivesSerializationWithoutRematching()
    {
        var method = typeof(LibraryRecommendationService).GetMethod("ResolveWeeklyTrackMappingAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var track = new RecommendationTrackDto("spotify-id", "Song", 180, "ISRC", 1,
            new("artist", "Artist"), new("album", "Album", ""), "spotify", "https://open.spotify.com/track/spotify-id");
        var calls = 0;
        Func<Task<string?>> resolve = () => { calls++; return Task.FromResult<string?>("12345"); };
        var mapped = await (Task<RecommendationTrackDto>)method.Invoke(null, [track, resolve])!;
        var saved = System.Text.Json.JsonSerializer.Serialize(mapped);
        var restored = System.Text.Json.JsonSerializer.Deserialize<RecommendationTrackDto>(saved)!;
        var repeated = await (Task<RecommendationTrackDto>)method.Invoke(null, [restored, resolve])!;
        Assert.Equal(1, calls);
        Assert.Equal("spotify-id", repeated.Id);
        Assert.Equal("spotify", repeated.Source);
        Assert.Contains("12345", saved);
        Assert.Equal(mapped, repeated);
    }

    [Fact]
    public async Task UnmatchedWeeklyTrackRetainsSpotifyIdentity()
    {
        var method = typeof(LibraryRecommendationService).GetMethod("ResolveWeeklyTrackMappingAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var track = new RecommendationTrackDto("spotify-id", "Song", 180, "", 1,
            new("artist", "Artist"), new("album", "Album", ""), "spotify");
        Func<Task<string?>> resolve = () => Task.FromResult<string?>(null);
        var result = await (Task<RecommendationTrackDto>)method.Invoke(null, [track, resolve])!;
        Assert.Equal(track.Id, result.Id);
        Assert.Equal(track.Source, result.Source);
        Assert.Contains("unmatched", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task WeeklyHydrationUsesLibrespotIsrcAndPreservesSavedMatches()
    {
        var method = typeof(LibraryRecommendationService).GetMethod("HydrateWeeklyTrackIsrcsAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var missing = new RecommendationTrackDto("spotify-id", "Song", 180, "", 1,
            new("artist", "Artist"), new("album", "Album", ""), "spotify");
        var matched = missing with { Id = "saved-id", DeezerId = "12345", MappingStatus = "matched" };
        var detail = new RecommendationDetailDto(new("library:1:weekly-missing-favourites", "Weekly", "", "weekly", null, 2), [missing, matched], DateTimeOffset.UtcNow);
        Func<List<SpotifyTrackSummary>, Task<List<SpotifyTrackSummary>>> hydrate = tracks =>
        {
            Assert.Equal("spotify-id", Assert.Single(tracks).Id);
            return Task.FromResult(tracks.Select(t => t with { Isrc = "USABC2600001" }).ToList());
        };
        var result = await (Task<RecommendationDetailDto>)method.Invoke(null, [detail, hydrate])!;
        Assert.Equal("USABC2600001", result.Tracks[0].Isrc);
        Assert.Equal(matched, result.Tracks[1]);
        Assert.Equal(detail.GeneratedAtUtc, result.GeneratedAtUtc);
    }

}
