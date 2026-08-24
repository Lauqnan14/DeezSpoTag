using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SharedIdentityResolverTests : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private LibraryRepository _repository = default!;
    private SharedIdentityResolver _resolver = default!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-persisted-identity-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = string.Concat("Data Source=", Path.Join(_tempRoot, "library.db"))
            })
            .Build();
        await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
        _repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
        _resolver = new SharedIdentityResolver(_repository);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("plex", "plex-301")]
    [InlineData("jellyfin", "jellyfin-301")]
    [InlineData("navidrome", "navidrome-301")]
    public async Task ResolveAsync_UsesOnlyPersistedMediaServerIdentity(string service, string itemId)
    {
        await _repository.UpsertMediaServerTrackMetadataAsync([
            new MediaServerTrackMetadataUpsertDto(301, service, itemId, "/music/301.flac", DateTimeOffset.UtcNow)
        ]);

        var result = Assert.Single(await _resolver.ResolveAsync(service, [new SharedIdentityResolveItem(301)]));

        Assert.Equal(itemId, result.TargetItemId);
        Assert.Equal(SharedIdentityResolver.StatusResolved, result.Status);
    }

    [Fact]
    public async Task ResolveAsync_MissingIdentityRemainsPendingWithoutSearching()
    {
        var result = Assert.Single(await _resolver.ResolveAsync("plex", [new SharedIdentityResolveItem(401)]));

        Assert.Null(result.TargetItemId);
        Assert.Equal(SharedIdentityResolver.StatusPendingRefresh, result.Status);
    }
}
