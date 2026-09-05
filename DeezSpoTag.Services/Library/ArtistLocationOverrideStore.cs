using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Utils;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Library;

/// <summary>A manually set artist location that overrides the resolved (Audiomack) value.</summary>
public sealed record ArtistLocationOverride(string? City, string? Country, string? CountryCode, DateTimeOffset UpdatedAt);

/// <summary>
/// Persists per-artist manual location overrides ("Edit Location" on the
/// library artist page) in the shared library SQLite DB
/// (table artist_location_override), so any component of the app — web,
/// workers, tagging — can read them through this repository or plain SQL.
/// Manual values take precedence over Audiomack-resolved locations; clearing
/// an override falls back to source data. Migrates the legacy JSON file store
/// once when found.
/// </summary>
public sealed class ArtistLocationOverrideStore
{
    private const string LegacyFileName = "library-artist-location-overrides.json";
    private static readonly TimeSpan LegacyImportLookback = TimeSpan.FromDays(3650);

    private readonly IConfiguration _configuration;
    private readonly ILogger<ArtistLocationOverrideStore> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _tableEnsured;
    private bool _legacyImportAttempted;

    public ArtistLocationOverrideStore(IConfiguration configuration, ILogger<ArtistLocationOverrideStore> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ArtistLocationOverride?> GetAsync(long artistId, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        const string sql = @"
SELECT city, country, country_code, updated_at
FROM artist_location_override
WHERE artist_id = $artist_id;";

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$artist_id", artistId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return new ArtistLocationOverride(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                ParseTimestamp(reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Artist location override lookup failed for artist {ArtistId}", artistId);
            return null;
        }
    }

    /// <summary>Stores an override, or removes it when city and country are both empty.</summary>
    public async Task SetAsync(long artistId, string? city, string? country, string? countryCode, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var hasValue = !string.IsNullOrWhiteSpace(city) || !string.IsNullOrWhiteSpace(country);
        var sql = hasValue
            ? @"
INSERT INTO artist_location_override (artist_id, city, country, country_code, updated_at)
VALUES ($artist_id, $city, $country, $country_code, $updated_at)
ON CONFLICT(artist_id) DO UPDATE SET
    city = excluded.city,
    country = excluded.country,
    country_code = excluded.country_code,
    updated_at = excluded.updated_at;"
            : "DELETE FROM artist_location_override WHERE artist_id = $artist_id;";

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$artist_id", artistId);
            if (hasValue)
            {
                command.Parameters.AddWithValue("$city", string.IsNullOrWhiteSpace(city) ? null : city.Trim());
                command.Parameters.AddWithValue("$country", string.IsNullOrWhiteSpace(country) ? null : country.Trim());
                command.Parameters.AddWithValue("$country_code", string.IsNullOrWhiteSpace(countryCode) ? null : countryCode.Trim());
                command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Artist location override persist failed for artist {ArtistId}", artistId);
        }
    }

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_tableEnsured && _legacyImportAttempted)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tableEnsured && _legacyImportAttempted)
            {
                return;
            }

            await EnsureTableAsync(cancellationToken).ConfigureAwait(false);
            _tableEnsured = true;
            await ImportLegacyJsonAsync(cancellationToken).ConfigureAwait(false);
            _legacyImportAttempted = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task EnsureTableAsync(CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE IF NOT EXISTS artist_location_override (
    artist_id BIGINT NOT NULL,
    city TEXT,
    country TEXT,
    country_code TEXT,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (artist_id)
);";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Artist location override table ensure failed");
        }
    }

    /// <summary>
    /// Imports the legacy JSON file store once, then renames it so the import
    /// never re-runs. The legacy file is only considered when it sits in the
    /// data root that OWNS this store's database file — derived from the
    /// resolved DB path — so stray processes (tests, tooling) pointing at a
    /// temporary database never import or rename the production file.
    /// </summary>
    private async Task ImportLegacyJsonAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connectionString = GetConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return;
            }

            var builder = new SqliteConnectionStringBuilder(connectionString);
            var dbPath = builder.DataSource?.Trim();
            if (string.IsNullOrWhiteSpace(dbPath) || !Path.IsPathRooted(dbPath) || IsSpecialDataSource(dbPath))
            {
                return;
            }

            // <dataRoot>/db/library/<file> → <dataRoot>
            var dbDirectory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
            if (string.IsNullOrWhiteSpace(dbDirectory))
            {
                return;
            }

            var dataRoot = Path.GetFullPath(Path.Join(dbDirectory, "..", ".."));
            var legacyPath = Path.Join(dataRoot, LegacyFileName);
            if (!File.Exists(legacyPath))
            {
                return;
            }

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Never import into a table that already has data.
            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM artist_location_override;";
            var existingRows = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (existingRows > 0)
            {
                return;
            }

            var json = await File.ReadAllTextAsync(legacyPath, cancellationToken).ConfigureAwait(false);
            var legacy = JsonSerializer.Deserialize<Dictionary<string, LegacyOverrideEntry>>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            if (legacy == null || legacy.Count == 0)
            {
                return;
            }

            var imported = 0;
            foreach (var pair in legacy)
            {
                if (!long.TryParse(pair.Key, out var artistId) || pair.Value == null)
                {
                    continue;
                }

                await using var insert = connection.CreateCommand();
                insert.CommandText = @"
INSERT INTO artist_location_override (artist_id, city, country, country_code, updated_at)
VALUES ($id, $city, $country, $code, $updated_at);";
                insert.Parameters.AddWithValue("$id", artistId);
                insert.Parameters.AddWithValue("$city", (object?)pair.Value.City ?? DBNull.Value);
                insert.Parameters.AddWithValue("$country", (object?)pair.Value.Country ?? DBNull.Value);
                insert.Parameters.AddWithValue("$code", (object?)pair.Value.CountryCode ?? DBNull.Value);
                insert.Parameters.AddWithValue("$updated_at", (pair.Value.UpdatedAt ?? DateTimeOffset.UtcNow - LegacyImportLookback).ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                imported++;
            }

            if (imported > 0)
            {
                File.Move(legacyPath, legacyPath + ".imported", overwrite: true);
                _logger.LogInformation("Migrated {Count} artist location override(s) from the legacy JSON store", imported);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Legacy artist location override migration failed; JSON left in place");
        }
    }

    private static bool IsSpecialDataSource(string dataSource) =>
        dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
        || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    private string? GetConnectionString()
    {
        var rawConnection = Environment.GetEnvironmentVariable("LIBRARY_DB")
            ?? _configuration.GetConnectionString("Library");
        return SqliteConnectionStringResolver.Resolve(rawConnection, "deezspotag.db");
    }

    private static DateTimeOffset ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.UtcNow - LegacyImportLookback;

    private sealed class LegacyOverrideEntry
    {
        public string? City { get; set; }
        public string? Country { get; set; }
        public string? CountryCode { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
    }
}
