using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.YouTube;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Controllers.Api;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class YouTubeBrowseTest
{
    private const string Fixture = """
    {"contents":{"sectionListRenderer":{"contents":[{"musicCarouselShelfRenderer":{"header":{"musicCarouselShelfBasicHeaderRenderer":{"title":{"runs":[{"text":"New albums & singles"}]}}},"contents":[{"musicTwoRowItemRenderer":{"title":{"runs":[{"text":"Album"}]},"navigationEndpoint":{"browseEndpoint":{"browseId":"MPREalbum123"}},"thumbnailRenderer":{"musicThumbnailRenderer":{"thumbnail":{"thumbnails":[{"url":"https://images.test/small"},{"url":"https://images.test/large"}]}}}}}]}},{"musicCarouselShelfRenderer":{"header":{"musicCarouselShelfBasicHeaderRenderer":{"title":{"runs":[{"text":"Moods & genres"}]}}},"contents":[{"musicNavigationButtonRenderer":{"buttonText":{"runs":[{"text":"Afrobeats"}]},"clickCommand":{"browseEndpoint":{"browseId":"FEmusic_moods_and_genres_category","params":"abc="}}}}]}},{"musicCarouselShelfRenderer":{"header":{"musicCarouselShelfBasicHeaderRenderer":{"title":{"runs":[{"text":"Trending"}]}}},"contents":[{"musicResponsiveListItemRenderer":{"flexColumns":[{"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Song"}]}}}],"navigationEndpoint":{"watchEndpoint":{"videoId":"song123"}}}}]}},{"gridRenderer":{"header":{"gridHeaderRenderer":{"title":{"runs":[{"text":"Genres"}]}}},"items":[{"musicNavigationButtonRenderer":{"buttonText":{"runs":[{"text":"Afrobeats"}]},"clickCommand":{"browseEndpoint":{"browseId":"FEmusic_moods_and_genres_category","params":"abc="}}}}]}}]}}}
    """;

    [Theory]
    [InlineData("PLabcdefghij", "playlist", "VLPLabcdefghij")]
    [InlineData("MPREalbum123", "album", "MPREalbum123")]
    public async Task Collections_PreserveMarketPagesAndRecordingIdentity(string id, string type, string normalizedId)
    {
        using var handler = new CollectionHandler(); using var http = new HttpClient(handler);
        var collection = await new YouTubeMusicSearchClient(http).GetCollectionAsync(id, type, "KE", "sw", CancellationToken.None);
        Assert.Equal(normalizedId, collection["id"]!.GetValue<string>());
        Assert.Equal("Collection", collection["title"]!.GetValue<string>());
        Assert.Equal(3, collection["tracks"]!.AsArray().Count);
        Assert.Equal("abcdefghijk", collection["tracks"]![0]!["videoId"]!.GetValue<string>());
        Assert.Equal("abcdefghijk", collection["tracks"]![1]!["videoId"]!.GetValue<string>());
        Assert.Equal("lmnopqrstuv", collection["tracks"]![2]!["videoId"]!.GetValue<string>());
        Assert.Equal("", collection["tracks"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(201, collection["tracks"]![0]!["duration"]!.GetValue<int>());
        foreach (var body in handler.Bodies)
        {
            Assert.Equal("KE", body["context"]!["client"]!["gl"]!.GetValue<string>());
            Assert.Equal("sw", body["context"]!["client"]!["hl"]!.GetValue<string>());
        }
        Assert.Equal("next", handler.Bodies[1]["continuation"]!.GetValue<string>());
        using var failing = new CollectionHandler { FailContinuation = true }; using var failingHttp = new HttpClient(failing);
        await Assert.ThrowsAsync<HttpRequestException>(() => new YouTubeMusicSearchClient(failingHttp).GetCollectionAsync("PLabcdefghij", "playlist", "US", "en", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => new YouTubeMusicSearchClient(http).GetCollectionAsync("https://evil.test/", "album", "KE", "en", CancellationToken.None));
    }

    [Fact]
    public async Task CollectionEndpoint_UsesConfiguredMarketAndRejectsForeignUrls()
    {
        var directory = Path.Combine(Path.GetTempPath(), "yt-collection-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
            typeof(DeezSpoTagSettingsService).GetField("_configFolder", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(settings, directory);
            var file = Path.Combine(directory, "config.json");
            File.WriteAllText(file, "{\"youTubeMusicCountry\":\"KE\",\"deezerLanguage\":\"sw\"}");
            typeof(DeezSpoTagSettingsService).GetField("_settingsFilePath", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(settings, file);
            using var handler = new CollectionHandler(); using var http = new HttpClient(handler);
            var controller = new YouTubeTracklistApiController(new YouTubeMusicSearchClient(http), settings, NullLogger<YouTubeTracklistApiController>.Instance);
            var result = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Get("MPREalbum123", "album"));
            var payload = JsonSerializer.SerializeToNode(result.Value)!;
            Assert.True(payload["available"]!.GetValue<bool>());
            Assert.Equal("MPREalbum123", payload["tracklist"]!["id"]!.GetValue<string>());
            Assert.Equal("KE", payload["tracklist"]!["country"]!.GetValue<string>());
            Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(await controller.Get("https://evil.test/", "playlist"));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class CollectionHandler : HttpMessageHandler
    {
        public bool FailContinuation { get; init; }
        public List<JsonObject> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject(); Bodies.Add(body);
            if (body["continuation"] is not null && FailContinuation) return new HttpResponseMessage(HttpStatusCode.BadGateway);
            JsonObject Row(string id) => JsonNode.Parse("""
            {"musicResponsiveListItemRenderer":{"playlistItemData":{"videoId":"abcdefghijk"},"fixedColumns":[{"musicResponsiveListItemFixedColumnRenderer":{"text":{"runs":[{"text":"3:21"}]}}}],"flexColumns":[{"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Song"}]}}},{"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Artist","navigationEndpoint":{"browseEndpoint":{"browseId":"UCartist"}}}]}}}]}}
            """.Replace("abcdefghijk", id))!.AsObject();
            var rows = body["continuation"] is null ? new JsonArray(Row("abcdefghijk")) : new JsonArray(Row("abcdefghijk"), Row("lmnopqrstuv"));
            JsonObject result;
            if (body["continuation"] is null)
                result = new JsonObject { ["header"] = new JsonObject { ["musicResponsiveHeaderRenderer"] = new JsonObject { ["title"] = new JsonObject { ["runs"] = new JsonArray(new JsonObject { ["text"] = "Collection" }) } } },
                    ["contents"] = new JsonObject { ["musicPlaylistShelfRenderer"] = new JsonObject { ["contents"] = rows, ["continuations"] = new JsonArray(new JsonObject { ["nextContinuationData"] = new JsonObject { ["continuation"] = "next" } }) } } };
            else result = new JsonObject { ["continuationContents"] = new JsonObject { ["musicPlaylistShelfContinuation"] = new JsonObject { ["contents"] = rows } } };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result.ToJsonString()) };
        }
    }

    [Fact]
    public void QueueMetadata_PreservesExactIdAlbumAndDuration()
    {
        using var document = JsonDocument.Parse("""
        {"queueDatas":[{"content":{"playlistPanelVideoRenderer":{"videoId":"abcdefghijk","title":{"runs":[{"text":"Song"}]},"longBylineText":{"runs":[{"text":"Artist","navigationEndpoint":{"browseEndpoint":{"browseId":"UCartist"}}},{"text":" • "},{"text":"Album","navigationEndpoint":{"browseEndpoint":{"browseId":"MPREalbum"}}}]},"lengthText":{"runs":[{"text":"3:21"}]}}}}]}
        """);
        var method = typeof(YouTubeMusicSearchClient).GetMethod("ParseQueueSongMetadata", BindingFlags.Static | BindingFlags.Public);
        Assert.NotNull(method);
        var track = Assert.IsType<YouTubeMusicSearchResult>(method!.Invoke(null, [document.RootElement, "abcdefghijk"]));
        Assert.Equal("Song", track.Title);
        Assert.Equal("Album", track.Album);
        Assert.Equal(201, track.DurationSeconds);
        Assert.Equal("Artist", track.Artist);
        Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [document.RootElement, "xxxxxxxxxxx"]));
    }

    [Fact]
    public void TrendingResponsiveRows_PreserveArtistLineForSharedRenderer()
    {
        using var document = JsonDocument.Parse("""
        {"musicResponsiveListItemRenderer":{"playlistItemData":{"videoId":"dQw4w9WgXcQ"},"navigationEndpoint":{"watchEndpoint":{"videoId":"dQw4w9WgXcQ"}},"flexColumns":[
          {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Song"}]}}},
          {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Artist","navigationEndpoint":{"browseEndpoint":{"browseId":"UCartist"}}},{"text":" • "},{"text":"3M views"},{"text":" • "},{"text":"3:00"}]}}}
        ]}}
        """);
        var row = YouTubeMusicSearchClient.MapBrowseSections(document.RootElement)[0]!["items"]![0]!;
        Assert.Equal("Song", row["title"]!.GetValue<string>());
        Assert.Equal("Artist", row["artist"]!.GetValue<string>());
        Assert.Equal("Artist", row["subtitle"]!.GetValue<string>());
    }

    [Fact]
    public void Categories_PreserveProviderGridGroups()
    {
        using var document = JsonDocument.Parse("""
        {"contents":[{"gridRenderer":{"header":{"gridHeaderRenderer":{"title":{"runs":[{"text":"Moods"}]}}},"items":[{"musicNavigationButtonRenderer":{"buttonText":{"runs":[{"text":"Chill"}]},"solid":{"leftStripeColor":4278190335},"clickCommand":{"browseEndpoint":{"browseId":"FEmusic_moods_and_genres_category","params":"mood"}}}}]}},{"gridRenderer":{"header":{"gridHeaderRenderer":{"title":{"runs":[{"text":"Genres"}]}}},"items":[{"musicNavigationButtonRenderer":{"buttonText":{"runs":[{"text":"Jazz"}]},"clickCommand":{"browseEndpoint":{"browseId":"FEmusic_moods_and_genres_category","params":"genre"}}}}]}}]}
        """);
        var sections = YouTubeMusicSearchClient.MapBrowseSections(document.RootElement, true);
        Assert.Equal(2, sections.Count);
        Assert.Equal("Moods", sections[0]!["title"]!.GetValue<string>());
        Assert.Equal("Genres", sections[1]!["title"]!.GetValue<string>());
        Assert.Equal("mood", sections[0]!["items"]![0]!["params"]!.GetValue<string>());
        Assert.Equal(4278190335L, sections[0]!["items"]![0]!["stripeColor"]!.GetValue<long>());
    }

    [Fact]
    public void LiveRendererShapes_PreserveTitlesArtworkNavigationAndOrder()
    {
        using var document = JsonDocument.Parse(Fixture);
        var mapped = YouTubeMusicSearchClient.MapBrowseSections(document.RootElement);
        Assert.Equal(3, mapped.Count);
        Assert.Equal("New albums & singles", mapped[0]!["title"]!.GetValue<string>());
        Assert.Equal("https://images.test/large", mapped[0]!["items"]![0]!["coverUrl"]!.GetValue<string>());
        Assert.Equal("song123", mapped[2]!["items"]![0]!["id"]!.GetValue<string>());
        Assert.Contains("music.youtube.com/watch", mapped[2]!["items"]![0]!["url"]!.GetValue<string>());
        var categories = YouTubeMusicSearchClient.MapBrowseSections(document.RootElement, true);
        Assert.Equal("Afrobeats", categories[0]!["items"]![0]!["title"]!.GetValue<string>());
        Assert.Equal("abc=", categories[0]!["items"]![0]!["params"]!.GetValue<string>());
    }

    [Fact]
    public void MarketRanking_UsesExistingRegionsAndInterleavesSources()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "DeezSpoTag.Web", "wwwroot"))) root = root.Parent;
        Assert.NotNull(root);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = new YouTubeBrowseApiController(null!, null!, cache, NullLogger<YouTubeBrowseApiController>.Instance,
            new BrowseEnvironment { WebRootPath = Path.Combine(root.FullName, "DeezSpoTag.Web", "wwwroot") });
        var regionMethod = typeof(YouTubeBrowseApiController).GetMethod("MarketRegions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var rankMethod = typeof(YouTubeBrowseApiController).GetMethod("MarketRank", BindingFlags.NonPublic | BindingFlags.Static)!;
        var kenya = (string[])regionMethod.Invoke(controller, ["KE"])!;
        var britain = (string[])regionMethod.Invoke(controller, ["GB"])!;
        Assert.Contains("Africa", kenya); Assert.Contains("Europe", britain);
        Assert.Equal(0, rankMethod.Invoke(null, ["Kenyan favourites", "KE", kenya]));
        Assert.Equal(1, rankMethod.Invoke(null, ["African essentials", "KE", kenya]));
        Assert.Equal(2, rankMethod.Invoke(null, ["Indian favourites", "KE", kenya]));
        Assert.Equal(2, rankMethod.Invoke(null, ["Blues Mix by Ken E", "KE", kenya]));
        Assert.Equal(1, rankMethod.Invoke(null, ["European favourites", "GB", britain]));
        var interleave = typeof(YouTubeBrowseApiController).GetMethod("Interleave", BindingFlags.NonPublic | BindingFlags.Static)!;
        var sources = new[] { new[] { new JsonObject { ["title"] = "A1" }, new JsonObject { ["title"] = "A2" } }, new[] { new JsonObject { ["title"] = "B1" } } };
        var mixed = (IEnumerable<JsonObject>)interleave.Invoke(null, [sources])!;
        Assert.Equal(new[] { "A1", "B1", "A2" }, System.Linq.Enumerable.Select(mixed, item => item["title"]!.GetValue<string>()));
    }

    private sealed class BrowseEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public async Task GlobalChartSelection_PreservesClientMarketAndValidatesSelection()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        using var result = await new YouTubeMusicSearchClient(http).BrowseAsync("FEmusic_charts", "KE", "en", null, CancellationToken.None, chartSelection: "XY");
        var body = JsonNode.Parse(handler.Bodies[0])!;
        Assert.Equal("KE", body["context"]!["client"]!["gl"]!.GetValue<string>());
        Assert.Equal("XY", body["formData"]!["selectedValues"]![0]!.GetValue<string>());
        using var menu = JsonDocument.Parse("""
        {"musicMultiSelectMenuItemRenderer":{"title":{"runs":[{"text":"Global"}]},"formItemEntityKey":"PLACEHOLDER"}}
        """.Replace("PLACEHOLDER", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("explore_charts_country_menu_123XY "))));
        Assert.Equal("XY", YouTubeMusicSearchClient.FindGlobalChartSelection(menu.RootElement));
        handler.WrongChart = true;
        await Assert.ThrowsAsync<JsonException>(() => new YouTubeMusicSearchClient(http).BrowseAsync("FEmusic_charts", "KE", "en", null, CancellationToken.None, "XY"));
    }

    [Fact]
    public async Task ChartRequests_SelectCountryExplicitlyAndPreserveLanguage()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new YouTubeMusicSearchClient(http);
        foreach (var country in new[] { "KE", "US" })
        {
            using var result = await client.BrowseAsync("FEmusic_charts", country, "en", null, CancellationToken.None);
            using var request = JsonDocument.Parse(handler.Bodies[^1]);
            Assert.Equal(country, request.RootElement.GetProperty("context").GetProperty("client").GetProperty("gl").GetString());
            Assert.Equal(country, request.RootElement.GetProperty("formData").GetProperty("selectedValues")[0].GetString());
            Assert.Equal("en", request.RootElement.GetProperty("context").GetProperty("client").GetProperty("hl").GetString());
        }
        await Assert.ThrowsAsync<ArgumentException>(() => client.BrowseAsync("https://evil.test", "KE", "en", null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.BrowseAsync("FEmusic_explore", "Kenya", "en", null, CancellationToken.None));
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task ControllerCache_IsolatesMarketsAndDoesNotReplaceSuccessWithFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "yt-browse-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
            typeof(DeezSpoTagSettingsService).GetField("_configFolder", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(settings, directory);
            var file = Path.Combine(directory, "config.json");
            typeof(DeezSpoTagSettingsService).GetField("_settingsFilePath", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(settings, file);
            void Market(string country) {
                File.WriteAllText(file, JsonSerializer.Serialize(new { youTubeMusicCountry=country, deezerLanguage="en" }));
                typeof(DeezSpoTagSettingsService).GetField("_cachedSettingsJson", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(settings, null);
            }
            using var handler = new Handler(); using var http = new HttpClient(handler); using var cache = new MemoryCache(new MemoryCacheOptions());
            var controller = new YouTubeBrowseApiController(new YouTubeMusicSearchClient(http), settings, cache, NullLogger<YouTubeBrowseApiController>.Instance);
            Market("KE");
            var kenya = await controller.LoadAsync("categories", null, CancellationToken.None);
            await controller.LoadAsync("categories", null, CancellationToken.None);
            Assert.Single(handler.Bodies);
            Market("US"); await controller.LoadAsync("categories", null, CancellationToken.None);
            Assert.Equal(2, handler.Bodies.Count);
            handler.Fail=true;
            await Assert.ThrowsAsync<HttpRequestException>(()=>controller.LoadAsync("categories", null, CancellationToken.None, refresh:true));
            var retained=await controller.LoadAsync("categories", null, CancellationToken.None);
            Assert.Equal("US", retained["country"]!.GetValue<string>());
            Assert.Equal("KE", kenya["country"]!.GetValue<string>());
            Assert.Equal(3, handler.Bodies.Count);
        }
        finally { Directory.Delete(directory,true); }
    }

    [Fact]
    public async Task BackgroundWorker_WarmsHomepageAndCategoriesAndStops()
    {
        var directory=Path.Combine(Path.GetTempPath(),"yt-worker-test-"+Guid.NewGuid());Directory.CreateDirectory(directory);
        try {
            var settings=new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
            typeof(DeezSpoTagSettingsService).GetField("_configFolder",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(settings,directory);
            var file=Path.Combine(directory,"config.json");File.WriteAllText(file,"{\"youTubeMusicCountry\":\"KE\",\"deezerLanguage\":\"en\"}");
            typeof(DeezSpoTagSettingsService).GetField("_settingsFilePath",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(settings,file);
            using var handler=new Handler();using var http=new HttpClient(handler);using var cache=new MemoryCache(new MemoryCacheOptions());
            using var services=new ServiceCollection().AddSingleton(settings).AddSingleton(new YouTubeMusicSearchClient(http)).AddSingleton<IMemoryCache>(cache)
                .AddSingleton<Microsoft.Extensions.Logging.ILogger<YouTubeBrowseApiController>>(NullLogger<YouTubeBrowseApiController>.Instance).BuildServiceProvider();
            using var worker=new YouTubeBrowseRefreshHostedService(services.GetRequiredService<IServiceScopeFactory>(),NullLogger<YouTubeBrowseRefreshHostedService>.Instance);
            await worker.StartAsync(CancellationToken.None);
            using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while(handler.Bodies.Count<5) await Task.Delay(10,limit.Token);
            await worker.StopAsync(limit.Token);
            Assert.Equal(5,handler.Bodies.Count);
            var controller=new YouTubeBrowseApiController(new YouTubeMusicSearchClient(http),settings,cache,NullLogger<YouTubeBrowseApiController>.Instance);
            await controller.LoadAsync("sections",null,CancellationToken.None);await controller.LoadAsync("categories",null,CancellationToken.None);
            Assert.Equal(5,handler.Bodies.Count);
        } finally {Directory.Delete(directory,true);}
    }

    [Fact]
    public void Browser_NativeLinksAndThirdProviderPreserveExistingVideoOrder()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Join(root.FullName,"DeezSpoTag.Web","wwwroot","js","home-index.js"))) root=root.Parent;
        Assert.NotNull(root);
        const string script="""
        const fs=require('fs'),vm=require('vm'),assert=require('assert'),root=process.argv[1];
        const ctx={URLSearchParams,escapeHtml:s=>String(s||'').replaceAll('"','&quot;'),location:{search:''},console};ctx.globalThis=ctx;vm.createContext(ctx);
        vm.runInContext(fs.readFileSync(root+'/DeezSpoTag.Web/wwwroot/js/home-view-helpers.js','utf8'),ctx);
        const homeSource=fs.readFileSync(root+'/DeezSpoTag.Web/wwwroot/js/home-index.js','utf8');
        for (const name of ['buildHomeTrendingPlayAttributes','buildHomeTrendingPlayButton','renderHomeTrendingSongItem']) {
          const start=homeSource.indexOf('function '+name+'('),end=homeSource.indexOf('\nfunction ',start+1);
          vm.runInContext(homeSource.slice(start,end),ctx);
        }
        Object.assign(ctx,{
          buildHomeTrendingCardInteractivity:()=>({spotifyAttr:'',clickAttr:'',clickableAttr:''}),
          resolveHomeTrendingCover:()=>'',buildHomeTrendingSubtitle:()=>'',resolveHomeTrendingDeezerTrackId:()=>'',resolveHomeTrendingSpotifyTrackUrl:()=>'',
          normalizeHomeDeezerPlaybackContext:()=>null,appendHomeTrendingUrlAttributes:()=>{},appendHomeTrendingTrackMetadataAttributes:()=>{}
        });
        const trendingItem={id:'dQw4w9WgXcQ',type:'track',title:'Song',source:'ytmusic',url:'https://music.youtube.com/watch?v=dQw4w9WgXcQ'};
        const shared=ctx.renderHomeTrendingSongItem(trendingItem);
        const youtube=ctx.HomeViewHelpers.renderYouTubeBrowseItem(trendingItem,ctx.renderHomeTrendingSongItem);
        assert.equal(youtube,shared);assert.equal((youtube.match(/<button /g)||[]).length,1);assert(youtube.includes('playHomeTrendingTrackInApp(this)'));
        const card=ctx.HomeViewHelpers.renderYouTubeBrowseItem({type:'playlist',id:'VLPLabcdefghij',title:'Playlist',coverUrl:'https://i.ytimg.com/vi/123/hqdefault.jpg',url:'https://music.youtube.com/playlist?list=PLabcdefghij'},ctx.HomeViewHelpers.renderTopGenresItem);assert(card.includes('/Tracklist'));assert(card.includes('source=ytmusic'));assert(!card.includes('globalThis.open'));assert.equal(ctx.HomeViewHelpers.buildYouTubeBrowseClick({type:'album',id:'MPREalbum123',url:'https://music.youtube.com/browse/MPREalbum123'}),"globalThis.location.href='/Tracklist?id=MPREalbum123&type=album&source=ytmusic'");assert(card.includes('top-genres-card'));assert(!card.includes('youtube-browse-card'));
        assert.equal(ctx.HomeViewHelpers.renderYouTubeBrowseItem({url:'https://evil.test/'}),'');
        const home=fs.readFileSync(root+'/DeezSpoTag.Web/wwwroot/js/home-index.js','utf8');
        function load(name){const a=home.indexOf('function '+name+'('),b=home.indexOf('\nfunction ',a+1);assert(a>=0);vm.runInContext(home.slice(a,b<0?undefined:b),ctx)};
        vm.runInContext("let homeTrendingProvider='spotify',homeLatestSections=[],homeYouTubeStatus='ready',homeYouTubeSections=[{role:'trending',title:'Trending 20 Kenya',items:[{title:'Song',url:'https://music.youtube.com/watch?v=123'}]}];",ctx);
        ctx.renderHomeTrendingSongItem=item=>`<div class="home-top-song-item">${ctx.HomeViewHelpers.buildYouTubeBrowseClick(item)}</div>`;ctx.renderHomeSectionsWithLazyImages=()=>{};load('selectHomeTrendingProvider');load('renderHomeTrendingProviders');load('renderYouTubeHomeTrending');
        ctx.selectHomeTrendingProvider('ytmusic');assert(ctx.renderHomeTrendingProviders().includes('YouTube Music'));assert(!ctx.renderYouTubeHomeTrending().includes('Trending 20 Kenya'));assert(ctx.renderYouTubeHomeTrending().includes('home-top-song-item'));
        assert(home.indexOf("container.insertAdjacentHTML('beforeend', renderYouTubeHomeRows())")<home.indexOf("container.insertAdjacentHTML('beforeend', renderAppleHomeRows())"));
        const categories=fs.readFileSync(root+'/DeezSpoTag.Web/Views/Categories/Index.cshtml','utf8'),elements={};
        ctx.document={addEventListener:()=>{},createElement:()=>({textContent:'',get innerHTML(){return String(this.textContent||'').replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;')}}),getElementById:id=>elements[id]||(elements[id]={textContent:'',innerHTML:'',hidden:false})};
        vm.runInContext(categories.slice(categories.indexOf('<script>')+8,categories.lastIndexOf('</script>')),ctx);
        let calls=0;ctx.fetch=async(url)=>{calls++;return {ok:true,json:async()=>({success:true,country:'KE',categories:[{title:'Afrobeats',type:'browse-category',browseId:'FEmusic_moods_and_genres_category',params:'abc=',url:'https://music.youtube.com/browse/FEmusic_moods_and_genres_category'}],sections:url.endsWith('/categories')?[{title:'Genres',items:[{title:'Afrobeats',type:'browse-category',browseId:'FEmusic_moods_and_genres_category',url:'https://music.youtube.com/browse/FEmusic_moods_and_genres_category'}]}]:[{title:'Playlists',items:[{title:'Playlist',url:'https://music.youtube.com/playlist?list=123'}]}]})}};
        (async()=>{
          await ctx.loadYouTubeCategories();assert(elements['youtube-categories'].innerHTML.includes('Afrobeats'));assert(elements['youtube-categories'].innerHTML.includes('top-genres-card'));assert(!elements['youtube-categories'].innerHTML.includes('youtube-browse-card'));assert(elements['youtube-status'].textContent.includes('KE'));
          await ctx.openYouTubeCategory('FEmusic_moods_and_genres_category','abc%3D');assert(elements['youtube-categories'].innerHTML.includes('Playlist'));assert.equal(elements['youtube-back'].hidden,false);
          await ctx.loadYouTubeCategories();assert.equal(calls,2);assert.equal(elements['youtube-back'].hidden,true);
          let release;ctx.fetch=()=>new Promise(resolve=>release=resolve);const pending=ctx.openYouTubeCategory('FEmusic_new_releases','');assert(elements['youtube-categories'].innerHTML.includes('Afrobeats'));
          release({ok:false,json:async()=>({success:false,error:'Unavailable'})});await pending;assert(elements['youtube-categories'].innerHTML.includes('Afrobeats'));assert(elements['youtube-status'].textContent.includes('Unavailable'));
          const tracklist=fs.readFileSync(root+'/DeezSpoTag.Web/Views/Tracklist/Index.cshtml','utf8');
          const hydrationStart=tracklist.indexOf('async function hydrateBrowseTrackMetadata('),hydrationEnd=tracklist.indexOf('\nasync function loadTracklist(',hydrationStart+1);
          vm.runInContext(tracklist.slice(hydrationStart,hydrationEnd),ctx);
          ctx.tracklistSource='apple';const previousDocument=ctx.document;ctx.document={querySelector:()=>null};let metadataUrl;
          ctx.fetch=async url=>{metadataUrl=url;return {ok:true,json:async()=>({tracklist:{tracks:[{id:'123',album:{title:'Catalog Album'},duration:201}]}})}};
          const appleTrack={album:{title:''},duration:0,link:'/album/example/1?i=123'};
          await ctx.hydrateBrowseTrackMetadata([appleTrack],[{appleId:'123'}],'ke');
          assert(metadataUrl.includes('appleUrl=https%3A%2F%2Fmusic.apple.com%2Fke%2Falbum'));assert.equal(appleTrack.album.title,'Catalog Album');assert.equal(appleTrack.duration,201);ctx.document=previousDocument;
          let playbackRequest,downloadRequest;
          ctx.localStorage={getItem:()=> '60'};
          ctx.DeezerUnifiedPlayback={play:async request=>{playbackRequest=request;await request.resolve()}};
          ctx.fetch=async()=>({ok:true,json:async()=>({success:true,url:'/api/youtube/browse/stream?id=dQw4w9WgXcQ'})});
          const button={dataset:{youtubeId:'dQw4w9WgXcQ',trackTitle:'Song',trackArtist:'Artist'},disabled:false};
          await ctx.HomeViewHelpers.playYouTubeBrowse(button);assert.equal(playbackRequest.source,'ytmusic');assert.equal(playbackRequest.volume,0.6);
          ctx.DeezSpoTag={showNotification:()=>{}};ctx.DeezSpoTagDownload={buildIntentContext:()=>({preferredEngine:'qobuz'}),apiFetch:async(url,options)=>{downloadRequest=JSON.parse(options.body);return {ok:true,json:async()=>({success:true})}}};
          await ctx.HomeViewHelpers.downloadYouTubeBrowse(button);assert.equal(downloadRequest.intents[0].sourceUrl,'https://music.youtube.com/watch?v=dQw4w9WgXcQ');assert(!downloadRequest.intents[0].qobuzId);
          const playable=ctx.HomeViewHelpers.renderYouTubeBrowseItem({id:'dQw4w9WgXcQ',type:'track',title:'Song',url:'https://music.youtube.com/watch?v=dQw4w9WgXcQ'},item=>`<div class="home-top-song-item" onclick="${ctx.HomeViewHelpers.buildYouTubeBrowseClick(item)}"><div class="home-top-song-item__thumb"></div><div class="home-top-song-item__info"><div class="home-top-song-item__title">Song</div></div></div>`);
          assert(playable.includes('HomeViewHelpers.playYouTubeBrowse'));assert(!playable.includes('HomeViewHelpers.downloadYouTubeBrowse'));assert(!playable.includes('globalThis.open'));assert(!playable.includes('action-btn action-btn-sm'));assert(!playable.includes('>Download</button>'));assert(!playable.includes('track-quick-download-btn'));assert(!playable.includes('padding-right:28px'));
        })().catch(error=>{console.error(error);process.exitCode=1});
        """;
        var start=new System.Diagnostics.ProcessStartInfo("node"){RedirectStandardError=true};start.ArgumentList.Add("-e");start.ArgumentList.Add(script);start.ArgumentList.Add(root!.FullName);
        using var process=System.Diagnostics.Process.Start(start)!;var error=process.StandardError.ReadToEnd();process.WaitForExit();Assert.True(process.ExitCode==0,error);
    }

    [Fact]
    public async Task FeaturedPlaylists_ReuseMarketCategoryPagesAndCache()
    {
        var directory = Path.Combine(Path.GetTempPath(), "yt-featured-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
            typeof(DeezSpoTagSettingsService).GetField("_configFolder", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(settings, directory);
            var file = Path.Combine(directory, "config.json");
            File.WriteAllText(file, "{\"youTubeMusicCountry\":\"KE\",\"deezerLanguage\":\"sw\"}");
            typeof(DeezSpoTagSettingsService).GetField("_settingsFilePath", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(settings, file);
            using var handler = new FeaturedHandler(); using var http = new HttpClient(handler); using var cache = new MemoryCache(new MemoryCacheOptions());
            var controller = new YouTubeBrowseApiController(new YouTubeMusicSearchClient(http), settings, cache, NullLogger<YouTubeBrowseApiController>.Instance);
            var result = await controller.LoadAsync("sections", null, CancellationToken.None);
            var shelf = System.Linq.Enumerable.Single(result["sections"]!.AsArray(), s => s?["role"]?.GetValue<string>() == "featured-playlists")!;
            Assert.Equal(20, shelf["items"]!.AsArray().Count);
            var titles = System.Linq.Enumerable.Select(shelf["items"]!.AsArray(), item => item!["title"]!.GetValue<string>()).ToArray();
            Assert.Contains("Daily KE", titles); Assert.Contains("Weekly KE", titles);
            Assert.Contains("Daily Global", titles); Assert.DoesNotContain("Weekly Global", titles);
            Assert.DoesNotContain("Trending 20 KE", titles);
            Assert.Equal(20, System.Linq.Enumerable.Count(System.Linq.Enumerable.Distinct(System.Linq.Enumerable.Select(shelf["items"]!.AsArray(), item => item!["browseId"]!.GetValue<string>()))));
            Assert.Equal(8, handler.CategoryRequests);
            await controller.LoadAsync("sections", null, CancellationToken.None, refresh: true);
            Assert.Equal(8, handler.CategoryRequests);
            handler.Country = "US";
            File.WriteAllText(file, "{\"youTubeMusicCountry\":\"US\",\"deezerLanguage\":\"sw\"}");
            typeof(DeezSpoTagSettingsService).GetField("_cachedSettingsJson", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(settings, null);
            var changed = await controller.LoadAsync("sections", null, CancellationToken.None);
            Assert.Equal("US", changed["country"]!.GetValue<string>());
            Assert.Equal(16, handler.CategoryRequests);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class FeaturedHandler : HttpMessageHandler
    {
        public int CategoryRequests;
        public string Country = "KE";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.Equal(Country, body["context"]!["client"]!["gl"]!.GetValue<string>());
            Assert.Equal("sw", body["context"]!["client"]!["hl"]!.GetValue<string>());
            var id = body["browseId"]!.GetValue<string>();
            var payload = JsonNode.Parse(Fixture)!;
            if (id == "FEmusic_charts")
            {
                var selection = body["formData"]!["selectedValues"]![0]!.GetValue<string>();
                var global = selection == "XY";
                var label = global ? "Global" : Country;
                var chartItems = new JsonArray();
                foreach (var title in global ? new[] { "Daily Global", "Weekly Global" } : new[] { "Trending 20 " + Country, "Daily " + Country, "Weekly " + Country })
                    chartItems.Add(new JsonObject { ["musicTwoRowItemRenderer"] = new JsonObject { ["title"] = new JsonObject { ["runs"] = new JsonArray(new JsonObject { ["text"] = title }) }, ["navigationEndpoint"] = new JsonObject { ["browseEndpoint"] = new JsonObject { ["browseId"] = "VLPLchartabcdefghij" + title.Replace(" ", "") } } } });
                payload = new JsonObject { ["contents"] = new JsonArray(new JsonObject { ["musicCarouselShelfRenderer"] = new JsonObject { ["header"] = new JsonObject { ["musicCarouselShelfBasicHeaderRenderer"] = new JsonObject { ["title"] = new JsonObject { ["runs"] = new JsonArray(new JsonObject { ["text"] = "Video charts" }) } } }, ["contents"] = chartItems } }),
                    ["countryMenu"] = new JsonArray(new JsonObject { ["musicMultiSelectMenuItemRenderer"] = new JsonObject { ["title"] = new JsonObject { ["runs"] = new JsonArray(new JsonObject { ["text"] = label }) }, ["formItemEntityKey"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("explore_charts_country_menu_123" + selection + " ")) } },
                        new JsonObject { ["musicMultiSelectMenuItemRenderer"] = new JsonObject { ["title"] = new JsonObject { ["runs"] = new JsonArray(new JsonObject { ["text"] = "Global" }) }, ["selectedCommand"] = new JsonObject(), ["formItemEntityKey"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("explore_charts_country_menu_123XY ")) } }) };
            }
            if (id == "FEmusic_moods_and_genres")
            {
                var grids = new JsonArray();
                foreach (var group in new[] { "Moods", "Genres" })
                {
                    var items = new JsonArray();
                    for (var n = 0; n < 6; n++) items.Add(new JsonObject { ["musicNavigationButtonRenderer"] = new JsonObject { ["buttonText"] = new JsonObject { ["runs"] = new JsonArray(new JsonObject { ["text"] = group + n }) }, ["clickCommand"] = new JsonObject { ["browseEndpoint"] = new JsonObject { ["browseId"] = "FEmusic_moods_and_genres_category", ["params"] = group + n } } } });
                    grids.Add(new JsonObject { ["gridRenderer"] = new JsonObject { ["header"] = new JsonObject { ["gridHeaderRenderer"] = new JsonObject { ["title"] = new JsonObject { ["runs"] = new JsonArray(new JsonObject { ["text"] = group }) } } }, ["items"] = items } });
                }
                payload = new JsonObject { ["contents"] = grids };
            }
            if (id == "FEmusic_moods_and_genres_category")
            {
                Interlocked.Increment(ref CategoryRequests);
                var parameter = body["params"]!.GetValue<string>();
                Assert.True(parameter.EndsWith("0") || parameter.EndsWith("1") || parameter.EndsWith("2") || parameter.EndsWith("3"));
                var items = new JsonArray();
                for (var n = 0; n < 5; n++) items.Add(new JsonObject { ["musicTwoRowItemRenderer"] = new JsonObject { ["title"] = new JsonObject { ["runs"] = new JsonArray(new JsonObject { ["text"] = "Playlist" }) }, ["navigationEndpoint"] = new JsonObject { ["browseEndpoint"] = new JsonObject { ["browseId"] = "VLPLabcdefghij" + (n == 0 ? "shared" : parameter + n) } } } });
                payload = new JsonObject { ["contents"] = new JsonArray(new JsonObject { ["musicCarouselShelfRenderer"] = new JsonObject { ["header"] = new JsonObject { ["musicCarouselShelfBasicHeaderRenderer"] = new JsonObject { ["title"] = new JsonObject { ["runs"] = new JsonArray(new JsonObject { ["text"] = "Playlists" }) } } }, ["contents"] = items } }) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload.ToJsonString()) };
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Bodies {get;}=[]; public bool Fail; public bool WrongChart;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken) {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var payload=JsonNode.Parse(Fixture)!;
            using var body=JsonDocument.Parse(Bodies[^1]);
            if(body.RootElement.GetProperty("browseId").GetString()=="FEmusic_charts") {
                var country=WrongChart ? "NO" : body.RootElement.GetProperty("formData").GetProperty("selectedValues")[0].GetString();
                payload["countryMenu"]=new JsonObject { ["musicMultiSelectMenuItemRenderer"]=new JsonObject { ["formItemEntityKey"]=Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("explore_charts_country_menu_123"+country+" ")) } };
            }
            return new HttpResponseMessage(Fail?HttpStatusCode.BadGateway:HttpStatusCode.OK){Content=new StringContent(payload.ToJsonString())};
        }
    }
}
