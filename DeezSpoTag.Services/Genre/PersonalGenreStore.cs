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

    public async Task<IReadOnlyList<PersonalGenreTaxon>> GetCustomTaxaAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
SELECT id, name, kind, parent_ids_json, context_only, aliases_json
FROM personal_genre_taxon
ORDER BY kind, name COLLATE NOCASE, id;
""";
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var output = new List<PersonalGenreTaxon>();
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new PersonalGenreTaxon(
                reader.GetString(0),
                reader.GetString(1),
                ParseTaxonKind(reader.GetString(2)),
                ParseStringList(reader.GetString(3)),
                reader.GetInt32(4) != 0,
                ParseStringList(reader.GetString(5))));
        }

        return output;
    }

    public async Task<PersonalGenreTaxon> UpsertCustomTaxonAsync(
        PersonalGenreTaxon taxon,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var id = NormalizeCustomTaxonId(taxon.Id);
        var name = RequireValue(taxon.Name, nameof(taxon.Name));
        if (PersonalGenreTaxonomy.TryGetById(id, out _))
        {
            throw new ArgumentException($"Built-in Personal Genre taxon '{id}' cannot be replaced.", nameof(taxon.Id));
        }

        var parentIds = (taxon.ParentIds ?? Array.Empty<string>())
            .Where(parentId => !string.IsNullOrWhiteSpace(parentId))
            .Select(parentId => parentId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (parentIds.Any(parentId => string.Equals(parentId, id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("A taxonomy term cannot be its own parent.", nameof(taxon.ParentIds));
        }

        foreach (var parentId in parentIds)
        {
            await ValidateTaxonAsync(parentId, cancellationToken);
        }

        var aliases = (taxon.Aliases ?? Array.Empty<string>())
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var normalizedKind = taxon.ContextOnly
            ? PersonalGenreTaxonKind.Context
            : taxon.Kind;
        var normalized = new PersonalGenreTaxon(
            id,
            name,
            normalizedKind,
            parentIds,
            normalizedKind == PersonalGenreTaxonKind.Context,
            aliases);

        await ValidateCustomTaxonLookupAsync(normalized, cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
INSERT INTO personal_genre_taxon
    (id, name, kind, parent_ids_json, context_only, aliases_json, created_at_utc, updated_at_utc)
VALUES
    (@id, @name, @kind, @parentIdsJson, @contextOnly, @aliasesJson, @now, @now)
ON CONFLICT(id) DO UPDATE SET
    name = excluded.name,
    kind = excluded.kind,
    parent_ids_json = excluded.parent_ids_json,
    context_only = excluded.context_only,
    aliases_json = excluded.aliases_json,
    updated_at_utc = excluded.updated_at_utc;
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("id", normalized.Id);
        command.Parameters.AddWithValue("name", normalized.Name);
        command.Parameters.AddWithValue("kind", normalized.Kind.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("parentIdsJson", JsonSerializer.Serialize(normalized.ParentIds ?? Array.Empty<string>()));
        command.Parameters.AddWithValue("contextOnly", normalized.ContextOnly ? 1 : 0);
        command.Parameters.AddWithValue("aliasesJson", JsonSerializer.Serialize(normalized.Aliases ?? Array.Empty<string>()));
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return normalized;
    }

    public async Task DeleteCustomTaxonAsync(
        string taxonId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var id = NormalizeCustomTaxonId(taxonId);
        if (PersonalGenreTaxonomy.TryGetById(id, out _))
        {
            throw new ArgumentException("Built-in Personal Genre taxonomy terms cannot be deleted.", nameof(taxonId));
        }

        await using var connection = await OpenAsync(cancellationToken);
        const string referenceSql = """
SELECT
    (SELECT COUNT(*) FROM personal_genre_mapping WHERE lower(target_taxon_id) = lower(@id)) +
    (SELECT COUNT(*) FROM personal_genre_rule WHERE lower(target_taxon_id) = lower(@id)) +
    (SELECT COUNT(*) FROM personal_genre_lock WHERE lower(taxon_id) = lower(@id)) +
    (SELECT COUNT(*)
       FROM personal_genre_taxon term, json_each(term.parent_ids_json) parent
      WHERE lower(CAST(parent.value AS TEXT)) = lower(@id));
""";
        await using (var referenceCommand = new SqliteCommand(referenceSql, connection))
        {
            referenceCommand.Parameters.AddWithValue("id", id);
            var references = Convert.ToInt32(await referenceCommand.ExecuteScalarAsync(cancellationToken) ?? 0, CultureInfo.InvariantCulture);
            if (references > 0)
            {
                throw new InvalidOperationException(
                    $"Custom taxon '{id}' is still referenced by a mapping, rule, lock, or child term.");
            }
        }

        const string deleteSql = "DELETE FROM personal_genre_taxon WHERE lower(id) = lower(@id);";
        await using var deleteCommand = new SqliteCommand(deleteSql, connection);
        deleteCommand.Parameters.AddWithValue("id", id);
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PersonalGenreMapping>> GetMappingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
SELECT id, match_value, target_taxon_id, source, priority, enabled, action
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
                reader.GetInt32(5) != 0,
                ParseMappingAction(reader.IsDBNull(6) ? "map" : reader.GetString(6))));
        }

        return output;
    }

    public async Task<PersonalGenreMapping> UpsertMappingAsync(
        PersonalGenreMapping mapping,
        CancellationToken cancellationToken = default)
    {
        if (mapping.Action is PersonalGenreMappingAction.Map or PersonalGenreMappingAction.ContextOnly)
        {
            await ValidateTaxonAsync(mapping.TargetTaxonId, cancellationToken);
        }
        var matchValue = RequireValue(mapping.MatchValue, nameof(mapping.MatchValue));
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
    action = @action,
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
    (match_value, target_taxon_id, source, priority, enabled, action, created_at_utc, updated_at_utc)
VALUES
    (@matchValue, @targetTaxonId, @source, @priority, @enabled, @action, @now, @now);
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
        await ValidateTaxonAsync(rule.TargetTaxonId, cancellationToken);
        var matchValue = RequireValue(rule.MatchValue, nameof(rule.MatchValue));
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
     classifications_json, decisions_json, applied_rule_ids_json, evidence_json, resolver_version, resolved_at_utc)
VALUES
    (@trackId, @primaryGenre, @genresJson, @stylesJson, @substylesJson, @contextsJson,
     @classificationsJson, @decisionsJson, @appliedRuleIdsJson, @evidenceJson, @resolverVersion, @resolvedAtUtc)
ON CONFLICT(track_id) DO UPDATE SET
    primary_genre = excluded.primary_genre,
    genres_json = excluded.genres_json,
    styles_json = excluded.styles_json,
    substyles_json = excluded.substyles_json,
    contexts_json = excluded.contexts_json,
    classifications_json = excluded.classifications_json,
    decisions_json = excluded.decisions_json,
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
        command.Parameters.AddWithValue("classificationsJson", JsonSerializer.Serialize(result.Resolution.Classifications));
        command.Parameters.AddWithValue("decisionsJson", JsonSerializer.Serialize(result.Resolution.Decisions));
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
       classifications_json, decisions_json, applied_rule_ids_json, evidence_json, resolver_version, resolved_at_utc
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
            ParseClassifications(reader.GetString(5)),
            ParseDecisions(reader.GetString(6)),
            ParseStringList(reader.GetString(7)),
            ParseEvidence(reader.GetString(8)),
            reader.GetString(9));

        var resolvedAtUtc = DateTimeOffset.TryParse(
            reader.GetString(10),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

        return new PersonalGenreTrackResult(trackId, resolution, resolvedAtUtc);
    }

    public async Task<IReadOnlyList<PersonalGenreLock>> GetLocksAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
SELECT track_id, taxon_id, enabled, updated_at_utc
FROM personal_genre_lock
WHERE track_id = @trackId
ORDER BY taxon_id;
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("trackId", trackId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var output = new List<PersonalGenreLock>();
        while (await reader.ReadAsync(cancellationToken))
        {
            DateTimeOffset? updatedAt = null;
            if (!reader.IsDBNull(3)
                && DateTimeOffset.TryParse(
                    reader.GetString(3),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed))
            {
                updatedAt = parsed;
            }

            output.Add(new PersonalGenreLock(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt32(2) != 0,
                updatedAt));
        }

        return output;
    }

    public async Task<PersonalGenreLock> SaveLockAsync(
        PersonalGenreLock item,
        CancellationToken cancellationToken = default)
    {
        await ValidateTaxonAsync(item.TaxonId, cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        const string sql = """
INSERT INTO personal_genre_lock
    (track_id, taxon_id, enabled, updated_at_utc)
VALUES
    (@trackId, @taxonId, @enabled, @updatedAtUtc)
ON CONFLICT(track_id, taxon_id) DO UPDATE SET
    enabled = excluded.enabled,
    updated_at_utc = excluded.updated_at_utc;
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("trackId", item.TrackId);
        command.Parameters.AddWithValue("taxonId", item.TaxonId);
        command.Parameters.AddWithValue("enabled", item.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("updatedAtUtc", now.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return item with { UpdatedAtUtc = now };
    }

    public async Task DeleteLockAsync(
        long trackId,
        string taxonId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
DELETE FROM personal_genre_lock
WHERE track_id = @trackId AND taxon_id = @taxonId;
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("trackId", trackId);
        command.Parameters.AddWithValue("taxonId", taxonId);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

CREATE TABLE IF NOT EXISTS personal_genre_taxon (
    id TEXT NOT NULL PRIMARY KEY,
    name TEXT NOT NULL,
    kind TEXT NOT NULL,
    parent_ids_json TEXT NOT NULL DEFAULT '[]',
    context_only INTEGER NOT NULL DEFAULT 0,
    aliases_json TEXT NOT NULL DEFAULT '[]',
    created_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX IF NOT EXISTS idx_personal_genre_taxon_kind_name
    ON personal_genre_taxon (kind, name COLLATE NOCASE);

CREATE TABLE IF NOT EXISTS personal_genre_mapping (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    match_value TEXT NOT NULL,
    target_taxon_id TEXT NOT NULL,
    source TEXT,
    priority INTEGER NOT NULL DEFAULT 100,
    enabled INTEGER NOT NULL DEFAULT 1,
    action TEXT NOT NULL DEFAULT 'map',
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

CREATE TABLE IF NOT EXISTS personal_genre_lock (
    track_id BIGINT NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    taxon_id TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    updated_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (track_id, taxon_id)
);

CREATE TABLE IF NOT EXISTS personal_genre_track (
    track_id BIGINT NOT NULL PRIMARY KEY REFERENCES track(id) ON DELETE CASCADE,
    primary_genre TEXT,
    genres_json TEXT NOT NULL DEFAULT '[]',
    styles_json TEXT NOT NULL DEFAULT '[]',
    substyles_json TEXT NOT NULL DEFAULT '[]',
    contexts_json TEXT NOT NULL DEFAULT '[]',
    classifications_json TEXT NOT NULL DEFAULT '[]',
    decisions_json TEXT NOT NULL DEFAULT '[]',
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

    private async Task ValidateCustomTaxonLookupAsync(
        PersonalGenreTaxon taxon,
        CancellationToken cancellationToken)
    {
        var candidateValues = new[] { taxon.Id, taxon.Name }
            .Concat(taxon.Aliases ?? Array.Empty<string>())
            .Select(PersonalGenreTaxonomy.Normalize)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var builtIn in PersonalGenreTaxonomy.GetDefaultTaxa())
        {
            var builtInValues = new[] { builtIn.Id, builtIn.Name }
                .Concat(builtIn.Aliases ?? Array.Empty<string>())
                .Select(PersonalGenreTaxonomy.Normalize);
            if (builtInValues.Any(candidateValues.Contains))
            {
                throw new ArgumentException(
                    $"Custom taxon '{taxon.Id}' conflicts with protected built-in taxon '{builtIn.Id}'. " +
                    "Use a mapping or user rule for intentional remapping.",
                    nameof(taxon));
            }
        }

        var existing = await GetCustomTaxaAsync(cancellationToken);
        foreach (var other in existing.Where(item =>
            !string.Equals(item.Id, taxon.Id, StringComparison.OrdinalIgnoreCase)))
        {
            var otherValues = new[] { other.Id, other.Name }
                .Concat(other.Aliases ?? Array.Empty<string>())
                .Select(PersonalGenreTaxonomy.Normalize);
            if (otherValues.Any(candidateValues.Contains))
            {
                throw new ArgumentException(
                    $"Custom taxon '{taxon.Id}' conflicts with existing custom taxon '{other.Id}'.",
                    nameof(taxon));
            }
        }
    }

    private async Task ValidateTaxonAsync(
        string taxonId,
        CancellationToken cancellationToken)
    {
        var normalized = taxonId?.Trim() ?? string.Empty;
        if (PersonalGenreTaxonomy.TryGetById(normalized, out _))
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
SELECT 1
FROM personal_genre_taxon
WHERE lower(id) = lower(@id)
LIMIT 1;
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("id", normalized);
        var exists = await command.ExecuteScalarAsync(cancellationToken);
        if (exists is null)
        {
            throw new ArgumentException($"Unknown Personal Genre taxon '{taxonId}'.", nameof(taxonId));
        }
    }

    private static string NormalizeCustomTaxonId(string? taxonId)
    {
        var value = taxonId?.Trim().ToLowerInvariant() ?? string.Empty;
        if (value.Length == 0
            || value.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException(
                "Custom taxon ID must contain only letters, numbers, hyphens, or underscores.",
                nameof(taxonId));
        }

        return value;
    }

    private static PersonalGenreTaxonKind ParseTaxonKind(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "genre" => PersonalGenreTaxonKind.Genre,
            "style" => PersonalGenreTaxonKind.Style,
            "substyle" => PersonalGenreTaxonKind.Substyle,
            "context" => PersonalGenreTaxonKind.Context,
            _ => throw new InvalidOperationException($"Unknown Personal Genre taxon kind '{value}'.")
        };

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
        command.Parameters.AddWithValue("action", mapping.Action.ToString().ToLowerInvariant());
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

    private static PersonalGenreMappingAction ParseMappingAction(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "contextonly" or "context_only" => PersonalGenreMappingAction.ContextOnly,
            "ignore" => PersonalGenreMappingAction.Ignore,
            "ambiguous" => PersonalGenreMappingAction.Ambiguous,
            _ => PersonalGenreMappingAction.Map
        };

    private static IReadOnlyList<PersonalGenreClassification> ParseClassifications(string json)
        => JsonSerializer.Deserialize<List<PersonalGenreClassification>>(json) ?? [];

    private static IReadOnlyList<PersonalGenreEvidenceDecision> ParseDecisions(string json)
        => JsonSerializer.Deserialize<List<PersonalGenreEvidenceDecision>>(json) ?? [];

    private static IReadOnlyList<PersonalGenreEvidence> ParseEvidence(string json)
        => JsonSerializer.Deserialize<List<PersonalGenreEvidence>>(json) ?? [];
}
