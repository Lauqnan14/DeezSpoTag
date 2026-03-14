using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DeezSpoTag.Services.Utils;
using System.Globalization;

namespace DeezSpoTag.Services.Library;

public sealed class ArtistPageCacheRepository
{
    private const string SourceParameterName = "$source";
    private const string SourceIdParameterName = "$source_id";
    private readonly IConfiguration _configuration;
    private readonly ILogger<ArtistPageCacheRepository> _logger;
    private readonly TimeSpan _ttl = TimeSpan.FromDays(7);
    private readonly TimeSpan _maxStale = TimeSpan.FromDays(30);

    public ArtistPageCacheRepository(IConfiguration configuration, ILogger<ArtistPageCacheRepository> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsFresh(DateTimeOffset fetchedUtc) => DateTimeOffset.UtcNow - fetchedUtc <= _ttl;

    public bool IsUsable(DateTimeOffset fetchedUtc) => DateTimeOffset.UtcNow - fetchedUtc <= _maxStale;

    public async Task<ArtistCacheEntry?> TryGetAsync(string source, string sourceId, CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        const string sql = @"
SELECT payload_json, fetched_utc
FROM artist_page_cache
WHERE source = $source AND source_id = $source_id
LIMIT 1;";

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue(SourceParameterName, source);
            command.Parameters.AddWithValue(SourceIdParameterName, sourceId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var payload = reader.GetString(0);
            var fetchedText = reader.GetString(1);
            if (!DateTimeOffset.TryParse(
                    fetchedText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var fetchedUtc))
            {
                fetchedUtc = DateTimeOffset.MinValue;
            }

            return new ArtistCacheEntry(payload, fetchedUtc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Artist cache lookup failed (source={Source}, id={Id})", source, sourceId);
            return null;
        }
    }

    public async Task UpsertAsync(string source, string sourceId, string payloadJson, DateTimeOffset fetchedUtc, CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        const string sql = @"
INSERT INTO artist_page_cache (source, source_id, payload_json, fetched_utc, created_at, updated_at)
VALUES ($source, $source_id, $payload_json, $fetched_utc, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
ON CONFLICT(source, source_id) DO UPDATE SET
    payload_json = excluded.payload_json,
    fetched_utc = excluded.fetched_utc,
    updated_at = CURRENT_TIMESTAMP;";

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue(SourceParameterName, source);
            command.Parameters.AddWithValue(SourceIdParameterName, sourceId);
            command.Parameters.AddWithValue("$payload_json", payloadJson);
            command.Parameters.AddWithValue("$fetched_utc", fetchedUtc.ToUniversalTime().ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Artist cache upsert failed (source={Source}, id={Id})", source, sourceId);
        }
    }

    public async Task ClearAsync(string? source, CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var sql = string.IsNullOrWhiteSpace(source)
            ? "DELETE FROM artist_page_cache;"
            : "DELETE FROM artist_page_cache WHERE source = $source;";

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqliteCommand(sql, connection);
            if (!string.IsNullOrWhiteSpace(source))
            {
                command.Parameters.AddWithValue(SourceParameterName, source);
            }
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Artist cache clear failed (source={Source})", source);
        }
    }

    public async Task ClearEntryAsync(string source, string sourceId, CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        const string deleteCacheSql = "DELETE FROM artist_page_cache WHERE source = $source AND source_id = $source_id;";
        const string deleteGenresSql = "DELETE FROM artist_page_genre WHERE source = $source AND source_id = $source_id;";

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using (var deleteCommand = new SqliteCommand(deleteCacheSql, connection, (SqliteTransaction)transaction))
            {
                deleteCommand.Parameters.AddWithValue(SourceParameterName, source);
                deleteCommand.Parameters.AddWithValue(SourceIdParameterName, sourceId);
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteCommand = new SqliteCommand(deleteGenresSql, connection, (SqliteTransaction)transaction))
            {
                deleteCommand.Parameters.AddWithValue(SourceParameterName, source);
                deleteCommand.Parameters.AddWithValue(SourceIdParameterName, sourceId);
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Artist cache clear failed (source={Source}, id={Id})", source, sourceId);
        }
    }

    public async Task<ArtistCacheStats?> TryGetStatsAsync(CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        const string sql = @"
SELECT source, COUNT(*)
FROM artist_page_cache
GROUP BY source;";

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqliteCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var bySource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var total = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                var source = reader.GetString(0);
                var count = reader.GetInt32(1);
                bySource[source] = count;
                total += count;
            }

            return new ArtistCacheStats(total, bySource);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Artist cache stats failed");
            return null;
        }
    }

    public async Task UpsertGenresAsync(string source, string sourceId, IEnumerable<string> genres, CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var genreList = genres?.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct().ToList()
            ?? new List<string>();
        if (genreList.Count == 0)
        {
            return;
        }

        const string deleteSql = "DELETE FROM artist_page_genre WHERE source = $source AND source_id = $source_id;";
        const string insertSql = "INSERT OR IGNORE INTO artist_page_genre (source, source_id, genre) VALUES ($source, $source_id, $genre);";

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using (var deleteCommand = new SqliteCommand(deleteSql, connection, (SqliteTransaction)transaction))
            {
                deleteCommand.Parameters.AddWithValue(SourceParameterName, source);
                deleteCommand.Parameters.AddWithValue(SourceIdParameterName, sourceId);
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var genre in genreList)
            {
                await using var insertCommand = new SqliteCommand(insertSql, connection, (SqliteTransaction)transaction);
                insertCommand.Parameters.AddWithValue(SourceParameterName, source);
                insertCommand.Parameters.AddWithValue(SourceIdParameterName, sourceId);
                insertCommand.Parameters.AddWithValue("$genre", genre);
                await insertCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Artist genre upsert failed (source={Source}, id={Id})", source, sourceId);
        }
    }

    public async Task<List<string>> GetLocalGenresByArtistNameAsync(string artistName, CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(artistName))
        {
            return new List<string>();
        }

        const string sql = @"
SELECT genre
FROM artist_page_genre
WHERE source = 'local'
  AND LOWER(source_id) = LOWER($name)
ORDER BY genre;";

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("$name", artistName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var genres = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                genres.Add(reader.GetString(0));
            }
            return genres;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read local genres for artist {ArtistName}", artistName);
            return new List<string>();
        }
    }

    public async Task<List<string>> GetGenresAsync(string source, string sourceId, CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString)
            || string.IsNullOrWhiteSpace(source)
            || string.IsNullOrWhiteSpace(sourceId))
        {
            return new List<string>();
        }

        const string sql = @"
SELECT genre
FROM artist_page_genre
WHERE source = $source
  AND source_id = $source_id
ORDER BY genre;";

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue(SourceParameterName, source);
            command.Parameters.AddWithValue(SourceIdParameterName, sourceId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var genres = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                genres.Add(reader.GetString(0));
            }

            return genres;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read artist genres (source={Source}, id={Id})", source, sourceId);
            return new List<string>();
        }
    }

    private string? GetConnectionString()
    {
        var rawConnection = Environment.GetEnvironmentVariable("LIBRARY_DB")
            ?? _configuration.GetConnectionString("Library");
        return SqliteConnectionStringResolver.Resolve(rawConnection, "deezspotag.db");
    }
}

public sealed record ArtistCacheEntry(string PayloadJson, DateTimeOffset FetchedUtc);

public sealed record ArtistCacheStats(int Total, Dictionary<string, int> BySource);
