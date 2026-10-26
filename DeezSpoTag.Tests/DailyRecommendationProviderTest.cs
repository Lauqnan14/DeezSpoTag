using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations;
using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DailyRecommendationProviderTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DailySelectionLimitsEverySeedToThreeAcrossProvidersIncludingSavedSelections(bool saved)
    {
        var sources = new[] { "deezer", "spotify", "tidal" };
        var rows = sources.SelectMany((source, index) => Enumerable.Range(1, 24).SelectMany(seed =>
            Enumerable.Range(1, seed == 1 ? 60 : 4).Select(n => new DeezSpoTag.Services.Library.RecommendationTrackDto(
                $"{index + 1}{seed:D3}{n:D3}", $"Song {source} {seed} {n}", 180, "", n,
                new($"a{source}{seed}{n}", $"Artist {source} {seed} {n}"), new("album", "Album", ""), RecommendationOrigin: source,
                Provenance: [new(source, $"native{source}{seed}{n}", seed, "seed" + seed, n)])))).ToArray();
        var quotas = sources.ToDictionary(source => source, _ => 20);
        var preferred = saved ? rows.Where(t => t.Provenance![0].LocalSeedTrackId == 1).Take(60).Select(t => t.Id).ToArray() : null;
        var selected = LibraryRecommendationService.BuildSavedDailySelection(rows, quotas, 60, new(2026, 10, 26), null, preferred);
        Assert.Equal(60, selected.Count);
        Assert.True(LibraryRecommendationService.HasCompleteDailyAllocation(selected, quotas));
        Assert.All(selected.SelectMany(t => t.Provenance!.Select(p => p.LocalSeedTrackId).Distinct()).GroupBy(id => id),
            group => Assert.InRange(group.Count(), 1, 3));
    }

    [Fact]
    public async Task FullReservesDoNotStopCollectionBeforeTheConfiguredVisibleTotalIsPossible()
    {
        var seeds = Enumerable.Range(1,64).Select(n => new DeezSpoTag.Services.Library.LibraryRecommendationSeedTrackDto(
            n,"Seed " + n,"Artist " + n,"Album",180000,null,n.ToString())).ToArray();
        var quotas = new Dictionary<string,int>{{"deezer",80},{"spotify",10},{"tidal",10}};
        var processed = new Dictionary<string,HashSet<long>>();
        var calls = 0;
        Task<IReadOnlyList<DeezSpoTag.Services.Library.RecommendationTrackDto>> Fetch(string source,
            IReadOnlyList<DeezSpoTag.Services.Library.LibraryRecommendationSeedTrackDto> batch,int desired,CancellationToken token)
        {
            calls++;
            IReadOnlyList<DeezSpoTag.Services.Library.RecommendationTrackDto> rows = batch.SelectMany(seed => Enumerable.Range(1,9).Select(n =>
                new DeezSpoTag.Services.Library.RecommendationTrackDto($"{Array.IndexOf(new[]{"deezer","spotify","tidal"},source)+1}{seed.TrackId:D3}{n:D3}",
                    $"Song {source} {seed.TrackId} {n}",180,"",n,new($"a{source}{seed.TrackId}{n}",$"Artist {source} {seed.TrackId} {n}"),
                    new("album","Album",""),RecommendationOrigin:source,Provenance:[new(source,"native" + n,seed.TrackId,"seed" + seed.TrackId,n)]))).ToArray();
            return Task.FromResult(rows);
        }
        var pool = await LibraryRecommendationService.CollectDailySeedBatchesAsync(seeds,quotas,new(2026,10,26),[],processed,new(),Fetch,
            _ => Task.CompletedTask,CancellationToken.None);
        var selected = LibraryRecommendationService.BuildAllocatedDailySelection(pool,quotas,100,new(2026,10,26));
        Assert.Equal(100,selected.Count);
        Assert.True(LibraryRecommendationService.HasCompleteDailyAllocation(selected,quotas));
        Assert.True(calls > 3,"All first batches contain only 32 seeds, which cannot supply 100 songs at three per seed.");
    }

    [Fact]
    public void SeedCapPreservesScarceProviderSongsWhenOtherProvidersHaveOtherSeeds()
    {
        DeezSpoTag.Services.Library.RecommendationTrackDto Row(string source, long seed, int id) =>
            new(id.ToString(), "Song " + id, 180, "", 1, new("a" + id, "Artist " + id), new("album", "Album", ""),
                RecommendationOrigin: source, Provenance: [new(source, "native" + id, seed, "seed" + seed, 1)]);
        var rows = Enumerable.Range(1,3).Select(n => Row("tidal",1,90+n))
            .Concat(Enumerable.Range(1,3).Select(n => Row("spotify",1,n)))
            .Concat(Enumerable.Range(1,3).Select(n => Row("spotify",2,10+n))).ToArray();
        var selected = LibraryRecommendationService.BuildAllocatedDailySelection(rows,
            new Dictionary<string,int>{{"tidal",3},{"spotify",3}},6,new(2026,10,26));
        Assert.Equal(6,selected.Count);
        Assert.All(selected.SelectMany(t => t.Provenance!.Select(p => p.LocalSeedTrackId)).GroupBy(id => id),
            group => Assert.InRange(group.Count(),1,3));
        Assert.Equal(3,selected.Count(t => t.RecommendationOrigin == "tidal"));
    }

    [Fact]
    public void DailySelectionDoesNotRepeatARecordingWithDifferentReleaseIdsAndIsrcs()
    {
        DeezSpoTag.Services.Library.RecommendationTrackDto Row(string id, string title, string isrc) =>
            new(id, title, 229, isrc, 1, new("artist", "Richie Spice"), new(id, "Different release", ""), RecommendationOrigin: "tidal",
                Provenance: [new("tidal", id, long.Parse(id), "seed" + id, 1)]);
        var selected = LibraryRecommendationService.BuildAllocatedDailySelection(
            [Row("1", "Groovin My Girl", "ISRC1"), Row("2", "Groovin My Girl", "ISRC2"), Row("3", "Groovin My Girl (Live)", "ISRC3")],
            new Dictionary<string,int>{{"tidal",3}}, 3, new(2026,10,26));
        Assert.Equal(2, selected.Count);
        Assert.Single(selected.Where(t => t.Title == "Groovin My Girl"));
        Assert.Contains(selected, t => t.Title.EndsWith("(Live)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(60)]
    [InlineData(100)]
    public void ReserveAllocationSupportsAllThreeSourcesBeyondThePublicTrackLimit(int total)
    {
        var quotas=new Dictionary<string,int>{{"deezer",total/3*3},{"spotify",total/3*3},{"tidal",(total-2*(total/3))*3}};
        var rows=quotas.SelectMany((p,source)=>Enumerable.Range(1,p.Value).Select(n=>new DeezSpoTag.Services.Library.RecommendationTrackDto(
            (source*1000+n).ToString(),"Song "+n,180,"",n,new("a"+source+"-"+n,"Artist"),new("b","Album",""),RecommendationOrigin:p.Key))).ToArray();
        var result=LibraryRecommendationService.BuildAllocatedDailySelection(rows,quotas,total*3,new(2026,10,24));
        Assert.Equal(total*3,result.Count);
        Assert.True(LibraryRecommendationService.HasCompleteDailyAllocation(result,quotas));
    }

    [Fact]
    public async Task MissingSpotifySessionDefersBeforeResolvingOrConsumingSeedIdentities()
    {
        var service=new LibraryRecommendationService(new LibraryRecommendationService.LibraryRecommendationCollaborators
        {
            PlatformIdentities=(PlatformTrackIdentityResolver)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PlatformTrackIdentityResolver)),
            SpotifyRecommendations=(SpotifyRecommendationService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SpotifyRecommendationService))
        },new RecommendationCacheServingTest.EnvironmentFixture(),Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryRecommendationService>.Instance);
        var failures=new Dictionary<string,string>();var raw=new Dictionary<string,int>();
        var method=typeof(LibraryRecommendationService).GetMethod("BuildStreamingDailyRecommendationsAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        var result=await (Task<IReadOnlyList<DeezSpoTag.Services.Library.RecommendationTrackDto>>)method.Invoke(service,
            new object[]{"spotify",new DeezSpoTag.Services.Library.LibraryRecommendationSeedTrackDto[]{new(1,"Seed","Artist","Album",180000,null,"1")},60,
                DeezSpoTag.Core.Models.Settings.DailyRecommendationAiFilterMode.Allow,new Dictionary<string,string?>(),(Action<string>)(_=>{}),
                new Dictionary<string,int>(),raw,failures,CancellationToken.None,null})!;
        Assert.Empty(result);Assert.Equal("provider_unavailable",failures["spotify"]);Assert.Empty(raw);
    }

    [Fact]
    public void RefilledReservesDoNotReplaceThePublishedSelectionWithoutARejection()
    {
        var rows=Enumerable.Range(1,180).Select(n=>new DeezSpoTag.Services.Library.RecommendationTrackDto(n.ToString(),"Song "+n,180,"",n,
            new("a"+n,"Artist "+n),new("b"+n,"Album",""),RecommendationOrigin:"deezer")).ToArray();
        var selected=rows.Take(60).Select(t=>t.Id).ToArray();
        var quotas=new Dictionary<string,int>{{"deezer",60}};
        var result=LibraryRecommendationService.BuildSavedDailySelection(rows,quotas,60,new(2026,10,24),null,selected);
        Assert.Equal(selected.Order(),result.Select(t=>t.Id).Order());
        var replaced=LibraryRecommendationService.BuildSavedDailySelection(rows,quotas,60,new(2026,10,24),new(){"1"},selected);
        Assert.Equal(60,replaced.Count);Assert.DoesNotContain(replaced,t=>t.Id=="1");
        Assert.Equal(59,replaced.Count(t=>selected.Contains(t.Id)));
    }

    [Fact]
    public async Task TidalTrackRadioUsesTrackMixWithApplicationTokenAndAllTrackModules()
    {
        using var handler = new Handler((request, index) =>
        {
            Assert.Equal("api.tidal.com", request.RequestUri!.Host);
            Assert.Equal("Bearer test-token", request.Headers.Authorization!.ToString());
            Assert.Contains("countryCode=GB", request.RequestUri.Query);
            if (index == 0)
            {
                Assert.Equal("/v1/tracks/99", request.RequestUri.AbsolutePath);
                return Json("{\"mixes\":{\"TRACK_MIX\":\"mix1\"}}");
            }
            Assert.Equal("/v1/pages/mix", request.RequestUri.AbsolutePath);
            Assert.Contains("mixId=mix1", request.RequestUri.Query);
            Assert.Contains("deviceType=BROWSER", request.RequestUri.Query);
            return Json("""
                {"rows":[{"modules":[{"type":"MIX_HEADER"},
                  {"type":"TRACK_LIST","supportsPaging":false,"pagedList":{"items":[
                    {"id":99,"title":"Seed","duration":100},
                    {"id":1,"title":"First","duration":180,"isrc":"GB0000000001","ai":true,"artists":[{"name":"Artist"}],"album":{"title":"Album"}}]}},
                  {"type":"TRACK_LIST","supportsPaging":false,"pagedList":{"items":[
                    {"id":1,"title":"Duplicate","duration":180},
                    {"id":2,"title":"Second","duration":200,"ai":false}]}}]}]}
                """);
        });
        using var http = new HttpClient(handler);
        var client = new TidalCatalogClient(new TargetApiTransport(http, TimeSpan.Zero), _ => Task.FromResult<TidalTargetCredentials?>(new("test-token", "GB")));
        var result = await client.GetTrackRadioAsync("99", 20);
        Assert.Equal(TidalTrackRadioOutcome.Ready, result.Outcome);
        Assert.Equal(new[] { "1", "2" }, result.Tracks.Select(t => t.Id));
        Assert.Equal(180000, result.Tracks[0].DurationMs);
        Assert.Equal("GB0000000001", result.Tracks[0].Isrc);
        Assert.True(result.Tracks[0].Ai);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"mixes\":{\"TRACK_MIX\":{}}}")]
    public async Task TidalMissingMixIsAnExplicitFailureWithoutAnotherRoute(string body)
    {
        using var handler = new Handler((_, _) => Json(body));
        using var http = new HttpClient(handler);
        var client = new TidalCatalogClient(new TargetApiTransport(http, TimeSpan.Zero), _ => Task.FromResult<TidalTargetCredentials?>(new("token")));
        var result = await client.GetTrackRadioAsync("99", 20);
        Assert.Equal(TidalTrackRadioOutcome.Failed, result.Outcome);
        Assert.Empty(result.Tracks);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task TidalFailedMixIsNotAnEmptySuccess(HttpStatusCode failure)
    {
        using var handler = new Handler((_, index) => index == 0 ? Json("{\"mixes\":{\"TRACK_MIX\":\"mix1\"}}") : new HttpResponseMessage(failure));
        using var http = new HttpClient(handler);
        var client = new TidalCatalogClient(new TargetApiTransport(http, TimeSpan.Zero), _ => Task.FromResult<TidalTargetCredentials?>(new("token")));
        var result = await client.GetTrackRadioAsync("99", 20);
        Assert.Equal(TidalTrackRadioOutcome.Failed, result.Outcome);
        Assert.Empty(result.Tracks);
    }

    [Fact]
    public async Task TidalUnavailableApplicationTokenMakesNoProviderCalls()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Must not call provider"));
        using var http = new HttpClient(handler);
        var client = new TidalCatalogClient(new TargetApiTransport(http, TimeSpan.Zero), _ => Task.FromResult<TidalTargetCredentials?>(null));
        Assert.Equal(TidalTrackRadioOutcome.Unavailable, (await client.GetTrackRadioAsync("99", 20)).Outcome);
        Assert.Equal(0, handler.Count);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetTrackRadioAsync("99", 20, cts.Token));
    }

    [Theory]
    [InlineData("0eGsygTp906u18L0Oimnem", "37i9dQZF1E8IrAPFM8awVr")]
    [InlineData("4hM9jLSD1lgswviJTkHsPP", "37i9dQZF1E8zBYZSL2HCjC")]
    public async Task SpotifyUsesEachSeedSongRadioAndLoadsItsPlaylist(string seedId, string playlistId)
    {
        var seed = Track(seedId, "seed-album") with { Isrc = "US0000000001" };
        var calls = new List<string>();
        Task<SpotifyUrlMetadata?> Metadata(string url, CancellationToken token)
        {
            calls.Add(url);
            if (url == "https://open.spotify.com/track/" + seedId)
                return Task.FromResult<SpotifyUrlMetadata?>(MetadataResult(new[] { seed }));
            Assert.Equal("https://open.spotify.com/playlist/" + playlistId, url);
            return Task.FromResult<SpotifyUrlMetadata?>(MetadataResult(new[] { seed, Track("recommendation", "related-album"), Track("recommendation", "related-album") }));
        }
        Task<string?> Radio(string id, CancellationToken token)
        {
            Assert.Equal(seedId, id);
            return Task.FromResult<string?>("spotify:playlist:" + playlistId);
        }
        var tracks = await SpotifyRecommendationService.FetchSeedTrackRecommendationsCoreAsync("https://open.spotify.com/track/" + seedId, 20, Metadata, Radio, CancellationToken.None);
        Assert.Equal("recommendation", Assert.Single(tracks).Id);
        Assert.Equal(new[] { "https://open.spotify.com/track/" + seedId, "https://open.spotify.com/playlist/" + playlistId }, calls);
    }

    [Fact]
    public async Task SpotifyUnavailableSeedDoesNotInventRecommendationContext()
    {
        var tracks = await SpotifyRecommendationService.FetchSeedTrackRecommendationsCoreAsync("https://open.spotify.com/track/0eGsygTp906u18L0Oimnem", 20,
            (_, _) => Task.FromResult<SpotifyUrlMetadata?>(null), (_, _) => throw new InvalidOperationException("No seed context"), CancellationToken.None);
        Assert.Empty(tracks);
    }

    [Fact]
    public async Task SpotifyRadioExcludesSeedRecordingAndRespectsLimit()
    {
        var seed = Track("seed", "album");
        var alias = Track("alias", "album") with { Isrc = seed.Isrc };
        Task<SpotifyUrlMetadata?> Metadata(string url, CancellationToken token) => Task.FromResult<SpotifyUrlMetadata?>(
            MetadataResult(url.Contains("/track/", StringComparison.Ordinal) ? new[] { seed } : new[] { seed, alias, Track("one", "album"), Track("two", "album") }));
        var tracks = await SpotifyRecommendationService.FetchSeedTrackRecommendationsCoreAsync("https://open.spotify.com/track/seed", 1,
            Metadata, (_, _) => Task.FromResult<string?>("spotify:playlist:37i9dQZF1E8IrAPFM8awVr"), CancellationToken.None);
        Assert.Equal("one", Assert.Single(tracks).Id);
    }

    [Fact]
    public async Task SpotifySongRadioSendsVerifiedAuthenticatedRequest()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://spclient.wg.spotify.com/inspiredby-mix/v2/seed_to_playlist/spotify:track:0eGsygTp906u18L0Oimnem?response-format=json", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer access", request.Headers.Authorization!.ToString());
            Assert.Equal("client", Assert.Single(request.Headers.GetValues("Client-Token")));
            Assert.Equal("version", Assert.Single(request.Headers.GetValues("Spotify-App-Version")));
            Assert.Equal("WebPlayer", Assert.Single(request.Headers.GetValues("App-Platform")));
            return Json("{\"total\":1,\"mediaItems\":[{\"uri\":\"spotify:playlist:37i9dQZF1E8IrAPFM8awVr\"}]}");
        });
        using var http = new HttpClient(handler);
        var uri = await SpotifyPathfinderMetadataClient.FetchSongRadioPlaylistUriCoreAsync(http, new("access", "client", "version"), "0eGsygTp906u18L0Oimnem", CancellationToken.None);
        Assert.Equal("spotify:playlist:37i9dQZF1E8IrAPFM8awVr", uri);
    }

    [Fact]
    public async Task SpotifyRadioReadsUtf8JsonEvenWhenResponseCharsetAliasIsInvalid()
    {
        using var handler = new Handler((_, _) =>
        {
            var response = Json("{\"total\":1,\"mediaItems\":[{\"uri\":\"spotify:playlist:37i9dQZF1E8IrAPFM8awVr\"}]}");
            response.Content.Headers.ContentType!.CharSet = "utf8";
            return response;
        });
        using var http = new HttpClient(handler);
        Assert.Equal("spotify:playlist:37i9dQZF1E8IrAPFM8awVr", await SpotifyPathfinderMetadataClient.FetchSongRadioPlaylistUriCoreAsync(http, new("access", "client", "version"), "0eGsygTp906u18L0Oimnem", CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task SpotifyRadioHttpFailureDoesNotBecomeAlbumRecommendations(HttpStatusCode status)
    {
        using var handler = new Handler((_, _) => new(status));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => SpotifyPathfinderMetadataClient.FetchSongRadioPlaylistUriCoreAsync(http, new("access", "client", "version"), "0eGsygTp906u18L0Oimnem", CancellationToken.None));
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"mediaItems\":{}}")]
    [InlineData("{\"mediaItems\":[{\"uri\":\"https://evil.example/playlist\"}]}")]
    public async Task SpotifyRadioRejectsMalformedOrUntrustedPlaylistPayload(string json)
    {
        using var handler = new Handler((_, _) => Json(json));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => SpotifyPathfinderMetadataClient.FetchSongRadioPlaylistUriCoreAsync(http, new("access", "client", "version"), "0eGsygTp906u18L0Oimnem", CancellationToken.None));
    }

    [Fact]
    public async Task SpotifyEmptyRadioDoesNotFetchAlternativeRecommendations()
    {
        using var handler = new Handler((_, _) => Json("{\"total\":0,\"mediaItems\":[]}"));
        using var http = new HttpClient(handler);
        Assert.Null(await SpotifyPathfinderMetadataClient.FetchSongRadioPlaylistUriCoreAsync(http, new("access", "client", "version"), "0eGsygTp906u18L0Oimnem", CancellationToken.None));
        var tracks = await SpotifyRecommendationService.FetchSeedTrackRecommendationsCoreAsync("https://open.spotify.com/track/seed", 20,
            (url, _) => url.Contains("/track/", StringComparison.Ordinal) ? Task.FromResult<SpotifyUrlMetadata?>(MetadataResult(new[] { Track("seed", "album") })) : throw new InvalidOperationException("No playlist available"),
            (_, _) => Task.FromResult<string?>(null), CancellationToken.None);
        Assert.Empty(tracks);
    }

    [Fact]
    public async Task SpotifyRadioCancellationDoesNotCallProvider()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Cancelled request must not run"));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SpotifyPathfinderMetadataClient.FetchSongRadioPlaylistUriCoreAsync(http, new("access", "client", "version"), "0eGsygTp906u18L0Oimnem", cancellation.Token));
        Assert.Equal(0, handler.Count);
    }

    internal static SpotifyTrackSummary Track(string id, string album) => new(id, "Song " + id, "Artist", "Album", 180000, "https://open.spotify.com/track/" + id, null, id == "seed" ? "US0000000001" : "US0000000002") { AlbumId = album };
    private static SpotifyUrlMetadata MetadataResult(IEnumerable<SpotifyTrackSummary> tracks) => new("album", "album", "Album", "", null, null, null, null, tracks.ToList(), new(), null, null, null);
    private static string Page(string id, string? next) => "{\"data\":[{\"type\":\"tracks\",\"id\":\"" + id + "\",\"attributes\":{\"title\":\"Track " + id + "\",\"duration\":\"PT3M\"}}],\"links\":{\"next\":" + (next is null ? "null" : "\"https://openapi.tidal.com/v2/tracks/99/relationships/similarTracks?page%5Bcursor%5D=" + next + "\"") + "}}";
    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/vnd.api+json") };
    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request, Count++));
        }
    }
    [Fact]
    public async Task ProgressiveCollectionRetainsProgressAndRetriesOnlyFailedBatches()
    {
        var seeds=Enumerable.Range(1,35).Select(i=>new DeezSpoTag.Services.Library.LibraryRecommendationSeedTrackDto(
            i,"Seed","Artist","Album",180000,null,i.ToString())).ToArray();
        var processed=new Dictionary<string,HashSet<long>>();
        var blocked=new HashSet<string>();
        var calls=0;var checkpointCount=0;
        Task Checkpoint(IReadOnlyList<DeezSpoTag.Services.Library.RecommendationTrackDto> rows) {checkpointCount++;return Task.CompletedTask;}
        Task<IReadOnlyList<DeezSpoTag.Services.Library.RecommendationTrackDto>> Failed(string source,IReadOnlyList<DeezSpoTag.Services.Library.LibraryRecommendationSeedTrackDto> batch,int desired,CancellationToken token)
        {calls++;blocked.Add(source);return Task.FromResult<IReadOnlyList<DeezSpoTag.Services.Library.RecommendationTrackDto>>([]);}
        var quotas=new Dictionary<string,int>{{"spotify",20}};
        Assert.Empty(await LibraryRecommendationService.CollectDailySeedBatchesAsync(seeds,quotas,new(2026,10,24),[],processed,blocked,Failed,Checkpoint,CancellationToken.None));
        Assert.Equal(1,calls);
        Assert.Empty(processed["spotify"]);
        blocked.Clear();
        Task<IReadOnlyList<DeezSpoTag.Services.Library.RecommendationTrackDto>> Empty(string source,IReadOnlyList<DeezSpoTag.Services.Library.LibraryRecommendationSeedTrackDto> batch,int desired,CancellationToken token)
        {calls++;return Task.FromResult<IReadOnlyList<DeezSpoTag.Services.Library.RecommendationTrackDto>>([]);}
        Assert.Empty(await LibraryRecommendationService.CollectDailySeedBatchesAsync(seeds,quotas,new(2026,10,24),[],processed,blocked,Empty,Checkpoint,CancellationToken.None));
        Assert.Equal(35,processed["spotify"].Count);
        Assert.Equal(3,calls);
        // A restart with persisted processed IDs must not fetch the exhausted seeds again.
        var restored=processed.ToDictionary(p=>p.Key,p=>p.Value.ToHashSet());
        await LibraryRecommendationService.CollectDailySeedBatchesAsync(seeds,quotas,new(2026,10,24),[],restored,blocked,Empty,Checkpoint,CancellationToken.None);
        Assert.Equal(3,calls);
        Assert.Equal(3,checkpointCount);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(60)]
    [InlineData(100)]
    public async Task ProgressiveCollectionContinuesBeyond32FilteredSeedsAndBuildsReserves(int total)
    {
        var method = typeof(LibraryRecommendationService).GetMethod("CollectDailySeedBatchesAsync",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var seeds = Enumerable.Range(1, 80).Select(i => new DeezSpoTag.Services.Library.LibraryRecommendationSeedTrackDto(
            i, "Seed", "Artist", "Album", 180000, null, i.ToString())).ToArray();
        var calls = 0; var checkpoints = 0;
        Func<string, IReadOnlyList<DeezSpoTag.Services.Library.LibraryRecommendationSeedTrackDto>, int, CancellationToken,
            Task<IReadOnlyList<DeezSpoTag.Services.Library.RecommendationTrackDto>>> fetch = (source,batch,desired,token) =>
        {
            calls++;
            IReadOnlyList<DeezSpoTag.Services.Library.RecommendationTrackDto> rows = batch.Where(seed => seed.TrackId > 32)
                .SelectMany(seed => Enumerable.Range(0,10).Select(rank => new DeezSpoTag.Services.Library.RecommendationTrackDto(
                    (seed.TrackId * 10 + rank).ToString(), "Song", 180, "", rank, new($"a{seed.TrackId}-{rank}", $"Artist {seed.TrackId}-{rank}"),
                    new($"b{seed.TrackId}-{rank}", "Album", ""), RecommendationOrigin: source))).ToArray();
            return Task.FromResult(rows);
        };
        var processed = new Dictionary<string, HashSet<long>>();
        Func<IReadOnlyList<DeezSpoTag.Services.Library.RecommendationTrackDto>,Task> checkpoint = _ => {checkpoints++;return Task.CompletedTask;};
        var rows = await (Task<List<DeezSpoTag.Services.Library.RecommendationTrackDto>>)method.Invoke(null,
            new object[] {seeds, new Dictionary<string,int>{{"deezer",total}},new DateOnly(2026,10,24),
                Array.Empty<DeezSpoTag.Services.Library.RecommendationTrackDto>(),processed,new HashSet<string>(),fetch,checkpoint,CancellationToken.None})!;
        Assert.Equal(total * 3, rows.Count);
        Assert.All(rows, row => Assert.Equal("deezer",row.RecommendationOrigin));
        Assert.True(calls >= 2);
        Assert.True(checkpoints >= 2);
        Assert.Contains(64L, processed["deezer"]);
    }

}
