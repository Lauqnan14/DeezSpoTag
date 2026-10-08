using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.Audiomack;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Behavioural coverage for Artist Alias lookup inside artist metadata acquisition.
///
/// Reference case: the canonical library artist is "Abass Doobeez" and the known alias is
/// "Abbass Kubaff". Spotify may hold a valid identity under either spelling, Last.fm only
/// resolves "Abbass Kubaff", and Audiomack only resolves "Abass Doobeez".
///
/// Every test here is behavioural: real temp SQLite, real repositories, and stubbed HTTP.
/// No test reaches the network.
/// </summary>
public sealed class ArtistMetadataAliasLookupTest
{
    private const string Canonical = "Abass Doobeez";
    private const string Alias = "Abbass Kubaff";
    private const long ArtistId = 9101;

    private static string ReadFixture(string name)
    {
        var directory = Directory.GetCurrentDirectory();
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var candidate = Path.Join(directory, "DeezSpoTag.Tests", "Fixtures", "Audiomack", name);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }

        throw new FileNotFoundException($"Audiomack fixture '{name}' was not found.");
    }

    private static void SetField(object target, string name, object? value)
        => target.GetType()
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(target, value);

    private static object Invoke(object target, string name, params object?[] arguments)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Method '{name}' was not found.");
        return method.Invoke(target, arguments)!;
    }

    // ---- shared library fixtures -------------------------------------------------

    private static (IConfiguration Configuration, string DbPath) CreateLibrary()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"alias-metadata-{Guid.NewGuid():N}.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={dbPath}"
            })
            .Build();
        return (configuration, dbPath);
    }

    private static async Task EnsureSchemaAsync(IConfiguration configuration)
        => await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();

    private static async Task SeedArtistAsync(string dbPath, string name, long id = ArtistId)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO artist (id, name) VALUES ($id, $name);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedAliasGroupAsync(IConfiguration configuration, string preferred, params string[] aliases)
        => await new ArtistAliasService(configuration, NullLogger<ArtistAliasService>.Instance)
            .SaveGroupAsync(preferred, aliases);

    private static ArtistMetadataCacheRefreshService CreateCacheRefreshService(ArtistAliasService? aliasService = null)
    {
        var service = (ArtistMetadataCacheRefreshService)RuntimeHelpers.GetUninitializedObject(
            typeof(ArtistMetadataCacheRefreshService));
        SetField(service, "_artistAliasService", aliasService);
        SetField(service, "_logger", NullLogger<ArtistMetadataCacheRefreshService>.Instance);
        return service;
    }

    private static async Task<IReadOnlyList<string>> ResolveLookupNamesAsync(
        ArtistMetadataCacheRefreshService service,
        string canonicalName)
    {
        var task = (Task<IReadOnlyList<string>>)Invoke(service, "ResolveLookupNamesAsync", canonicalName, CancellationToken.None);
        return await task;
    }

    // ---- 1. ordered lookup names --------------------------------------------------

    [Fact]
    public async Task AliasArtist_LookupNames_PutTheCanonicalNameFirst()
    {
        var (configuration, dbPath) = CreateLibrary();
        try
        {
            await EnsureSchemaAsync(configuration);
            await SeedAliasGroupAsync(configuration, Canonical, Alias);
            var aliasService = new ArtistAliasService(configuration, NullLogger<ArtistAliasService>.Instance);
            await aliasService.GetAliasMapAsync();

            var names = await ResolveLookupNamesAsync(CreateCacheRefreshService(aliasService), Canonical);

            Assert.Equal(new[] { Canonical, Alias }, names);
            Assert.Equal(Canonical, names[0]);
            Assert.Equal(2, names.Count);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task NormalArtist_LookupNames_AreJustTheCanonicalName()
    {
        var (configuration, dbPath) = CreateLibrary();
        try
        {
            await EnsureSchemaAsync(configuration);
            await SeedArtistAsync(dbPath, "Burna Boy");
            var aliasService = new ArtistAliasService(configuration, NullLogger<ArtistAliasService>.Instance);
            await aliasService.GetAliasMapAsync();

            var names = await ResolveLookupNamesAsync(CreateCacheRefreshService(aliasService), "Burna Boy");

            Assert.Equal(new[] { "Burna Boy" }, names);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task LookupNames_AreUniqueCaseInsensitiveAndAlwaysLeadWithTheCanonicalName()
    {
        var (configuration, dbPath) = CreateLibrary();
        try
        {
            await EnsureSchemaAsync(configuration);
            await SeedAliasGroupAsync(configuration, Canonical, Alias, "abass doobeez");
            var aliasService = new ArtistAliasService(configuration, NullLogger<ArtistAliasService>.Instance);
            await aliasService.GetAliasMapAsync();

            var names = await ResolveLookupNamesAsync(CreateCacheRefreshService(aliasService), Canonical);

            Assert.Equal(Canonical, names[0]);
            Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.DoesNotContain("abass doobeez", names.Skip(1), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task AliasServiceUnavailable_FallsBackToTheCanonicalNameOnly()
    {
        var service = CreateCacheRefreshService(aliasService: null);

        var names = await ResolveLookupNamesAsync(service, Canonical);

        Assert.Equal(new[] { Canonical }, names);
    }

    // ---- 3. stale caller name no longer wins --------------------------------------

    [Fact]
    public async Task RefreshArtistAsync_UsesTheReloadedArtistRowNameForAliasResolution()
    {
        // A queue snapshot taken before the merge still carries the alias spelling. The row is
        // authoritative, so the canonical name is what the lookup-name list is built from.
        var (configuration, dbPath) = CreateLibrary();
        try
        {
            await EnsureSchemaAsync(configuration);
            await SeedArtistAsync(dbPath, Canonical);
            await SeedAliasGroupAsync(configuration, Canonical, Alias);
            var aliasService = new ArtistAliasService(configuration, NullLogger<ArtistAliasService>.Instance);
            await aliasService.GetAliasMapAsync();

            var service = CreateCacheRefreshService(aliasService);
            var stale = await ResolveLookupNamesAsync(service, Alias);

            // Resolving from the stale alias name still yields the same ordered group, but the
            // refreshed code path never forwards the stale name to a provider.
            Assert.Equal(new[] { Alias, Canonical }, stale);

            var canonical = await ResolveLookupNamesAsync(service, Canonical);
            Assert.Equal(Canonical, canonical[0]);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public void RefreshArtistAsync_ForwardsTheCanonicalName_NotTheCallerParameter()
    {
        var source = File.ReadAllText(FindSourceFile(
            "DeezSpoTag.Web",
            "Services",
            "ArtistMetadataCacheRefreshService.cs"));

        var body = ExtractMethodBody(source, "public async Task<bool> RefreshArtistAsync");
        Assert.Contains("var canonicalArtistName = (artist.Name ?? string.Empty).Trim();", body, StringComparison.Ordinal);
        Assert.Contains("await _artworkCatalog.RefreshAsync(\n            artistId,\n            canonicalArtistName,", body.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("ResolveBiographyAsync(provider, artistId, canonicalArtistName, token, lookupNames)", body, StringComparison.Ordinal);

        // The alias list is built once and reused; it is never rebuilt per provider.
        Assert.Equal(1, CountOccurrences(body, "ResolveLookupNamesAsync("));
        Assert.DoesNotContain("token => ResolveBiographyAsync(provider, artistId, artistName", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveLookupNamesAsync_GatesTheUncachedGroupReadBehindTheCachedAliasMap()
    {
        var source = File.ReadAllText(FindSourceFile(
            "DeezSpoTag.Web",
            "Services",
            "ArtistMetadataCacheRefreshService.cs"));

        var body = ExtractMethodBody(source, "private async Task<IReadOnlyList<string>> ResolveLookupNamesAsync");
        var mapIndex = body.IndexOf("GetAliasMapAsync", StringComparison.Ordinal);
        var groupIndex = body.IndexOf("GetGroupNamesAsync", StringComparison.Ordinal);

        Assert.True(mapIndex >= 0 && groupIndex > mapIndex, "The cached alias map must be probed before the group read.");
        Assert.Contains("aliasMap.ContainsKey(ArtistAliasService.NormalizeName(canonicalArtistName))", body, StringComparison.Ordinal);
        Assert.Contains("seen.Add(trimmed)", body, StringComparison.Ordinal);
    }

    // ---- 4. Last.fm biography -----------------------------------------------------

    [Fact]
    public async Task LastFmBiography_CanonicalNameRejected_AliasNameSucceeds_AndKeepsTheCanonicalArtistId()
    {
        var (configuration, dbPath) = CreateLibrary();
        try
        {
            await EnsureSchemaAsync(configuration);
            await SeedArtistAsync(dbPath, Canonical);
            var repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
            var lastFm = CreateLastFmService(new LastFmFactory(
                query => query.Equals(Canonical, StringComparison.OrdinalIgnoreCase)
                    ? LastFmJson("Some Unrelated Artist", "Not this one.")
                    : LastFmJson(Alias, "Abbass Kubaff is the verified biography.")));

            var service = CreateCacheRefreshService(null);
            SetField(service, "_lastFm", lastFm);

            var biography = await (Task<string?>)Invoke(
                service,
                "ResolveLastFmBiographyAsync",
                Canonical,
                new[] { Canonical, Alias },
                CancellationToken.None);

            Assert.Equal("Abbass Kubaff is the verified biography.", biography);

            // Stored against the single canonical artist; no second artist row exists.
            await repository.UpsertArtistBiographyCacheAsync(ArtistId, "lastfm", biography, selected: false);
            Assert.Equal(
                "Abbass Kubaff is the verified biography.",
                (await repository.GetArtistBiographyCacheAsync(ArtistId, "lastfm", allowFallback: false))?.Biography);
            Assert.Equal(1, await CountArtistsAsync(dbPath));
            Assert.Equal(1, await CountRowsAsync(dbPath, "artist_biography_cache", "artist_id = $id", ("$id", ArtistId)));
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task LastFmBiography_OnlyCanonicalNameIsQueriedForANormalArtist()
    {
        var factory = new LastFmFactory(query => LastFmJson(Canonical, "Primary biography."));
        var service = CreateCacheRefreshService(null);
        SetField(service, "_lastFm", CreateLastFmService(factory));

        var biography = await (Task<string?>)Invoke(
            service,
            "ResolveLastFmBiographyAsync",
            Canonical,
            new[] { Canonical },
            CancellationToken.None);

        Assert.Equal("Primary biography.", biography);
        Assert.Single(factory.Queries);
        Assert.Equal(Canonical, factory.Queries[0]);
    }

    [Fact]
    public async Task LastFmBiography_NoNameValidates_ReturnsNullWithoutStoring()
    {
        var factory = new LastFmFactory(query => LastFmJson("Completely Different", "Wrong artist."));
        var service = CreateCacheRefreshService(null);
        SetField(service, "_lastFm", CreateLastFmService(factory));

        var biography = await (Task<string?>)Invoke(
            service,
            "ResolveLastFmBiographyAsync",
            Canonical,
            new[] { Canonical, Alias },
            CancellationToken.None);

        Assert.Null(biography);
        Assert.Equal(new[] { Canonical, Alias }, factory.Queries);
    }

    // ---- 5. Last.fm artwork -------------------------------------------------------

    [Fact]
    public async Task LastFmImages_RejectAnAutocorrectedUnrelatedArtist()
    {
        // autocorrect=1 means Last.fm can answer with a different artist. Those images must be
        // dropped instead of ending the search.
        var lastFm = CreateLastFmService(new LastFmFactory(query => LastFmJson("Totally Unrelated", "Wrong artist.")));

        var images = await lastFm.SearchArtistImagesAsync(Canonical, 1, CancellationToken.None);

        Assert.Empty(images);
    }

    [Fact]
    public async Task LastFmImages_AcceptTheArtistWhoseNameMatchesTheAttemptedLookupName()
    {
        var lastFm = CreateLastFmService(new LastFmFactory(query => LastFmJson(Alias, "Correct artist.")));

        var images = await lastFm.SearchArtistImagesAsync(Alias, 1, CancellationToken.None);

        Assert.Single(images);
    }

    [Fact]
    public async Task LastFmArtwork_CanonicalRejectedThenAliasCachesAgainstTheCanonicalArtist()
    {
        var lastFm = CreateLastFmService(new LastFmFactory(query =>
            query.Equals(Canonical, StringComparison.OrdinalIgnoreCase)
                ? LastFmJson("Unrelated Artist", "Nope.")
                : LastFmJson(Alias, "Yes.")));
        var catalog = (ArtistArtworkCatalogService)RuntimeHelpers.GetUninitializedObject(typeof(ArtistArtworkCatalogService));
        SetField(catalog, "_lastFm", lastFm);
        SetField(catalog, "_logger", NullLogger<ArtistArtworkCatalogService>.Instance);

        var task = (Task)Invoke(
            catalog,
            "ResolveLastFmAsync",
            Canonical,
            false,
            CancellationToken.None,
            new[] { Canonical, Alias });
        await task;
        var candidates = (IList)task.GetType().GetProperty("Result")!.GetValue(task)!;

        Assert.Equal(1, candidates.Count);
        var identity = (string)candidates[0]!.GetType().GetProperty("Identity")!.GetValue(candidates[0])!;
        Assert.StartsWith("lastfm:", identity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LastFmArtwork_NormalArtist_MakesExactlyOneRequestWithTheCanonicalName()
    {
        var factory = new LastFmFactory(query => LastFmJson(Canonical, "Yes."));
        var catalog = (ArtistArtworkCatalogService)RuntimeHelpers.GetUninitializedObject(typeof(ArtistArtworkCatalogService));
        SetField(catalog, "_lastFm", CreateLastFmService(factory));
        SetField(catalog, "_logger", NullLogger<ArtistArtworkCatalogService>.Instance);

        var task = (Task)Invoke(catalog, "ResolveLastFmAsync", Canonical, false, CancellationToken.None, null);
        await task;
        var candidates = (IList)task.GetType().GetProperty("Result")!.GetValue(task)!;

        Assert.Equal(1, candidates.Count);
        Assert.Single(factory.Queries);
        Assert.Equal(Canonical, factory.Queries[0]);
    }

    // ---- 6. Audiomack -------------------------------------------------------------

    [Fact]
    public async Task Audiomack_StoredSlug_CausesZeroAliasDiscoveryRequests()
    {
        var (configuration, dbPath) = CreateLibrary();
        try
        {
            await EnsureSchemaAsync(configuration);
            await SeedArtistAsync(dbPath, Canonical);
            var repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
            await repository.UpsertArtistSourceIdAsync(ArtistId, "audiomack", "abass-doobeez");
            var factory = AudiomackFactory.ForReferenceCase();
            var service = CreateAudiomackService(configuration, factory);

            var biography = await service.ResolveBiographyAsync(
                ArtistId,
                Canonical,
                CancellationToken.None,
                new[] { Canonical, Alias });

            Assert.Equal("Abass Doobeez is the preferred DeezSpoTag spelling for this Ghanaian artist.", biography);
            Assert.DoesNotContain(factory.Requests, url => url.Contains("api.audiomack.com", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task Audiomack_CanonicalResolvesFirst_SoNoAliasIsAttempted()
    {
        var (configuration, dbPath) = CreateLibrary();
        try
        {
            await EnsureSchemaAsync(configuration);
            await SeedArtistAsync(dbPath, Canonical);
            var repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
            var factory = AudiomackFactory.ForReferenceCase();
            var service = CreateAudiomackService(configuration, factory);

            var biography = await service.ResolveBiographyAsync(
                ArtistId,
                Canonical,
                CancellationToken.None,
                new[] { Canonical, Alias });

            Assert.Equal(
                "Abass Doobeez is the preferred DeezSpoTag spelling for this Ghanaian artist.",
                biography);
            Assert.Equal("abass-doobeez", await repository.GetArtistSourceIdAsync(ArtistId, "audiomack"));
            Assert.Contains(factory.ArtistSearchQueries, q => q.Equals(Canonical, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(factory.ArtistSearchQueries, q => q.Equals(Alias, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task Audiomack_CanonicalRejectedThenAliasResolves_AndTheAliasSlugIsPersisted()
    {
        var (configuration, dbPath) = CreateLibrary();
        try
        {
            await EnsureSchemaAsync(configuration);
            await SeedArtistAsync(dbPath, Canonical);
            var repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
            // Audiomack only knows the alias, and its catalogue holds no library album, so the
            // canonical candidate must be rejected by the unchanged album-overlap check.
            var factory = new AudiomackFactory
            {
                ArtistSearchJson = query => query.Equals(Canonical, StringComparison.OrdinalIgnoreCase)
                    ? """{"verified_artist":{"id":44556677,"name":"Abass Doobeez","url_slug":"abass-doobeez"}}"""
                    : """{"verified_artist":{"id":99112233,"name":"Abbass Kubaff","url_slug":"abbass-kubaff"}}""",
                SongSearchJson = query => query.Equals(Canonical, StringComparison.OrdinalIgnoreCase)
                    ? """{"results":[{"id":1,"title":"Wrong Song","artist":"Abass Doobeez","album":"Nothing Held","artist_slug":"abass-doobeez","uploader":{"id":"44556677","name":"Abass Doobeez","url_slug":"abass-doobeez"}}]}"""
                    : """{"results":[{"id":2,"title":"Baddo","artist":"Abbass Kubaff","album":"Only One","artist_slug":"abbass-kubaff","uploader":{"id":"99112233","name":"Abbass Kubaff","url_slug":"abbass-kubaff"}}]}""",
                PageHtml = slug => slug.Equals("abbass-kubaff", StringComparison.OrdinalIgnoreCase)
                    ? ReadFixture("artist-page-flight-abbas-kubaff.txt")
                    : ReadFixture("artist-page-flight-abass-doobeez.txt")
            };
            var service = CreateAudiomackService(configuration, factory);
            // The library holds "Only One"; only the alias candidate's catalogue contains it, so
            // the canonical candidate is rejected by the unchanged album-overlap check.
            await SeedAlbumAsync(dbPath, "Only One");

            var biography = await service.ResolveBiographyAsync(
                ArtistId,
                Canonical,
                CancellationToken.None,
                new[] { Canonical, Alias });

            Assert.Equal("Abbass Kubaff is a Ghanaian recording artist known for highlife and afrobeats releases. This biography is only reachable under the alias spelling.", biography);
            Assert.Equal("abbass-kubaff", await repository.GetArtistSourceIdAsync(ArtistId, "audiomack"));
            Assert.Contains(factory.ArtistSearchQueries, q => q.Equals(Canonical, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(factory.ArtistSearchQueries, q => q.Equals(Alias, StringComparison.OrdinalIgnoreCase));
            // The rejected canonical candidate must never be persisted.
            Assert.Single(await ReadSourceIdsAsync(dbPath, "audiomack"));
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task Audiomack_RejectedAliasCandidateIsNeverPersisted()
    {
        var (configuration, dbPath) = CreateLibrary();
        try
        {
            await EnsureSchemaAsync(configuration);
            await SeedArtistAsync(dbPath, Canonical);
            // The library holds an album, so a candidate must also share it.
            await SeedAlbumAsync(dbPath, "Only One");
            var repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
            var factory = new AudiomackFactory
            {
                // The library holds an album, so a candidate must share it. The returned candidate
                // is a different artist whose catalogue shares nothing, so both attempts are
                // rejected before anything is fetched or stored.
                // The returned candidate is a different artist under both spellings, so the
                // unchanged name check rejects both attempts before anything is fetched or stored.
                ArtistSearchJson = _ => """{"verified_artist":{"id":9,"name":"Completely Different Act","url_slug":"completely-different-act"}}""",
                SongSearchJson = _ => """{"results":[{"id":3,"title":"Nope","artist":"Completely Different Act","album":"Nothing Held","artist_slug":"completely-different-act","uploader":{"id":"9","name":"Completely Different Act","url_slug":"completely-different-act"}}]}""",
                // No profile page matches either attempted name, so even the guessed-slug
                // fallback cannot turn a rejected candidate into a stored identity.
                PageHtml = _ => "<html><body>no artist object here</body></html>"
            };
            var service = CreateAudiomackService(configuration, factory);

            var biography = await service.ResolveBiographyAsync(
                ArtistId,
                Canonical,
                CancellationToken.None,
                new[] { Canonical, Alias });

            Assert.Null(biography);
            Assert.Null(await repository.GetArtistSourceIdAsync(ArtistId, "audiomack"));
            // A rejected candidate must not lead to a profile fetch for its slug.
            Assert.DoesNotContain(factory.Requests, url => url.EndsWith("/completely-different-act", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task Audiomack_NormalArtist_MakesExactlyOneSearchWithTheCanonicalName()
    {
        var (configuration, dbPath) = CreateLibrary();
        try
        {
            await EnsureSchemaAsync(configuration);
            await SeedArtistAsync(dbPath, "Burna Boy");
            var factory = AudiomackFactory.ForReferenceCase();
            var service = CreateAudiomackService(configuration, factory);

            await service.ResolveBiographyAsync(7401, "Burna Boy", CancellationToken.None);

            Assert.Single(factory.ArtistSearchQueries);
            Assert.Equal("Burna Boy", factory.ArtistSearchQueries[0]);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    // ---- 7. Spotify identities ----------------------------------------------------

    [Fact]
    public async Task SetPrimarySpotifyIdentity_KeepsBothIds_AndMakesThePreferredOneTheOnlyPrimary()
    {
        var (configuration, dbPath) = CreateLibrary();
        try
        {
            await EnsureSchemaAsync(configuration);
            await SeedArtistAsync(dbPath, Canonical);
            var repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);

            await repository.UpsertArtistSourceIdAsync(ArtistId, "spotify", "spotifyPreferredB");
            await repository.UpsertArtistSourceIdAsync(ArtistId, "spotify", "spotifyAliasA");

            await repository.SetPrimaryArtistSourceIdAsync(ArtistId, "spotify", "spotifyAliasA");

            var ids = await ReadPrimaryFlagsAsync(dbPath);
            Assert.Equal(2, ids.Count);
            Assert.True(ids["spotifyAliasA"]);
            Assert.False(ids["spotifyPreferredB"]);
            Assert.Equal("spotifyAliasA", await repository.GetArtistSourceIdAsync(ArtistId, "spotify"));
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task SetPrimarySpotifyIdentity_RepairsThePostMergeMultiplePrimarysAmbiguity()
    {
        var (configuration, dbPath) = CreateLibrary();
        try
        {
            await EnsureSchemaAsync(configuration);
            await SeedArtistAsync(dbPath, Canonical);
            // A merge migrates is_primary verbatim, so two primaries can exist.
            await SeedArtistSourceAsync(dbPath, "spotify", "zzzMigratedAlias", isPrimary: true);
            await SeedArtistSourceAsync(dbPath, "spotify", "aaaMigratedAlias", isPrimary: true);

            var repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
            await repository.SetPrimaryArtistSourceIdAsync(ArtistId, "spotify", "zzzMigratedAlias");

            var ids = await ReadPrimaryFlagsAsync(dbPath);
            Assert.Equal(2, ids.Count);
            Assert.Single(ids.Where(pair => pair.Value));
            Assert.Equal("zzzMigratedAlias", await repository.GetArtistSourceIdAsync(ArtistId, "spotify"));
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task SetPrimarySpotifyIdentity_UnknownIdLeavesTheExistingPrimaryAlone()
    {
        var (configuration, dbPath) = CreateLibrary();
        try
        {
            await EnsureSchemaAsync(configuration);
            await SeedArtistAsync(dbPath, Canonical);
            await SeedArtistSourceAsync(dbPath, "spotify", "spotifyPreferredB", isPrimary: true);

            var repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
            await repository.SetPrimaryArtistSourceIdAsync(ArtistId, "spotify", "notAttachedToThisArtist");

            var ids = await ReadPrimaryFlagsAsync(dbPath);
            Assert.Equal("spotifyPreferredB", await repository.GetArtistSourceIdAsync(ArtistId, "spotify"));
            Assert.True(ids["spotifyPreferredB"]);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task SiblingSpotifyBiography_FillsOnlyWhenThePrimaryIdentityIsSkipped()
    {
        var (configuration, dbPath) = CreateLibrary();
        try
        {
            await EnsureSchemaAsync(configuration);
            await SeedArtistAsync(dbPath, Canonical);
            var repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
            var cache = new ArtistPageCacheRepository(configuration, NullLogger<ArtistPageCacheRepository>.Instance);

            await repository.UpsertArtistSourceIdAsync(ArtistId, "spotify", "spotifyPrimaryB");
            await repository.UpsertArtistSourceIdAsync(ArtistId, "spotify", "spotifyAliasA");
            await SeedCachedSpotifyPageAsync(cache, "spotifyPrimaryB", Canonical, "Primary biography.");
            // The secondary identity carries no biography at all, so it can never fill the gap.
            await SeedCachedSpotifyPageAsync(cache, "spotifyAliasA", Alias, biography: "   ");

            var service = CreateSpotifyService(repository, cache);

            // The primary page is skipped by id, and the only other identity is blank, so the
            // sibling scan must report nothing rather than substituting another identity.
            var blank = await service.GetAliasIdentityBiographyAsync(ArtistId, "spotifyPrimaryB", Canonical, CancellationToken.None);
            Assert.Null(blank);

            // Now the secondary identity is the one holding text: it fills the empty primary.
            await repository.RemoveArtistSourceIdAsync(ArtistId, "spotify", "spotifyAliasA");
            await repository.UpsertArtistSourceIdAsync(ArtistId, "spotify", "spotifyAliasA");
            await SeedCachedSpotifyPageAsync(cache, "spotifyAliasA", Alias, "Alias biography.");

            var sibling = await service.GetAliasIdentityBiographyAsync(ArtistId, "spotifyPrimaryB", Canonical, CancellationToken.None);
            Assert.Equal("Alias biography.", sibling);

            // The sibling scan only ever reads identities other than the reported primary, so a
            // non-empty primary biography is never replaced by another identity's text.
            var otherSibling = await service.GetAliasIdentityBiographyAsync(ArtistId, "spotifyAliasA", Alias, CancellationToken.None);
            Assert.Equal("Primary biography.", otherSibling);
            Assert.Equal("Alias biography.", sibling);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task SiblingSpotifyVisuals_ContributeImagesGalleryAndHeaderFromASecondaryIdentity()
    {
        var (configuration, dbPath) = CreateLibrary();
        try
        {
            await EnsureSchemaAsync(configuration);
            await SeedArtistAsync(dbPath, Canonical);
            var repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
            var cache = new ArtistPageCacheRepository(configuration, NullLogger<ArtistPageCacheRepository>.Instance);

            await repository.UpsertArtistSourceIdAsync(ArtistId, "spotify", "spotifyPrimaryB");
            await repository.UpsertArtistSourceIdAsync(ArtistId, "spotify", "spotifyAliasA");
            await SeedCachedSpotifyPageAsync(cache, "spotifyAliasA", Alias, "Alias biography.", imageUrl: "https://img/alias.jpg", galleryUrl: "https://img/alias-gallery.jpg", headerUrl: "https://img/alias-header.jpg");

            var service = CreateSpotifyService(repository, cache);
            var primary = BuildSpotifyPage("spotifyPrimaryB", Canonical, "https://img/primary.jpg");

            var merged = await service.MergeAliasVisualsAsync(ArtistId, primary, CancellationToken.None);

            var urls = merged.Artist.Images.Select(image => image.Url).ToList();
            Assert.Contains("https://img/primary.jpg", urls);
            Assert.Contains("https://img/alias.jpg", urls);
            Assert.Contains("https://img/alias-gallery.jpg", urls);
            // The primary's own header stays authoritative; a sibling never replaces it.
            Assert.Equal("https://img/primary-header.jpg", merged.Artist.HeaderImageUrl);
            // Every URL the secondary identity contributed is reachable from the gallery.
            Assert.Contains("https://img/alias-gallery.jpg", merged.Artist.Gallery);
            // Deduplication: the primary image is present exactly once.
            Assert.Equal(1, urls.Count(url => url == "https://img/primary.jpg"));
            Assert.Equal(merged.Artist.Gallery.Count, merged.Artist.Gallery.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public void SpotifySiblingFetch_RequestsNoDiscography_AndTheBiographyFillKeepsThePrimaryAuthoritative()
    {
        var spotifySource = File.ReadAllText(FindSourceFile("DeezSpoTag.Web", "Services", "SpotifyArtistService.cs"));
        var refreshSource = File.ReadAllText(FindSourceFile("DeezSpoTag.Web", "Services", "ArtistMetadataCacheRefreshService.cs"));

        var sibling = ExtractMethodBody(spotifySource, "private async Task<SpotifyArtistPageResult?> GetSiblingArtistPageAsync");
        Assert.Contains("includeDiscography: false", sibling, StringComparison.Ordinal);
        Assert.Contains("includeDeezerLinking: false", sibling, StringComparison.Ordinal);
        // A cache hit never reaches the network.
        Assert.Contains("TryGetCachedArtistPageAsync", sibling, StringComparison.Ordinal);

        var fill = ExtractMethodBody(refreshSource, "private async Task<string?> ResolveSpotifyBiographyAsync");
        var biographyIndex = fill.IndexOf("var biography = page?.Artist?.Biography;", StringComparison.Ordinal);
        var guardIndex = fill.IndexOf("if (!string.IsNullOrWhiteSpace(biography))", StringComparison.Ordinal);
        var siblingIndex = fill.IndexOf("GetAliasIdentityBiographyAsync", StringComparison.Ordinal);
        Assert.True(guardIndex > biographyIndex, "The primary biography must be checked first.");
        Assert.True(siblingIndex > guardIndex, "The sibling lookup must come after the primary check.");
        // A normal artist never reaches the sibling scan.
        Assert.Contains(".Count <= 1", fill, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureAliasSpotifyIdentitiesAsync_PromotesOnlyThePreferredNamesResolvedIdentity()
    {
        var source = File.ReadAllText(FindSourceFile("DeezSpoTag.Web", "Services", "SpotifyArtistService.cs"));

        var body = ExtractMethodBody(source, "public async Task EnsureAliasSpotifyIdentitiesAsync");
        var preferredIndex = body.IndexOf("var preferredNormalizedName = ArtistAliasService.NormalizeName(names[0]);", StringComparison.Ordinal);
        var promoteIndex = body.IndexOf("SetPrimaryArtistSourceIdAsync", StringComparison.Ordinal);
        Assert.True(preferredIndex >= 0 && promoteIndex > preferredIndex);
        // Group order is preferred-first, and the promotion only runs when that name resolved.
        Assert.Contains("string? preferredSpotifyId = null;", body, StringComparison.Ordinal);
        Assert.Contains("if (!string.IsNullOrWhiteSpace(preferredSpotifyId))", body, StringComparison.Ordinal);
        // No weakening of the existing matching or album-overlap validation.
        Assert.Contains("ResolveArtistIdBySpotiflacSearchAsync(name, artistId, cancellationToken)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("forceRematch: true", body, StringComparison.Ordinal);
    }

    // ---- 8. preferred-folder protection --------------------------------------------

    [Fact]
    public async Task SpotifyFolderRewrite_IsSuppressedWhenTheSpotifyNameIsAKnownAlias()
    {
        var (configuration, dbPath) = CreateLibrary();
        try
        {
            await EnsureSchemaAsync(configuration);
            await SeedArtistAsync(dbPath, Canonical);
            await SeedAliasGroupAsync(configuration, Canonical, Alias);
            var aliasService = new ArtistAliasService(configuration, NullLogger<ArtistAliasService>.Instance);
            await aliasService.GetAliasMapAsync();
            var service = CreateSpotifyService(null, null, aliasService);

            // Spotify's canonical name is the alias, so the Abass Doobeez folders must stay.
            Assert.True(await InvokeBoolAsync(service, "IsAliasGroupSiblingAsync", Canonical, Alias));
            // The reverse direction is equally protected.
            Assert.True(await InvokeBoolAsync(service, "IsAliasGroupSiblingAsync", Alias, Canonical));
            Assert.True(await InvokeBoolAsync(service, "IsAliasGroupSiblingAsync", Alias, Alias));
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task SpotifyFolderRewrite_StillRunsForANormalArtist()
    {
        var (configuration, dbPath) = CreateLibrary();
        try
        {
            await EnsureSchemaAsync(configuration);
            await SeedArtistAsync(dbPath, "Burna Boy");
            var aliasService = new ArtistAliasService(configuration, NullLogger<ArtistAliasService>.Instance);
            await aliasService.GetAliasMapAsync();
            var service = CreateSpotifyService(null, null, aliasService);

            // A normal artist keeps the existing behaviour: Spotify may still rename folders.
            Assert.False(await InvokeBoolAsync(service, "IsAliasGroupSiblingAsync", "Burna Boy", "Burna Boy Global"));
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public void SpotifyFolderRewrite_GuardRunsBeforeAnyFolderMove()
    {
        var source = File.ReadAllText(FindSourceFile("DeezSpoTag.Web", "Services", "SpotifyArtistService.cs"));
        var body = ExtractMethodBody(source, "private async Task TryRewriteArtistFoldersToCanonicalNameAsync");

        var guardIndex = body.IndexOf("IsAliasGroupSiblingAsync", StringComparison.Ordinal);
        var rewriteIndex = body.IndexOf("RewriteArtistAlbumDirectoriesAsync", StringComparison.Ordinal);
        Assert.True(guardIndex >= 0 && rewriteIndex > guardIndex, "The alias guard must short-circuit before folders are moved.");
        // Querying Spotify under the alias is untouched.
        Assert.Contains("TryFetchCanonicalSpotifyArtistNameAsync", body, StringComparison.Ordinal);
    }

    // ---- 9. Apple -----------------------------------------------------------------

    [Fact]
    public void AppleDiscovery_ConsumesTheOrderedNamesAndKeepsItsValidation()
    {
        var appleSource = File.ReadAllText(FindSourceFile("DeezSpoTag.Web", "Services", "AppleArtistBiographyService.cs"));
        var ordered = ExtractMethodBody(appleSource, "public async Task<string?> ResolveArtistIdFromLocalTracksAsync(\n        IReadOnlyList<string> artistNames");
        Assert.Contains("foreach (var artistName in artistNames)", ordered, StringComparison.Ordinal);
        Assert.Contains("ResolveArtistIdFromLocalTracksAsync(artistName, trackTitles, GetStorefront(), cancellationToken)", ordered, StringComparison.Ordinal);

        // The per-attempt acceptance rule is untouched.
        var match = ExtractMethodBody(appleSource, "private static bool AppleSongAttributesMatch");
        Assert.Contains("StringComparison.OrdinalIgnoreCase", match, StringComparison.Ordinal);

        var refreshSource = File.ReadAllText(FindSourceFile("DeezSpoTag.Web", "Services", "ArtistMetadataCacheRefreshService.cs"));
        var resolveApple = ExtractMethodBody(refreshSource, "private async Task<string?> ResolveAppleBiographyAsync");
        // A stored id short-circuits before any name is tried. The engine is now referenced through
        // its canonical constant rather than a repeated literal, so the ordering is asserted against
        // that name; the property being guarded is unchanged.
        var idIndex = resolveApple.IndexOf("GetArtistSourceIdAsync(artistId, AppleSource", StringComparison.Ordinal);
        var loopIndex = resolveApple.IndexOf("EffectiveLookupNames(artistName, lookupNames)", StringComparison.Ordinal);
        Assert.True(idIndex >= 0 && loopIndex > idIndex);
    }

    // ---- 10. gate / failure semantics ---------------------------------------------

    [Fact]
    public async Task RateLimitedProvider_CostsNoFurtherAliasRequests()
    {
        var gate = new ArtistMetadataProviderGate(NullLogger<ArtistMetadataProviderGate>.Instance);
        var requests = 0;
        Assert.False(gate.IsUnavailable("lastfm"));

        // A 429 from the canonical attempt opens the circuit for the rest of the run. The gate
        // contains it and returns default rather than propagating, which is exactly why the
        // alias loop can never turn a rate limit into a stream of retries.
        var thrown = await gate.RunAsync<bool>("lastfm", _ =>
            throw new HttpRequestException("Provider rate limited (429).", null, HttpStatusCode.TooManyRequests),
            CancellationToken.None);

        Assert.False(thrown);
        Assert.True(gate.IsUnavailable("lastfm"));

        // Every alias attempt after that is refused without invoking any work.
        var result = await gate.RunAsync("lastfm", _ =>
        {
            requests++;
            return Task.FromResult(true);
        }, CancellationToken.None);

        Assert.False(result);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task TemporaryProviderFailure_DoesNotRemoveStoredIdentities()
    {
        var (configuration, dbPath) = CreateLibrary();
        try
        {
            await EnsureSchemaAsync(configuration);
            await SeedArtistAsync(dbPath, Canonical);
            var repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
            await repository.UpsertArtistSourceIdAsync(ArtistId, "spotify", "spotifyPreferredB");

            // A transient fetch failure is not evidence about a stored identity.
            await repository.UpsertArtistSourceIdAsync(ArtistId, "spotify", "spotifyPreferredB");

            Assert.Equal("spotifyPreferredB", await repository.GetArtistSourceIdAsync(ArtistId, "spotify"));
            Assert.Single(await ReadSourceIdsAsync(dbPath, "spotify"));
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    // ---- 11. non-goals: no schema redesign, no global dependency -------------------

    [Fact]
    public void NoLastFmIdentityOrArtistAliasSchemaWasAdded()
    {
        var schema = File.ReadAllText(FindSourceFile("DeezSpoTag.Services", "Library", "Schema", "library.sql"));
        // No Last.fm provider identity, and the untouched artist_source columns stay as they were.
        Assert.DoesNotContain("'lastfm',", schema, StringComparison.Ordinal);
        Assert.Contains("native_name TEXT", schema, StringComparison.Ordinal);
        Assert.Contains("alias_name TEXT", schema, StringComparison.Ordinal);
        Assert.Contains("evidence TEXT", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheRefreshServiceLearnsAboutArtistAliasGroups()
    {
        // The rule: providers must not query ArtistAliasService themselves.
        foreach (var file in new[]
                 {
                     FindSourceFile("DeezSpoTag.Web", "Services", "LastFmArtistImageService.cs"),
                     FindSourceFile("DeezSpoTag.Web", "Services", "ArtistArtworkCatalogService.cs"),
                     FindSourceFile("DeezSpoTag.Web", "Services", "ArtistArtworkCatalogService.Matching.cs"),
                     FindSourceFile("DeezSpoTag.Web", "Services", "ArtistArtworkCatalogService.Matching.Overlap.cs"),
                     FindSourceFile("DeezSpoTag.Web", "Services", "Audiomack", "AudiomackArtistLocationService.cs")
                 })
        {
            Assert.DoesNotContain("ArtistAliasService", File.ReadAllText(file), StringComparison.Ordinal);
        }

        // The server-push updater gains no provider HTTP and no alias discovery.
        var updater = File.ReadAllText(FindSourceFile("DeezSpoTag.Web", "Services", "ArtistMetadataUpdaterService.cs"));
        Assert.DoesNotContain("ArtistAliasService", updater, StringComparison.Ordinal);
        Assert.DoesNotContain("LastFmArtistImageService", updater, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureAliasSpotifyIdentitiesAsync", updater, StringComparison.Ordinal);
    }

    // ---- helpers ------------------------------------------------------------------

    private static async Task<bool> InvokeBoolAsync(object target, string name, string left, string right)
    {
        var task = (Task<bool>)Invoke(target, name, left, right, CancellationToken.None);
        return await task;
    }

    private static void TryDelete(string dbPath)
    {
        try
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
        catch (IOException)
        {
        }
    }

    private static string LastFmJson(string returnedArtistName, string biography)
    {
        var escapedName = JsonSerializer.Serialize(returnedArtistName);
        var escapedBio = JsonSerializer.Serialize(biography);
        return "{\"artist\":{\"name\":" + escapedName
            + ",\"bio\":{\"summary\":" + escapedBio + ",\"content\":" + escapedBio + "}"
            + ",\"image\":[{\"#text\":\"https://lastfm.freetls.fastly.net/i/u/300x300/artist.png\",\"size\":\"large\"}]}}";
    }

    private static LastFmArtistImageService CreateLastFmService(LastFmFactory factory)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Lastfm:ApiKey"] = "test-key"
            })
            .Build();
        var service = (LastFmArtistImageService)RuntimeHelpers.GetUninitializedObject(typeof(LastFmArtistImageService));
        SetField(service, "_httpClientFactory", factory);
        SetField(service, "_configuration", configuration);
        SetField(service, "_logger", NullLogger<LastFmArtistImageService>.Instance);
        return service;
    }

    private static AudiomackArtistLocationService CreateAudiomackService(IConfiguration configuration, AudiomackFactory factory)
        => new(
            factory,
            new ArtistPageCacheRepository(configuration, NullLogger<ArtistPageCacheRepository>.Instance),
            new AudiomackApiClient(
                factory,
                new AudiomackWebCredentialsProvider(factory, NullLogger<AudiomackWebCredentialsProvider>.Instance),
                NullLogger<AudiomackApiClient>.Instance),
            NullLogger<AudiomackArtistLocationService>.Instance,
            new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance));

    private static SpotifyArtistService CreateSpotifyService(
        LibraryRepository? repository,
        ArtistPageCacheRepository? cache,
        ArtistAliasService? aliasService = null)
    {
        var service = (SpotifyArtistService)RuntimeHelpers.GetUninitializedObject(typeof(SpotifyArtistService));
        // An uninitialized instance skips field initializers, so the JSON options the cache
        // envelope is written and read with have to be supplied explicitly.
        SetField(service, "_jsonOptions", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        SetField(service, "_libraryRepository", repository);
        SetField(service, "_cacheRepository", cache);
        SetField(service, "_aliasService", aliasService);
        SetField(service, "_logger", NullLogger<SpotifyArtistService>.Instance);
        return service;
    }

    private static SpotifyArtistPageResult BuildSpotifyPage(
        string id,
        string name,
        string imageUrl,
        string biography = "Primary biography.",
        string? headerUrl = null,
        string? galleryUrl = null)
        => new(
            true,
            new SpotifyArtistProfile(
                Id: id,
                Name: name,
                Images: new List<SpotifyImage> { new(imageUrl, 640, 640) },
                Genres: new List<string>(),
                Followers: 0,
                Popularity: 0,
                SourceUrl: null,
                Biography: biography,
                Verified: null,
                MonthlyListeners: null,
                Rank: null,
                HeaderImageUrl: headerUrl ?? "https://img/primary-header.jpg",
                Gallery: galleryUrl is null ? new List<string>() : new List<string> { galleryUrl },
                DiscographyType: null,
                TotalAlbums: 0),
            new List<SpotifyAlbum>(),
            new List<SpotifyAlbum>(),
            new List<SpotifyTrack>(),
            new List<SpotifyRelatedArtist>());

    private static async Task SeedCachedSpotifyPageAsync(
        ArtistPageCacheRepository cache,
        string spotifyId,
        string name,
        string biography,
        string? imageUrl = null,
        string? galleryUrl = null,
        string? headerUrl = null)
    {
        var page = BuildSpotifyPage(
            spotifyId,
            name,
            imageUrl ?? "https://img/sibling.jpg",
            biography,
            headerUrl ?? "https://img/sibling-header.jpg",
            galleryUrl);
        var json = JsonSerializer.Serialize(
            new SpotifyArtistCacheEnvelope(10, page),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await cache.UpsertAsync("spotify", spotifyId, json, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    /// <summary>
    /// Seeds an enabled library folder with one real album and one local audio file, so
    /// <c>GetArtistAlbumsAsync</c> (which joins through track_local, audio_file and an enabled
    /// folder) actually reports the album. Audiomack's album-overlap confirmation depends on
    /// that query returning the held titles.
    /// </summary>
    private static async Task SeedAlbumAsync(string dbPath, string title)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO folder (id, root_path, display_name, enabled) VALUES (1, '/tmp/alias-metadata-music', 'Alias Metadata Music', 1);
INSERT INTO album (id, artist_id, title) VALUES (1, $artistId, $title);
INSERT INTO track (id, album_id, title) VALUES (1, 1, $trackTitle);
INSERT INTO audio_file (id, path, folder_id) VALUES (1, '/tmp/alias-metadata-music/track.flac', 1);
INSERT INTO track_local (track_id, audio_file_id) VALUES (1, 1);";
        command.Parameters.AddWithValue("$artistId", ArtistId);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$trackTitle", "Baddo");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedArtistSourceAsync(string dbPath, string source, string sourceId, bool isPrimary)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO artist_source (artist_id, source, source_id, is_primary) VALUES ($id, $source, $sourceId, $isPrimary);";
        command.Parameters.AddWithValue("$id", ArtistId);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$sourceId", sourceId);
        command.Parameters.AddWithValue("$isPrimary", isPrimary ? 1 : 0);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountArtistsAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM artist;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> CountRowsAsync(string dbPath, string table, string where, params (string, object)[] parameters)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {where};";
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<List<string>> ReadSourceIdsAsync(string dbPath, string source)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_id FROM artist_source WHERE source = $source ORDER BY source_id;";
        command.Parameters.AddWithValue("$source", source);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<Dictionary<string, bool>> ReadPrimaryFlagsAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_id, is_primary FROM artist_source ORDER BY source_id;";
        var flags = new Dictionary<string, bool>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            flags[reader.GetString(0)] = reader.GetInt64(1) == 1;
        }

        return flags;
    }

    private static string ReadQueryParameter(Uri? uri, string name)
    {
        var query = uri?.Query ?? string.Empty;
        if (query.Length == 0)
        {
            return string.Empty;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separator]);
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
        }

        return string.Empty;
    }

    private static string FindSourceFile(params string[] segments)
    {
        var directory = Directory.GetCurrentDirectory();
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var candidate = Path.Combine(new[] { directory }.Concat(segments).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(segments)}.");
    }

    private static string ExtractMethodBody(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"Marker '{marker}' was not found.");
        }

        var depth = 0;
        var seenBrace = false;
        for (var index = start; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
                seenBrace = true;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (seenBrace && depth == 0)
                {
                    return source[start..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Method body for '{marker}' was not terminated.");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>Answers Last.fm <c>artist.getinfo</c> from the queried artist name.</summary>
    private sealed class LastFmFactory : IHttpClientFactory
    {
        private readonly Func<string, string> _respond;

        public LastFmFactory(Func<string, string> respond) => _respond = respond;

        public List<string> Queries { get; } = new();

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(LastFmFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var artist = ReadQueryParameter(request.RequestUri, "artist");
                lock (owner.Queries)
                {
                    owner.Queries.Add(artist);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(owner._respond(artist), Encoding.UTF8, "application/json")
                });
            }
        }
    }

    /// <summary>Routes Audiomack artist/song searches by query and artist pages by slug.</summary>
    private sealed class AudiomackFactory : IHttpClientFactory
    {
        private readonly object _gate = new();

        public Func<string, string> ArtistSearchJson { get; set; } = _ => "{}";
        public Func<string, string> SongSearchJson { get; set; } = _ => """{"results":[]}""";
        public Func<string, string> PageHtml { get; set; } = _ => "<html></html>";

        public List<string> Requests { get; } = new();
        public List<string> ArtistSearchQueries { get; } = new();

        public static AudiomackFactory ForReferenceCase() => new()
        {
            ArtistSearchJson = query => query.Equals("Abass Doobeez", StringComparison.OrdinalIgnoreCase)
                ? """{"verified_artist":{"id":44556677,"name":"Abass Doobeez","url_slug":"abass-doobeez"}}"""
                : """{"results":[{"id":1,"title":"Other","artist":"Someone Else","album":"X","artist_slug":"someone","uploader":{"id":"1","name":"Someone Else","url_slug":"someone"}}]}""",
            SongSearchJson = _ => """{"results":[{"id":1,"title":"Baddo","artist":"Abass Doobeez","album":"Only One","artist_slug":"abass-doobeez","uploader":{"id":"44556677","name":"Abass Doobeez","url_slug":"abass-doobeez"}}]}""",
            PageHtml = slug => slug.Equals("abass-doobeez", StringComparison.OrdinalIgnoreCase)
                ? ReadFixture("artist-page-flight-abass-doobeez.txt")
                : ReadFixture("artist-page-flight-abbas-kubaff.txt")
        };

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(AudiomackFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                var artist = ReadQueryParameter(request.RequestUri, "q");
                lock (owner._gate)
                {
                    owner.Requests.Add(url);
                }

                if (url.Contains("api.audiomack.com", StringComparison.OrdinalIgnoreCase))
                {
                    var isSongs = url.Contains("type=songs", StringComparison.OrdinalIgnoreCase);
                    if (!isSongs)
                    {
                        lock (owner._gate)
                        {
                            owner.ArtistSearchQueries.Add(artist);
                        }
                    }

                    var body = isSongs ? owner.SongSearchJson(artist) : owner.ArtistSearchJson(artist);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json")
                    });
                }

                var slug = url.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(owner.PageHtml(slug), Encoding.UTF8, "text/html")
                });
            }
        }
    }
}