using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using DeezSpoTag.Services.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class RecommendationSchedulingTest
{
    [Theory]
    [InlineData("Africa/Nairobi", "2026-10-24T18:59:59Z", true)]
    [InlineData("Africa/Nairobi", "2026-10-24T19:00:00Z", false)]
    [InlineData("Africa/Nairobi", "2026-10-24T20:00:00Z", false)]
    [InlineData("Africa/Nairobi", "2026-10-24T21:00:00Z", true)]
    [InlineData("America/New_York", "2026-10-25T01:59:59Z", true)]
    [InlineData("America/New_York", "2026-10-25T02:00:00Z", false)]
    [InlineData("America/New_York", "2026-10-25T03:00:00Z", false)]
    [InlineData("America/New_York", "2026-10-25T04:00:00Z", true)]
    public void DailyCutoffUsesTheConfiguredCalendarDay(string zone,string utc,bool allowed)
    {
        var now=TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(utc),TimeZoneInfo.FindSystemTimeZoneById(zone));
        Assert.Equal(allowed,LibraryRecommendationService.AutomaticDailyAllowed(now));
    }

    [Theory]
    [InlineData("America/New_York","2026-03-08","2026-03-09T04:00:00Z")]
    [InlineData("America/New_York","2026-11-01","2026-11-02T05:00:00Z")]
    [InlineData("Africa/Nairobi","2026-10-24","2026-10-24T21:00:00Z")]
    public void ExpiryUsesNextLocalMidnightIncludingDaylightSavingChanges(string zone,string day,string expected)
        => Assert.Equal(DateTimeOffset.Parse(expected),LibraryRecommendationService.DailyBoundaryUtc(
            DateOnly.Parse(day).AddDays(1),TimeOnly.MinValue,TimeZoneInfo.FindSystemTimeZoneById(zone)));

    [Theory]
    [InlineData(21,59,true)]
    [InlineData(22,0,false)]
    [InlineData(23,0,false)]
    [InlineData(0,0,true)]
    public async Task AutomaticServiceSkipsLateGenerationButManualRebuildRemainsAvailable(int hour,int minute,bool automatic)
    {
        var fixture=new RecommendationCacheServingTest();await fixture.InitializeAsync();
        try
        {
            var clock=new SchedulingClock(new DateTimeOffset(2026,10,24,hour,minute,0,TimeSpan.FromHours(3)).ToUniversalTime());
            var service=fixture.Service(clock);var day=new DateOnly(2026,10,24);
            var station=$"daily-rotation:l{fixture.Folder.LibraryId}:f{fixture.Folder.Id}";
            await service.RefreshDailyRecommendationsForDateAsync(day,"scheduled",CancellationToken.None);
            var state=await fixture.Repository.GetRecommendationGenerationStateAsync(fixture.Folder.LibraryId!.Value,fixture.Folder.Id,station,day);
            Assert.Equal(automatic,state is not null);
            if (!automatic)
            {
                var response=(await service.GetRecommendationsAsync(fixture.Folder.LibraryId.Value,station))!;
                Assert.Equal("waiting",response.Status);Assert.Empty(response.Tracks);Assert.Contains("22:00",response.Message!);
                await service.RebuildRecommendationsAsync(fixture.Folder.LibraryId.Value,station);
                Assert.NotNull(await fixture.Repository.GetRecommendationGenerationStateAsync(fixture.Folder.LibraryId.Value,fixture.Folder.Id,station,day));
            }
        }
        finally {await fixture.DisposeAsync();}
    }

    [Fact]
    public async Task MidnightCancelsPreviousDayWorkAndImmediatelyAllowsMondaysRun()
    {
        var clock = new SchedulingClock(DateTimeOffset.Parse("2026-10-11T20:59:00Z"));
        var coordinator = new BackgroundWorkCoordinator(clock);
        var worker = SchedulingWorker(clock, coordinator);
        var started = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sunday = RunCalendarDay(worker, async (now, token) =>
        {
            started.SetResult(now);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        Assert.Equal(DayOfWeek.Sunday, (await started.Task).DayOfWeek);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await sunday.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(coordinator.GetSnapshot().ActiveHeavyOperation);
        DateTimeOffset? monday = null;
        Assert.False(await RunCalendarDay(worker, (now, token) =>
        {
            monday = now;
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(DateTimeOffset.Parse("2026-10-12T00:00:00+03:00"), monday);
    }

    [Fact]
    public async Task MidnightCancelsQueuedOldDayAndUsesFreshDateAfterAcquiringSlot()
    {
        var clock = new SchedulingClock(DateTimeOffset.Parse("2026-10-11T20:59:00Z"));
        var coordinator = new BackgroundWorkCoordinator(clock);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = coordinator.RunHeavyWorkAsync(_ => release.Task, CancellationToken.None);
        var worker = SchedulingWorker(clock, coordinator);
        var calls = 0;
        var queued = RunCalendarDay(worker, (_, _) => { calls++; return Task.CompletedTask; });
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await queued.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, calls);
        DateTimeOffset? actual = null;
        var monday = RunCalendarDay(worker, (now, _) => { actual = now; return Task.CompletedTask; });
        clock.Advance(TimeSpan.FromSeconds(30));
        release.SetResult();
        await blocker;
        Assert.False(await monday.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(DateTimeOffset.Parse("2026-10-12T00:00:30+03:00"), actual);
    }

    [Fact]
    public async Task StoppingTheWorkerDoesNotStartAnotherCalendarDay()
    {
        var clock = new SchedulingClock(DateTimeOffset.Parse("2026-10-11T20:59:00Z"));
        using var stopping = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = SchedulingWorker(clock, new BackgroundWorkCoordinator(clock));
        var run = RunCalendarDay(worker, async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, stopping.Token);
        await started.Task;
        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    private static LibraryRecommendationAutomationHostedService SchedulingWorker(TimeProvider clock, BackgroundWorkCoordinator coordinator)
        => new(null!, coordinator, new ConfigurationBuilder().Build(),
            NullLogger<LibraryRecommendationAutomationHostedService>.Instance, clock);

    private static Task<bool> RunCalendarDay(LibraryRecommendationAutomationHostedService worker,
        Func<DateTimeOffset, CancellationToken, Task> reconcile, CancellationToken stoppingToken = default)
    {
        var method = typeof(LibraryRecommendationAutomationHostedService).GetMethod("RunCalendarDayAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (Task<bool>)method.Invoke(worker, [reconcile, stoppingToken])!;
    }

    private sealed class SchedulingClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        private readonly List<SchedulingTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Africa/Nairobi");
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new SchedulingTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
            foreach (var timer in _timers.ToArray()) timer.Fire();
        }
        private sealed class SchedulingTimer(SchedulingClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset _due = DateTimeOffset.MaxValue;
            private TimeSpan _period;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.GetUtcNow() + dueTime;
                _period = period;
                return !_disposed;
            }
            public void Fire()
            {
                if (_disposed || clock.GetUtcNow() < _due) return;
                _due = _period == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.GetUtcNow() + _period;
                callback(state);
            }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [Theory]
    [InlineData("2026-10-10T00:01:00+03:00", "2026-10-09T21:00:00Z")]
    [InlineData("2026-10-10T23:59:00-04:00", "2026-10-10T04:00:00Z")]
    [InlineData("2026-10-10T12:00:00Z", "2026-10-10T00:00:00Z")]
    public void ShazamFreshnessBeginsAtTheUsersCalendarDay(string now, string expected)
        => Assert.Equal(DateTimeOffset.Parse(expected), LibraryRecommendationService.GetShazamDailyFreshnessBoundary(DateTimeOffset.Parse(now)));
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
                "v1:20261009", System.Text.Json.JsonSerializer.Serialize(new { SeedScopeKey = dailyId, GeneratedAtUtc = clock.GetUtcNow(), Tracks = new[] { track } })));
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
                "v1:20261005", System.Text.Json.JsonSerializer.Serialize(new { SeedScopeKey = key.StationId, GeneratedAtUtc = DateTimeOffset.UtcNow, Tracks = new[] { new RecommendationTrackDto("12345", "Saved", 180, "", 1, new("artist", "Artist"), new("album", "Album", "")) } })));
            Assert.True(await Check());
            var second = await fixture.Repository.AddFolderAsync(new LibraryRepository.FolderUpsertInput(
                RootPath: fixture.Folder.RootPath + "-second", DisplayName: "Second", Enabled: true, LibraryName: "Music", DesiredQuality: "flac",
                ConvertEnabled: false, ConvertFormat: null, ConvertBitrate: null, AutoTagProfileId: "test-profile"));
            Assert.False(await Check()); // Every eligible scope must succeed, not just the first.
            var secondKey = new RecommendationGenerationStateKey(second.LibraryId!.Value, second.Id,
                $"daily-rotation:l{second.LibraryId}:f{second.Id}", day);
            Assert.True(await fixture.Repository.TryStartRecommendationGenerationAsync(secondKey, "test"));
            Assert.True(await fixture.Repository.PublishRecommendationSnapshotAsync(secondKey, "recommendations-daily-pool", secondKey.StationId,
                "v1:20261005", System.Text.Json.JsonSerializer.Serialize(new { SeedScopeKey = secondKey.StationId, GeneratedAtUtc = DateTimeOffset.UtcNow, Tracks = new[] { new RecommendationTrackDto("23456", "Second", 180, "", 1, new("artist", "Artist"), new("album", "Album", "")) } })));
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
                System.Text.Json.JsonSerializer.Serialize(new { SeedScopeKey = dailyId, GeneratedAtUtc = clock.GetUtcNow(), Tracks = new[] { track } }), 0, null, null, true);
            if (daysLater > 0)
                await fixture.Repository.UpsertPlaylistTrackCandidateCacheAsync("recommendations-daily-pool", dailyId + $":day:{monday:yyyyMMdd}", $"v1:{monday:yyyyMMdd}",
                    System.Text.Json.JsonSerializer.Serialize(new { SeedScopeKey = dailyId, GeneratedAtUtc = clock.GetUtcNow().AddDays(-daysLater), Tracks = new[] { track } }), 0, null, null, true);
            if (daysLater > 0)
                await fixture.Repository.UpsertPlaylistTrackCandidateCacheAsync("recommendations-daily-pool", dailyId, $"v1:{today:yyyyMMdd}",
                    System.Text.Json.JsonSerializer.Serialize(new { SeedScopeKey = dailyId, GeneratedAtUtc = clock.GetUtcNow(), Tracks = new[] { track } }), 0, null, null, true);
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
