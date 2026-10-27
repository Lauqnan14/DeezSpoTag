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
                    CacheRequest = new(null, null, "auto"), PendingNotice = "unfinished source work" } });
            await System.IO.File.WriteAllTextAsync(statePath, retained);
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
INSERT INTO album(id,artist_id,title) VALUES(1,17,'Fixture Album');
INSERT INTO track(id,album_id,title) VALUES(1,1,'Fixture Track');
INSERT INTO audio_file(id,path,folder_id) VALUES(1,'/fixture/track.flac',1);
INSERT INTO track_local(track_id,audio_file_id) VALUES(1,1);";
                await command.ExecuteNonQueryAsync();
            }
            var repository = new DeezSpoTag.Services.Library.LibraryRepository(config,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Library.LibraryRepository>.Instance);
            var queue = new LibraryArtistMetadataQueueService(repository, null!, null!, null!, env,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryArtistMetadataQueueService>.Instance);
            var coordinator = new ArtistMetadataAutomationCoordinator(null!, null!,
                new UserPreferencesStore(env, Microsoft.Extensions.Logging.Abstractions.NullLogger<UserPreferencesStore>.Instance), env,
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
            using var snapshot = JsonDocument.Parse(await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(root,"artist-metadata-queue.json")));
            Assert.Equal(2,snapshot.RootElement.GetArrayLength());
            Assert.All(snapshot.RootElement.EnumerateArray(),item=>Assert.True(item.GetProperty("Request").GetProperty("ForceProviderRefresh").GetBoolean()));
            var restored = new LibraryArtistMetadataQueueService(repository, null!, null!, null!, env,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryArtistMetadataQueueService>.Instance);
            using var cancelled = new System.Threading.CancellationTokenSource();
            cancelled.Cancel();
            try { await restored.StartAsync(cancelled.Token); } catch (OperationCanceledException) { }
            Assert.Equal(runId,restored.GetRefreshStatus(runId)!.RunId);
            Assert.Equal(other.RunId,restored.GetRefreshStatus(other.RunId)!.RunId);
            restored.Dispose();
            Assert.Equal(retained, await System.IO.File.ReadAllTextAsync(statePath));
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
INSERT INTO album(id,artist_id,title) VALUES(1,17,'Fixture Album');
INSERT INTO track(id,album_id,title) VALUES(1,1,'Fixture Track');
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
INSERT INTO album(id,artist_id,title) VALUES(1,17,'Fixture Album');
INSERT INTO track(id,album_id,title) VALUES(1,1,'Fixture Track');
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
            var legacyOutcomes = new List<ArtistRunOutcomeRecord>();
            await service.RefreshAsync(request, null,
                new ArtistRunResumeContext(new[] {new ArtistRunOutcomeRecord(17, ArtistRunOutcomes.Succeeded)}, new long[] {17}),
                legacyOutcomes.Add, System.Threading.CancellationToken.None);
            Assert.NotEmpty(Assert.Single(legacyOutcomes).CacheWork!);
            var calls = factory.Calls;
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
            await VerifyHttpJobRecoveryAsync(service, cache, env, factory, request, repository);
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
            Assert.Equal(2,enriched.Work.Count);
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
                var task = (System.Threading.Tasks.Task)typeof(ArtistMetadataAutomationCoordinator).GetField("_activeOperation", flags)!.GetValue(coordinator)!;
                await task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            foreach (var failure in new[] {"http503", "http429", "timeout", "required-write", "journal"})
            {
                var priorClock = coordinator.GetStatus().LastCacheRefreshUtc;
                provider.OnRequest = failure == "journal" ? () => typeof(ArtistMetadataAutomationCoordinator)
                    .GetField("_journalFailure",flags)!.SetValue(coordinator,new System.IO.IOException("fixture journal unavailable")) : null;
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
                Assert.Equal(failure == "journal" ? 0 : 1, result!.Result.Failed);
                var restarted = Create();
                await (System.Threading.Tasks.Task)typeof(ArtistMetadataAutomationCoordinator).GetMethod("ResumeInterruptedRunAsync", flags)!
                    .Invoke(restarted, new object[] {System.Threading.CancellationToken.None})!;
                Assert.Equal(runId, restarted.GetStatus().RunId);
                Assert.Equal("pending-retry", restarted.GetStatus().RunState);
                if (failure == "required-write") await RenameBiographyTable(true);
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
                var paused = await client.PostAsJsonAsync("/api/library/artist-metadata/cache/pause", new {runId});
                Assert.True(paused.IsSuccessStatusCode, await paused.Content.ReadAsStringAsync());
                var resumed = await client.PostAsJsonAsync("/api/library/artist-metadata/cache/resume", new {runId});
                resumed.EnsureSuccessStatusCode();
                await WaitJob();
                Assert.Equal("idle", coordinator.GetStatus().RunState);
                Assert.NotNull(coordinator.GetStatus().LastCacheRefreshUtc);
                Assert.Equal(0,(await service.GetRunResultAsync(runId, System.Threading.CancellationToken.None))!.Result.Failed);
            }
            // Artist-page requests must operate independently of a retained bulk sweep, while
            // using the same provider executor and retaining partial work for explicit retry.
            var savedPreferences = await preferences.LoadAsync();
            savedPreferences.MetadataUpdaterSource = "lastfm";
            await preferences.SaveAsync(savedPreferences);
            var statePath = System.IO.Path.Combine(env.ContentRootPath,"library-artist-images","metadata-automation-state.json");
            var retained = JsonSerializer.Serialize(new ArtistMetadataAutomationState {ActiveRun = new ArtistMetadataActiveRun {
                Operation="cache-refresh",RunId="retained-fixture",Paused=true,CacheRequest=new(null,null,"auto") }});
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
            queue.EnqueueArtists(new[] {new DeezSpoTag.Services.Library.NewlyIndexedArtist(17,"Fixture Artist")});
            var pageRun=await SubmitArtistPage();
            bool? firstWasManual=null;
            provider.OnRequest=()=>{firstWasManual ??= queue.GetRefreshStatus(pageRun)?.RunState=="running";};
            await queue.StartAsync(System.Threading.CancellationToken.None);
            await WaitArtistPage(pageRun,true);
            provider.OnRequest=null;
            Assert.True(firstWasManual,"A manual request must run before the automatic backlog.");
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
            }
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
            var json = JsonSerializer.Serialize(new { artist = new { name = "Fixture Artist", bio = new { content = owner.Empty ? "" : "Freshly verified biography." } } });
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
