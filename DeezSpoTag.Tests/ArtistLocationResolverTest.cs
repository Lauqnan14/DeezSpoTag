using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services.ArtistLocation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the location precedence: a manual override beats everything, MusicBrainz
/// is the default source, and Audiomack is the fallback.
/// </summary>
/// <remarks>
/// The providers are stubbed through <see cref="IArtistLocationSource"/> because what
/// is verified here is the ordering and the fall-through. Each provider's own
/// matching rules are covered by MusicBrainzArtistLocationTest and
/// AudiomackArtistLocationTest. The manual override uses a real store because it is
/// sealed and database-backed, and a fake would not test the query.
/// </remarks>
public sealed class ArtistLocationResolverTest : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "artist-location-resolver-" + Guid.NewGuid());

    public ArtistLocationResolverTest() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task MusicBrainzWinsWhenItHasAnAnswerAndNoOverrideExists()
    {
        var mb = StubSource.WithAnswer("musicbrainz", "Dar es Salaam", "Tanzania", "TZ");
        var am = StubSource.WithAnswer("audiomack", "Lagos", "Nigeria", "NG");
        var resolver = new ArtistLocationResolver(
            NullLogger<ArtistLocationResolver>.Instance, mb, am);

        var result = await resolver.ResolveAsync(1, "Alikiba", CancellationToken.None);

        Assert.Equal("musicbrainz", result?.Source);
        Assert.Equal("Dar es Salaam", result?.City);
        // MusicBrainz answered, so the fallback must not have been consulted.
        Assert.Equal(0, am.Calls);
    }

    [Fact]
    public async Task AudiomackIsUsedWhenMusicBrainzHasNoAnswer()
    {
        var mb = StubSource.WithNoAnswer("musicbrainz");
        var am = StubSource.WithAnswer("audiomack", "Lagos", "Nigeria", "NG");
        var resolver = new ArtistLocationResolver(
            NullLogger<ArtistLocationResolver>.Instance, mb, am);

        var result = await resolver.ResolveAsync(1, "Alikiba", CancellationToken.None);

        Assert.Equal("audiomack", result?.Source);
        Assert.Equal("Lagos", result?.City);
        Assert.Equal(1, mb.Calls);
        Assert.Equal(1, am.Calls);
    }

    [Fact]
    public async Task AManualOverrideBeatsBothProviders()
    {
        var mb = StubSource.WithAnswer("musicbrainz", "Dar es Salaam", "Tanzania", "TZ");
        var am = StubSource.WithAnswer("audiomack", "Lagos", "Nigeria", "NG");
        var resolver = new ArtistLocationResolver(
            NullLogger<ArtistLocationResolver>.Instance, mb, am, await CreateStoreAsync());

        var result = await resolver.ResolveAsync(1, "Alikiba", CancellationToken.None);

        Assert.Equal("manual", result?.Source);
        Assert.Equal("Johannesburg", result?.City);
        // A user edit is authoritative, so no provider should have been asked.
        Assert.Equal(0, mb.Calls);
        Assert.Equal(0, am.Calls);
    }

    /// <summary>
    /// Clearing both fields deletes the override rather than setting an empty one.
    /// It has to fall through, or clearing the editor would blank the page instead
    /// of restoring what the providers know.
    /// </summary>
    [Fact]
    public async Task AnEmptyOverrideFallsThroughToTheProviders()
    {
        var store = await CreateStoreAsync();
        await store.SetAsync(1, null, null, null);
        var mb = StubSource.WithAnswer("musicbrainz", "Dar es Salaam", "Tanzania", "TZ");
        var resolver = new ArtistLocationResolver(
            NullLogger<ArtistLocationResolver>.Instance, mb, null, store);

        var result = await resolver.ResolveAsync(1, "Alikiba", CancellationToken.None);

        Assert.Equal("musicbrainz", result?.Source);
    }

    [Fact]
    public async Task NoSourceKnowingAnythingYieldsNull()
    {
        var resolver = new ArtistLocationResolver(
            NullLogger<ArtistLocationResolver>.Instance,
            StubSource.WithNoAnswer("musicbrainz"),
            StubSource.WithNoAnswer("audiomack"));

        Assert.Null(await resolver.ResolveAsync(1, "Alikiba", CancellationToken.None));
    }

    [Fact]
    public async Task AResolverWithNoSourcesYieldsNullRatherThanThrowing()
    {
        var resolver = new ArtistLocationResolver(NullLogger<ArtistLocationResolver>.Instance);

        Assert.Null(await resolver.ResolveAsync(1, "Alikiba", CancellationToken.None));
    }

    /// <summary>
    /// A source that throws must not take the page down, and must not stop the
    /// next source from answering.
    /// </summary>
    [Fact]
    public async Task AThrowingSourceFallsThroughToTheNext()
    {
        var am = StubSource.WithAnswer("audiomack", "Lagos", "Nigeria", "NG");
        var resolver = new ArtistLocationResolver(
            NullLogger<ArtistLocationResolver>.Instance, new ThrowingSource(), am);

        var result = await resolver.ResolveAsync(1, "Alikiba", CancellationToken.None);

        Assert.Equal("audiomack", result?.Source);
    }

    [Fact]
    public async Task AThrowingFallbackYieldsNull()
    {
        var resolver = new ArtistLocationResolver(
            NullLogger<ArtistLocationResolver>.Instance,
            StubSource.WithNoAnswer("musicbrainz"),
            new ThrowingSource());

        Assert.Null(await resolver.ResolveAsync(1, "Alikiba", CancellationToken.None));
    }

    [Fact]
    public void TheManualSourceNameIsStable()
    {
        // The artist page keys its editable-versus-resolved behaviour off this exact
        // string, so a rename would silently break the Location editor.
        Assert.Equal("manual", ArtistLocationResolver.ManualSourceName);
    }

    [Fact]
    public async Task GenreContext_UsesOnlyMainArtistsSeparatedGeography()
    {
        await CreateStoreAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Library"] = $"Data Source={Path.Combine(_directory, "library.db")}"
        }).Build();
        await using var db = new Microsoft.Data.Sqlite.SqliteConnection(configuration.GetConnectionString("Library"));
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO artist(id,name) VALUES(1,'Main Artist'); INSERT INTO album(id,artist_id,title) VALUES(1,1,'Album'); INSERT INTO track(id,album_id,title) VALUES(1,1,'Song');";
        await cmd.ExecuteNonQueryAsync();
        var resolver = new ArtistLocationResolver(NullLogger<ArtistLocationResolver>.Instance, audiomack: new RegionalSource());
        var service = new DeezSpoTag.Web.Services.PersonalGenreService(
            new DeezSpoTag.Services.Genre.PersonalGenreStore(configuration),
            new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance),
            NullLogger<DeezSpoTag.Web.Services.PersonalGenreService>.Instance, artistLocations: resolver);
        // ProviderChain is the AutoTag policy and is the one that may consult the
        // source, so that is what this test exercises.
        var method = typeof(DeezSpoTag.Web.Services.PersonalGenreService)
            .GetMethod("ResolveArtistLocationAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var task = (Task<DeezSpoTag.Web.Services.GenreArtistLocationProvenance?>)method.Invoke(service,
            new object?[]
            {
                (long?)1,
                DeezSpoTag.Web.Services.GenreIntelligenceLocationPolicy.ProviderChain,
                CancellationToken.None,
                null
            })!;
        var provenance = Assert.IsType<DeezSpoTag.Web.Services.GenreArtistLocationProvenance>(await task);
        Assert.Equal("Accra", provenance.City);
        Assert.Equal("Greater Accra", provenance.Region);
        Assert.Equal("Ghana", provenance.Country);
        Assert.Equal("audiomack", provenance.Source);
        Assert.True(provenance.HasValue);

        var context = Assert.Single(provenance.ToGenreContext());
        Assert.True(context.IsMainArtist);
    }

    /// <summary>
    /// A preview must never reach a provider, so StoredOnly ignores one entirely.
    /// </summary>
    /// <remarks>
    /// The policy is what keeps the cleanup and construction preview endpoints
    /// offline. Without it, rendering a preview would start an outbound lookup, and
    /// a stored location would be indistinguishable from a fetched one.
    /// </remarks>
    [Fact]
    public async Task StoredOnlyPolicyNeverConsultsAnArtistLocationProvider()
    {
        await CreateStoreAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Library"] = $"Data Source={Path.Combine(_directory, "library.db")}"
        }).Build();
        await using var db = new Microsoft.Data.Sqlite.SqliteConnection(configuration.GetConnectionString("Library"));
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO artist(id,name) VALUES(1,'Main Artist'); INSERT INTO album(id,artist_id,title) VALUES(1,1,'Album'); INSERT INTO track(id,album_id,title) VALUES(1,1,'Song');";
        await cmd.ExecuteNonQueryAsync();

        var counting = new CountingSource();
        var resolver = new ArtistLocationResolver(NullLogger<ArtistLocationResolver>.Instance, audiomack: counting);
        var service = new DeezSpoTag.Web.Services.PersonalGenreService(
            new DeezSpoTag.Services.Genre.PersonalGenreStore(configuration),
            new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance),
            NullLogger<DeezSpoTag.Web.Services.PersonalGenreService>.Instance, artistLocations: resolver);

        var method = typeof(DeezSpoTag.Web.Services.PersonalGenreService)
            .GetMethod("ResolveArtistLocationAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var task = (Task<DeezSpoTag.Web.Services.GenreArtistLocationProvenance?>)method.Invoke(service,
            new object?[]
            {
                (long?)1,
                DeezSpoTag.Web.Services.GenreIntelligenceLocationPolicy.StoredOnly,
                CancellationToken.None,
                null
            })!;

        // No override is stored, so the result is empty rather than a provider value,
        // and the provider was never asked.
        Assert.Null(await task);
        Assert.Equal(0, counting.Calls);
    }

    private sealed class CountingSource : IArtistLocationSource
    {
        public int Calls { get; private set; }

        public string SourceName => "audiomack";

        public Task<ArtistLocationResult?> ResolveAsync(
            long artistId,
            string? artistName,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<ArtistLocationResult?>(
                new ArtistLocationResult("Accra, Ghana", "Accra", "Ghana", "GH", SourceName));
        }
    }

    private sealed class RegionalSource : IArtistLocationSource
    {
        public string SourceName => "audiomack";
        public Task<ArtistLocationResult?> ResolveAsync(long artistId, string? artistName, CancellationToken cancellationToken = default)
            => Task.FromResult<ArtistLocationResult?>(new("Accra, Greater Accra, Ghana", "Accra", "Ghana", "GH", SourceName) { Region = "Greater Accra", Hometown = "Konongo" });
    }

    private sealed class StubSource : IArtistLocationSource
    {
        private readonly string? _city;
        private readonly string? _country;
        private readonly string? _code;

        private StubSource(string source, string? city, string? country, string? code)
        {
            SourceName = source;
            _city = city;
            _country = country;
            _code = code;
        }

        public static StubSource WithAnswer(string source, string city, string country, string code)
            => new(source, city, country, code);

        public static StubSource WithNoAnswer(string source) => new(source, null, null, null);

        public string SourceName { get; }

        public int Calls { get; private set; }

        public Task<ArtistLocationResult?> ResolveAsync(
            long artistId,
            string? artistName,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<ArtistLocationResult?>(
                _city is null && _country is null && _code is null
                    ? null
                    : new ArtistLocationResult(null, _city, _country, _code, SourceName));
        }
    }

    private sealed class ThrowingSource : IArtistLocationSource
    {
        public string SourceName => "throwing";

        public Task<ArtistLocationResult?> ResolveAsync(
            long artistId,
            string? artistName,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("upstream is down");
    }

    private async Task<ArtistLocationOverrideStore> CreateStoreAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={Path.Combine(_directory, "library.db")}"
            })
            .Build();
        await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
        var store = new ArtistLocationOverrideStore(configuration, NullLogger<ArtistLocationOverrideStore>.Instance);
        await store.SetAsync(1, "Johannesburg", "South Africa", "ZA");
        return store;
    }
}
