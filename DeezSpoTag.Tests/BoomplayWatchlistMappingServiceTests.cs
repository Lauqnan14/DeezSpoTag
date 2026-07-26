using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Controllers.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class BoomplayWatchlistMappingServiceTests : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private IConfiguration _configuration = default!;
    private LibraryRepository _repository = default!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-boomplay-watchlist-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={Path.Join(_tempRoot, "library.db")}" 
            })
            .Build();
        await new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
        _repository = NewRepository();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task MatchedDeezerIdentity_IsDurableAndReusedAfterRestart()
    {
        var resolverCalls = 0;
        var service = NewService((_, _) =>
        {
            resolverCalls++;
            return Task.FromResult<BoomplayDeezerMatchResult?>(new(
                "3135556", "Mapped title", "Mapped artist", "Mapped album", "cover", 201, "USRC17607839"));
        });

        var first = Assert.Single(await service.ResolveTracksAsync(
            [new BoomplayWatchlistTrackInput("boom-1", "https://www.boomplay.com/songs/boom-1", null, null, null, null, null, null)],
            CancellationToken.None));

        Assert.True(first.IsMatched);
        Assert.Equal("3135556", first.DeezerTrackId);
        Assert.Equal(1, resolverCalls);

        var restartedService = new BoomplayWatchlistMappingService(
            NewRepository(),
            (_, _) => throw new InvalidOperationException("A durable ID-only mapping must not call the resolver."),
            NullLogger<BoomplayWatchlistMappingService>.Instance);
        var restarted = Assert.Single(await restartedService.ResolveTracksAsync(
            [new BoomplayWatchlistTrackInput("boom-1", null, null, null, null, null, null, null)],
            CancellationToken.None));

        Assert.True(restarted.IsMatched);
        Assert.Equal("3135556", restarted.DeezerTrackId);
        Assert.Equal("USRC17607839", restarted.Isrc);
    }

    [Fact]
    public async Task MissingMatch_IsRetryableAndDoesNotRepeatedlyCallProviderInsideRetryWindow()
    {
        var resolverCalls = 0;
        var service = NewService((_, _) =>
        {
            resolverCalls++;
            return Task.FromResult<BoomplayDeezerMatchResult?>(null);
        });
        var input = new BoomplayWatchlistTrackInput(
            "boom-2", null, "Title", "Artist", "Album", null, 200_000, null);

        var first = Assert.Single(await service.ResolveTracksAsync([input], CancellationToken.None));
        var second = Assert.Single(await service.ResolveTracksAsync([input], CancellationToken.None));

        Assert.False(first.IsMatched);
        Assert.False(second.IsMatched);
        Assert.Equal(BoomplayWatchlistMappingService.MappingRetryStatus, second.MappingStatus);
        Assert.Equal(1, resolverCalls);
        var persisted = await NewRepository().GetBoomplayDeezerTrackMappingAsync("boom-2");
        Assert.NotNull(persisted);
        Assert.Null(persisted.DeezerTrackId);
        Assert.True(persisted.NextRetryUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task TemporaryRematchFailure_DoesNotErasePreviouslyVerifiedDeezerIdentity()
    {
        var input = new BoomplayWatchlistTrackInput(
            "boom-3", null, "Original", "Artist", "Album", null, 200_000, null);
        var initial = NewService((_, _) => Task.FromResult<BoomplayDeezerMatchResult?>(new(
            "777", "Original", "Artist", "Album", "cover", 200)));
        Assert.True(Assert.Single(await initial.ResolveTracksAsync([input], CancellationToken.None)).IsMatched);

        var changedInput = input with { Title = "Changed source metadata" };
        var failedRematch = NewService((_, _) => Task.FromResult<BoomplayDeezerMatchResult?>(null));
        var result = Assert.Single(await failedRematch.ResolveTracksAsync([changedInput], CancellationToken.None));

        Assert.True(result.IsMatched);
        Assert.Equal("777", result.DeezerTrackId);
        var persisted = await NewRepository().GetBoomplayDeezerTrackMappingAsync("boom-3");
        Assert.Equal(BoomplayWatchlistMappingService.MatchedStatus, persisted?.Status);
        Assert.Equal("777", persisted?.DeezerTrackId);
    }

    [Fact]
    public async Task VerifiedDeezerIdentity_IsReusedWhenBoomplayMetadataChanges()
    {
        var resolverCalls = 0;
        var input = new BoomplayWatchlistTrackInput(
            "boom-verified", null, "Original", "Artist", "Album", null, 200_000, null);
        var initial = NewService((_, _) =>
        {
            resolverCalls++;
            return Task.FromResult<BoomplayDeezerMatchResult?>(new(
                "555123", "Original", "Artist", "Album", "cover", 200));
        });
        Assert.True(Assert.Single(await initial.ResolveTracksAsync([input], CancellationToken.None)).IsMatched);

        var changedInput = input with { Title = "Changed by Boomplay", Artist = "Changed Artist" };
        var restarted = new BoomplayWatchlistMappingService(
            NewRepository(),
            (_, _) => throw new InvalidOperationException("Verified Boomplay to Deezer mappings must be reused."),
            NullLogger<BoomplayWatchlistMappingService>.Instance);
        var result = Assert.Single(await restarted.ResolveTracksAsync([changedInput], CancellationToken.None));

        Assert.True(result.IsMatched);
        Assert.Equal("555123", result.DeezerTrackId);
        Assert.Equal(1, resolverCalls);
    }

    [Fact]
    public async Task TracksAreMappedSequentiallyInPlaylistOrder()
    {
        var activeResolvers = 0;
        var maxActiveResolvers = 0;
        var resolvedOrder = new List<string>();
        var service = NewService(async (request, _) =>
        {
            var current = Interlocked.Increment(ref activeResolvers);
            maxActiveResolvers = Math.Max(maxActiveResolvers, current);
            await Task.Delay(10);
            Interlocked.Decrement(ref activeResolvers);
            resolvedOrder.Add(request.Url!.Split('/').Last());
            return new BoomplayDeezerMatchResult(
                request.Url!.Split('/').Last().Replace("boom-", "deezer-"),
                request.Title ?? string.Empty,
                request.Artist ?? string.Empty,
                request.Album ?? string.Empty,
                "cover",
                request.DurationMs.GetValueOrDefault() / 1000);
        });

        var results = await service.ResolveTracksAsync(
            [
                new BoomplayWatchlistTrackInput("boom-1", "https://www.boomplay.com/songs/boom-1", "One", "Artist", "Album", null, 200_000, null),
                new BoomplayWatchlistTrackInput("boom-2", "https://www.boomplay.com/songs/boom-2", "Two", "Artist", "Album", null, 210_000, null),
                new BoomplayWatchlistTrackInput("boom-3", "https://www.boomplay.com/songs/boom-3", "Three", "Artist", "Album", null, 220_000, null)
            ],
            CancellationToken.None);

        Assert.Equal(["deezer-1", "deezer-2", "deezer-3"], results.Select(static result => result.DeezerTrackId));
        Assert.Equal(["boom-1", "boom-2", "boom-3"], resolvedOrder);
        Assert.Equal(1, maxActiveResolvers);
    }

    [Fact]
    public async Task ConcurrentConsumersResolveOnePreviouslyUnseenTrackOnlyOnce()
    {
        var resolverCalls = 0;
        var first = NewService(async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref resolverCalls);
            await Task.Delay(50, cancellationToken);
            return new BoomplayDeezerMatchResult(
                "445566", "Title", "Artist", "Album", "cover", 200);
        });
        var second = NewService((_, _) =>
        {
            Interlocked.Increment(ref resolverCalls);
            return Task.FromResult<BoomplayDeezerMatchResult?>(new(
                "445566", "Title", "Artist", "Album", "cover", 200));
        });
        var input = new BoomplayWatchlistTrackInput(
            "boom-single-flight", null, "Title", "Artist", "Album", null, 200_000, null);

        var results = await Task.WhenAll(
            first.ResolveTracksAsync([input], CancellationToken.None),
            second.ResolveTracksAsync([input], CancellationToken.None));

        Assert.Equal(1, resolverCalls);
        Assert.All(results, result => Assert.Equal("445566", Assert.Single(result).DeezerTrackId));
    }

    [Fact]
    public async Task BulkMappingReadReturnsOnlyRequestedDurableMappings()
    {
        var service = NewService((request, _) =>
            Task.FromResult<BoomplayDeezerMatchResult?>(new(
                request.Url!.EndsWith("one", StringComparison.Ordinal) ? "101" : "202",
                request.Title ?? string.Empty,
                request.Artist ?? string.Empty,
                request.Album ?? string.Empty,
                "cover",
                200)));
        await service.ResolveTracksAsync(
            [
                new BoomplayWatchlistTrackInput("one", "https://www.boomplay.com/songs/one", "One", "Artist", "Album", null, 200_000, null),
                new BoomplayWatchlistTrackInput("two", "https://www.boomplay.com/songs/two", "Two", "Artist", "Album", null, 200_000, null)
            ],
            CancellationToken.None);

        var mappings = await NewRepository().GetBoomplayDeezerTrackMappingsAsync(
            ["two", "missing"],
            CancellationToken.None);

        var mapping = Assert.Single(mappings);
        Assert.Equal("two", mapping.Key);
        Assert.Equal("202", mapping.Value.DeezerTrackId);
    }

    [Fact]
    public async Task RuntimeResetDoesNotDeleteCanonicalBoomplayMapping()
    {
        var service = NewService((_, _) =>
            Task.FromResult<BoomplayDeezerMatchResult?>(new(
                "303", "Title", "Artist", "Album", "cover", 200)));
        await service.ResolveTracksAsync(
            [new BoomplayWatchlistTrackInput(
                "survives-reset",
                "https://www.boomplay.com/songs/survives-reset",
                "Title",
                "Artist",
                "Album",
                null,
                200_000,
                null)],
            CancellationToken.None);

        await NewRepository().ClearWatchlistRuntimeAsync(CancellationToken.None);

        var mapping = await NewRepository().GetBoomplayDeezerTrackMappingAsync(
            "survives-reset",
            CancellationToken.None);
        Assert.Equal("303", mapping?.DeezerTrackId);
        Assert.Equal(BoomplayWatchlistMappingService.MatchedStatus, mapping?.Status);
    }

    [Fact]
    public async Task FirstViewEndpointPersistsMatchAndLaterRequestReusesIt()
    {
        var resolverCalls = 0;
        var mappingService = NewService((_, _) =>
        {
            resolverCalls++;
            return Task.FromResult<BoomplayDeezerMatchResult?>(new(
                "909", "Title", "Artist", "Album", "cover", 200));
        });
        var controller = new BoomplayApiController(
            boomplayMetadataService: null!,
            libraryRepository: _repository,
            boomplayWatchlistMappingService: mappingService,
            httpClientFactory: null!,
            NullLogger<BoomplayApiController>.Instance);

        var first = Assert.IsType<OkObjectResult>(await controller.ResolveDeezer(
            "first-view",
            "https://www.boomplay.com/songs/first-view",
            "Title",
            "Artist",
            "Album",
            null,
            200_000,
            "cover",
            CancellationToken.None));
        var second = Assert.IsType<OkObjectResult>(await controller.ResolveDeezer(
            "first-view",
            "https://www.boomplay.com/songs/first-view",
            "Title",
            "Artist",
            "Album",
            null,
            200_000,
            "cover",
            CancellationToken.None));

        Assert.Equal(1, resolverCalls);
        Assert.Contains("\"deezerId\":\"909\"", JsonConvert.SerializeObject(first.Value), StringComparison.Ordinal);
        Assert.Contains("\"deezerId\":\"909\"", JsonConvert.SerializeObject(second.Value), StringComparison.Ordinal);
    }

    [Fact]
    public void BoomplayCandidateResolution_IsPerTrack()
    {
        var unresolved = new PlaylistTrackCandidate(
            "boom-4", null, "Title", "Artist", "Album", null, 200_000, null, Array.Empty<string>());
        var mapped = unresolved with
        {
            DeezerId = "998877",
            MappingStatus = BoomplayWatchlistMappingService.MatchedStatus
        };

        Assert.False(PlaylistCandidateContract.IsResolvable("boomplay", unresolved));
        Assert.True(PlaylistCandidateContract.IsResolvable("boomplay", mapped));
        Assert.Equal([mapped], PlaylistCandidateContract.ResolvableCandidates("boomplay", [mapped, unresolved]));
    }

    [Fact]
    public void BoomplayQueueIntent_RequiresAndUsesCanonicalDeezerIdentity()
    {
        var unresolved = new PlaylistTrackCandidate(
            "boom-4", null, "Title", "Artist", "Album", null, 200_000, null, Array.Empty<string>());
        Assert.Null(WatchlistEngine.BuildWatchDownloadIntentFromCandidate("boomplay", unresolved));

        var mapped = unresolved with { DeezerId = "998877", MappingStatus = BoomplayWatchlistMappingService.MatchedStatus };
        var intent = Assert.IsType<DeezSpoTag.Services.Download.Shared.Models.DownloadIntent>(
            WatchlistEngine.BuildWatchDownloadIntentFromCandidate("boomplay", mapped));

        Assert.Equal("deezer", intent.SourceService);
        Assert.Equal("998877", intent.DeezerId);
        Assert.Equal("https://www.deezer.com/track/998877", intent.SourceUrl);
        Assert.DoesNotContain("boomplay", intent.SourceUrl, StringComparison.OrdinalIgnoreCase);
    }

    private BoomplayWatchlistMappingService NewService(
        Func<BoomplayDeezerMatchRequest, CancellationToken, Task<BoomplayDeezerMatchResult?>> resolver)
        => new(_repository, resolver, NullLogger<BoomplayWatchlistMappingService>.Instance);

    private LibraryRepository NewRepository()
        => new(_configuration, NullLogger<LibraryRepository>.Instance);
}
