using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Controllers.Api;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the artist identity lookups the Navidrome metadata-agent plugin relies on.
/// The plugin sends Navidrome's own artist id, which only resolves because
/// ArtistMetadataUpdaterService records it in artist_source on every push.
/// </summary>
public sealed class MetadataAgentArtistResolutionTests : IAsyncLifetime
{
    private const string NavidromeSource = "navidrome";

    private string _tempRoot = string.Empty;
    private IConfiguration _configuration = default!;
    private LibraryRepository _repository = default!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-metadata-agent-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);

        var dbPath = Path.Join(_tempRoot, "library.db");
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={dbPath}"
            })
            .Build();

        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        _repository = new LibraryRepository(_configuration, NullLogger<LibraryRepository>.Instance);

        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = new SqliteCommand(
            "INSERT INTO artist (id, name) VALUES (4101, 'Sauti Sol'), (4102, 'Nyashinski');",
            connection);
        await command.ExecuteNonQueryAsync();
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
    public async Task FindArtistIdBySourceIdResolvesMappedNavidromeArtist()
    {
        await _repository.UpsertArtistSourceIdAsync(4101, NavidromeSource, "nd-artist-abc");

        var resolved = await _repository.FindArtistIdBySourceIdAsync(NavidromeSource, "nd-artist-abc");

        Assert.Equal(4101, resolved);
    }

    [Fact]
    public async Task FindArtistIdBySourceIdIgnoresOtherSources()
    {
        await _repository.UpsertArtistSourceIdAsync(4101, "spotify", "shared-id");

        var resolved = await _repository.FindArtistIdBySourceIdAsync(NavidromeSource, "shared-id");

        Assert.Null(resolved);
    }

    [Fact]
    public async Task FindArtistIdBySourceIdReturnsNullForUnknownAndBlankInput()
    {
        Assert.Null(await _repository.FindArtistIdBySourceIdAsync(NavidromeSource, "nd-missing"));
        Assert.Null(await _repository.FindArtistIdBySourceIdAsync(NavidromeSource, "   "));
        Assert.Null(await _repository.FindArtistIdBySourceIdAsync("   ", "nd-artist-abc"));
    }

    [Fact]
    public async Task FindArtistIdByNameIsCaseInsensitiveAndTrimmed()
    {
        Assert.Equal(4102, await _repository.FindArtistIdByNameAsync("nyashinski"));
        Assert.Equal(4102, await _repository.FindArtistIdByNameAsync("  Nyashinski  "));
        Assert.Null(await _repository.FindArtistIdByNameAsync("Unknown Artist"));
    }

    /// <summary>
    /// The plugin's bootstrap path: an artist DeezSpoTag never pushed resolves by name once,
    /// and persisting the mapping means every later request resolves by id instead.
    /// </summary>
    [Fact]
    public async Task NameBootstrapMappingMakesSubsequentLookupsResolveById()
    {
        Assert.Null(await _repository.FindArtistIdBySourceIdAsync(NavidromeSource, "nd-artist-xyz"));

        var byName = await _repository.FindArtistIdByNameAsync("Sauti Sol");
        Assert.Equal(4101, byName);

        await _repository.UpsertArtistSourceIdAsync(byName!.Value, NavidromeSource, "nd-artist-xyz");

        Assert.Equal(4101, await _repository.FindArtistIdBySourceIdAsync(NavidromeSource, "nd-artist-xyz"));
    }

    [Fact]
    public async Task SelectedBiographyIsPreferredAndBlankEntriesAreSkipped()
    {
        await _repository.UpsertArtistBiographyCacheAsync(4101, "lastfm", "   ", selected: false);
        await _repository.UpsertArtistBiographyCacheAsync(4101, "spotify", "Kenyan afro-pop band.", selected: true);

        var any = await _repository.GetArtistBiographyCacheAsync(4101, null, allowFallback: true);
        Assert.NotNull(any);
        Assert.Equal("spotify", any!.Source);
        Assert.Equal("Kenyan afro-pop band.", any.Biography);

        // No biography at all must stay null so the controller can answer 204 and let
        // Navidrome fall through to the next agent rather than caching an empty result.
        Assert.Null(await _repository.GetArtistBiographyCacheAsync(4102, null, allowFallback: true));
    }

    /// <summary>
    /// Spotify and Qobuz biographies arrive as HTML with anchors pointing at
    /// spotify: URIs, which are dead links in any Subsonic client.
    /// </summary>
    [Fact]
    public void CleanBiographyUnwrapsAnchorsAndDecodesEntities()
    {
        const string raw =
            "MC 21 Savage hit with &quot;Picky.&quot; He joined "
            + "<a href=\"spotify:artist:0iEtIxbK0KxaSlF7G42ZOp\" data-name=\"Metro Boomin\">Metro Boomin</a>"
            + " for Savage Mode.";

        var cleaned = DeezSpoTag.Web.Controllers.Api.MetadataAgentApiController.CleanBiography(raw);

        Assert.Equal(
            "MC 21 Savage hit with \"Picky.\" He joined Metro Boomin for Savage Mode.",
            cleaned);
        Assert.DoesNotContain("spotify:artist:", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("<a", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanBiographyCollapsesWhitespaceAndHandlesEmptyInput()
    {
        Assert.Equal(string.Empty, MetadataAgentApiController.CleanBiography(null));
        Assert.Equal(string.Empty, MetadataAgentApiController.CleanBiography("   "));
        // Markup-only input must collapse to empty so the endpoint answers 204.
        Assert.Equal(string.Empty, MetadataAgentApiController.CleanBiography("<p></p>"));
        Assert.Equal("one two", MetadataAgentApiController.CleanBiography("one   <br/>  two"));
    }
}
