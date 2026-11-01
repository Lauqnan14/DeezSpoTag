using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net.Http.Json;
using System.Linq;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Text.Json;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public class ArtistMetadataCacheRefreshTest
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    public async System.Threading.Tasks.Task OnlyArtistPageRefreshRestartsAnExistingRetainedRequest(
        bool artistScoped, bool automatic, bool hasRun)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "artist-manual-priority-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Library"] = "Data Source=" + System.IO.Path.Combine(root, "library.db") + ";Pooling=False" }).Build();
            await new DeezSpoTag.Services.Library.LibraryDbService(config,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Library.LibraryDbService>.Instance).EnsureSchemaAsync();
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(config["ConnectionStrings:Library"]))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = @"INSERT INTO artist(id,name) VALUES(1475,'Abbas Doobeez');
INSERT INTO folder(id,root_path,display_name,enabled) VALUES(1,'/fixture','fixture',1);
INSERT INTO album(id,artist_id,title) VALUES(1,1475,'');
INSERT INTO track(id,album_id,title) VALUES(1,1,'');
INSERT INTO audio_file(id,path,folder_id) VALUES(1,'/fixture/track.flac',1);
INSERT INTO track_local(track_id,audio_file_id) VALUES(1,1);";
                await command.ExecuteNonQueryAsync();
            }
            var repository = new DeezSpoTag.Services.Library.LibraryRepository(config,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Library.LibraryRepository>.Instance);
            using var queue = new LibraryArtistMetadataQueueService(repository, null!, null!, null!, new ProfileEnvironment(root),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryArtistMetadataQueueService>.Instance);
            var request = new ArtistMetadataCacheRefreshRequest(artistScoped ? 1475 : null, null, "auto", ForceProviderRefresh: true);
            var runId = Guid.NewGuid().ToString("N");
            var retained = hasRun ? new ArtistMetadataActiveRun { RunId = runId, CacheRequest = request,
                TargetArtistIds = new() { 1475 }, Paused = true, Automatic = automatic,
                NextAttemptUtc = DateTimeOffset.UtcNow.AddDays(1), ConsecutiveFailures = 3,
                CacheAttempts = new() { ["1475:lastfm:biographies"] = 3 } } : null;
            var itemType = typeof(LibraryArtistMetadataQueueService).GetNestedType("QueueItem", System.Reflection.BindingFlags.NonPublic)!;
            var item = JsonSerializer.Deserialize(JsonSerializer.Serialize(new { ArtistId = artistScoped ? 1475 : 0,
                ArtistName = "Abbas Doobeez", Attempts = 3, NeedsAttention = true, Request = request,
                NextAttemptUtc = DateTimeOffset.UtcNow.AddDays(1), RunId = runId, QueueKey = -9,
                Run = retained, UserPaused = true, AuthenticationBlocked = true, RecoveryAfterUtc = DateTimeOffset.UtcNow.AddDays(2) }), itemType)!;
            var field = typeof(LibraryArtistMetadataQueueService).GetField("_queueItems", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var items = (System.Collections.IDictionary)field.GetValue(queue)!;
            items.Add(-9L, item);
            var result = await queue.EnqueueRefreshAsync(request, System.Threading.CancellationToken.None, automatic: automatic);
            Assert.True(result.Queued);
            Assert.Equal(runId, result.RunId);
            var updated = JsonSerializer.SerializeToElement(items[-9L], itemType);
            if (artistScoped && !automatic)
            {
                Assert.False(updated.GetProperty("NeedsAttention").GetBoolean());
                Assert.Equal(0, updated.GetProperty("Attempts").GetInt32());
                Assert.Equal(JsonValueKind.Null, updated.GetProperty("NextAttemptUtc").ValueKind);
                Assert.False(updated.GetProperty("UserPaused").GetBoolean());
                Assert.False(updated.GetProperty("AuthenticationBlocked").GetBoolean());
                Assert.Equal(JsonValueKind.Null, updated.GetProperty("RecoveryAfterUtc").ValueKind);
                Assert.False(updated.GetProperty("Run").GetProperty("Paused").GetBoolean());
                Assert.Empty(updated.GetProperty("Run").GetProperty("CacheAttempts").EnumerateObject());
                Assert.Equal("queued", queue.GetRefreshStatus(runId)!.RunState);
                var saved = await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(root, "artist-metadata-queue.json"));
                Assert.Contains(runId, saved);
            }
            else Assert.Same(item, items[-9L]);
        }
        finally { System.IO.Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("auto", "apple,musicbrainz,audiomack")]
    [InlineData("apple", "apple")]
    [InlineData("musicbrainz", "musicbrainz")]
    [InlineData("audiomack", "audiomack")]
    public void EnrichmentStagesShareTheRequestedLocationPrecedence(string source, string expected)
    {
        var method = typeof(ArtistMetadataCacheRefreshService).GetMethod("RequestedEnrichmentProviders", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var result = (System.Collections.Generic.IReadOnlyList<string>)method.Invoke(null, new object[] { source })!;
        Assert.Equal(expected, string.Join(",", result));
    }

    [Fact]
    public void ExplicitDeezerSelectionMustNotBecomeAuto()
    {
        var method = typeof(ArtistMetadataCacheRefreshService).GetMethod("ParseProvider", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal("Deezer", method.Invoke(null, new object[] {"deezer"})?.ToString());
    }

    [Fact]
    public async System.Threading.Tasks.Task FreshQobuzProfileBypassesMemoryCacheWithoutChangingNormalReads()
    {
        var proxy = System.Reflection.DispatchProxy.Create<DeezSpoTag.Integrations.Qobuz.IQobuzApiClient, QobuzProxy>();
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var service = new DeezSpoTag.Services.Metadata.Qobuz.QobuzArtistService(proxy, cache,
            Microsoft.Extensions.Options.Options.Create(new DeezSpoTag.Integrations.Qobuz.QobuzApiConfig {DefaultStore = "us-en"}));
        var first = await service.GetArtistAsync(9, "us-en", System.Threading.CancellationToken.None);
        var method = service.GetType().GetMethod("GetArtistAsync", new[] {typeof(int), typeof(string), typeof(System.Threading.CancellationToken), typeof(bool)});
        Assert.NotNull(method);
        var fresh = await (System.Threading.Tasks.Task<DeezSpoTag.Core.Models.Qobuz.QobuzArtist?>)method!.Invoke(service,
            new object[] {9, "us-en", System.Threading.CancellationToken.None, true})!;
        Assert.Equal("Profile 1", first!.Name);
        Assert.Equal("Profile 2", fresh!.Name);
        Assert.Equal("Profile 2", (await service.GetArtistAsync(9, "us-en", System.Threading.CancellationToken.None))!.Name);
        Assert.Equal(2, ((QobuzProxy)(object)proxy).Calls);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(401)]
    [InlineData(503)]
    public async System.Threading.Tasks.Task RequiredQobuzHttpPreservesFailureStatus(int status)
    {
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var client = new DeezSpoTag.Integrations.Qobuz.QobuzApiClient(new System.Net.Http.HttpClient(new QobuzFailureHandler(status)), cache,
            Microsoft.Extensions.Options.Options.Create(new DeezSpoTag.Integrations.Qobuz.QobuzApiConfig {BaseUrl = "https://fixture.qobuz.com", DefaultStore = "us-en"}));
        var method = client.GetType().GetMethod("GetArtistRequiredAsync");
        Assert.NotNull(method);
        var error = await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() =>
            (System.Threading.Tasks.Task)method!.Invoke(client, new object[] {9, "us-en", 0, 1, System.Threading.CancellationToken.None})!);
        Assert.Equal((System.Net.HttpStatusCode)status, error.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task RequiredLocationRefreshHonoursSourceAndPropagatesFailure()
    {
        var excluded = new LocationSource("musicbrainz", false);
        var selected = new LocationSource("audiomack", true);
        var resolver = new DeezSpoTag.Web.Services.ArtistLocation.ArtistLocationResolver(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Web.Services.ArtistLocation.ArtistLocationResolver>.Instance,
            new DeezSpoTag.Web.Services.ArtistLocation.IArtistLocationSource[] {excluded, selected});
        var gate = new ArtistMetadataProviderGate(Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtistMetadataProviderGate>.Instance,
            _ => TimeSpan.Zero, TimeSpan.FromSeconds(1));
        var method = resolver.GetType().GetMethod("ResolveRequiredAsync");
        Assert.NotNull(method);
        var error = await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() =>
            (System.Threading.Tasks.Task)method!.Invoke(resolver, new object[] {17L, "Fixture Artist", "audiomack", gate, System.Threading.CancellationToken.None})!);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(0, excluded.Calls);
        Assert.Equal(1, selected.Calls);
    }

    private sealed class LocationSource(string name, bool fail, bool hasLocation = false) : DeezSpoTag.Web.Services.ArtistLocation.IArtistLocationSource
    {
        public int Calls;
        public string SourceName => name;
        public System.Threading.Tasks.Task<DeezSpoTag.Web.Services.ArtistLocation.ArtistLocationResult?> ResolveAsync(long id, string? artist, System.Threading.CancellationToken ct = default)
        {
            Calls++;
            if (fail) throw new System.Net.Http.HttpRequestException("fixture", null, System.Net.HttpStatusCode.ServiceUnavailable);
            return System.Threading.Tasks.Task.FromResult<DeezSpoTag.Web.Services.ArtistLocation.ArtistLocationResult?>(hasLocation
                ? new("Nairobi, Kenya", "Nairobi", "Kenya", "KE", name) : null);
        }
    }

    private sealed class QobuzFailureHandler(int status) : System.Net.Http.HttpMessageHandler
    {
        protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken ct)
            => System.Threading.Tasks.Task.FromResult(new System.Net.Http.HttpResponseMessage((System.Net.HttpStatusCode)status)
                { Content = new System.Net.Http.StringContent("fixture") });
    }

    [Fact]
    public async System.Threading.Tasks.Task RequiredGenreWriteCannotHideStorageFailure()
    {
        var path=System.IO.Path.GetTempFileName();
        try
        {
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Library"]="Data Source="+path}).Build();
            var cache=new DeezSpoTag.Services.Library.ArtistPageCacheRepository(config,Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Library.ArtistPageCacheRepository>.Instance);
            var method=cache.GetType().GetMethod("UpsertGenresAsync")!;
            Assert.Equal(5,method.GetParameters().Length);
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(()=>(System.Threading.Tasks.Task)method.Invoke(cache,new object[] {"spotify","fixture",new[] {"Jazz"},System.Threading.CancellationToken.None,true})!);
        }
        finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();System.IO.File.Delete(path);}
    }

    public class QobuzProxy : System.Reflection.DispatchProxy
    {
        public int Calls;
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name is not ("GetArtistAsync" or "GetArtistRequiredAsync")) throw new NotSupportedException();
            return System.Threading.Tasks.Task.FromResult<DeezSpoTag.Core.Models.Qobuz.QobuzArtist?>(
                new DeezSpoTag.Core.Models.Qobuz.QobuzArtist { Id = 9, Name = "Profile " + (++Calls) });
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task ArtistPageRequestIsAcceptedWithoutChangingRetainedBulkRun()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "artist-retained-request-" + Guid.NewGuid().ToString("N"));
        var folder = System.IO.Path.Combine(root, "library-artist-images");
        System.IO.Directory.CreateDirectory(folder);
        try
        {
            var statePath = System.IO.Path.Combine(folder, "metadata-automation-state.json");
            var retained = JsonSerializer.Serialize(new ArtistMetadataAutomationState { ActiveRun = new ArtistMetadataActiveRun
                { Operation = "cache-refresh", RunId = "retained-bulk", Paused = true,
                    CacheRequest = new(null, null, "auto"), TargetArtistIds = new() { 17 }, PendingNotice = "unfinished source work",
                    CacheAttempts = new() { ["17:lastfm:biographies"] = 2 } } });
            await System.IO.File.WriteAllTextAsync(statePath, retained);
            var legacyJournalPath = System.IO.Path.Combine(folder, "metadata-automation-outcomes.ndjson");
            await using (var journal = await ArtistMetadataOutcomeJournal.OpenAsync(legacyJournalPath, "retained-bulk", true,
                new[] { new ArtistRunOutcomeRecord(17, ArtistRunOutcomes.Failed,
                    CacheWork: new[] { new ArtistCacheWorkOutcome("lastfm", "biographies", ArtistCacheWorkStatus.Failed) }) }, System.Threading.CancellationToken.None))
                await journal.FlushAsync(System.Threading.CancellationToken.None);
            var env = new ProfileEnvironment(root);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
                { ["ConnectionStrings:Library"] = "Data Source=" + System.IO.Path.Combine(root,"library.db") }).Build();
            await new DeezSpoTag.Services.Library.LibraryDbService(config,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Library.LibraryDbService>.Instance).EnsureSchemaAsync();
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(config["ConnectionStrings:Library"]))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = @"INSERT INTO artist(id,name) VALUES(17,'Fixture Artist');
INSERT INTO folder(id,root_path,display_name,enabled) VALUES(1,'/fixture','fixture',1);
INSERT INTO album(id,artist_id,title) VALUES(1,17,'');
INSERT INTO track(id,album_id,title) VALUES(1,1,'');
INSERT INTO audio_file(id,path,folder_id) VALUES(1,'/fixture/track.flac',1);
INSERT INTO track_local(track_id,audio_file_id) VALUES(1,1);";
                await command.ExecuteNonQueryAsync();
            }
            var repository = new DeezSpoTag.Services.Library.LibraryRepository(config,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Library.LibraryRepository>.Instance);
            var queue = new LibraryArtistMetadataQueueService(repository, null!, null!, null!, env,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryArtistMetadataQueueService>.Instance);
            var preferences = new UserPreferencesStore(env, Microsoft.Extensions.Logging.Abstractions.NullLogger<UserPreferencesStore>.Instance);
            var refresh = new ArtistMetadataCacheRefreshService(repository, null!, null!, null!, null!, null!, null!, null!, null!, null!,
                preferences, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtistMetadataCacheRefreshService>.Instance);
            var coordinator = new ArtistMetadataAutomationCoordinator(refresh, null!, preferences, env,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtistMetadataAutomationCoordinator>.Instance, queue);
            var (queued, runId) = await coordinator.EnqueueArtistCacheRefreshAsync(17, System.Threading.CancellationToken.None,
                ArtistCacheRefreshScope.Artwork);
            Assert.True(queued);
            Assert.True(Guid.TryParseExact(runId,"N",out _));
            Assert.Equal("queued", coordinator.GetArtistCacheRefreshStatus(runId)!.RunState);
            Assert.Null(coordinator.GetArtistCacheRefreshStatus(runId, 999));
            var duplicate = await coordinator.EnqueueArtistCacheRefreshAsync(17, System.Threading.CancellationToken.None,
                ArtistCacheRefreshScope.Artwork);
            Assert.Equal(runId,duplicate.RunId);
            var other = await coordinator.EnqueueArtistCacheRefreshAsync(17, System.Threading.CancellationToken.None,
                ArtistCacheRefreshScope.Biographies);
            Assert.True(other.Queued);
            Assert.NotEqual(runId,other.RunId);
            await using (var outside = new Microsoft.Data.Sqlite.SqliteConnection(config["ConnectionStrings:Library"]))
            {
                await outside.OpenAsync(); await using var sql = outside.CreateCommand();
                sql.CommandText = "INSERT INTO folder(id,root_path,display_name,enabled) VALUES(2,'/other','other',1); INSERT INTO artist(id,name) VALUES(19,'Outside Scope'); INSERT INTO album(id,artist_id,title) VALUES(19,19,''); INSERT INTO track(id,album_id,title) VALUES(19,19,''); INSERT INTO audio_file(id,path,folder_id) VALUES(19,'/other/outside.flac',2); INSERT INTO track_local(track_id,audio_file_id) VALUES(19,19);";
                await sql.ExecuteNonQueryAsync();
            }
            Assert.True(await coordinator.EnqueueCacheRefreshAsync(new(null, 1, "ignored"), System.Threading.CancellationToken.None));
            var bulk = queue.GetLibraryRefreshStatus()!;
            Assert.Equal(1, bulk.CacheRefresh.TotalArtists);
            Assert.NotEqual(runId, bulk.RunId);
            // New library additions cannot silently change an already accepted scope.
            await using (var added = new Microsoft.Data.Sqlite.SqliteConnection(config["ConnectionStrings:Library"]))
            {
                await added.OpenAsync(); await using var sql = added.CreateCommand();
                sql.CommandText = "INSERT INTO artist(id,name) VALUES(18,'Added Later'); INSERT INTO album(id,artist_id,title) VALUES(2,18,''); INSERT INTO track(id,album_id,title) VALUES(2,2,''); INSERT INTO audio_file(id,path,folder_id) VALUES(2,'/fixture/later.flac',1); INSERT INTO track_local(track_id,audio_file_id) VALUES(2,2);";
                await sql.ExecuteNonQueryAsync();
            }
            using var snapshot = JsonDocument.Parse(await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(root,"artist-metadata-queue.json")));
            Assert.Equal(3,snapshot.RootElement.GetArrayLength());
            var frozen = snapshot.RootElement.EnumerateArray().Single(item => item.GetProperty("RunId").GetString() == bulk.RunId);
            Assert.Equal(new[] {17L}, frozen.GetProperty("Run").GetProperty("TargetArtistIds").EnumerateArray().Select(id => id.GetInt64()));
            Assert.All(snapshot.RootElement.EnumerateArray(),item=>Assert.True(item.GetProperty("Request").GetProperty("ForceProviderRefresh").GetBoolean()));
            var restored = new LibraryArtistMetadataQueueService(repository, null!, null!, null!, env,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryArtistMetadataQueueService>.Instance);
            using var cancelled = new System.Threading.CancellationTokenSource();
            cancelled.Cancel();
            try { await restored.StartAsync(cancelled.Token); } catch (OperationCanceledException) { }
            Assert.Equal(runId,restored.GetRefreshStatus(runId)!.RunId);
            Assert.Equal(other.RunId,restored.GetRefreshStatus(other.RunId)!.RunId);
            Assert.Equal(1, restored.GetRefreshStatus(bulk.RunId)!.CacheRefresh.TotalArtists);
            restored.Dispose();
            Assert.Equal(retained, await System.IO.File.ReadAllTextAsync(statePath));
            Assert.True(await queue.ControlAsync(runId!, "pause", System.Threading.CancellationToken.None));
            var queuePath = System.IO.Path.Combine(root, "artist-metadata-queue.json");
            var pausedSnapshot = await System.IO.File.ReadAllTextAsync(queuePath);
            System.IO.File.Delete(queuePath); System.IO.Directory.CreateDirectory(queuePath);
            try
            {
                await Assert.ThrowsAnyAsync<System.IO.IOException>(() => queue.ControlAsync(runId!, "resume", System.Threading.CancellationToken.None));
                Assert.Equal("paused", queue.GetRefreshStatus(runId)!.RunState);
            }
            finally { System.IO.Directory.Delete(queuePath); await System.IO.File.WriteAllTextAsync(queuePath, pausedSnapshot); }
            Assert.True(await queue.ControlAsync(runId!, "resume", System.Threading.CancellationToken.None));
            Assert.True(await queue.ControlAsync(bulk.RunId!, "cancel", System.Threading.CancellationToken.None));
            Assert.NotNull(queue.GetRefreshStatus(runId)); Assert.NotNull(queue.GetRefreshStatus(other.RunId));
            // Paused legacy sweeps migrate into the same queue without unpausing or widening them.
            await (System.Threading.Tasks.Task)typeof(ArtistMetadataAutomationCoordinator)
                .GetMethod("ResumeInterruptedRunAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(coordinator, new object[] {System.Threading.CancellationToken.None})!;
            var migrated = JsonSerializer.Deserialize<ArtistMetadataAutomationState>(await System.IO.File.ReadAllTextAsync(statePath))!;
            Assert.Null(migrated.ActiveRun);
            using var migratedQueue = JsonDocument.Parse(await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(root,"artist-metadata-queue.json")));
            var legacy = migratedQueue.RootElement.EnumerateArray().Single(item => item.GetProperty("Run").GetProperty("Paused").GetBoolean());
            Assert.Equal(new[] {17L}, legacy.GetProperty("Run").GetProperty("TargetArtistIds").EnumerateArray().Select(id => id.GetInt64()));
            Assert.Equal("unfinished source work", legacy.GetProperty("Run").GetProperty("PendingNotice").GetString());
            Assert.Equal(2, legacy.GetProperty("Run").GetProperty("CacheAttempts").GetProperty("17:lastfm:biographies").GetInt32());
            var migratedId = legacy.GetProperty("RunId").GetString()!;
            var evidence = ArtistMetadataOutcomeJournal.Read(System.IO.Path.Combine(root, "artist-cache-runs", migratedId + ".ndjson"), migratedId);
            Assert.Equal("lastfm", Assert.Single(Assert.Single(evidence).CacheWork!).Provider);
            Assert.False(System.IO.File.Exists(legacyJournalPath));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); System.IO.Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("real")]
    [InlineData("placeholder")]
    [InlineData("outage")]
    public async System.Threading.Tasks.Task AppleBiographyRefreshUsesExistingPageAndRetainsFailures(string scenario)
    {
        var root = System.IO.Directory.CreateTempSubdirectory("apple-biography-refresh-").FullName;
        try
        {
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string,string?> { ["ConnectionStrings:Library"] = "Data Source=" + System.IO.Path.Combine(root,"library.db") }).Build();
            await new DeezSpoTag.Services.Library.LibraryDbService(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Library.LibraryDbService>.Instance).EnsureSchemaAsync();
            var repository = new DeezSpoTag.Services.Library.LibraryRepository(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Library.LibraryRepository>.Instance);
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(config.GetConnectionString("Library")))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = @"INSERT INTO artist(id,name) VALUES(17,'21 Savage');
INSERT INTO folder(id,root_path,display_name,enabled) VALUES(1,'/fixture','fixture',1);
INSERT INTO album(id,artist_id,title) VALUES(1,17,'');
INSERT INTO track(id,album_id,title) VALUES(1,1,'');
INSERT INTO audio_file(id,path,folder_id) VALUES(1,'/fixture/track.flac',1);
INSERT INTO track_local(track_id,audio_file_id) VALUES(1,1);";
                await command.ExecuteNonQueryAsync();
            }
            await repository.UpsertArtistSourceIdAsync(17,"apple","894820464",userConfirmed:true);
            if (scenario == "outage")
                await repository.UpsertArtistBiographyCacheAsync(17,"apple","894820464","Previously verified biography.",false,null,null);
            var settings = new DeezSpoTag.Services.Settings.DeezSpoTagSettingsService(Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>.Instance);
            foreach (var (field,value) in new[] { ("_configFolder",root),("_settingsFilePath",System.IO.Path.Combine(root,"config.json")) })
                settings.GetType().GetField(field,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(settings,value);
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(root,"config.json"),"{\"appleMusic\":{\"storefront\":\"us\"}}");
            using var memory = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
            using (var entry = memory.CreateEntry("apple:artist:us:894820464"))
                entry.Value = "{\"data\":[{\"id\":\"894820464\",\"attributes\":{\"name\":\"21 Savage\"}}]}";
            var factory = new AppleBiographyPageFactory(scenario);
            var catalog = new DeezSpoTag.Services.Apple.AppleMusicCatalogService(factory,settings,Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Apple.AppleMusicCatalogService>.Instance,memory);
            catalog.GetType().GetField("_cachedToken", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(catalog,"fixture-token");
            catalog.GetType().GetField("_cachedTokenExpiresAt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(catalog,DateTimeOffset.UtcNow.AddHours(1));
            var apple = new AppleArtistBiographyService(catalog,settings,factory,Microsoft.Extensions.Logging.Abstractions.NullLogger<AppleArtistBiographyService>.Instance);
            var cache = new DeezSpoTag.Services.Library.ArtistPageCacheRepository(config,Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Library.ArtistPageCacheRepository>.Instance);
            var artwork = new ArtistArtworkCatalogService(repository,null!,null!,null!,null!,null!,apple,null!,factory,new ProfileEnvironment(root),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtistArtworkCatalogService>.Instance);
            var service = new ArtistMetadataCacheRefreshService(repository,artwork,null!,apple,null!,null!,null!,null!,null!,null!,
                new UserPreferencesStore(new ProfileEnvironment(root),Microsoft.Extensions.Logging.Abstractions.NullLogger<UserPreferencesStore>.Instance),factory,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtistMetadataCacheRefreshService>.Instance,artistPageCache:cache,
                providerGate:new ArtistMetadataProviderGate(Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtistMetadataProviderGate>.Instance,_=>TimeSpan.Zero,TimeSpan.FromSeconds(5)));
            var result = await service.RefreshArtistWithOutcomeAsync(new ArtistMetadataCacheRefreshRequest(17,null,"apple",ForceProviderRefresh:true,Scope:ArtistCacheRefreshScope.Biographies),System.Threading.CancellationToken.None);
            Assert.True(factory.Calls == 1,JsonSerializer.Serialize(result));
            Assert.Equal(scenario != "outage",result.IsComplete);
            Assert.Equal(scenario == "outage" ? ArtistCacheWorkStatus.Failed : scenario == "placeholder" ? ArtistCacheWorkStatus.NoData : ArtistCacheWorkStatus.Succeeded,Assert.Single(result.Work).Status);
            var rows = await repository.GetArtistBiographyRowsAsync(17);
            if (scenario == "placeholder") Assert.Empty(rows);
            else Assert.Equal(scenario == "real" ? "A genuine artist biography." : "Previously verified biography.",Assert.Single(rows).Biography);
            if (scenario == "outage")
            {
                factory.Scenario = "real";
                var recovered = await service.RefreshArtistWithOutcomeAsync(new ArtistMetadataCacheRefreshRequest(17,null,"apple",ForceProviderRefresh:false,Scope:ArtistCacheRefreshScope.Biographies),System.Threading.CancellationToken.None);
                Assert.True(recovered.IsComplete);
                Assert.Equal(ArtistCacheWorkStatus.Succeeded,Assert.Single(recovered.Work).Status);
                Assert.Equal("A genuine artist biography.",Assert.Single(await repository.GetArtistBiographyRowsAsync(17)).Biography);
                Assert.Equal(2,factory.Calls);
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); System.IO.Directory.Delete(root,true); }
    }

    private sealed class AppleBiographyPageFactory(string scenario) : System.Net.Http.IHttpClientFactory
    {
        public int Calls;
        public string Scenario = scenario;
        public System.Net.Http.HttpClient CreateClient(string name) => new(new PageHandler(this));
        private sealed class PageHandler(AppleBiographyPageFactory owner) : System.Net.Http.HttpMessageHandler
        {
            protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,System.Threading.CancellationToken token)
            {
                if (request.RequestUri!.Host == "amp-api.music.apple.com")
                {
                    Assert.Contains("extend=artistBio,origin,bornOrFormed,isGroup", request.RequestUri.Query);
                    return System.Threading.Tasks.Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    { Content = new System.Net.Http.StringContent("{\"data\":[{\"id\":\"894820464\",\"attributes\":{\"name\":\"21 Savage\"}}]}") });
                }
                owner.Calls++;
                Assert.Equal("https://music.apple.com/us/artist/894820464",request.RequestUri!.AbsoluteUri);
                var description = owner.Scenario == "placeholder" ? "Listen to music by 21 Savage on Apple Music. Find top songs and albums by 21 Savage." : "A genuine artist biography.";
                return System.Threading.Tasks.Task.FromResult(new System.Net.Http.HttpResponseMessage(owner.Scenario == "outage" ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK)
                { Content = new System.Net.Http.StringContent("<script type=\"application/ld+json\">"+JsonSerializer.Serialize(new Dictionary<string,string> { ["@type"]="MusicGroup",["name"]="21 Savage",["url"]="https://music.apple.com/us/artist/21-savage/894820464",["description"]=description })+"</script>") });
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task ProviderFailurePreservesBiographyAndRecoveryPersistsCompletion(bool noBiography)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "artist-cache-flow-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var db = System.IO.Path.Combine(root, "library.db");
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:Library"] = "Data Source=" + db, ["Lastfm:ApiKey"] = "fixture" }).Build();
            await new DeezSpoTag.Services.Library.LibraryDbService(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Library.LibraryDbService>.Instance).EnsureSchemaAsync();
            var repository = new DeezSpoTag.Services.Library.LibraryRepository(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Library.LibraryRepository>.Instance);
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + db))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = @"INSERT INTO artist(id,name) VALUES(17,'Fixture Artist');
INSERT INTO folder(id,root_path,display_name,enabled) VALUES(1,'/fixture','fixture',1);
INSERT INTO album(id,artist_id,title) VALUES(1,17,'');
INSERT INTO track(id,album_id,title) VALUES(1,1,'');
INSERT INTO audio_file(id,path,folder_id) VALUES(1,'/fixture/track.flac',1);
INSERT INTO track_local(track_id,audio_file_id) VALUES(1,1);";
                await command.ExecuteNonQueryAsync();
            }
            await repository.UpsertArtistBiographyCacheAsync(17, "lastfm", "Fixture Artist", "Previously verified biography.", false, "Fixture Artist", "Fixture Artist");
            var factory = new ProfileFactory();
            var env = new ProfileEnvironment(root);
            var cache = new DeezSpoTag.Services.Library.ArtistPageCacheRepository(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Library.ArtistPageCacheRepository>.Instance);
            var lastfm = new LastFmArtistImageService(factory, config, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<LastFmArtistImageService>.Instance);
            var gate = new ArtistMetadataProviderGate(Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtistMetadataProviderGate>.Instance,
                _ => TimeSpan.Zero, TimeSpan.FromSeconds(1));
            var catalog = new ArtistArtworkCatalogService(repository, null!, null!, null!, null!, null!, null!, lastfm, factory, env,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtistArtworkCatalogService>.Instance, providerGate: gate, completionCache: cache);
            var diagnostic = new CacheLogger();
            var service = new ArtistMetadataCacheRefreshService(repository, catalog, null!, null!, null!, null!, lastfm, null!, null!, null!,
                new UserPreferencesStore(env, Microsoft.Extensions.Logging.Abstractions.NullLogger<UserPreferencesStore>.Instance), factory,
                diagnostic, artistPageCache: cache, providerGate: gate);
            var request = new ArtistMetadataCacheRefreshRequest(17, null, "lastfm", ForceProviderRefresh: true, Scope: ArtistCacheRefreshScope.Biographies);
            var failed = await service.RefreshArtistWithOutcomeAsync(request, System.Threading.CancellationToken.None);
            Assert.False(failed.IsComplete);
            Assert.Equal(ArtistCacheWorkStatus.Failed, Assert.Single(failed.Work).Status);
            Assert.Equal("Previously verified biography.", Assert.Single(await repository.GetArtistBiographyRowsAsync(17)).Biography);
            factory.Failed = false;
            factory.Empty = noBiography;
            var recovered = await service.RefreshArtistWithOutcomeAsync(request, System.Threading.CancellationToken.None);
            Assert.True(recovered.IsComplete, diagnostic.Last?.ToString() ?? JsonSerializer.Serialize(recovered));
            Assert.Equal(noBiography ? ArtistCacheWorkStatus.NoData : ArtistCacheWorkStatus.Succeeded, Assert.Single(recovered.Work).Status);
            Assert.True(await service.IsCompleteAsync(request with { ForceProviderRefresh = false }, System.Threading.CancellationToken.None));
            // Legacy visited-only evidence must be upgraded by the same durable worker.
            var configStore = new LibraryConfigStore(repository, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryConfigStore>.Instance, env);
            using (var legacyQueue = new LibraryArtistMetadataQueueService(repository, configStore, service, cache, env,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryArtistMetadataQueueService>.Instance))
            {
                var imported = new ArtistMetadataActiveRun { Operation = "cache-refresh", RunId = Guid.NewGuid().ToString("N"),
                    CacheRequest = request, TargetArtistIds = new() { 17 } };
                await legacyQueue.EnqueueRefreshAsync(request, System.Threading.CancellationToken.None, imported,
                    new[] { new ArtistRunOutcomeRecord(17, ArtistRunOutcomes.Succeeded) });
                await legacyQueue.StartAsync(System.Threading.CancellationToken.None);
                Assert.True(await legacyQueue.WaitForAttemptAsync(imported.RunId, System.Threading.CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10)));
                await legacyQueue.StopAsync(System.Threading.CancellationToken.None);
                Assert.Equal(0, (await service.GetRunResultAsync(imported.RunId, System.Threading.CancellationToken.None))!.Result.Failed);
            }
            var calls = factory.Calls;
            var revision = await cache.GetArtistRevisionAsync(17, default);
            Assert.Equal(revision, await cache.GetArtistRevisionAsync(17, default));
            var otherArtistRevision = await cache.GetArtistRevisionAsync(999, default);
            await using (var revisionConnection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
            {
                await revisionConnection.OpenAsync();
                await using var update = revisionConnection.CreateCommand();
                update.CommandText = "UPDATE artist_biography_cache SET biography = biography || ' Updated', selected = 1 - selected WHERE artist_id = 17;";
                await update.ExecuteNonQueryAsync();
            }
            Assert.NotEqual(revision, await cache.GetArtistRevisionAsync(17, default));
            Assert.Equal(otherArtistRevision, await cache.GetArtistRevisionAsync(999, default));
            var cached = await service.RefreshArtistWithOutcomeAsync(request with { ForceProviderRefresh = false }, System.Threading.CancellationToken.None);
            Assert.True(cached.IsComplete);
            Assert.Equal(calls, factory.Calls);
            var extraScope = await service.RefreshArtistWithOutcomeAsync(request with {
                ForceProviderRefresh = false, Scope = ArtistCacheRefreshScope.Biographies | ArtistCacheRefreshScope.Enrichment }, System.Threading.CancellationToken.None);
            Assert.True(extraScope.IsComplete);
            Assert.Equal(calls,factory.Calls); // Adding another scope cannot invalidate a completed biography stage.
            factory.Entered=new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Release=new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            var first=service.RefreshArtistWithOutcomeAsync(request,System.Threading.CancellationToken.None);
            await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var afterFirst=factory.Calls;
            var second=service.RefreshArtistWithOutcomeAsync(request with {ForceProviderRefresh=false},System.Threading.CancellationToken.None);
            using(var cancelled=new System.Threading.CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.RefreshArtistWithOutcomeAsync(request,cancelled.Token));
            }
            await System.Threading.Tasks.Task.Delay(30);
            Assert.Equal(afterFirst,factory.Calls);Assert.False(second.IsCompleted);
            factory.Release.SetResult();
            Assert.True((await first).IsComplete);Assert.True((await second).IsComplete);
            Assert.Equal(afterFirst,factory.Calls);
            factory.Entered=null;factory.Release=null;
            await VerifyBulkScopeAndRestartAsync(service, cache, env, factory, request, repository);
            await VerifyManualPriorityAsync(service, cache, env, factory, request, repository);
            await VerifyManualPreemptionAsync(service, cache, env, factory, request, repository);
            await VerifyHttpJobRecoveryAsync(service, cache, env, factory, request, repository);
            await VerifyLegacyAutomaticRecoveryAsync(service, cache, env, factory, repository);
            var primary = new LocationSource("musicbrainz", true);
            var secondary = new LocationSource("audiomack", false, hasLocation: true);
            var resolver = new DeezSpoTag.Web.Services.ArtistLocation.ArtistLocationResolver(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Web.Services.ArtistLocation.ArtistLocationResolver>.Instance,
                new DeezSpoTag.Web.Services.ArtistLocation.IArtistLocationSource[] {primary, secondary});
            var enrichedService = new ArtistMetadataCacheRefreshService(repository, catalog, null!, null!, null!, null!, lastfm, null!, null!, null!,
                new UserPreferencesStore(env, Microsoft.Extensions.Logging.Abstractions.NullLogger<UserPreferencesStore>.Instance), factory, diagnostic,
                locationResolver: resolver, artistPageCache: cache, providerGate: gate);
            var enrichmentRequest = request with {Source="auto",Scope=ArtistCacheRefreshScope.Enrichment};
            var enriched = await enrichedService.RefreshArtistWithOutcomeAsync(enrichmentRequest, System.Threading.CancellationToken.None);
            Assert.Equal(3,enriched.Work.Count);
            Assert.Equal("apple", enriched.Work[0].Provider);
            Assert.Contains(enriched.Work,work=>work.Provider=="musicbrainz"&&!work.IsComplete);
            Assert.Contains(enriched.Work,work=>work.Provider=="audiomack"&&work.Status==ArtistCacheWorkStatus.Succeeded);
            Assert.NotNull(await cache.TryGetRequiredAsync("artist-enrichment","17",System.Threading.CancellationToken.None));
            await enrichedService.RefreshArtistWithOutcomeAsync(enrichmentRequest with {ForceProviderRefresh=false},System.Threading.CancellationToken.None);
            Assert.Equal(2,primary.Calls);
            Assert.Equal(1,secondary.Calls);
            await using var failureConnection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + db);
            await failureConnection.OpenAsync();
            await using var failCommand = failureConnection.CreateCommand();
            failCommand.CommandText = "DROP TABLE artist_page_cache";
            await failCommand.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => service.RefreshArtistWithOutcomeAsync(request, System.Threading.CancellationToken.None));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); System.IO.Directory.Delete(root, true); }
    }

    private static async System.Threading.Tasks.Task VerifyBulkScopeAndRestartAsync(ArtistMetadataCacheRefreshService service,
        DeezSpoTag.Services.Library.ArtistPageCacheRepository cache, ProfileEnvironment env, ProfileFactory provider,
        ArtistMetadataCacheRefreshRequest request, DeezSpoTag.Services.Library.LibraryRepository repository)
    {
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + System.IO.Path.Combine(env.ContentRootPath, "library.db")))
        {
            await connection.OpenAsync(); await using var sql = connection.CreateCommand();
            sql.CommandText = "INSERT INTO folder(id,root_path,display_name,enabled) VALUES(2,'/other','other',1); INSERT INTO artist(id,name) VALUES(18,'Second Artist'),(19,'Outside Artist'); INSERT INTO album(id,artist_id,title) VALUES(18,18,''),(19,19,''); INSERT INTO track(id,album_id,title) VALUES(18,18,''),(19,19,''); INSERT INTO audio_file(id,path,folder_id) VALUES(18,'/fixture/second.flac',1),(19,'/other/outside.flac',2); INSERT INTO track_local(track_id,audio_file_id) VALUES(18,18),(19,19);";
            await sql.ExecuteNonQueryAsync();
        }
        var queueEnv = new ProfileEnvironment(System.IO.Path.Combine(env.ContentRootPath, "bulk-fixture"));
        var configStore = new LibraryConfigStore(repository, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryConfigStore>.Instance, queueEnv);
        LibraryArtistMetadataQueueService CreateQueue() => new(repository, configStore, service, cache, queueEnv,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryArtistMetadataQueueService>.Instance);
        var wasEmpty = provider.Empty;
        provider.Empty = false;
        provider.Failed = false; provider.Delay = false; provider.Release = null; provider.Entered = null;
        using var queue = CreateQueue();
        var bulk = await queue.EnqueueRefreshAsync(request with { ArtistId = null, FolderId = 1 }, System.Threading.CancellationToken.None);
        Assert.Equal(2, queue.GetRefreshStatus(bulk.RunId)!.CacheRefresh.TotalArtists);
        Assert.Equal(bulk.RunId, queue.GetRefreshStatus(null, 17)!.RunId);
        Assert.Equal(1, queue.GetRefreshStatus(bulk.RunId, 17)!.CacheRefresh.TotalArtists);
        Assert.Null(queue.GetRefreshStatus(bulk.RunId, 19));
        Assert.True(await queue.ControlAsync(bulk.RunId!, "pause", System.Threading.CancellationToken.None));
        await queue.StartAsync(System.Threading.CancellationToken.None);
        // A paused library run must not retain the global lock or block an artist request.
        var artist = await queue.EnqueueRefreshAsync(request, System.Threading.CancellationToken.None);
        Assert.True(await queue.WaitForAttemptAsync(artist.RunId!, System.Threading.CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("paused", queue.GetRefreshStatus(bulk.RunId)!.RunState);
        var secondStarted = new System.Threading.Tasks.TaskCompletionSource(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new System.Threading.Tasks.TaskCompletionSource(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        var batchCalls = 0;
        provider.OnRequest = () => { if (++batchCalls == 2) {provider.Release = release;secondStarted.TrySetResult();} };
        Assert.True(await queue.ControlAsync(bulk.RunId!, "resume", System.Threading.CancellationToken.None));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Second Artist", queue.GetRefreshStatus(bulk.RunId)!.CacheRefresh.CurrentArtist);
        Assert.Equal("idle", queue.GetRefreshStatus(bulk.RunId, 17)!.RunState);
        Assert.Equal("running", queue.GetRefreshStatus(bulk.RunId, 18)!.RunState);
        // Stop during the second artist. The first must already have durable completion evidence.
        await queue.StopAsync(System.Threading.CancellationToken.None);
        Assert.Equal(2, batchCalls);
        provider.OnRequest = null; provider.Release = null;
        var beforeRestart = provider.Calls;
        using var restored = CreateQueue();
        await restored.StartAsync(System.Threading.CancellationToken.None);
        try
        {
            Assert.True(await restored.WaitForAttemptAsync(bulk.RunId!, System.Threading.CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(beforeRestart + 1, provider.Calls);
            var completed = await service.GetRunResultAsync(bulk.RunId!, System.Threading.CancellationToken.None);
            Assert.Null(completed!.ArtistId);
            Assert.Equal(2, completed.Result.Total); Assert.Equal(2, completed.Result.Succeeded); Assert.Equal(0, completed.Result.Failed);
            Assert.NotEmpty(await repository.GetArtistBiographyRowsAsync(18));
            Assert.Empty(await repository.GetArtistBiographyRowsAsync(19));
        }
        finally { await restored.StopAsync(System.Threading.CancellationToken.None); provider.OnRequest = null; provider.Release = null; provider.Empty = wasEmpty; }
    }

    private static async System.Threading.Tasks.Task VerifyManualPriorityAsync(ArtistMetadataCacheRefreshService service,
        DeezSpoTag.Services.Library.ArtistPageCacheRepository cache, ProfileEnvironment env, ProfileFactory provider,
        ArtistMetadataCacheRefreshRequest request, DeezSpoTag.Services.Library.LibraryRepository repository)
    {
        var queueEnvironment = new ProfileEnvironment(System.IO.Path.Combine(env.ContentRootPath, "priority-fixture"));
        var configStore = new LibraryConfigStore(repository, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryConfigStore>.Instance, queueEnvironment);
        using var queue = new LibraryArtistMetadataQueueService(repository, configStore, service, cache, queueEnvironment,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryArtistMetadataQueueService>.Instance);
        provider.Failed = false;
        provider.Delay = false;
        queue.EnqueueArtists(new[] { new DeezSpoTag.Services.Library.NewlyIndexedArtist(17, "Fixture Artist") });
        var manual = await queue.EnqueueRefreshAsync(request, System.Threading.CancellationToken.None);
        bool? firstWasManual = null;
        provider.OnRequest = () => firstWasManual ??= queue.GetRefreshStatus(manual.RunId)?.RunState == "running";
        await queue.StartAsync(System.Threading.CancellationToken.None);
        try
        {
            Assert.True(await queue.WaitForAttemptAsync(manual.RunId!, System.Threading.CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(firstWasManual, "A manual request must run before the automatic backlog.");
        }
        finally { await queue.StopAsync(System.Threading.CancellationToken.None); provider.OnRequest = null; }

        // Reproduce the actual retained snapshot: an exhausted structured request with no Run.
        var restoredRoot = System.IO.Path.Combine(env.ContentRootPath, "manual-legacy-fixture");
        System.IO.Directory.CreateDirectory(restoredRoot);
        var retainedId = Guid.NewGuid().ToString("N");
        var pausedId = Guid.NewGuid().ToString("N");
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(restoredRoot, "artist-metadata-queue.json"),
            JsonSerializer.Serialize(new object[] {
                new { ArtistId = 17, ArtistName = "Fixture Artist", Attempts = 3, NeedsAttention = true,
                    Request = request, RunId = retainedId, QueueKey = -9, NextAttemptUtc = DateTimeOffset.UtcNow.AddDays(1) },
                new { ArtistId = 17, ArtistName = "Other paused scope", UserPaused = true,
                    Request = request with { Scope = ArtistCacheRefreshScope.Artwork }, RunId = pausedId, QueueKey = -10,
                    Run = new ArtistMetadataActiveRun { RunId = pausedId, Paused = true, TargetArtistIds = new() { 17 } } }
            }));
        using var restored = new LibraryArtistMetadataQueueService(repository, configStore, service, cache,
            new ProfileEnvironment(restoredRoot), Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryArtistMetadataQueueService>.Instance);
        await restored.StartAsync(System.Threading.CancellationToken.None);
        try
        {
            var before = provider.Calls;
            var resumed = await restored.EnqueueRefreshAsync(request, System.Threading.CancellationToken.None);
            Assert.Equal(retainedId, resumed.RunId);
            Assert.True(await restored.WaitForAttemptAsync(retainedId, System.Threading.CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(provider.Calls > before, "The restored artist request must actually fetch its biography.");
            Assert.Equal("paused", restored.GetRefreshStatus(pausedId)!.RunState);
            Assert.Contains(pausedId, await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(restoredRoot, "artist-metadata-queue.json")));
        }
        finally { await restored.StopAsync(System.Threading.CancellationToken.None); }
    }

    private static async System.Threading.Tasks.Task VerifyManualPreemptionAsync(ArtistMetadataCacheRefreshService service,
        DeezSpoTag.Services.Library.ArtistPageCacheRepository cache, ProfileEnvironment env, ProfileFactory provider,
        ArtistMetadataCacheRefreshRequest request, DeezSpoTag.Services.Library.LibraryRepository repository)
    {
        var queueEnvironment = new ProfileEnvironment(System.IO.Path.Combine(env.ContentRootPath, "preemption-fixture"));
        var configStore = new LibraryConfigStore(repository, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryConfigStore>.Instance, queueEnvironment);
        using var queue = new LibraryArtistMetadataQueueService(repository, configStore, service, cache, queueEnvironment,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryArtistMetadataQueueService>.Instance);
        var entered = new System.Threading.Tasks.TaskCompletionSource(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new System.Threading.Tasks.TaskCompletionSource(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Entered = entered; provider.Release = release; provider.Failed = false; provider.Delay = false;
        var automatic = await queue.EnqueueRefreshAsync(request with { ArtistId = 17, ForceProviderRefresh = true }, default, automatic: true);
        await queue.StartAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The active HTTP request keeps its captured release task; later requests may finish.
            provider.Release = null;
            var manual = await queue.EnqueueRefreshAsync(request with { ArtistId = 18, ForceProviderRefresh = true }, default);
            Assert.True(await queue.WaitForAttemptAsync(manual.RunId!, default).WaitAsync(TimeSpan.FromSeconds(5)),
                "The artist-page refresh must not wait for the current background artist to finish.");
            Assert.True(await queue.WaitForAttemptAsync(automatic.RunId!, default).WaitAsync(TimeSpan.FromSeconds(5)),
                "Yielding must preserve and resume the interrupted background artist.");
            Assert.Equal(0, (await service.GetRunResultAsync(automatic.RunId!, default))!.Result.Failed);
        }
        finally
        {
            release.TrySetResult(); provider.Release = null; provider.Entered = null;
            await queue.StopAsync(default);
        }
    }

    private static async System.Threading.Tasks.Task VerifyLegacyAutomaticRecoveryAsync(ArtistMetadataCacheRefreshService service,
        DeezSpoTag.Services.Library.ArtistPageCacheRepository cache, ProfileEnvironment env, ProfileFactory provider,
        DeezSpoTag.Services.Library.LibraryRepository repository)
    {
        var root = System.IO.Path.Join(env.AppDataRoot,"legacy-automatic-recovery");
        System.IO.Directory.CreateDirectory(root);
        var workerEnv = new ProfileEnvironment(root);
        var preferences = new UserPreferencesStore(env,Microsoft.Extensions.Logging.Abstractions.NullLogger<UserPreferencesStore>.Instance);
        var saved = await preferences.LoadAsync();
        saved.MetadataUpdaterSource = "lastfm";
        await preferences.SaveAsync(saved);
        await cache.ClearEntryAsync("artist-refresh","17:lastfm:biographies",default);
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Join(root,"artist-metadata-queue.json"),
            "[{\"ArtistId\":17,\"ArtistName\":\"Fixture Artist\",\"Attempts\":3,\"NeedsAttention\":true,\"NextAttemptUtc\":\"2026-01-01T00:00:00Z\"}]");
        var configStore = new LibraryConfigStore(repository,Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryConfigStore>.Instance,workerEnv);
        using var queue = new LibraryArtistMetadataQueueService(repository,configStore,service,cache,workerEnv,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryArtistMetadataQueueService>.Instance);
        var freshEnv = new ProfileEnvironment(System.IO.Path.Join(root,"fresh"));
        using (var freshQueue = new LibraryArtistMetadataQueueService(repository,configStore,service,cache,freshEnv,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryArtistMetadataQueueService>.Instance))
        {
            var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
            var type=typeof(LibraryArtistMetadataQueueService);
            var item=JsonSerializer.Deserialize("{\"ArtistId\":17,\"ArtistName\":\"Fixture Artist\"}",type.GetNestedType("QueueItem",System.Reflection.BindingFlags.NonPublic)!)!;
            var preparation=(System.Threading.Tasks.Task)type.GetMethod("PrepareAsync",flags)!.Invoke(freshQueue,new object[]{item,System.Threading.CancellationToken.None})!;
            await preparation;
            var prepared=preparation.GetType().GetProperty("Result")!.GetValue(preparation)!;
            Assert.Equal(false,prepared.GetType().GetProperty("UserPaused")!.GetValue(prepared));
        }
        provider.Failed=false;provider.Delay=false;
        var before = provider.Calls;
        await queue.StartAsync(default);
        try
        {
            var deadline=DateTimeOffset.UtcNow.AddSeconds(10);
            while(DateTimeOffset.UtcNow<deadline)
            {
                var completed = await cache.TryGetRequiredAsync("artist-refresh","17:lastfm:biographies",default);
                if (completed is not null && provider.Calls>before) break;
                await System.Threading.Tasks.Task.Delay(20);
            }
            Assert.True(provider.Calls>before,"The stranded automatic job never resumed provider work.");
            Assert.NotNull(await cache.TryGetRequiredAsync("artist-refresh","17:lastfm:biographies",default));
            Assert.NotEmpty(await repository.GetArtistBiographyRowsAsync(17));
        }
        finally { await queue.StopAsync(default); }
    }

    private static async System.Threading.Tasks.Task VerifyHttpJobRecoveryAsync(ArtistMetadataCacheRefreshService service,
        DeezSpoTag.Services.Library.ArtistPageCacheRepository cache, ProfileEnvironment env, ProfileFactory provider,
        ArtistMetadataCacheRefreshRequest request, DeezSpoTag.Services.Library.LibraryRepository repository)
    {
        var updater = (ArtistMetadataUpdaterService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ArtistMetadataUpdaterService));
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(ArtistMetadataUpdaterService).GetField("_statusLock", flags)!.SetValue(updater, new object());
        typeof(ArtistMetadataUpdaterService).GetField("_status", flags)!.SetValue(updater, MetadataUpdaterStatusSnapshot.Idle());
        var preferences = new UserPreferencesStore(env, Microsoft.Extensions.Logging.Abstractions.NullLogger<UserPreferencesStore>.Instance);
        var configStore = new LibraryConfigStore(repository, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryConfigStore>.Instance, env);
        var queue = new LibraryArtistMetadataQueueService(repository, configStore, service, cache, env,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryArtistMetadataQueueService>.Instance);
        ArtistMetadataAutomationCoordinator Create() => new(service, updater, preferences, env,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtistMetadataAutomationCoordinator>.Instance, queue);
        var coordinator = Create();
        var sharedPreferences = await preferences.LoadAsync();
        sharedPreferences.MetadataUpdaterSource = "lastfm";
        await preferences.SaveAsync(sharedPreferences);
        request = request with { ArtistId = null, FolderId = 1 };
        await queue.StartAsync(System.Threading.CancellationToken.None);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions {EnvironmentName="Development", ContentRootPath=env.ContentRootPath});
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(coordinator);
        builder.Services.AddSingleton(repository);
        builder.Services.AddSingleton(configStore);
        builder.Services.AddSingleton(service);
        builder.Services.AddSingleton(new LibraryArtistMetadataServices(null!, cache, null!, null!, null!, env, null!, null!, null!));
        builder.Services.AddAuthorization(options => options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build());
        builder.Services.AddControllersWithViews(options => options.Filters.Add(new Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryTokenAttribute {Order=int.MaxValue}))
            .AddApplicationPart(typeof(DeezSpoTag.Web.Controllers.Api.ArtistMetadataAutomationApiController).Assembly);
        await using var app = builder.Build();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
            var address = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.Single();
            using var client = new System.Net.Http.HttpClient {BaseAddress=new Uri(address)};
            async System.Threading.Tasks.Task WaitJob()
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    if (coordinator.GetStatus().RunState is "pending-retry" or "paused" or "idle") return;
                    await System.Threading.Tasks.Task.Delay(20);
                }
                Assert.Fail("Shared cache job did not reach a persisted outcome.");
            }
            foreach (var failure in new[] {"http503", "http429", "timeout", "required-write", "journal"})
            {
                var priorClock = coordinator.GetStatus().LastCacheRefreshUtc;
                provider.OnRequest = failure == "journal" ? () => System.IO.Directory.CreateDirectory(
                    System.IO.Path.Combine(env.ContentRootPath, "artist-cache-runs", coordinator.GetStatus().RunId + ".ndjson")) : null;
                provider.Failed = failure is "http503" or "http429";
                provider.FailureStatus = failure == "http429" ? System.Net.HttpStatusCode.TooManyRequests : System.Net.HttpStatusCode.ServiceUnavailable;
                provider.Delay = failure == "timeout";
                async System.Threading.Tasks.Task RenameBiographyTable(bool restore)
                {
                    await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+System.IO.Path.Combine(env.ContentRootPath,"library.db"));
                    await connection.OpenAsync(); await using var command=connection.CreateCommand();
                    command.CommandText=restore ? "ALTER TABLE fixture_unavailable_biography RENAME TO artist_biography_cache"
                        : "ALTER TABLE artist_biography_cache RENAME TO fixture_unavailable_biography";
                    await command.ExecuteNonQueryAsync();
                }
                if (failure == "required-write") {provider.Empty=false;await RenameBiographyTable(false);}
                var submitted = await client.PostAsJsonAsync("/api/library/artist-metadata/cache/refresh", request);
                Assert.True(submitted.IsSuccessStatusCode, await submitted.Content.ReadAsStringAsync());
                await WaitJob();
                Assert.Equal("pending-retry", coordinator.GetStatus().RunState);
                Assert.Equal(priorClock,coordinator.GetStatus().LastCacheRefreshUtc);
                var runId = coordinator.GetStatus().RunId!;
                var result = await service.GetRunResultAsync(runId, System.Threading.CancellationToken.None);
                if (failure == "journal") Assert.Null(result);
                else Assert.Equal(result!.Result.Total, result.Result.Failed);
                var restarted = Create();
                await (System.Threading.Tasks.Task)typeof(ArtistMetadataAutomationCoordinator).GetMethod("ResumeInterruptedRunAsync", flags)!
                    .Invoke(restarted, new object[] {System.Threading.CancellationToken.None})!;
                Assert.Equal(runId, restarted.GetStatus().RunId);
                Assert.Equal("pending-retry", restarted.GetStatus().RunState);
                if (failure == "required-write") await RenameBiographyTable(true);
                if (failure == "journal") System.IO.Directory.Delete(System.IO.Path.Combine(env.ContentRootPath, "artist-cache-runs", runId + ".ndjson"));
                provider.OnRequest=null;
                provider.Failed = false;
                provider.Delay = false;
                if (failure == "http429")
                {
                    // Advance only the isolated fixture's backoff clock; production limits remain unchanged.
                    var gate=(ArtistMetadataProviderGate)typeof(ArtistMetadataCacheRefreshService).GetField("_providerGate",flags)!.GetValue(service)!;
                    var backoff=(Dictionary<string,DateTimeOffset>)typeof(ArtistMetadataProviderGate).GetField("_rateLimitedUntil",flags)!.GetValue(gate)!;
                    lock(backoff) backoff["lastfm"]=DateTimeOffset.UtcNow.AddMinutes(-1);
                }
                var callsBeforeResume = provider.Calls;
                var paused = await client.PostAsJsonAsync("/api/library/artist-metadata/cache/pause", new {runId});
                Assert.True(paused.IsSuccessStatusCode, await paused.Content.ReadAsStringAsync());
                var resumed = await client.PostAsJsonAsync("/api/library/artist-metadata/cache/resume", new {runId});
                resumed.EnsureSuccessStatusCode();
                await WaitJob();
                Assert.Equal("idle", coordinator.GetStatus().RunState);
                Assert.NotNull((await service.GetSweepCompletionAsync(System.Threading.CancellationToken.None)).LastCacheRefreshUtc);
                Assert.Equal(0,(await service.GetRunResultAsync(runId, System.Threading.CancellationToken.None))!.Result.Failed);
                if (failure == "journal") Assert.Equal(callsBeforeResume + 1, provider.Calls); // Only the not-yet-visited second artist is fetched.
            }
            // Artist-page requests must operate independently of a retained bulk sweep, while
            // using the same provider executor and retaining partial work for explicit retry.
            var savedPreferences = await preferences.LoadAsync();
            savedPreferences.MetadataUpdaterSource = "lastfm";
            await preferences.SaveAsync(savedPreferences);
            var statePath = System.IO.Path.Combine(env.ContentRootPath,"library-artist-images","metadata-automation-state.json");
            var retained = JsonSerializer.Serialize(new ArtistMetadataAutomationState {ActiveRun = new ArtistMetadataActiveRun {
                Operation="cache-refresh",RunId="retained-fixture",Paused=true,CacheRequest=new(null,null,"auto") }});
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(statePath)!);
            await System.IO.File.WriteAllTextAsync(statePath,retained);
            async System.Threading.Tasks.Task<string> SubmitArtistPage()
            {
                using var response = await client.PostAsync("/api/library/artists/17/biographies/refresh",null);
                Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.True(payload.RootElement.GetProperty("queued").GetBoolean());
                return payload.RootElement.GetProperty("runId").GetString()!;
            }
            async System.Threading.Tasks.Task WaitArtistPage(string id,bool complete)
            {
                var deadline=DateTimeOffset.UtcNow.AddSeconds(10);
                while(DateTimeOffset.UtcNow<deadline)
                {
                    var result=await service.GetRunResultAsync(id,System.Threading.CancellationToken.None);
                    var status=queue.GetRefreshStatus(id);
                    if(result is not null && (complete ? status is null && result.Result.Failed==0 : status?.RunState=="pending-retry" && result.Result.Failed==1))return;
                    await System.Threading.Tasks.Task.Delay(20);
                }
                Assert.Fail("Artist-page job did not reach its expected persisted state.");
            }
            provider.Failed=false;provider.Delay=false;provider.OnRequest=null;
            // Manual-first scheduling itself is checked with a stopped queue in the scoped queue test.
            queue.EnqueueArtists(new[] {new DeezSpoTag.Services.Library.NewlyIndexedArtist(17,"Fixture Artist")});
            var pageRun=await SubmitArtistPage();
            await WaitArtistPage(pageRun,true);
            provider.OnRequest=null;
            Assert.Equal(retained,await System.IO.File.ReadAllTextAsync(statePath));
            provider.Failed=true;
            var failedPageRun=await SubmitArtistPage();
            await WaitArtistPage(failedPageRun,false);
            using(var response=await client.GetAsync("/api/library/artists/17/external-cache/status?runId="+failedPageRun))
            {
                response.EnsureSuccessStatusCode();
                using var status=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(failedPageRun,status.RootElement.GetProperty("status").GetProperty("runId").GetString());
                Assert.Equal("pending-retry",status.RootElement.GetProperty("status").GetProperty("runState").GetString());
                Assert.Equal(64, status.RootElement.GetProperty("revision").GetString()!.Length);
                Assert.Equal(failedPageRun, status.RootElement.GetProperty("runId").GetString());
            }
            var callsBeforeStatus = provider.Calls;
            using (var response = await client.GetAsync("/api/library/artists/17/external-cache/status"))
            {
                response.EnsureSuccessStatusCode();
                using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(failedPageRun, status.RootElement.GetProperty("runId").GetString());
                Assert.False(status.RootElement.GetProperty("complete").GetBoolean());
            }
            Assert.Equal(callsBeforeStatus, provider.Calls); // Reopening observes cached work; it does not fetch providers.
            provider.Failed=false;
            Assert.Equal(failedPageRun,await SubmitArtistPage());
            await WaitArtistPage(failedPageRun,true);
            using(var response=await client.PostAsync("/api/library/artists/17/external-cache/refresh",null))
            {
                Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
                using var payload=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.True(payload.RootElement.GetProperty("queued").GetBoolean());
                var fullRun=payload.RootElement.GetProperty("runId").GetString()!;
                Assert.NotEqual(failedPageRun,fullRun);
                // This fixture deliberately has no accessible Last.fm gallery. Full refresh must
                // retain that failure rather than falsely reporting a complete image cache.
                await WaitArtistPage(fullRun,false);
            }
            Assert.Equal(retained,await System.IO.File.ReadAllTextAsync(statePath));
        }
        finally {await queue.StopAsync(System.Threading.CancellationToken.None);queue.Dispose();await app.StopAsync();}
    }

    private sealed class CacheLogger : Microsoft.Extensions.Logging.ILogger<ArtistMetadataCacheRefreshService>
    {
        public Exception? Last;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state,
            Exception? exception, Func<TState,Exception?,string> formatter) { if (exception is not null) Last=exception; }
    }
    private sealed class ProfileFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Threading.Tasks.TaskCompletionSource? Entered,Release;
        public Action? OnRequest;
        public bool Failed = true;
        public bool Empty;
        public bool Delay;
        public System.Net.HttpStatusCode FailureStatus = System.Net.HttpStatusCode.ServiceUnavailable;
        public int Calls;
        public System.Net.Http.HttpClient CreateClient(string name) => new(new ProfileHandler(this));
    }
    private sealed class ProfileHandler(ProfileFactory owner) : System.Net.Http.HttpMessageHandler
    {
        protected override async System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken token)
        {
            owner.Calls++;
            owner.OnRequest?.Invoke();
            owner.Entered?.TrySetResult();
            if(owner.Release is not null) await owner.Release.Task.WaitAsync(token);
            if(owner.Delay) await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(2),token);
            var requestedArtist = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query)["artist"].FirstOrDefault() ?? "Fixture Artist";
            var json = JsonSerializer.Serialize(new { artist = new { name = requestedArtist, bio = new { content = owner.Empty ? "" : "Freshly verified biography." } } });
            return new System.Net.Http.HttpResponseMessage(owner.Failed ? owner.FailureStatus : System.Net.HttpStatusCode.OK)
                { Content = new System.Net.Http.StringContent(json) };
        }
    }
    private sealed class ProfileEnvironment(string root) : Microsoft.AspNetCore.Hosting.IWebHostEnvironment, IAppDataRootOverride
    {
        public string? AppDataRoot => root;
        public string EnvironmentName {get;set;} = "Development";
        public string ApplicationName {get;set;} = "Tests";
        public string ContentRootPath {get;set;} = root;
        public string WebRootPath {get;set;} = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider {get;set;} = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider {get;set;} = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Theory]
    [InlineData(ArtistCacheWorkStatus.Succeeded, true)]
    [InlineData(ArtistCacheWorkStatus.NoData, true)]
    [InlineData(ArtistCacheWorkStatus.NotApplicable, true)]
    [InlineData(ArtistCacheWorkStatus.Disabled, true)]
    [InlineData(ArtistCacheWorkStatus.Deferred, false)]
    [InlineData(ArtistCacheWorkStatus.AuthenticationRequired, false)]
    [InlineData(ArtistCacheWorkStatus.Failed, false)]
    [InlineData(ArtistCacheWorkStatus.TimedOut, false)]
    [InlineData(ArtistCacheWorkStatus.Cancelled, false)]
    public void OnlySettledStagesCompleteAnArtist(ArtistCacheWorkStatus status, bool complete)
    {
        var result = new ArtistCacheRefreshOutcome(3, new[] {
            new ArtistCacheWorkOutcome("spotify", "artwork", status, 6, 5, null, null),
            new ArtistCacheWorkOutcome("spotify", "biographies", ArtistCacheWorkStatus.Succeeded, 1, 1, null, null)
        });
        Assert.Equal(complete, result.IsComplete);
        Assert.Equal(complete ? 0 : 1, result.UnresolvedWork.Count);
    }

    [Fact]
    public void EmptyEvidenceDoesNotAssertCompletion()
        => Assert.False(new ArtistCacheRefreshOutcome(3, Array.Empty<ArtistCacheWorkOutcome>()).IsComplete);

    [Fact]
    public void CacheWorkSurvivesRunJournalSerialization()
    {
        var row = new ArtistRunOutcomeRecord(3, ArtistRunOutcomes.Failed,
            CacheWork: new[] {new ArtistCacheWorkOutcome("lastfm", "artwork", ArtistCacheWorkStatus.Deferred, 0, 0, "rate-limit", DateTimeOffset.UtcNow.AddMinutes(10))});
        var copy = JsonSerializer.Deserialize<ArtistRunOutcomeRecord>(JsonSerializer.Serialize(row))!;
        Assert.Single(copy.CacheWork!);
        Assert.True(copy.HasUnresolvedWork);
        Assert.Empty(copy.PendingWorkList);
    }
}
