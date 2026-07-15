using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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
