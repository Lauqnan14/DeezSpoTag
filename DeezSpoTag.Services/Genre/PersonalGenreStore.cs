using System.Globalization;
using System.Text.Json;
using DeezSpoTag.Services.Utils;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace DeezSpoTag.Services.Genre;

public sealed class PersonalGenreStore
{
    private readonly string? _connectionString;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    public PersonalGenreStore(IConfiguration configuration)
    {
        var rawConnection = Environment.GetEnvironmentVariable("LIBRARY_DB")
            ?? configuration.GetConnectionString("Library");
        _connectionString = SqliteConnectionStringResolver.Resolve(rawConnection, "deezspotag.db");
    }

    public async Task<PersonalGenreSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
SELECT enabled, max_genres, preserve_provider_fallback, include_parent_genres
FROM personal_genre_settings
WHERE id = 1;
""";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new PersonalGenreSettings();
        }

        return new PersonalGenreSettings(
            reader.GetInt32(0) != 0,
            Math.Clamp(reader.GetInt32(1), 1, 10),
            reader.GetInt32(2) != 0,
            reader.GetInt32(3) != 0);
    }

    public async Task<PersonalGenreSettings> SaveSettingsAsync(
        PersonalGenreSettings settings,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var normalized = settings with { MaxGenres = Math.Clamp(settings.MaxGenres, 1, 10) };
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
INSERT INTO personal_genre_settings
    (id, enabled, max_genres, preserve_provider_fallback, include_parent_genres, updated_at_utc)
VALUES
    (1, @enabled, @maxGenres, @preserveProviderFallback, @includeParentGenres, @updatedAtUtc)
ON CONFLICT(id) DO UPDATE SET
    enabled = excluded.enabled,
    max_genres = excluded.max_genres,
    preserve_provider_fallback = excluded.preserve_provider_fallback,
    include_parent_genres = excluded.include_parent_genres,
    updated_at_utc = excluded.updated_at_utc;
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("enabled", normalized.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("maxGenres", normalized.MaxGenres);
        command.Parameters.AddWithValue("preserveProviderFallback", normalized.PreserveProviderFallback ? 1 : 0);
        command.Parameters.AddWithValue("includeParentGenres", normalized.IncludeParentGenres ? 1 : 0);
        command.Parameters.AddWithValue("updatedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return normalized;
    }

    public async Task<IReadOnlyList<PersonalGenreMapping>> GetMappingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
SELECT id, match_value, target_taxon_id, source, priority, enabled
FROM personal_genre_mapping
ORDER BY priority DESC, id;
""";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var output = new List<PersonalGenreMapping>();
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new PersonalGenreMapping(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5) != 0));
        }

        return output;
    }

    public async Task<PersonalGenreMapping> UpsertMappingAsync(
        PersonalGenreMapping mapping,
        CancellationToken cancellationToken = default)
    {
        ValidateTaxon(mapping.TargetTaxonId);
        var matchValue = RequireValue(mapping.MatchValue, nameof(mapping.MatchValue));
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);

        if (mapping.Id > 0)
        {
            const string updateSql = """
UPDATE personal_genre_mapping
SET match_value = @matchValue,
    target_taxon_id = @targetTaxonId,
    source = @source,
    priority = @priority,
    enabled = @enabled,
    updated_at_utc = @updatedAtUtc
WHERE id = @id;
""";
            await using var update = new SqliteCommand(updateSql, connection);
            BindMapping(update, mapping with { MatchValue = matchValue });
            update.Parameters.AddWithValue("id", mapping.Id);
            update.Parameters.AddWithValue("updatedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            var rows = await update.ExecuteNonQueryAsync(cancellationToken);
            if (rows > 0)
            {
                return mapping with { MatchValue = matchValue };
            }
        }

        const string insertSql = """
INSERT INTO personal_genre_mapping
    (match_value, target_taxon_id, source, priority, enabled, created_at_utc, updated_at_utc)
VALUES
    (@matchValue, @targetTaxonId, @source, @priority, @enabled, @now, @now);
SELECT last_insert_rowid();
""";
        await using var insert = new SqliteCommand(insertSql, connection);
        BindMapping(insert, mapping with { MatchValue = matchValue });
        insert.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return mapping with { Id = id, MatchValue = matchValue };
    }

    public async Task<IReadOnlyList<PersonalGenreRule>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
SELECT id, match_value, target_taxon_id, source, priority, enabled
FROM personal_genre_rule
ORDER BY priority DESC, id;
""";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var output = new List<PersonalGenreRule>();
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new PersonalGenreRule(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5) != 0));
        }

        return output;
    }

    public async Task<PersonalGenreRule> UpsertRuleAsync(
        PersonalGenreRule rule,
        CancellationToken cancellationToken = default)
    {
        ValidateTaxon(rule.TargetTaxonId);
        var matchValue = RequireValue(rule.MatchValue, nameof(rule.MatchValue));
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);

        if (rule.Id > 0)
        {
            const string updateSql = """
UPDATE personal_genre_rule
SET match_value = @matchValue,
    target_taxon_id = @targetTaxonId,
    source = @source,
    priority = @priority,
    enabled = @enabled,
    updated_at_utc = @updatedAtUtc
WHERE id = @id;
""";
            await using var update = new SqliteCommand(updateSql, connection);
            BindRule(update, rule with { MatchValue = matchValue });
            update.Parameters.AddWithValue("id", rule.Id);
            update.Parameters.AddWithValue("updatedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            var rows = await update.ExecuteNonQueryAsync(cancellationToken);
            if (rows > 0)
            {
                return rule with { MatchValue = matchValue };
            }
        }

        const string insertSql = """
INSERT INTO personal_genre_rule
    (match_value, target_taxon_id, source, priority, enabled, created_at_utc, updated_at_utc)
VALUES
    (@matchValue, @targetTaxonId, @source, @priority, @enabled, @now, @now);
SELECT last_insert_rowid();
""";
        await using var insert = new SqliteCommand(insertSql, connection);
        BindRule(insert, rule with { MatchValue = matchValue });
        insert.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return rule with { Id = id, MatchValue = matchValue };
    }

    public async Task SaveTrackResultAsync(
        PersonalGenreTrackResult result,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
INSERT INTO personal_genre_track
    (track_id, primary_genre, genres_json, styles_json, substyles_json, contexts_json,
     applied_rule_ids_json, evidence_json, resolver_version, resolved_at_utc)
VALUES
    (@trackId, @primaryGenre, @genresJson, @stylesJson, @substylesJson, @contextsJson,
     @appliedRuleIdsJson, @evidenceJson, @resolverVersion, @resolvedAtUtc)
ON CONFLICT(track_id) DO UPDATE SET
    primary_genre = excluded.primary_genre,
    genres_json = excluded.genres_json,
    styles_json = excluded.styles_json,
    substyles_json = excluded.substyles_json,
    contexts_json = excluded.contexts_json,
    applied_rule_ids_json = excluded.applied_rule_ids_json,
    evidence_json = excluded.evidence_json,
    resolver_version = excluded.resolver_version,
    resolved_at_utc = excluded.resolved_at_utc;
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("trackId", result.TrackId);
        command.Parameters.AddWithValue("primaryGenre", (object?)result.Resolution.PrimaryGenre ?? DBNull.Value);
        command.Parameters.AddWithValue("genresJson", JsonSerializer.Serialize(result.Resolution.Genres));
        command.Parameters.AddWithValue("stylesJson", JsonSerializer.Serialize(result.Resolution.Styles));
        command.Parameters.AddWithValue("substylesJson", JsonSerializer.Serialize(result.Resolution.Substyles));
        command.Parameters.AddWithValue("contextsJson", JsonSerializer.Serialize(result.Resolution.Contexts));
        command.Parameters.AddWithValue("appliedRuleIdsJson", JsonSerializer.Serialize(result.Resolution.AppliedRuleIds));
        command.Parameters.AddWithValue("evidenceJson", JsonSerializer.Serialize(result.Resolution.Evidence));
        command.Parameters.AddWithValue("resolverVersion", result.Resolution.ResolverVersion);
        command.Parameters.AddWithValue("resolvedAtUtc", result.ResolvedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PersonalGenreTrackResult?> GetTrackResultAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
SELECT primary_genre, genres_json, styles_json, substyles_json, contexts_json,
       applied_rule_ids_json, evidence_json, resolver_version, resolved_at_utc
FROM personal_genre_track
WHERE track_id = @trackId;
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("trackId", trackId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var resolution = new PersonalGenreResolution(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            ParseStringList(reader.GetString(1)),
            ParseStringList(reader.GetString(2)),
            ParseStringList(reader.GetString(3)),
            ParseStringList(reader.GetString(4)),
            ParseStringList(reader.GetString(5)),
            ParseEvidence(reader.GetString(6)),
            reader.GetString(7));

        var resolvedAtUtc = DateTimeOffset.TryParse(
            reader.GetString(8),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

        return new PersonalGenreTrackResult(trackId, resolution, resolvedAtUtc);
    }

    public async Task DeleteMappingAsync(long id, CancellationToken cancellationToken = default)
        => await DeleteByIdAsync("personal_genre_mapping", id, cancellationToken);

    public async Task DeleteRuleAsync(long id, CancellationToken cancellationToken = default)
        => await DeleteByIdAsync("personal_genre_rule", id, cancellationToken);

    private async Task DeleteByIdAsync(string table, long id, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var sql = table switch
        {
            "personal_genre_mapping" => "DELETE FROM personal_genre_mapping WHERE id = @id;",
            "personal_genre_rule" => "DELETE FROM personal_genre_rule WHERE id = @id;",
            _ => throw new InvalidOperationException("Unsupported Personal Genre table.")
        };
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaReady)
        {
            return;
        }

        await _schemaGate.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady)
            {
                return;
            }

            await using var connection = await OpenAsync(cancellationToken);
            const string sql = """
CREATE TABLE IF NOT EXISTS personal_genre_settings (
    id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
    enabled INTEGER NOT NULL DEFAULT 1,
    max_genres INTEGER NOT NULL DEFAULT 3,
    preserve_provider_fallback INTEGER NOT NULL DEFAULT 1,
    include_parent_genres INTEGER NOT NULL DEFAULT 0,
    updated_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
INSERT OR IGNORE INTO personal_genre_settings (id) VALUES (1);

CREATE TABLE IF NOT EXISTS personal_genre_mapping (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    match_value TEXT NOT NULL,
    target_taxon_id TEXT NOT NULL,
    source TEXT,
    priority INTEGER NOT NULL DEFAULT 100,
    enabled INTEGER NOT NULL DEFAULT 1,
    created_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX IF NOT EXISTS idx_personal_genre_mapping_match
    ON personal_genre_mapping (match_value, source, enabled, priority DESC);

CREATE TABLE IF NOT EXISTS personal_genre_rule (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    match_value TEXT NOT NULL,
    target_taxon_id TEXT NOT NULL,
    source TEXT,
    priority INTEGER NOT NULL DEFAULT 1000,
    enabled INTEGER NOT NULL DEFAULT 1,
    created_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX IF NOT EXISTS idx_personal_genre_rule_match
    ON personal_genre_rule (match_value, source, enabled, priority DESC);

CREATE TABLE IF NOT EXISTS personal_genre_track (
    track_id BIGINT NOT NULL PRIMARY KEY REFERENCES track(id) ON DELETE CASCADE,
    primary_genre TEXT,
    genres_json TEXT NOT NULL DEFAULT '[]',
    styles_json TEXT NOT NULL DEFAULT '[]',
    substyles_json TEXT NOT NULL DEFAULT '[]',
    contexts_json TEXT NOT NULL DEFAULT '[]',
    applied_rule_ids_json TEXT NOT NULL DEFAULT '[]',
    evidence_json TEXT NOT NULL DEFAULT '[]',
    resolver_version TEXT NOT NULL,
    resolved_at_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_personal_genre_track_primary
    ON personal_genre_track (primary_genre);
""";
            await using var command = new SqliteCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("Library database connection is not configured.");
        }

        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var pragma = new SqliteCommand("PRAGMA foreign_keys = ON;", connection);
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static void ValidateTaxon(string taxonId)
    {
        if (!PersonalGenreTaxonomy.TryGetById(taxonId, out _))
        {
            throw new ArgumentException($"Unknown Personal Genre taxon '{taxonId}'.", nameof(taxonId));
        }
    }

    private static string RequireValue(string value, string parameterName)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Value cannot be empty.", parameterName);
        }

        return trimmed;
    }

    private static void BindMapping(SqliteCommand command, PersonalGenreMapping mapping)
    {
        command.Parameters.AddWithValue("matchValue", mapping.MatchValue);
        command.Parameters.AddWithValue("targetTaxonId", mapping.TargetTaxonId);
        command.Parameters.AddWithValue("source", (object?)NormalizeOptional(mapping.Source) ?? DBNull.Value);
        command.Parameters.AddWithValue("priority", mapping.Priority);
        command.Parameters.AddWithValue("enabled", mapping.Enabled ? 1 : 0);
    }

    private static void BindRule(SqliteCommand command, PersonalGenreRule rule)
    {
        command.Parameters.AddWithValue("matchValue", rule.MatchValue);
        command.Parameters.AddWithValue("targetTaxonId", rule.TargetTaxonId);
        command.Parameters.AddWithValue("source", (object?)NormalizeOptional(rule.Source) ?? DBNull.Value);
        command.Parameters.AddWithValue("priority", rule.Priority);
        command.Parameters.AddWithValue("enabled", rule.Enabled ? 1 : 0);
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static IReadOnlyList<string> ParseStringList(string json)
        => JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static IReadOnlyList<PersonalGenreEvidence> ParseEvidence(string json)
        => JsonSerializer.Deserialize<List<PersonalGenreEvidence>>(json) ?? [];
}
