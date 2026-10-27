using Microsoft.Extensions.Configuration;
using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtistMediaExtrasCacheServiceTest
{
    [Fact]
    public void ValidTidalVideoWithNullAlbumRemainsCacheable()
    {
        var service=(ArtistMediaExtrasCacheService)RuntimeHelpers.GetUninitializedObject(typeof(ArtistMediaExtrasCacheService));
        typeof(ArtistMediaExtrasCacheService).GetField("_json",BindingFlags.Instance|BindingFlags.NonPublic)!
            .SetValue(service,new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var document=System.Text.Json.JsonDocument.Parse("""{"id":17,"title":"Video","artist":{"id":9,"name":"Artist"},"album":null,"imageId":"a-b"}""");
        var result=(System.Collections.Generic.List<System.Text.Json.JsonElement>)typeof(ArtistMediaExtrasCacheService)
            .GetMethod("MapTidalVideos",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(service,
                new object[]{new[]{document.RootElement},"9","Artist"})!;
        var video=Assert.Single(result);
        Assert.Equal("Video",video.GetProperty("name").GetString());
        Assert.Equal("https://resources.tidal.com/images/a/b/750x750.jpg",video.GetProperty("image").GetString());
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task FailedCatalogResponseIsNotAnEmptySuccessfulSection(int status)
    {
        var service = (ArtistMediaExtrasCacheService)RuntimeHelpers.GetUninitializedObject(typeof(ArtistMediaExtrasCacheService));
        typeof(ArtistMediaExtrasCacheService).GetField("_httpClients", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, new Factory((HttpStatusCode)status));
        var task = (Task)typeof(ArtistMediaExtrasCacheService).GetMethod("SearchTidalAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, new object[] {"tracks", "Artist", 5, "fixture-token", "US", CancellationToken.None})!;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => task);
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
    }

    [Fact]
    public async Task StrictAudiomackProfileFailureIsNotACachedNegative()
    {
        var type = typeof(DeezSpoTag.Web.Services.Audiomack.AudiomackArtistLocationService);
        var service = RuntimeHelpers.GetUninitializedObject(type);
        type.GetField("_httpClientFactory", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, new Factory(HttpStatusCode.ServiceUnavailable));
        var method = type.GetMethod("TryFetchProfileAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(5, method.GetParameters().Length);
        var task = (Task)method.Invoke(service, new object[] {"alicios", "Alicios", CancellationToken.None, true, true})!;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => task);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task StrictMusicBrainzLocationPreservesProviderStatus(int status)
    {
        var client=new DeezSpoTag.Web.Services.AutoTag.MusicBrainzClient(new HttpClient(new Handler((HttpStatusCode)status)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Web.Services.AutoTag.MusicBrainzClient>.Instance);
        var source=new DeezSpoTag.Web.Services.ArtistLocation.MusicBrainzArtistLocationService(client,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Web.Services.ArtistLocation.MusicBrainzArtistLocationService>.Instance);
        var method=source.GetType().GetMethod("ResolveRequiredAsync");
        Assert.NotNull(method);
        var error=await Assert.ThrowsAsync<HttpRequestException>(()=>(Task)method!.Invoke(source,new object[] {17L,"Fixture Artist",CancellationToken.None})!);
        Assert.Equal((HttpStatusCode)status,error.StatusCode);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task StrictSpotifyQueryPreservesProviderStatus(int status)
    {
        var type=typeof(SpotifyPathfinderMetadataClient);
        var client=RuntimeHelpers.GetUninitializedObject(type);
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var field=type.GetField("_requiredRefresh",flags);
        Assert.NotNull(field);
        field!.SetValue(client,new AsyncLocal<bool> {Value=true});
        type.GetField("_httpClientFactory",flags)!.SetValue(client,new Factory((HttpStatusCode)status));
        type.GetField("_logger",flags)!.SetValue(client,Microsoft.Extensions.Logging.Abstractions.NullLogger<SpotifyPathfinderMetadataClient>.Instance);
        type.GetField("_jsonOptions",flags)!.SetValue(client,new System.Text.Json.JsonSerializerOptions());
        var context=Activator.CreateInstance(type.GetNestedType("PathfinderAuthContext",BindingFlags.NonPublic)!,new object[] {"fixture","fixture","fixture","fixture"});
        var method=type.GetMethod("QueryAsync",flags)!;
        var error=await Assert.ThrowsAsync<HttpRequestException>(()=>(Task)method.Invoke(client,new object[] {context!,new {operationName="fixture"},CancellationToken.None})!);
        Assert.Equal((HttpStatusCode)status,error.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SectionFailureRetainsOldVideosAndRetrySkipsSuccessfulAtmos(bool freshPreviousSuccess)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "artist-extras-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
                new System.Collections.Generic.Dictionary<string,string?> { ["ConnectionStrings:Library"]="Data Source="+System.IO.Path.Combine(root,"library.db") }).Build();
            await new DeezSpoTag.Services.Library.LibraryDbService(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Library.LibraryDbService>.Instance).EnsureSchemaAsync();
            var cache = new DeezSpoTag.Services.Library.ArtistPageCacheRepository(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Library.ArtistPageCacheRepository>.Instance);
            var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
            var oldVideo = System.Text.Json.JsonSerializer.SerializeToElement(new {name="old video"});
            await cache.UpsertRequiredAsync(ArtistMediaExtrasCacheService.CacheSource,"17:apple",
                System.Text.Json.JsonSerializer.Serialize(new ArtistMediaExtrasPayload("9",null,[],[oldVideo],false),json),DateTimeOffset.UtcNow.AddDays(-9),CancellationToken.None);
            var previousVideoAt=DateTimeOffset.UtcNow.AddHours(-1);
            if(freshPreviousSuccess) await cache.UpsertRequiredAsync("artist-media-section","17:apple:videos:9","{}",previousVideoAt,CancellationToken.None);
            var service = (ArtistMediaExtrasCacheService)RuntimeHelpers.GetUninitializedObject(typeof(ArtistMediaExtrasCacheService));
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            typeof(ArtistMediaExtrasCacheService).GetField("_cache",flags)!.SetValue(service,cache);
            typeof(ArtistMediaExtrasCacheService).GetField("_json",flags)!.SetValue(service,json);
            typeof(ArtistMediaExtrasCacheService).GetField("_logger",flags)!.SetValue(service,Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtistMediaExtrasCacheService>.Instance);
            var atmosCalls=0;
            var videosFail=true;
            Func<Task<System.Collections.Generic.IReadOnlyList<System.Text.Json.JsonElement>>> atmos=() => {
                atmosCalls++; return Task.FromResult<System.Collections.Generic.IReadOnlyList<System.Text.Json.JsonElement>>([System.Text.Json.JsonSerializer.SerializeToElement(new {name="new atmos"})]);};
            Func<Task<(System.Collections.Generic.IReadOnlyList<System.Text.Json.JsonElement>,bool)>> videos=()=> videosFail
                ? Task.FromException<(System.Collections.Generic.IReadOnlyList<System.Text.Json.JsonElement>,bool)>(new TimeoutException("fixture"))
                : Task.FromResult<(System.Collections.Generic.IReadOnlyList<System.Text.Json.JsonElement>,bool)>(([],false));
            var method=typeof(ArtistMediaExtrasCacheService).GetMethod("RefreshSectionsAsync",flags)!;
            Task Refresh(bool force=false)=> (Task)method.Invoke(service,new object?[] {17L,"apple","9",null,force,atmos,videos,CancellationToken.None})!;
            await Assert.ThrowsAsync<TimeoutException>(()=>Refresh(true));
            var partial=await service.TryGetAsync(17,"apple",CancellationToken.None);
            Assert.Equal("old video",Assert.Single(partial!.Videos).GetProperty("name").GetString());
            Assert.Equal("new atmos",Assert.Single(partial.Atmos).GetProperty("name").GetString());
            var completed=await cache.TryGetRequiredAsync("artist-media-section","17:apple:atmos:9",CancellationToken.None);
            Assert.NotNull(completed);
            var unfinished=(await cache.TryGetRequiredAsync("artist-media-section","17:apple:videos:9",CancellationToken.None))!;
            Assert.NotNull(unfinished);
            using(var evidence=System.Text.Json.JsonDocument.Parse(unfinished.PayloadJson)) Assert.False(evidence.RootElement.GetProperty("complete").GetBoolean());
            if(freshPreviousSuccess) Assert.Equal(previousVideoAt,unfinished.FetchedUtc);
            videosFail=false;
            await Refresh();
            Assert.Equal(1,atmosCalls);
            Assert.Equal(completed!.FetchedUtc,(await cache.TryGetRequiredAsync("artist-media-section","17:apple:atmos:9",CancellationToken.None))!.FetchedUtc);
            Assert.Empty((await service.TryGetAsync(17,"apple",CancellationToken.None))!.Videos);
        }
        finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();System.IO.Directory.Delete(root,true);}
    }

    private sealed class Factory(HttpStatusCode status) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(status));
    }
    private sealed class Handler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("fixture failure") });
    }
}
