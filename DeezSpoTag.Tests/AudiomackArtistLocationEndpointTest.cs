using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services.Audiomack;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// The standalone location route: it must serve an Audiomack-resolved location for
/// an artist with no Spotify payload at all, and the manual override must still win.
/// The controller is created without running its constructor (repo convention) and
/// only the fields the route touches are populated.
/// </summary>
public sealed class AudiomackIndependentLocationEndpointTest
{
    [Fact]
    public async Task AudiomackOnlyArtist_LocationServedWithoutAnySpotifyPayload()
    {
        var (controller, _, artistId) = await CreateControllerAsync();

        var result = await controller.GetArtistLocation(artistId, CancellationToken.None);

        using var document = ReadOkJson(result);
        var root = document.RootElement;
        Assert.True(root.GetProperty("available").GetBoolean());
        var location = root.GetProperty("location");
        Assert.Equal("Dar es Salaam", location.GetProperty("city").GetString());
        Assert.Equal("Tanzania", location.GetProperty("country").GetString());
        Assert.Equal("TZ", location.GetProperty("country_code").GetString());
        Assert.Equal("audiomack", location.GetProperty("source").GetString());
    }

    [Fact]
    public async Task ManualOverride_StillWinsOnTheIndependentPath()
    {
        var (controller, overrides, artistId) = await CreateControllerAsync();
        await overrides.SetAsync(artistId, "Atlanta", "USA", "US");

        var result = await controller.GetArtistLocation(artistId, CancellationToken.None);

        using var document = ReadOkJson(result);
        var root = document.RootElement;
        Assert.True(root.GetProperty("available").GetBoolean());
        var location = root.GetProperty("location");
        Assert.Equal("Atlanta", location.GetProperty("city").GetString());
        Assert.Equal("US", location.GetProperty("country_code").GetString());
        Assert.Equal("manual", location.GetProperty("source").GetString());
    }

    [Fact]
    public async Task UnknownArtist_AnswersUnavailable()
    {
        var (controller, _, _) = await CreateControllerAsync();

        var result = await controller.GetArtistLocation(999999, CancellationToken.None);

        using var document = ReadOkJson(result);
        Assert.False(document.RootElement.GetProperty("available").GetBoolean());
    }

    private static JsonDocument ReadOkJson(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
    }

    private static async Task<(LibraryArtistSourceMetadataApiController Controller, ArtistLocationOverrideStore Overrides, long ArtistId)> CreateControllerAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"audiomack-standalone-location-{Guid.NewGuid():N}.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={dbPath}"
            })
            .Build();
        await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
        var artistId = await SeedLocalArtistAsync(dbPath, "Alikiba");

        var repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
        var overrides = new ArtistLocationOverrideStore(configuration, NullLogger<ArtistLocationOverrideStore>.Instance);
        var factory = new FixedPageHttpClientFactory(ReadFixture("artist-page-flight.txt"));
        var service = new AudiomackArtistLocationService(
            factory,
            new ArtistPageCacheRepository(configuration, NullLogger<ArtistPageCacheRepository>.Instance),
            new AudiomackApiClient(
                factory,
                new AudiomackWebCredentialsProvider(factory, NullLogger<AudiomackWebCredentialsProvider>.Instance),
                NullLogger<AudiomackApiClient>.Instance),
            NullLogger<AudiomackArtistLocationService>.Instance,
            repository);

        var controller = (LibraryArtistSourceMetadataApiController)RuntimeHelpers.GetUninitializedObject(
            typeof(LibraryArtistSourceMetadataApiController));
        SetField(controller, "_repository", repository);
        SetField(controller, "_locationOverrides", overrides);
        SetField(controller, "_audiomackArtistLocation", service);
        SetField(controller, "_logger", NullLogger<LibraryArtistSourceMetadataApiController>.Instance);
        return (controller, overrides, artistId);
    }

    /// <summary>
    /// Seeds a local artist with one track: GetArtistAsync only returns artists that
    /// have a local track, so the endpoint test needs the full folder→audio_file→
    /// track_local→track→album chain.
    /// </summary>
    private static async Task<long> SeedLocalArtistAsync(string dbPath, string artistName)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync();

        async Task<long> InsertAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var command = new Microsoft.Data.Sqlite.SqliteCommand(sql, connection, transaction);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        var artistId = await InsertAsync(
            "INSERT INTO artist (name) VALUES ($name) RETURNING id;",
            ("$name", artistName));
        var albumId = await InsertAsync(
            "INSERT INTO album (artist_id, title) VALUES ($artist, $title) RETURNING id;",
            ("$artist", artistId),
            ("$title", $"{artistName} Album"));
        var libraryId = await InsertAsync(
            "INSERT INTO library (name) VALUES ($name) RETURNING id;",
            ("$name", $"Library-{Guid.NewGuid():N}"));
        var folderId = await InsertAsync(
            "INSERT INTO folder (root_path, display_name, library_id, enabled) VALUES ($root, $display, $library, 1) RETURNING id;",
            ("$root", Path.Join(Path.GetTempPath(), $"music-{Guid.NewGuid():N}")),
            ("$display", "Music"),
            ("$library", libraryId));
        var audioFileId = await InsertAsync(
            "INSERT INTO audio_file (path, folder_id) VALUES ($path, $folder) RETURNING id;",
            ("$path", Path.Join(Path.GetTempPath(), $"track-{Guid.NewGuid():N}.flac")),
            ("$folder", folderId));
        var trackId = await InsertAsync(
            "INSERT INTO track (album_id, title) VALUES ($album, $title) RETURNING id;",
            ("$album", albumId),
            ("$title", "Track 01"));

        await using (var linkCommand = new Microsoft.Data.Sqlite.SqliteCommand(
            "INSERT INTO track_local (track_id, audio_file_id) VALUES ($track, $audio);",
            connection,
            transaction))
        {
            linkCommand.Parameters.AddWithValue("$track", trackId);
            linkCommand.Parameters.AddWithValue("$audio", audioFileId);
            await linkCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return artistId;
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

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

    private sealed class FixedPageHttpClientFactory : IHttpClientFactory
    {
        private readonly string _html;

        public FixedPageHttpClientFactory(string html)
        {
            _html = html;
        }

        public HttpClient CreateClient(string name) => new(new PageHandler(_html));

        private sealed class PageHandler : HttpMessageHandler
        {
            private readonly string _html;

            public PageHandler(string html)
            {
                _html = html;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_html, Encoding.UTF8, "text/html")
                });
        }
    }
}
