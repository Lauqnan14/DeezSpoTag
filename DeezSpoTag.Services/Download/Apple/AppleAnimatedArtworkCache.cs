using DeezSpoTag.Services.Utils;
using Microsoft.Data.Sqlite;

namespace DeezSpoTag.Services.Download.Apple;

internal static class AppleAnimatedArtworkCache
{
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);
    private static readonly HashSet<string> SchemaReadyConnections = new(StringComparer.Ordinal);

    public static async Task<(string? SquareUrl, string? TallUrl, string Status)?> TryGetAsync(
        string cacheKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        const string sql = """
SELECT square_url, tall_url, status
FROM apple_animated_artwork_cache
WHERE cache_key = @cacheKey
  AND status IN ('resolved', 'no_animated_artwork', 'conversion_failure')
  AND (expires_utc IS NULL OR expires_utc > @now);
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("cacheKey", cacheKey);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (
            await reader.IsDBNullAsync(0, cancellationToken) ? null : reader.GetString(0),
            await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
            reader.GetString(2));
    }

    public static async Task SaveNoArtworkAsync(
        string cacheKey,
        string resourceType,
        string resourceId,
        string storefront,
        int maxResolution,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        const string sql = """
INSERT INTO apple_animated_artwork_cache
    (cache_key, resource_type, resource_id, storefront, max_resolution, status, checked_utc, expires_utc)
VALUES
    (@cacheKey, @resourceType, @resourceId, @storefront, @maxResolution, 'no_animated_artwork', @checkedUtc, @expiresUtc)
ON CONFLICT(cache_key) DO UPDATE SET
    square_url = NULL,
    tall_url = NULL,
    status = excluded.status,
    checked_utc = excluded.checked_utc,
    expires_utc = excluded.expires_utc;
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("cacheKey", cacheKey);
        command.Parameters.AddWithValue("resourceType", resourceType);
        command.Parameters.AddWithValue("resourceId", resourceId);
        command.Parameters.AddWithValue("storefront", storefront);
        command.Parameters.AddWithValue("maxResolution", maxResolution);
        command.Parameters.AddWithValue("checkedUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("expiresUtc", DateTimeOffset.UtcNow.AddDays(30).ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task SaveArtifactsAsync(
        string cacheKey,
        IEnumerable<string> artifactPaths,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        foreach (var path in artifactPaths.Where(File.Exists))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var variant = stem.EndsWith("_tall", StringComparison.OrdinalIgnoreCase) ? "tall" : "square";
            const string sql = """
INSERT INTO apple_animated_artwork_artifact
    (cache_key, file_path, variant, format, verified_utc)
VALUES
    (@cacheKey, @filePath, @variant, @format, @verifiedUtc)
ON CONFLICT(file_path) DO UPDATE SET
    cache_key = excluded.cache_key,
    variant = excluded.variant,
    format = excluded.format,
    verified_utc = excluded.verified_utc;
""";
            await using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("cacheKey", cacheKey);
            command.Parameters.AddWithValue("filePath", Path.GetFullPath(path));
            command.Parameters.AddWithValue("variant", variant);
            command.Parameters.AddWithValue("format", Path.GetExtension(path).TrimStart('.').ToLowerInvariant());
            command.Parameters.AddWithValue("verifiedUtc", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public static async Task SaveStatusAsync(
        string cacheKey,
        string resourceType,
        string resourceId,
        string storefront,
        int maxResolution,
        string status,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        const string sql = """
INSERT INTO apple_animated_artwork_cache
    (cache_key, resource_type, resource_id, storefront, max_resolution, status, checked_utc, expires_utc)
VALUES
    (@cacheKey, @resourceType, @resourceId, @storefront, @maxResolution, @status, @checkedUtc, @expiresUtc)
ON CONFLICT(cache_key) DO UPDATE SET
    status = excluded.status,
    checked_utc = excluded.checked_utc,
    expires_utc = excluded.expires_utc;
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("cacheKey", cacheKey);
        command.Parameters.AddWithValue("resourceType", resourceType);
        command.Parameters.AddWithValue("resourceId", resourceId);
        command.Parameters.AddWithValue("storefront", storefront);
        command.Parameters.AddWithValue("maxResolution", maxResolution);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("checkedUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("expiresUtc", DateTimeOffset.UtcNow.Add(lifetime).ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task SaveStaticArtworkUrlAsync(
        string resourceType,
        string resourceId,
        string storefront,
        string staticArtworkUrl,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        const string sql = """
UPDATE apple_animated_artwork_cache
SET static_artwork_url = @staticArtworkUrl,
    checked_utc = @checkedUtc
WHERE resource_type = @resourceType
  AND resource_id = @resourceId
  AND storefront = @storefront;
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("staticArtworkUrl", staticArtworkUrl);
        command.Parameters.AddWithValue("checkedUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("resourceType", resourceType);
        command.Parameters.AddWithValue("resourceId", resourceId);
        command.Parameters.AddWithValue("storefront", storefront);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task SaveResolvedAsync(
        string cacheKey,
        string resourceType,
        string resourceId,
        string storefront,
        int maxResolution,
        string? squareUrl,
        string? tallUrl,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        const string sql = """
INSERT INTO apple_animated_artwork_cache
    (cache_key, resource_type, resource_id, storefront, max_resolution, square_url, tall_url, status, checked_utc, expires_utc)
VALUES
    (@cacheKey, @resourceType, @resourceId, @storefront, @maxResolution, @squareUrl, @tallUrl, 'resolved', @checkedUtc, @expiresUtc)
ON CONFLICT(cache_key) DO UPDATE SET
    resource_type = excluded.resource_type,
    resource_id = excluded.resource_id,
    storefront = excluded.storefront,
    max_resolution = excluded.max_resolution,
    square_url = excluded.square_url,
    tall_url = excluded.tall_url,
    status = excluded.status,
    checked_utc = excluded.checked_utc,
    expires_utc = excluded.expires_utc;
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("cacheKey", cacheKey);
        command.Parameters.AddWithValue("resourceType", resourceType);
        command.Parameters.AddWithValue("resourceId", resourceId);
        command.Parameters.AddWithValue("storefront", storefront);
        command.Parameters.AddWithValue("maxResolution", maxResolution);
        command.Parameters.AddWithValue("squareUrl", (object?)squareUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("tallUrl", (object?)tallUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("checkedUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("expiresUtc", DateTimeOffset.UtcNow.AddDays(7).ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var rawConnection = Environment.GetEnvironmentVariable("LIBRARY_DB");
        var connectionString = SqliteConnectionStringResolver.Resolve(rawConnection, "deezspotag.db")
            ?? throw new InvalidOperationException("The library database path could not be resolved.");
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var connectionKey = connection.ConnectionString;
        lock (SchemaReadyConnections)
        {
            if (SchemaReadyConnections.Contains(connectionKey))
            {
                return;
            }
        }

        await SchemaGate.WaitAsync(cancellationToken);
        try
        {
            lock (SchemaReadyConnections)
            {
                if (SchemaReadyConnections.Contains(connectionKey))
                {
                    return;
                }
            }

            const string sql = """
CREATE TABLE IF NOT EXISTS apple_animated_artwork_cache (
    cache_key TEXT PRIMARY KEY,
    resource_type TEXT NOT NULL,
    resource_id TEXT NOT NULL,
    storefront TEXT NOT NULL,
    max_resolution INTEGER NOT NULL,
    static_artwork_url TEXT,
    square_url TEXT,
    tall_url TEXT,
    status TEXT NOT NULL,
    checked_utc TEXT NOT NULL,
    expires_utc TEXT
);
CREATE INDEX IF NOT EXISTS ix_apple_animated_artwork_resource
    ON apple_animated_artwork_cache(resource_type, resource_id, storefront);
CREATE TABLE IF NOT EXISTS apple_animated_artwork_artifact (
    file_path TEXT PRIMARY KEY,
    cache_key TEXT NOT NULL,
    variant TEXT NOT NULL,
    format TEXT NOT NULL,
    verified_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_apple_animated_artwork_artifact_cache
    ON apple_animated_artwork_artifact(cache_key);
""";
            await using var command = new SqliteCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsureColumnAsync(
                connection,
                "apple_animated_artwork_cache",
                "static_artwork_url",
                "TEXT",
                cancellationToken);
            lock (SchemaReadyConnections)
            {
                SchemaReadyConnections.Add(connectionKey);
            }
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        await using var pragma = new SqliteCommand($"PRAGMA table_info({table});", connection);
        await using var reader = await pragma.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await reader.DisposeAsync();
        await using var alter = new SqliteCommand(
            $"ALTER TABLE {table} ADD COLUMN {column} {definition};",
            connection);
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
