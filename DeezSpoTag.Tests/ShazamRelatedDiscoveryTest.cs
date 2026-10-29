using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Controllers.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace DeezSpoTag.Tests;
public sealed class ShazamRelatedDiscoveryTest
{
    [Theory]
    [InlineData("https://www.shazam.com/track/869111682/nenda-nao", "")]
    [InlineData("https://evil.example/track/123?url=deezer.com/track/456", "")]
    [InlineData("https://deezer.com.evil.example/track/123", "")]
    [InlineData("https://www.deezer.com/en/track/123", "123")]
    [InlineData("deezer://track/123", "123")]
    [InlineData("deezer:track:123", "123")]
    public void DeezerIdsRequireTheCorrectProvider(string url, string expected)
    {
        var method = typeof(LibraryRecommendationService).GetMethod("TryExtractDeezerTrackId", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expected, method.Invoke(null, [url]));
    }

    [Theory]
    [InlineData("matched_no_related", 10, false)]
    [InlineData("matched_no_related", 31, true)]
    [InlineData("matched_no_deezer_resolution", 31, true)]
    [InlineData("no_match", 31, false)]
    public void RelatedCacheRetryIsBoundedAndDoesNotRetryUnrecognizedAudio(string status, int minutes, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new DeezSpoTag.Services.Library.ShazamTrackCacheDto(1, status, "123", "Seed", "Artist", null, [], now.AddMinutes(-minutes), null);
        var method = typeof(LibraryRecommendationService).GetMethod("NeedsShazamRelatedRefresh", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expected, method.Invoke(null, [cache, now]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedRecognitionRepairsOldNegativeOrWrongProviderIdsWithoutRecognizingAgain(bool corruptPositive)
    {
        var fixture = new RecommendationCacheServingTest();
        await fixture.InitializeAsync();
        using var env = new ShazamRelatedRuntimeTest.TestEnvironment();
        System.IO.File.WriteAllText(System.IO.Path.Join(env.ContentRootPath, "Tools", "shazam_related", "runtime.mjs"),
            "import {createInterface} from 'node:readline';createInterface({input:process.stdin}).on('line',l=>{const q=JSON.parse(l);console.log(JSON.stringify({requestId:q.requestId,ok:true,tracks:[{key:'99000000000000005',title:'New song',subtitle:'New artist',deezerUrl:'https://www.deezer.com/track/12345'}]}));});");
        await using var runtime = new ShazamRelatedRuntimeService(env, NullLogger<ShazamRelatedRuntimeService>.Instance);
        using var http = new HttpClient();
        try
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + fixture.DatabasePath);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO artist(id,name) VALUES(1,'Seed Artist'); INSERT INTO album(id,artist_id,title) VALUES(1,1,'Seed Album'); INSERT INTO track(id,album_id,title) VALUES(1,1,'Seed');";
            await command.ExecuteNonQueryAsync();
            var wrong = new DeezSpoTag.Services.Library.RecommendationTrackDto("42", "Wrong", 180, "", 1, new("a", "Wrong"), new("b", "Album", ""),
                RecommendationOrigin: "shazam", Provenance: [new("shazam", "42", 1, "123", 1)]);
            await fixture.Repository.UpsertTrackShazamCacheAsync(new(1, corruptPositive ? "matched" : "matched_no_related", "123", "Seed", "Seed Artist", null,
                corruptPositive ? [wrong] : [], DateTimeOffset.UtcNow.AddHours(-2), null, SpotifyId: "keep-spotify", Album: "Keep album"));
            var service = fixture.Service();
            typeof(LibraryRecommendationService).GetField("_shazamDiscoveryService", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, new ShazamDiscoveryService(http, NullLogger<ShazamDiscoveryService>.Instance, env, runtime));
            var scope = typeof(LibraryRecommendationService).GetMethod("BuildScope", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [fixture.Folder.LibraryId!.Value, fixture.Folder]);
            var operation = (Task)typeof(LibraryRecommendationService).GetMethod("BuildShazamRecommendationsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [scope, new List<DeezSpoTag.Services.Library.LibraryRecommendationSeedTrackDto> { new(1, "Seed", "Seed Artist", "Keep album", 180000, null, null) }, CancellationToken.None])!;
            await operation;
            var saved = (await fixture.Repository.GetShazamTrackCacheByTrackIdsAsync([1], CancellationToken.None))[1];
            var row = Assert.Single(saved.RelatedTracks);
            Assert.Equal("12345", row.Id);
            Assert.Equal("12345", row.DeezerId);
            Assert.Equal("keep-spotify", saved.SpotifyId);
            Assert.Equal("Keep album", saved.Album);
            Assert.Equal("123", saved.ShazamTrackId);
            Assert.Equal("matched", saved.Status);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task BackgroundEnrichmentPublishesBrowserCompatibleCardProperties()
    {
        using var env = new ShazamRelatedRuntimeTest.TestEnvironment();
        System.IO.File.WriteAllText(System.IO.Path.Join(env.ContentRootPath, "Tools", "shazam_related", "runtime.mjs"),
            "import {createInterface} from 'node:readline';createInterface({input:process.stdin}).on('line',l=>{const q=JSON.parse(l);console.log(JSON.stringify({requestId:q.requestId,ok:true,tracks:[{key:'99000000000000003',title:'Related song',subtitle:'Related artist',deezerUrl:'https://www.deezer.com/track/99'}]}));});");
        using var parentJson = JsonDocument.Parse("""{"key":"99000000000000002","title":"Recognized song","subtitle":"Artist"}""");
        var parent = (ShazamTrackCard)typeof(ShazamDiscoveryService).GetMethod("ParseTrack", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [parentJson.RootElement])!;
        typeof(ShazamDiscoveryService).GetMethod("CacheCard", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [parent]);
        await using var runtime = new ShazamRelatedRuntimeService(env, NullLogger<ShazamRelatedRuntimeService>.Instance);
        using var http = new HttpClient();
        var discovery = new ShazamDiscoveryService(http, NullLogger<ShazamDiscoveryService>.Instance, env, runtime);
        using var provider = new ServiceCollection().AddSingleton(discovery).BuildServiceProvider();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var worker = new ShazamEnrichmentQueueService(provider.GetRequiredService<IServiceScopeFactory>(), cache, NullLogger<ShazamEnrichmentQueueService>.Instance);
        var request = new ShazamEnrichmentRequest(new ShazamRecognitionInfo { TrackId = parent.Id, Title = parent.Title, Artist = parent.Artist }, "Recognized song Artist", "logo", "early", "test-session", "test-request");
        await (Task)typeof(ShazamEnrichmentQueueService).GetMethod("EnrichAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(worker, [request, CancellationToken.None])!;
        Assert.True(worker.TryGetResult(request.ClientRequestId, out var saved));
        var payload = JsonSerializer.SerializeToElement(saved, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("ready", payload.GetProperty("enrichment").GetProperty("relatedStatus").GetString());
        Assert.Equal(parent.Id, payload.GetProperty("track").GetProperty("id").GetString());
        foreach (var list in new[] { "related", "similar" })
        {
            var card = Assert.Single(payload.GetProperty(list).EnumerateArray());
            Assert.Equal("99000000000000003", card.GetProperty("id").GetString());
            Assert.Equal("Related song", card.GetProperty("title").GetString());
            Assert.Equal("Related artist", card.GetProperty("artist").GetString());
            Assert.Equal("https://www.deezer.com/track/99", card.GetProperty("deezerUrl").GetString());
            Assert.False(card.TryGetProperty("Id", out _));
        }
    }

    [Fact]
    public void ResultsPollingCoversTheBackgroundEnrichmentBudget()
    {
        var root = System.IO.Directory.GetCurrentDirectory();
        while (!System.IO.File.Exists(System.IO.Path.Join(root, "DeezSpoTag.Web", "Views", "Shazam", "Results.cshtml")))
            root = System.IO.Directory.GetParent(root)!.FullName;
        var source = System.IO.File.ReadAllText(System.IO.Path.Join(root, "DeezSpoTag.Web", "Views", "Shazam", "Results.cshtml"));
        int Constant(string name) => int.Parse(System.Text.RegularExpressions.Regex.Match(source, @"const " + name + @" = (\d+);").Groups[1].Value);
        Assert.True((Constant("ENRICHMENT_POLL_ATTEMPTS") - 1) * Constant("ENRICHMENT_POLL_INTERVAL_MS") >= 45000,
            "The results page must wait for the background worker's 45-second budget.");
    }

    [Fact]
    public void CachedShazamSupplyIsNotDiscardedBeforeTheThreePerSeedSelection()
    {
        var seeds = System.Linq.Enumerable.Range(1, 12).Select(n => (long)n).ToArray();
        var cache = new Dictionary<long, DeezSpoTag.Services.Library.ShazamTrackCacheDto>();
        foreach (var seed in seeds)
        {
            var rows = System.Linq.Enumerable.Range(1, 10).Select(n => new DeezSpoTag.Services.Library.RecommendationTrackDto(
                $"{seed}{n:D3}", $"Song {seed} {n}", 180, "", n, new("a", "Artist"), new("album", "Album", ""),
                RecommendationOrigin: "shazam", Provenance: [new("shazam", $"native{seed}{n}", seed, "seed" + seed, n)])).ToList();
            cache[seed] = new(seed, "matched", seed.ToString(), "Seed", "Artist", null, rows, DateTimeOffset.UtcNow, null);
        }
        var method = typeof(LibraryRecommendationService).GetMethod("BuildRecommendationsFromShazamCache", BindingFlags.NonPublic | BindingFlags.Static)!;
        var candidates = (List<DeezSpoTag.Services.Library.RecommendationTrackDto>)method.Invoke(null, [seeds, cache, CancellationToken.None])!;
        var selected = LibraryRecommendationService.BuildSavedDailySelection(candidates, new Dictionary<string, int> { ["shazam"] = 20 }, 20, new DateOnly(2026, 10, 29), null, null);
        Assert.Equal(20, selected.Count);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SparseRelatedResponseRetainsPreviouslyCachedProviderUrls(bool conflictingIsrc)
    {
        using var env = new ShazamRelatedRuntimeTest.TestEnvironment();
        var nativeIsrc = conflictingIsrc ? "GB9876543210" : "";
        System.IO.File.WriteAllText(System.IO.Path.Join(env.ContentRootPath, "Tools", "shazam_related", "runtime.mjs"),
            "import {createInterface} from 'node:readline';createInterface({input:process.stdin}).on('line',l=>{const q=JSON.parse(l);console.log(JSON.stringify({requestId:q.requestId,ok:true,tracks:[{key:'99000000000000001',title:'Song',subtitle:'Artist',isrc:'" + nativeIsrc + "'}]}));});");
        using var json = JsonDocument.Parse("""{"key":"99000000000000001","title":"Song","subtitle":"Artist","isrc":"US1234567890","durationMs":180000,"deezerUrl":"https://www.deezer.com/track/99","spotifyUrl":"https://open.spotify.com/track/example","appleMusicUrl":"https://music.apple.com/us/song/99","url":"https://www.shazam.com/track/99000000000000001"}""");
        var card = (ShazamTrackCard)typeof(ShazamDiscoveryService).GetMethod("ParseTrack", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [json.RootElement])!;
        typeof(ShazamDiscoveryService).GetMethod("CacheCard", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [card]);
        await using var runtime = new ShazamRelatedRuntimeService(env, NullLogger<ShazamRelatedRuntimeService>.Instance);
        using var http = new HttpClient();
        var service = new ShazamDiscoveryService(http, NullLogger<ShazamDiscoveryService>.Instance, env, runtime);
        var refreshed = Assert.Single(await service.GetRelatedTracksAsync("123"));
        Assert.Equal(conflictingIsrc ? null : card.DeezerUrl, refreshed.DeezerUrl);
        Assert.Equal(conflictingIsrc ? null : card.SpotifyUrl, refreshed.SpotifyUrl);
        Assert.Equal(conflictingIsrc ? null : card.AppleMusicUrl, refreshed.AppleMusicUrl);
        Assert.Equal(conflictingIsrc ? null : card.Url, refreshed.Url);
        Assert.Equal(conflictingIsrc ? nativeIsrc : card.Isrc, refreshed.Isrc);
        Assert.Equal(conflictingIsrc ? null : card.DurationMs, refreshed.DurationMs);
    }
    [Fact]
    public async Task CachedRelatedSongsDoNotRequireTheRecognitionRuntime()
    {
        var fixture = new RecommendationCacheServingTest();
        await fixture.InitializeAsync();
        try
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + fixture.DatabasePath);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO artist(id,name) VALUES(1,'Seed Artist'); INSERT INTO album(id,artist_id,title) VALUES(1,1,'Seed Album'); INSERT INTO track(id,album_id,title) VALUES(1,1,'Seed'); INSERT INTO audio_file(id,path,folder_id) VALUES(1,'/fixture/seed.flac',@folder); INSERT INTO track_local(track_id,audio_file_id) VALUES(1,1);";
            command.Parameters.AddWithValue("@folder", fixture.Folder.Id);
            await command.ExecuteNonQueryAsync();
            var row = new DeezSpoTag.Services.Library.RecommendationTrackDto("12345", "New Song", 180, "US1234567890", 1,
                new("artist", "New Artist"), new("album", "Album", ""), SourceUrl: "https://www.shazam.com/track/42", RecommendationOrigin: "shazam");
            await fixture.Repository.UpsertTrackShazamCacheAsync(new(1, "matched", "123", "Seed", "Seed Artist", null, [row], DateTimeOffset.UtcNow, null));
            var service = fixture.Service();
            var scope = typeof(LibraryRecommendationService).GetMethod("BuildScope", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [fixture.Folder.LibraryId!.Value, fixture.Folder]);
            var operation = (Task)typeof(LibraryRecommendationService).GetMethod("BuildShazamRecommendationsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [scope, new List<DeezSpoTag.Services.Library.LibraryRecommendationSeedTrackDto> { new(1, "Seed", "Seed Artist", "Seed Album", 180000, null, null) }, CancellationToken.None])!;
            await operation;
            var result = operation.GetType().GetProperty("Result")!.GetValue(operation)!;
            var rows = Assert.IsType<List<DeezSpoTag.Services.Library.RecommendationTrackDto>>(result.GetType().GetProperty("Tracks")!.GetValue(result));
            Assert.Equal("12345", Assert.Single(rows).Id);
        }
        finally { await fixture.DisposeAsync(); }
    }
    [Fact]
    public async Task SharedTransportPreservesCardsAndAppliesOffsetAndLimit()
    {
        using var env = new ShazamRelatedRuntimeTest.TestEnvironment();
        System.IO.File.WriteAllText(System.IO.Path.Join(env.ContentRootPath, "Tools", "shazam_related", "runtime.mjs"),
            "import {createInterface} from 'node:readline';createInterface({input:process.stdin}).on('line',l=>{const q=JSON.parse(l);console.log(JSON.stringify({requestId:q.requestId,ok:true,tracks:[{key:'41',title:'First',subtitle:'A'},{key:'42',title:'Second',subtitle:'B',deezerUrl:'https://www.deezer.com/track/55',url:'https://www.shazam.com/track/42'}]}));});");
        await using var runtime = new ShazamRelatedRuntimeService(env, NullLogger<ShazamRelatedRuntimeService>.Instance);
        using var http = new HttpClient();
        var service = new ShazamDiscoveryService(http, NullLogger<ShazamDiscoveryService>.Instance, env, runtime);
        var cards = await service.GetRelatedTracksAsync("123", limit: 1, offset: 1);
        var card = Assert.Single(cards);
        Assert.Equal("42", card.Id);
        Assert.Equal("https://www.shazam.com/track/42", card.Url);
        Assert.Equal("https://www.deezer.com/track/55", card.DeezerUrl);
    }
    [Fact]
    public void ExistingParserPreservesOrderedCardsAndAllNativeUrls()
    {
        using var json = JsonDocument.Parse("""
        {"tracks":[{"key":"42","title":"Song","subtitle":"Artist","isrc":"US1234567890","url":"https://www.shazam.com/track/42","appleMusicUrl":"https://music.apple.com/us/song/42","spotifyUrl":"https://open.spotify.com/track/example","deezerUrl":"https://www.deezer.com/track/123"},{"key":"43","title":"Other","subtitle":"Singer"}]}
        """);
        var parse = typeof(ShazamDiscoveryService).GetMethod("ParseTrackList", BindingFlags.NonPublic | BindingFlags.Static)!;
        var cards = Assert.IsType<List<ShazamTrackCard>>(parse.Invoke(null, [json.RootElement, 20]));
        Assert.Equal(new[] { "42", "43" }, cards.ConvertAll(c => c.Id));
        Assert.Equal("https://www.deezer.com/track/123", cards[0].DeezerUrl);
        Assert.Equal("https://www.shazam.com/track/42", cards[0].Url);
        Assert.Equal("https://music.apple.com/us/song/42", cards[0].AppleMusicUrl);
        Assert.Equal("https://open.spotify.com/track/example", cards[0].SpotifyUrl);
        Assert.Null(cards[1].Isrc);
    }
    [Fact]
    public async Task ApiReportsUnavailableRatherThanAnEmptySuccess()
    {
        using var env = new ShazamRelatedRuntimeTest.TestEnvironment();
        using var http = new HttpClient();
        var discovery = new ShazamDiscoveryService(http, NullLogger<ShazamDiscoveryService>.Instance, env);
        var controller = new ShazamDiscoveryApiController(discovery, NullLogger<ShazamDiscoveryApiController>.Instance);
        var result = Assert.IsType<ObjectResult>(await controller.Related("123"));
        Assert.Equal(503, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal("upstream_unavailable", json.GetProperty("error").GetString());
    }
    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("123456789012345678901")]
    public async Task RelatedApiRejectsInvalidId(string id)
    {
        using var env = new ShazamRelatedRuntimeTest.TestEnvironment();
        using var http = new HttpClient();
        var discovery = new ShazamDiscoveryService(http, NullLogger<ShazamDiscoveryService>.Instance, env);
        var controller = new ShazamDiscoveryApiController(discovery, NullLogger<ShazamDiscoveryApiController>.Instance);
        Assert.IsType<BadRequestObjectResult>(await controller.Related(id));
    }
}
