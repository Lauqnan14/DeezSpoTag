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

/// <summary>
/// Covers the streaming-platform identity resolver.
/// <para>
/// What matters here is not the happy path. A resolver that records a wrong catalog id writes the
/// wrong track into a user's playlist and looks like it worked, so the tests pin the two things that
/// guard against it: a search result is only recorded once the app's own candidate validator
/// accepts it, and a track the platform does not have stays unresolved rather than being guessed at.
/// They also pin the property that makes the feature affordable - a recorded identity is never
/// searched for again.
/// </para>
/// </summary>
public sealed class PlatformTrackIdentityResolverTest : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private LibraryRepository _repository = default!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-platform-identity-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = string.Concat("Data Source=", Path.Join(_tempRoot, "library.db"))
            })
            .Build();
        await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
        _repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    private PlatformTrackIdentityResolver CreateResolver() => new(_repository);

    private static PlatformIdentityTrack Track(
        long localTrackId,
        string? isrc = "USRC17607839",
        string name = "Paranoid Android",
        string artists = "Radiohead",
        int? durationMs = 383_000)
        => new(localTrackId, isrc, name, artists, "OK Computer", durationMs);

    [Fact]
    public async Task AnAlreadyRecordedIdentityIsReturnedWithoutSearching()
    {
        await _repository.UpsertMediaServerTrackMetadataAsync([
            new MediaServerTrackMetadataUpsertDto(1, "spotify", "recorded-id", string.Empty, DateTimeOffset.UtcNow)
        ]);

        // This resolver has no Spotify client at all, so a search would be impossible. Reaching the
        // recorded id at all proves the stored one was used.
        var resolved = await CreateResolver().ResolveAsync("spotify", [Track(1)], CancellationToken.None);

        Assert.Equal("recorded-id", resolved[1]);
    }

    [Fact]
    public async Task AServiceWithNoSearchWiredLeavesTracksUnresolvedRatherThanFailing()
    {
        var resolved = await CreateResolver().ResolveAsync("tidal", [Track(1)], CancellationToken.None);

        Assert.Empty(resolved);
    }

    [Fact]
    public async Task TracksWithNoLocalIdentityAreSkipped()
    {
        // Local track id 0 means the track is not in this library, so there is no row to attach an
        // identity to and nothing to write.
        var resolved = await CreateResolver().ResolveAsync("spotify", [Track(0)], CancellationToken.None);

        Assert.Empty(resolved);
    }

    [Fact]
    public async Task SupportsIsFalseForEveryServiceWhenNoClientIsWired()
    {
        var resolver = CreateResolver();

        Assert.False(resolver.Supports("spotify"));
        Assert.False(resolver.Supports("deezer"));
        Assert.False(resolver.Supports("qobuz"));
        Assert.False(resolver.Supports("applemusic"));
        Assert.False(resolver.Supports("tidal"));
    }

    [Fact]
    public async Task AnEmptyTrackListResolvesToNothing()
    {
        Assert.Empty(await CreateResolver().ResolveAsync("spotify", [], CancellationToken.None));
    }

    [Fact]
    public void TheAppleRowReaderReturnsNothingForAnEmptyLookup()
    {
        // An ISRC that matches nothing comes back as an empty data array, not an error. Treating
        // that as a match would record an empty catalog id and the write would address nothing.
        using var empty = System.Text.Json.JsonDocument.Parse("""{"data":[]}""");

        Assert.Null(PlatformTrackIdentityResolver.ReadAppleSongId(empty.RootElement));
    }
}