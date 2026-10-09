using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class RecommendationSchedulingTest
{
    [Fact]
    public async Task FridayInitializesMissingWeeklySectionsPerCompletedFolderAndRetainsRetryState()
    {
        var fixture = new RecommendationCacheServingTest();
        await fixture.InitializeAsync();
        try
        {
            var today = new DateOnly(2026, 10, 9);
            var week = new DateOnly(2026, 10, 5);
            // Repository checkpoints use the real UTC clock; keep retry comparisons on that clock.
            var clock = new FixedClock(DateTimeOffset.UtcNow);
            var service = fixture.Service(clock);
            var dailyId = $"daily-rotation:l{fixture.Folder.LibraryId}:f{fixture.Folder.Id}";
            var key = new RecommendationGenerationStateKey(fixture.Folder.LibraryId!.Value, fixture.Folder.Id, dailyId, today);
            Assert.True(await fixture.Repository.TryStartRecommendationGenerationAsync(key, "test"));
            var track = new RecommendationTrackDto("12345", "Daily", 180, "", 1, new("artist", "Artist"), new("album", "Album", ""));
            Assert.True(await fixture.Repository.PublishRecommendationSnapshotAsync(key, "recommendations-daily-pool", dailyId,
                "v1:20261009", System.Text.Json.JsonSerializer.Serialize(new { GeneratedAtUtc = clock.GetUtcNow(), Tracks = new[] { track } })));
            var second = await fixture.Repository.AddFolderAsync(new LibraryRepository.FolderUpsertInput(
                RootPath: fixture.Folder.RootPath + "-blocked", DisplayName: "Blocked", Enabled: true, LibraryName: "Music", DesiredQuality: "flac",
                ConvertEnabled: false, ConvertFormat: null, ConvertBitrate: null, AutoTagProfileId: "test-profile"));
            Assert.False(await service.AreDailyRecommendationsCompleteAsync(week, CancellationToken.None));
            var method = typeof(LibraryRecommendationService).GetMethod("InitializeMissingWeeklyRecommendationsAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            for (var attempt = 0; attempt < 2; attempt++)
                await (Task)method.Invoke(service, [today, week, CancellationToken.None])!;
            foreach (var type in new[] { LibraryRecommendationService.WeeklyMissingType, LibraryRecommendationService.WeeklySimilarType })
            {
                var id = $"weekly-rotation:l{key.LibraryId}:f{key.FolderId}:{type}";
                var state = await fixture.Repository.GetRecommendationGenerationStateAsync(key.LibraryId, key.FolderId, id, week);
                Assert.NotNull(state);
                Assert.Equal("failed", state.Status); // No provider configured: never manufacture a successful list.
                Assert.Equal(1, state.AttemptCount);
                Assert.Null(await fixture.Repository.GetRecommendationGenerationStateAsync(second.LibraryId!.Value, second.Id,
                    $"weekly-rotation:l{second.LibraryId}:f{second.Id}:{type}", week));
            }
            foreach (var type in new[] { LibraryRecommendationService.WeeklyMissingType, LibraryRecommendationService.WeeklySimilarType })
            {
                var id = await fixture.SeedWeekly(week.AddDays(-7), type);
                var saved = (await fixture.Repository.GetPlaylistTrackCandidateCacheAsync("recommendations-weekly-pool", id))!;
                await (Task)method.Invoke(fixture.Service(clock), [today, week, CancellationToken.None])!;
                var after = (await fixture.Repository.GetPlaylistTrackCandidateCacheAsync("recommendations-weekly-pool", id))!;
                Assert.Equal(saved.CandidatesJson, after.CandidatesJson);
                Assert.Equal(saved.SnapshotId, after.SnapshotId);
            }
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Theory]
    [InlineData("2026-10-04T23:59:00+03:00", 60)]
    [InlineData("2026-10-05T00:00:00+03:00", 300)]
    [InlineData("2026-10-06T23:58:00+03:00", 120)]
    public void WakeupHonoursLocalMidnight(string now, int seconds)
    {
        var method = typeof(LibraryRecommendationAutomationHostedService).GetMethod("GetReconciliationDelay", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(TimeSpan.FromSeconds(seconds), method.Invoke(null, [DateTimeOffset.Parse(now)]));
    }

    [Fact]
    public async Task DailyBarrierIsPersistedAndSurvivesServiceRestart()
    {
        var fixture = new RecommendationCacheServingTest();
        await fixture.InitializeAsync();
        try
        {
            var method = typeof(LibraryRecommendationService).GetMethod("AreDailyRecommendationsCompleteAsync");
            Assert.NotNull(method);
            var day = new DateOnly(2026, 10, 5);
            async Task<bool> Check() => await (Task<bool>)method.Invoke(fixture.Service(), [day, CancellationToken.None])!;
            Assert.False(await Check());
            var key = new RecommendationGenerationStateKey(fixture.Folder.LibraryId!.Value, fixture.Folder.Id,
                $"daily-rotation:l{fixture.Folder.LibraryId}:f{fixture.Folder.Id}", day);
            Assert.True(await fixture.Repository.TryStartRecommendationGenerationAsync(key, "test"));
            Assert.False(await Check());
            await fixture.Repository.FailRecommendationGenerationAsync(key, "test", "provider unavailable");
            Assert.False(await Check());
            Assert.True(await fixture.Repository.TryStartRecommendationGenerationAsync(key, "retry"));
            Assert.True(await fixture.Repository.PublishRecommendationSnapshotAsync(key, "recommendations-daily-pool", key.StationId,
                "v1:20261005", System.Text.Json.JsonSerializer.Serialize(new { GeneratedAtUtc = DateTimeOffset.UtcNow, Tracks = new[] { new RecommendationTrackDto("12345", "Saved", 180, "", 1, new("artist", "Artist"), new("album", "Album", "")) } })));
            Assert.True(await Check());
            var second = await fixture.Repository.AddFolderAsync(new LibraryRepository.FolderUpsertInput(
                RootPath: fixture.Folder.RootPath + "-second", DisplayName: "Second", Enabled: true, LibraryName: "Music", DesiredQuality: "flac",
                ConvertEnabled: false, ConvertFormat: null, ConvertBitrate: null, AutoTagProfileId: "test-profile"));
            Assert.False(await Check()); // Every eligible scope must succeed, not just the first.
            var secondKey = new RecommendationGenerationStateKey(second.LibraryId!.Value, second.Id,
                $"daily-rotation:l{second.LibraryId}:f{second.Id}", day);
            Assert.True(await fixture.Repository.TryStartRecommendationGenerationAsync(secondKey, "test"));
            Assert.True(await fixture.Repository.PublishRecommendationSnapshotAsync(secondKey, "recommendations-daily-pool", secondKey.StationId,
                "v1:20261005", System.Text.Json.JsonSerializer.Serialize(new { GeneratedAtUtc = DateTimeOffset.UtcNow, Tracks = new[] { new RecommendationTrackDto("23456", "Second", 180, "", 1, new("artist", "Artist"), new("album", "Album", "")) } })));
            Assert.True(await Check());
        }
        finally { await fixture.DisposeAsync(); }
    }
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task RestartReconcilesSavedSnapshotsAndOnlyClaimsUnfinishedSections(bool expiredLease, int daysLater)
    {
        var fixture = new RecommendationCacheServingTest();
        await fixture.InitializeAsync();
        try
        {
            var monday = RecommendationCacheServingTest.Monday;
            var today = monday.AddDays(daysLater);
            var clock = new FixedClock(new DateTimeOffset(today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero));
            var dailyId = $"daily-rotation:l{fixture.Folder.LibraryId}:f{fixture.Folder.Id}";
            var track = new RecommendationTrackDto("12345", "Saved", 180, "", 1, new("1", "Artist"), new("1", "Album", ""));
            // Simulate a restart after legacy cache publication but before its completion checkpoint.
            await fixture.Repository.UpsertPlaylistTrackCandidateCacheAsync("recommendations-daily-pool", dailyId, $"v1:{monday:yyyyMMdd}",
                System.Text.Json.JsonSerializer.Serialize(new { GeneratedAtUtc = clock.GetUtcNow(), Tracks = new[] { track } }), 0, null, null, true);
            if (daysLater > 0)
                await fixture.Repository.UpsertPlaylistTrackCandidateCacheAsync("recommendations-daily-pool", dailyId + $":day:{monday:yyyyMMdd}", $"v1:{monday:yyyyMMdd}",
                    System.Text.Json.JsonSerializer.Serialize(new { GeneratedAtUtc = clock.GetUtcNow().AddDays(-daysLater), Tracks = new[] { track } }), 0, null, null, true);
            if (daysLater > 0)
                await fixture.Repository.UpsertPlaylistTrackCandidateCacheAsync("recommendations-daily-pool", dailyId, $"v1:{today:yyyyMMdd}",
                    System.Text.Json.JsonSerializer.Serialize(new { GeneratedAtUtc = clock.GetUtcNow(), Tracks = new[] { track } }), 0, null, null, true);
            var firstId = await fixture.SeedWeekly(monday);
            var secondId = await fixture.SeedWeekly(monday, LibraryRecommendationService.WeeklySimilarType);
            var first = new RecommendationGenerationStateKey(fixture.Folder.LibraryId!.Value, fixture.Folder.Id, firstId, monday);
            Assert.True(await fixture.Repository.TryStartRecommendationGenerationAsync(first, "test"));
            if (expiredLease)
            {
                await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + fixture.DatabasePath);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE recommendation_generation_state SET started_at_utc='1990-01-01T00:00:00Z' WHERE station_id LIKE '%weekly-missing-favourites'";
                await command.ExecuteNonQueryAsync();
            }
            else await fixture.Repository.CompleteRecommendationGenerationAsync(first);
            var service = fixture.Service(clock);
            await service.ReconcileRecommendationGenerationAsync(clock.GetLocalNow(), CancellationToken.None);
            Assert.True(await service.AreDailyRecommendationsCompleteAsync(monday, CancellationToken.None));
            var firstState = (await fixture.Repository.GetRecommendationGenerationStateAsync(first.LibraryId, first.FolderId, firstId, monday))!;
            var secondState = (await fixture.Repository.GetRecommendationGenerationStateAsync(first.LibraryId, first.FolderId, secondId, monday))!;
            Assert.Equal("completed", firstState.Status);
            Assert.Equal(expiredLease ? 2 : 1, firstState.AttemptCount);
            Assert.Equal("completed", secondState.Status);
            Assert.Equal(1, secondState.AttemptCount);
            await fixture.Service(clock).ReconcileRecommendationGenerationAsync(clock.GetLocalNow(), CancellationToken.None);
            Assert.Equal(firstState.AttemptCount, (await fixture.Repository.GetRecommendationGenerationStateAsync(first.LibraryId, first.FolderId, firstId, monday))!.AttemptCount);
            Assert.Equal(1, (await fixture.Repository.GetRecommendationGenerationStateAsync(first.LibraryId, first.FolderId, secondId, monday))!.AttemptCount);
            Assert.Equal($"v1:{today:yyyyMMdd}", (await fixture.Repository.GetPlaylistTrackCandidateCacheAsync("recommendations-daily-pool", dailyId))!.SnapshotId);
            Assert.True(await service.AreDailyRecommendationsCompleteAsync(monday, CancellationToken.None));
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task MissingSnapshotDoesNotLeaveCompletedWeeklyCheckpointStuck()
    {
        var fixture = new RecommendationCacheServingTest();
        await fixture.InitializeAsync();
        try
        {
            var monday = RecommendationCacheServingTest.Monday;
            var id = $"weekly-rotation:l{fixture.Folder.LibraryId}:f{fixture.Folder.Id}:{LibraryRecommendationService.WeeklyMissingType}";
            var key = new RecommendationGenerationStateKey(fixture.Folder.LibraryId!.Value, fixture.Folder.Id, id, monday);
            Assert.True(await fixture.Repository.TryStartRecommendationGenerationAsync(key, "test"));
            await fixture.Repository.CompleteRecommendationGenerationAsync(key);
            var method = typeof(LibraryRecommendationService).GetMethod("GenerateWeeklyRecommendationsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)method.Invoke(fixture.Service(), [key.LibraryId, key.FolderId, LibraryRecommendationService.WeeklyMissingType, monday, "recovery", CancellationToken.None])!;
            var state = await fixture.Repository.GetRecommendationGenerationStateAsync(key.LibraryId, key.FolderId, key.StationId, monday);
            Assert.Equal(1, state!.AttemptCount); // Existing forced-reset semantics begin a new attempt series.
            Assert.Equal("failed", state.Status); // No provider is configured: retry, rather than publish fake success.
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconciliationDoesNotReclaimLiveLeaseOrResetRecentFailure(bool failed)
    {
        var fixture = new RecommendationCacheServingTest();
        await fixture.InitializeAsync();
        try
        {
            var monday = RecommendationCacheServingTest.Monday;
            var id = await fixture.SeedWeekly(monday);
            var key = new RecommendationGenerationStateKey(fixture.Folder.LibraryId!.Value, fixture.Folder.Id, id, monday);
            Assert.True(await fixture.Repository.TryStartRecommendationGenerationAsync(key, "test"));
            if (failed) await fixture.Repository.FailRecommendationGenerationAsync(key, "provider_failed", "unavailable");
            var before = await fixture.Repository.GetRecommendationGenerationStateAsync(key.LibraryId, key.FolderId, key.StationId, monday);
            var method = typeof(LibraryRecommendationService).GetMethod("GenerateWeeklyRecommendationsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)method.Invoke(fixture.Service(), [key.LibraryId, key.FolderId, LibraryRecommendationService.WeeklyMissingType, monday, "recovery", CancellationToken.None])!;
            var after = await fixture.Repository.GetRecommendationGenerationStateAsync(key.LibraryId, key.FolderId, key.StationId, monday);
            Assert.Equal(before, after);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task NoEligibleScopesRequireNoDailyJobs()
    {
        var fixture = new RecommendationCacheServingTest();
        await fixture.InitializeAsync();
        try
        {
            Assert.True(await fixture.Repository.DeleteFolderAsync(fixture.Folder.Id));
            Assert.True(await fixture.Service().AreDailyRecommendationsCompleteAsync(RecommendationCacheServingTest.Monday, CancellationToken.None));
            await fixture.Service().RefreshWeeklyRecommendationsForWeekAsync(RecommendationCacheServingTest.Monday, "scheduled", CancellationToken.None);
            Assert.Null(await fixture.Repository.GetRecommendationGenerationStateAsync(fixture.Folder.LibraryId!.Value, fixture.Folder.Id, $"weekly-rotation:l{fixture.Folder.LibraryId}:f{fixture.Folder.Id}:{LibraryRecommendationService.WeeklyMissingType}", RecommendationCacheServingTest.Monday));
        }
        finally { await fixture.DisposeAsync(); }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
