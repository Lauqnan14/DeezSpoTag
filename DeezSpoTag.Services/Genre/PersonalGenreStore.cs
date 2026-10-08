using System.Globalization;
using System.Text.Json;
using DeezSpoTag.Services.Utils;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace DeezSpoTag.Services.Genre;

public sealed class PersonalGenreStore
{
    /// <summary>
    /// Reads a recorded file snapshot for one AutoTag stage.
    /// </summary>
    public async Task<GenreSemanticSnapshot?> GetSnapshotAsync(
        string jobId, string filePath, GenreSnapshotStage stage,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqliteCommand(
            "SELECT snapshot_json, read_at_utc FROM personal_genre_snapshot WHERE job_id = @job AND file_path = @path AND stage = @stage;",
            connection);
        command.Parameters.AddWithValue("job", jobId);
        command.Parameters.AddWithValue("path", filePath);
        command.Parameters.AddWithValue("stage", StageName(stage));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var observations = JsonSerializer.Deserialize<List<GenreTagObservation>>(reader.GetString(0)) ?? [];
        var readAt = ParseDateTimeOffset(reader.GetString(1)) ?? DateTimeOffset.MinValue;
        return new GenreSemanticSnapshot(observations, readAt);
    }

    public async Task SaveSnapshotAsync(
        string jobId, string filePath, GenreSnapshotStage stage, GenreSemanticSnapshot snapshot,
        long? trackId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqliteCommand("""
INSERT INTO personal_genre_snapshot (job_id, file_path, stage, snapshot_json, track_id, read_at_utc)
VALUES (@job, @path, @stage, @snapshot, @track, @readAt)
ON CONFLICT(job_id, file_path, stage) DO UPDATE SET
    snapshot_json = excluded.snapshot_json,
    track_id = excluded.track_id,
    read_at_utc = excluded.read_at_utc;
""", connection);
        command.Parameters.AddWithValue("job", jobId);
        command.Parameters.AddWithValue("path", filePath);
        command.Parameters.AddWithValue("stage", StageName(stage));
        command.Parameters.AddWithValue("snapshot", JsonSerializer.Serialize(snapshot.Observations));
        command.Parameters.AddWithValue("track", (object?)trackId ?? DBNull.Value);
        command.Parameters.AddWithValue("readAt", snapshot.ReadAtUtc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static bool HasSnapshot(GenreSemanticSnapshot? snapshot, out string? error)
    {
        error = null;
        if (snapshot is not null)
        {
            return true;
        }

        error = "Genre Intelligence has no recorded file snapshot; existing metadata was preserved.";
        return false;
    }

    private static string StageName(GenreSnapshotStage stage)
        => stage.ToString().ToLowerInvariant();

    /// <summary>
    /// Reads back the resolution written by an AutoTag run.
    ///
    /// The AutoTag row holds only the resolution. The full record — including
    /// both file snapshots — is written to <c>personal_genre_track</c> by
    /// <see cref="SaveTrackResultAsync"/> whenever the track is identified, so
    /// this is the fallback for a run that tagged a file before the library
    /// knew which track it belonged to.
    /// </summary>
    public async Task<PersonalGenreTrackResult?> GetLatestAutoTagResultAsync(
        long trackId, string filePath, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqliteCommand("""
SELECT job_id, resolution_json, resolved_at_utc FROM personal_genre_autotag
WHERE file_path = @path AND write_status LIKE 'written:%' AND resolution_json IS NOT NULL
ORDER BY resolved_at_utc DESC LIMIT 1;
""", connection);
        command.Parameters.AddWithValue("path", filePath);
        string jobId;
        PersonalGenreResolution resolution;
        DateTimeOffset resolvedAt;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            jobId = reader.GetString(0);
            var deserialized = JsonSerializer.Deserialize<PersonalGenreResolution>(reader.GetString(1));
            if (deserialized is null)
            {
                return null;
            }

            resolution = deserialized;
            resolvedAt = ParseDateTimeOffset(reader.GetString(2)) ?? DateTimeOffset.MinValue;
        }

        return new PersonalGenreTrackResult(
            trackId,
            resolution,
            resolvedAt,
            // Both snapshots are keyed by job, so they are read back from the
            // table the run wrote. The post-AutoTag snapshot is deliberately not
            // derived from resolution.Observations: that list also holds the
            // values carried forward from the pre-AutoTag snapshot, so it is the
            // resolver's input rather than the file's state.
            await GetSnapshotAsync(jobId, filePath, GenreSnapshotStage.PreAutoTag, cancellationToken),
            await GetSnapshotAsync(jobId, filePath, GenreSnapshotStage.PostAutoTag, cancellationToken));
    }

    public async Task SaveAutoTagResultAsync(
        string jobId, string filePath, long? trackId, PersonalGenreResolution resolution, string writeStatus,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        // An upsert, not an update: snapshots live in their own table now, so
        // nothing else creates this row. Writing the result must not silently
        // do nothing just because the lifecycle row is absent.
        await using var command = new SqliteCommand("""
INSERT INTO personal_genre_autotag
    (job_id, file_path, evidence_json, track_id, resolution_json, write_status, resolved_at_utc)
VALUES
    (@job, @path, '[]', @track, @result, @status, @utc)
ON CONFLICT(job_id, file_path) DO UPDATE SET
    track_id = excluded.track_id,
    resolution_json = excluded.resolution_json,
    write_status = excluded.write_status,
    resolved_at_utc = excluded.resolved_at_utc;
""", connection);
        command.Parameters.AddWithValue("job", jobId);
        command.Parameters.AddWithValue("path", filePath);
        command.Parameters.AddWithValue("track", (object?)trackId ?? DBNull.Value);
        command.Parameters.AddWithValue("result", JsonSerializer.Serialize(resolution));
        command.Parameters.AddWithValue("status", writeStatus);
        command.Parameters.AddWithValue("utc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RememberAutoTagTrackIdAsync(string jobId, string filePath, long? trackId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqliteCommand("UPDATE personal_genre_autotag SET track_id = @track WHERE job_id = @job AND file_path = @path;", connection);
        command.Parameters.AddWithValue("track", (object?)trackId ?? DBNull.Value);
        command.Parameters.AddWithValue("job", jobId);
        command.Parameters.AddWithValue("path", filePath);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long?> GetAutoTagTrackIdAsync(string jobId, string filePath, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqliteCommand("SELECT track_id FROM personal_genre_autotag WHERE job_id = @job AND file_path = @path;", connection);
        command.Parameters.AddWithValue("job", jobId);
        command.Parameters.AddWithValue("path", filePath);
        var id = await command.ExecuteScalarAsync(cancellationToken);
        return id is null or DBNull ? null : Convert.ToInt64(id);
    }

    /// <summary>
    /// Carries the pre-AutoTag snapshot across a file move.
    ///
    /// AutoTag may materialise a file to its template path mid-run. The snapshot
    /// was taken against the original path, so it has to travel with the file or
    /// a resumed run would lose the user's original personal tags.
    /// </summary>
    public async Task CopyAutoTagCheckpointAsync(string jobId, string originalPath, string movedPath, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var command = new SqliteCommand("""
INSERT INTO personal_genre_snapshot (job_id, file_path, stage, snapshot_json, track_id, read_at_utc)
SELECT job_id, @moved, stage, snapshot_json, track_id, read_at_utc FROM personal_genre_snapshot
WHERE job_id = @job AND file_path = @original
ON CONFLICT(job_id, file_path, stage) DO UPDATE SET
    snapshot_json = excluded.snapshot_json,
    track_id = excluded.track_id,
    read_at_utc = excluded.read_at_utc;
""", connection, transaction))
            {
                command.Parameters.AddWithValue("job", jobId);
                command.Parameters.AddWithValue("original", originalPath);
                command.Parameters.AddWithValue("moved", movedPath);
                if (await command.ExecuteNonQueryAsync(cancellationToken) < 1)
                {
                    throw new InvalidOperationException("Genre Intelligence pre-AutoTag snapshot is missing.");
                }
            }

            await using (var command = new SqliteCommand("""
UPDATE personal_genre_autotag SET file_path = @moved
WHERE job_id = @job AND file_path = @original;
""", connection, transaction))
            {
                command.Parameters.AddWithValue("job", jobId);
                command.Parameters.AddWithValue("original", originalPath);
                command.Parameters.AddWithValue("moved", movedPath);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Records that a file's snapshot could not be taken or used.
    ///
    /// Upserted, because the snapshot table and the run-lifecycle row are
    /// written independently and the failure may be the first thing that touches
    /// this file for the job.
    /// </summary>
    public async Task MarkAutoTagSnapshotErrorAsync(string jobId, string filePath, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqliteCommand("""
INSERT INTO personal_genre_autotag (job_id, file_path, evidence_json, write_status)
VALUES (@job, @path, '[]', 'snapshot_error')
ON CONFLICT(job_id, file_path) DO UPDATE SET write_status = 'snapshot_error';
""", connection);
        command.Parameters.AddWithValue("job", jobId);
        command.Parameters.AddWithValue("path", filePath);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Whether a file is safe to classify.
    ///
    /// Validity is the presence of the pre-AutoTag snapshot, because that is the
    /// record of the file's own values. Checking the run-lifecycle row instead
    /// would report every first-run file as unusable, since the row is only
    /// written once a resolution exists.
    /// </summary>
    public async Task<bool> IsAutoTagSnapshotValidAsync(string jobId, string filePath, CancellationToken cancellationToken = default)
    {
        return await GetSnapshotAsync(jobId, filePath, GenreSnapshotStage.PreAutoTag, cancellationToken) is not null;
    }

    /// <summary>
    /// Whether this file was already marked unusable for the job.
    ///
    /// The marker is what makes a failure sticky. Without it, a retry would
    /// re-read a possibly half-written file and start trusting it again.
    /// </summary>
    public async Task<bool> HasSnapshotErrorMarkerAsync(string jobId, string filePath, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqliteCommand(
            "SELECT write_status FROM personal_genre_autotag WHERE job_id = @job AND file_path = @path;",
            connection);
        command.Parameters.AddWithValue("job", jobId);
        command.Parameters.AddWithValue("path", filePath);
        var status = await command.ExecuteScalarAsync(cancellationToken) as string;
        return string.Equals(status, "snapshot_error", StringComparison.Ordinal);
    }
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
SELECT enabled, max_genres, preserve_unmapped_tags, include_parent_genres,
       normalize_genre_tags, genre_tag_alias_rules_json, genre_tag_block_list_json
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
            reader.GetInt32(3) != 0,
            reader.GetInt32(4) != 0,
            ParseAliasRules(reader.GetString(5)),
            // An absent row means the user never configured a block list, which is
            // different from an explicitly empty one.
            reader.IsDBNull(6) ? null : ParseStringList(reader.GetString(6)));
    }

    /// <summary>
    /// Saves the settings, treating a null normalization field as "not supplied"
    /// rather than as "reset to defaults".
    ///
    /// A partial payload must not destroy work. A client that only means to change
    /// the genre count sends no alias rules, and silently replacing a user's rules
    /// with the shipped ones because of that would lose data through an ordinary
    /// edit elsewhere.
    /// </summary>
    public async Task<PersonalGenreSettings> SaveSettingsAsync(
        PersonalGenreSettings settings,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var existing = await GetSettingsAsync(cancellationToken);
        var normalized = settings with
        {
            MaxGenres = Math.Clamp(settings.MaxGenres, 1, 10),
            // An omitted rule list keeps the stored one; the shipped defaults are
            // folded in on every save, as they always were, so the list is a set
            // of conventions that is guaranteed present rather than a value that
            // can be accidentally emptied.
            GenreTagAliasRules = GenreNormalizationSnapshot.MergeDefaultAliasRules(
                settings.GenreTagAliasRules ?? existing.GenreTagAliasRules,
                PersonalGenreSettings.DefaultAliasRules),
            GenreTagBlockList = settings.GenreTagBlockList ?? existing.GenreTagBlockList
        };
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
INSERT INTO personal_genre_settings
    (id, enabled, max_genres, preserve_unmapped_tags, include_parent_genres,
     normalize_genre_tags, genre_tag_alias_rules_json, genre_tag_block_list_json, updated_at_utc)
VALUES
    (1, @enabled, @maxGenres, @preserveUnmappedTags, @includeParentGenres,
     @normalizeGenreTags, @aliasRulesJson, @blockListJson, @updatedAtUtc)
ON CONFLICT(id) DO UPDATE SET
    enabled = excluded.enabled,
    max_genres = excluded.max_genres,
    preserve_unmapped_tags = excluded.preserve_unmapped_tags,
    include_parent_genres = excluded.include_parent_genres,
    normalize_genre_tags = excluded.normalize_genre_tags,
    genre_tag_alias_rules_json = excluded.genre_tag_alias_rules_json,
    genre_tag_block_list_json = excluded.genre_tag_block_list_json,
    updated_at_utc = excluded.updated_at_utc;
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("enabled", normalized.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("maxGenres", normalized.MaxGenres);
        command.Parameters.AddWithValue("preserveUnmappedTags", normalized.PreserveUnmappedTags ? 1 : 0);
        command.Parameters.AddWithValue("includeParentGenres", normalized.IncludeParentGenres ? 1 : 0);
        command.Parameters.AddWithValue("normalizeGenreTags", normalized.NormalizeGenreTags ? 1 : 0);
        command.Parameters.AddWithValue("aliasRulesJson", JsonSerializer.Serialize(
            (normalized.GenreTagAliasRules ?? PersonalGenreSettings.DefaultAliasRules)
                .Select(rule => new PersonalGenreAliasRule(rule.Alias, rule.Canonical))
                .ToArray()));
        command.Parameters.AddWithValue("blockListJson", normalized.GenreTagBlockList is null
            ? DBNull.Value
            : JsonSerializer.Serialize(normalized.GenreTagBlockList));
        command.Parameters.AddWithValue("updatedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return normalized;
    }

    /// <summary>
    /// Whether a named one-time migration has already run.
    ///
    /// The marker is what makes a migration idempotent. Without it the import
    /// would run on every startup and would overwrite anything the user changed
    /// here in the meantime.
    /// </summary>
    public async Task<bool> HasMigrationAsync(string name, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqliteCommand(
            "SELECT 1 FROM personal_genre_migration WHERE name = @name LIMIT 1;", connection);
        command.Parameters.AddWithValue("name", name);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task MarkMigrationAsync(string name, string? detail, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqliteCommand("""
INSERT INTO personal_genre_migration (name, applied_at_utc, detail)
VALUES (@name, @appliedAt, @detail)
ON CONFLICT(name) DO NOTHING;
""", connection);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("appliedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("detail", (object?)detail ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyList<PersonalGenreAliasRule> ParseAliasRules(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<PersonalGenreAliasRule>>(json) ?? [];
        }
        catch (JsonException)
        {
            // A rule the user cannot parse is dropped rather than failing the whole
            // settings load, so one bad row cannot take genre configuration down.
            return [];
        }
    }

    public async Task<IReadOnlyList<PersonalGenreTaxon>> GetCustomTaxaAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
SELECT id, name, kind, parent_ids_json, context_only, aliases_json, regions_json
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
                ParseStringList(reader.GetString(5)),
                // A NULL column is the pre-region state and means unscoped.
                reader.IsDBNull(6) ? null : ParseStringList(reader.GetString(6))));
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
        if (IsShippedTaxonId(id))
        {
            throw new ArgumentException(
                $"'{id}' is a shipped Personal Genre term and cannot be replaced. "
                + "Add an alias, a mapping or a rule instead, or create your own term under a different id.",
                nameof(taxon.Id));
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
            && taxon.Kind is PersonalGenreTaxonKind.Genre
                or PersonalGenreTaxonKind.Style
                or PersonalGenreTaxonKind.Substyle
            ? PersonalGenreTaxonKind.Context
            : taxon.Kind;
        var contextOnly = taxon.ContextOnly
            || normalizedKind is PersonalGenreTaxonKind.Context
                or PersonalGenreTaxonKind.Scene
                or PersonalGenreTaxonKind.Language;
        // Region membership is normalized on the way in so a stored selection always
        // reads back in built-in display order with no duplicates and no blanks. An
        // empty result is stored as NULL, i.e. unscoped.
        var regions = PersonalGenreRegions.Normalize(taxon.Regions);
        var normalized = new PersonalGenreTaxon(
            id,
            name,
            normalizedKind,
            parentIds,
            contextOnly,
            aliases,
            regions);

        await ValidateCustomTaxonLookupAsync(normalized, cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
INSERT INTO personal_genre_taxon
    (id, name, kind, parent_ids_json, context_only, aliases_json, regions_json, created_at_utc, updated_at_utc)
VALUES
    (@id, @name, @kind, @parentIdsJson, @contextOnly, @aliasesJson, @regionsJson, @now, @now)
ON CONFLICT(id) DO UPDATE SET
    name = excluded.name,
    kind = excluded.kind,
    parent_ids_json = excluded.parent_ids_json,
    context_only = excluded.context_only,
    aliases_json = excluded.aliases_json,
    regions_json = excluded.regions_json,
    updated_at_utc = excluded.updated_at_utc;
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("id", normalized.Id);
        command.Parameters.AddWithValue("name", normalized.Name);
        command.Parameters.AddWithValue("kind", normalized.Kind.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("parentIdsJson", JsonSerializer.Serialize(normalized.ParentIds ?? Array.Empty<string>()));
        command.Parameters.AddWithValue("contextOnly", normalized.ContextOnly ? 1 : 0);
        command.Parameters.AddWithValue("aliasesJson", JsonSerializer.Serialize(normalized.Aliases ?? Array.Empty<string>()));
        command.Parameters.AddWithValue("regionsJson", regions is null
            ? DBNull.Value
            : JsonSerializer.Serialize(regions));
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
        if (IsShippedTaxonId(id))
        {
            throw new ArgumentException(
                $"'{id}' is a shipped Personal Genre term and cannot be deleted.",
                nameof(taxonId));
        }

        await using var connection = await OpenAsync(cancellationToken);
        const string referenceSql = """
SELECT
    (SELECT COUNT(*) FROM personal_genre_mapping WHERE lower(target_taxon_id) = lower(@id)) +
    (SELECT COUNT(*) FROM personal_genre_rule WHERE lower(target_taxon_id) = lower(@id)) +
    (SELECT COUNT(*) FROM personal_genre_lock WHERE lower(taxon_id) = lower(@id)) +
    (SELECT COUNT(*) FROM personal_genre_scope_lock WHERE lower(taxon_id) = lower(@id)) +
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
SELECT id, match_value, target_taxon_id, input_field, priority, enabled, action
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
                TryParseInputField(reader.IsDBNull(3) ? null : reader.GetString(3)),
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
    input_field = @inputField,
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
    (match_value, target_taxon_id, input_field, priority, enabled, action, created_at_utc, updated_at_utc)
VALUES
    (@matchValue, @targetTaxonId, @inputField, @priority, @enabled, @action, @now, @now);
SELECT last_insert_rowid();
""";
        await using var insert = new SqliteCommand(insertSql, connection);
        BindMapping(insert, mapping with { MatchValue = matchValue });
        insert.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return mapping with { Id = id, MatchValue = matchValue };
    }

    /// <summary>
    /// Pages the track ids a rebuild should consider.
    ///
    /// The candidate set is "indexed track with a readable file", because the
    /// file is the input. Track analysis is deliberately not part of it: a track
    /// that was never analysed still has tags worth classifying, and a track that
    /// was analysed but has no file cannot be resolved at all.
    /// </summary>
    public async Task<IReadOnlyList<long>> GetResolvableTrackIdsAfterAsync(
        long afterTrackId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
SELECT DISTINCT t.id FROM track t
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
WHERE t.id > @afterTrackId
ORDER BY t.id
LIMIT @limit;
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("afterTrackId", Math.Max(0, afterTrackId));
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var output = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(reader.GetInt64(0));
        }

        return output;
    }

    public async Task<IReadOnlyList<PersonalGenreRule>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
SELECT id, match_value, target_taxon_id, input_field, priority, enabled
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
                TryParseInputField(reader.IsDBNull(3) ? null : reader.GetString(3)),
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
    input_field = @inputField,
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
    (match_value, target_taxon_id, input_field, priority, enabled, created_at_utc, updated_at_utc)
VALUES
    (@matchValue, @targetTaxonId, @inputField, @priority, @enabled, @now, @now);
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
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            // The observed state is persisted before anything is written to a
            // file, and both the pre-AutoTag and post-AutoTag snapshots are kept
            // so a later run can show what the file actually contained.
            const string currentSql = """
INSERT INTO personal_genre_track
    (track_id, primary_genre, genres_json, styles_json, substyles_json, contexts_json, scenes_json, languages_json,
     preserved_json, classifications_json, decisions_json, applied_rule_ids_json, observations_json,
     pre_autotag_json, post_autotag_json, resolver_version, resolved_at_utc)
VALUES
    (@trackId, @primaryGenre, @genresJson, @stylesJson, @substylesJson, @contextsJson, @scenesJson, @languagesJson,
     @preservedJson, @classificationsJson, @decisionsJson, @appliedRuleIdsJson, @observationsJson,
     @preAutotagJson, @postAutotagJson, @resolverVersion, @resolvedAtUtc)
ON CONFLICT(track_id) DO UPDATE SET
    primary_genre = excluded.primary_genre,
    genres_json = excluded.genres_json,
    styles_json = excluded.styles_json,
    substyles_json = excluded.substyles_json,
    contexts_json = excluded.contexts_json,
    scenes_json = excluded.scenes_json,
    languages_json = excluded.languages_json,
    preserved_json = excluded.preserved_json,
    classifications_json = excluded.classifications_json,
    decisions_json = excluded.decisions_json,
    applied_rule_ids_json = excluded.applied_rule_ids_json,
    observations_json = excluded.observations_json,
    pre_autotag_json = excluded.pre_autotag_json,
    post_autotag_json = excluded.post_autotag_json,
    resolver_version = excluded.resolver_version,
    resolved_at_utc = excluded.resolved_at_utc;
""";
            await using (var current = new SqliteCommand(currentSql, connection, transaction))
            {
                BindTrackResult(current, result);
                await current.ExecuteNonQueryAsync(cancellationToken);
            }

            const string historySql = """
INSERT INTO personal_genre_resolution_history
    (track_id, primary_genre, genres_json, styles_json, substyles_json, contexts_json, scenes_json, languages_json,
     preserved_json, classifications_json, decisions_json, applied_rule_ids_json, resolver_version, resolved_at_utc)
VALUES
    (@trackId, @primaryGenre, @genresJson, @stylesJson, @substylesJson, @contextsJson, @scenesJson, @languagesJson,
     @preservedJson, @classificationsJson, @decisionsJson, @appliedRuleIdsJson, @resolverVersion, @resolvedAtUtc);
SELECT last_insert_rowid();
""";
            long historyId;
            await using (var history = new SqliteCommand(historySql, connection, transaction))
            {
                BindTrackResult(history, result, includeObservations: false);
                historyId = Convert.ToInt64(
                    await history.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture);
            }

            await AppendObservationHistoryAsync(
                connection,
                transaction,
                historyId,
                result.Resolution.Observations,
                cancellationToken);
            await AppendClassificationHistoryAsync(
                connection,
                transaction,
                historyId,
                result.Resolution.Classifications,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<PersonalGenreTrackResult?> GetTrackResultAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
SELECT primary_genre, genres_json, styles_json, substyles_json, contexts_json, scenes_json, languages_json,
       preserved_json, classifications_json, decisions_json, applied_rule_ids_json, observations_json,
       pre_autotag_json, post_autotag_json, resolver_version, resolved_at_utc
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

        var resolvedAtUtc = ParseDateTimeOffset(reader.IsDBNull(15) ? null : reader.GetString(15)) ?? DateTimeOffset.MinValue;
        var resolution = new PersonalGenreResolution(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            ParseStringList(reader.GetString(1)),
            ParseStringList(reader.GetString(2)),
            ParseStringList(reader.GetString(3)),
            ParseStringList(reader.GetString(4)),
            ParseStringList(reader.GetString(5)),
            ParseStringList(reader.GetString(6)),
            ParsePreserved(reader.GetString(7)),
            ParseClassifications(reader.GetString(8)),
            ParseDecisions(reader.GetString(9)),
            ParseStringList(reader.GetString(10)),
            ParseObservations(reader.GetString(11)),
            reader.GetString(14));

        return new PersonalGenreTrackResult(
            trackId,
            resolution,
            resolvedAtUtc,
            ParseSnapshot(reader.IsDBNull(12) ? null : reader.GetString(12), resolvedAtUtc),
            ParseSnapshot(reader.IsDBNull(13) ? null : reader.GetString(13), resolvedAtUtc));
    }

    public async Task<IReadOnlyList<PersonalGenreResolutionHistoryItem>> GetTrackHistoryAsync(
        long trackId,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
SELECT * FROM (
SELECT h.id,
       h.track_id,
       h.primary_genre,
       h.resolver_version,
       h.resolved_at_utc,
       (SELECT COUNT(*) FROM personal_genre_observation_history o WHERE o.resolution_id = h.id),
       (SELECT COUNT(*) FROM personal_genre_classification_history c WHERE c.resolution_id = h.id),
       h.decisions_json
FROM personal_genre_resolution_history h
WHERE h.track_id = @trackId
UNION ALL
SELECT -pg.rowid, tl.track_id,
       json_extract(pg.resolution_json, '$.PrimaryGenre'),
       json_extract(pg.resolution_json, '$.ResolverVersion'),
       pg.resolved_at_utc,
       json_array_length(pg.resolution_json, '$.Observations'),
       json_array_length(pg.resolution_json, '$.Classifications'),
       json_extract(pg.resolution_json, '$.Decisions')
FROM personal_genre_autotag pg
JOIN audio_file af ON af.path = pg.file_path
JOIN track_local tl ON tl.audio_file_id = af.id
WHERE tl.track_id = @trackId AND pg.track_id IS NULL
    AND pg.write_status LIKE 'written:%' AND pg.resolution_json IS NOT NULL
)
ORDER BY resolved_at_utc DESC
LIMIT @limit;
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("trackId", trackId);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var output = new List<PersonalGenreResolutionHistoryItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new PersonalGenreResolutionHistoryItem(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                ParseDateTimeOffset(reader.GetString(4)) ?? DateTimeOffset.MinValue,
                reader.GetInt32(5),
                reader.GetInt32(6),
                ParseDecisions(reader.GetString(7))));

        }

        return output;
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
            output.Add(new PersonalGenreLock(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt32(2) != 0,
                ParseDateTimeOffset(reader.IsDBNull(3) ? null : reader.GetString(3)),
                ScopeType: "track",
                ScopeId: reader.GetInt64(0)));
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
        return item with { UpdatedAtUtc = now, ScopeType = "track", ScopeId = item.TrackId };
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

    public async Task<IReadOnlyList<PersonalGenreScopedLock>> GetScopedLocksAsync(
        string scopeType,
        long scopeId,
        CancellationToken cancellationToken = default)
    {
        var normalizedScope = NormalizeScopedLockType(scopeType);
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
SELECT scope_type, scope_id, taxon_id, enabled, updated_at_utc
FROM personal_genre_scope_lock
WHERE scope_type = @scopeType AND scope_id = @scopeId
ORDER BY taxon_id;
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("scopeType", normalizedScope);
        command.Parameters.AddWithValue("scopeId", scopeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var output = new List<PersonalGenreScopedLock>();
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new PersonalGenreScopedLock(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt32(3) != 0,
                ParseDateTimeOffset(reader.IsDBNull(4) ? null : reader.GetString(4))));
        }

        return output;
    }

    public async Task<PersonalGenreScopedLock> SaveScopedLockAsync(
        PersonalGenreScopedLock item,
        CancellationToken cancellationToken = default)
    {
        var scopeType = NormalizeScopedLockType(item.ScopeType);
        if (item.ScopeId <= 0)
        {
            throw new ArgumentException("Scope ID must be greater than zero.", nameof(item.ScopeId));
        }

        await ValidateTaxonAsync(item.TaxonId, cancellationToken);
        await ValidateScopeEntityAsync(scopeType, item.ScopeId, cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        const string sql = """
INSERT INTO personal_genre_scope_lock
    (scope_type, scope_id, taxon_id, enabled, updated_at_utc)
VALUES
    (@scopeType, @scopeId, @taxonId, @enabled, @updatedAtUtc)
ON CONFLICT(scope_type, scope_id, taxon_id) DO UPDATE SET
    enabled = excluded.enabled,
    updated_at_utc = excluded.updated_at_utc;
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("scopeType", scopeType);
        command.Parameters.AddWithValue("scopeId", item.ScopeId);
        command.Parameters.AddWithValue("taxonId", item.TaxonId);
        command.Parameters.AddWithValue("enabled", item.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("updatedAtUtc", now.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return item with { ScopeType = scopeType, UpdatedAtUtc = now };
    }

    public async Task DeleteScopedLockAsync(
        string scopeType,
        long scopeId,
        string taxonId,
        CancellationToken cancellationToken = default)
    {
        var normalizedScope = NormalizeScopedLockType(scopeType);
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
DELETE FROM personal_genre_scope_lock
WHERE scope_type = @scopeType
  AND scope_id = @scopeId
  AND taxon_id = @taxonId;
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("scopeType", normalizedScope);
        command.Parameters.AddWithValue("scopeId", scopeId);
        command.Parameters.AddWithValue("taxonId", taxonId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PersonalGenreLock>> GetEffectiveLocksAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        var scope = await GetTrackScopeAsync(trackId, cancellationToken);
        if (scope is null)
        {
            return await GetLocksAsync(trackId, cancellationToken);
        }

        var output = new List<PersonalGenreLock>();
        var artistLocks = await GetScopedLocksAsync("artist", scope.ArtistId, cancellationToken);
        var albumLocks = await GetScopedLocksAsync("album", scope.AlbumId, cancellationToken);
        var trackLocks = await GetLocksAsync(trackId, cancellationToken);

        output.AddRange(artistLocks.Select(item => new PersonalGenreLock(
            trackId,
            item.TaxonId,
            item.Enabled,
            item.UpdatedAtUtc,
            ScopeType: "artist",
            ScopeId: item.ScopeId)));
        output.AddRange(albumLocks.Select(item => new PersonalGenreLock(
            trackId,
            item.TaxonId,
            item.Enabled,
            item.UpdatedAtUtc,
            ScopeType: "album",
            ScopeId: item.ScopeId)));
        output.AddRange(trackLocks);
        return output;
    }

    public async Task<PersonalGenreTrackScope?> GetTrackScopeAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
SELECT t.id, t.album_id, a.artist_id
FROM track t
JOIN album a ON a.id = t.album_id
WHERE t.id = @trackId
LIMIT 1;
""";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("trackId", trackId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PersonalGenreTrackScope(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2))
            : null;
    }

    private static void BindTrackResult(
        SqliteCommand command,
        PersonalGenreTrackResult result,
        bool includeObservations = true)
    {
        command.Parameters.AddWithValue("trackId", result.TrackId);
        command.Parameters.AddWithValue("primaryGenre", (object?)result.Resolution.PrimaryGenre ?? DBNull.Value);
        command.Parameters.AddWithValue("genresJson", JsonSerializer.Serialize(result.Resolution.Genres));
        command.Parameters.AddWithValue("stylesJson", JsonSerializer.Serialize(result.Resolution.Styles));
        command.Parameters.AddWithValue("substylesJson", JsonSerializer.Serialize(result.Resolution.Substyles));
        command.Parameters.AddWithValue("contextsJson", JsonSerializer.Serialize(result.Resolution.Contexts));
        command.Parameters.AddWithValue("scenesJson", JsonSerializer.Serialize(result.Resolution.Scenes));
        command.Parameters.AddWithValue("languagesJson", JsonSerializer.Serialize(result.Resolution.Languages));
        command.Parameters.AddWithValue("preservedJson", JsonSerializer.Serialize(result.Resolution.Preserved));
        command.Parameters.AddWithValue("classificationsJson", JsonSerializer.Serialize(result.Resolution.Classifications));
        command.Parameters.AddWithValue("decisionsJson", JsonSerializer.Serialize(result.Resolution.Decisions));
        command.Parameters.AddWithValue("appliedRuleIdsJson", JsonSerializer.Serialize(result.Resolution.AppliedRuleIds));
        if (includeObservations)
        {
            command.Parameters.AddWithValue("observationsJson", JsonSerializer.Serialize(result.Resolution.Observations));
            command.Parameters.AddWithValue("preAutotagJson", SerializeSnapshot(result.PreAutoTagSnapshot));
            command.Parameters.AddWithValue("postAutotagJson", SerializeSnapshot(result.PostAutoTagSnapshot));
        }
        command.Parameters.AddWithValue("resolverVersion", result.Resolution.ResolverVersion);
        command.Parameters.AddWithValue("resolvedAtUtc", result.ResolvedAtUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    private static object SerializeSnapshot(GenreSemanticSnapshot? snapshot)
        => snapshot is null
            ? DBNull.Value
            : JsonSerializer.Serialize(snapshot.Observations);

    private static GenreSemanticSnapshot? ParseSnapshot(string? json, DateTimeOffset fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return new GenreSemanticSnapshot(JsonSerializer.Deserialize<List<GenreTagObservation>>(json) ?? [], fallback);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task AppendObservationHistoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long historyId,
        IReadOnlyList<GenreTagObservation> observations,
        CancellationToken cancellationToken)
    {
        const string sql = """
INSERT INTO personal_genre_observation_history
    (resolution_id, sequence, raw_value, input_field, origin)
VALUES
    (@resolutionId, @sequence, @rawValue, @inputField, @origin);
""";
        for (var index = 0; index < observations.Count; index++)
        {
            var item = observations[index];
            await using var command = new SqliteCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("resolutionId", historyId);
            command.Parameters.AddWithValue("sequence", index);
            command.Parameters.AddWithValue("rawValue", item.RawValue);
            command.Parameters.AddWithValue("inputField", item.InputField.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("origin", item.Origin.ToString().ToLowerInvariant());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task AppendClassificationHistoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long historyId,
        IReadOnlyList<PersonalGenreClassification> classifications,
        CancellationToken cancellationToken)
    {
        const string sql = """
INSERT INTO personal_genre_classification_history
    (resolution_id, taxon_id, name, kind, origin_fields_json, user_locked, status)
VALUES
    (@resolutionId, @taxonId, @name, @kind, @originFieldsJson, @userLocked, @status);
""";
        foreach (var item in classifications)
        {
            await using var command = new SqliteCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("resolutionId", historyId);
            command.Parameters.AddWithValue("taxonId", item.TaxonId);
            command.Parameters.AddWithValue("name", item.Name);
            command.Parameters.AddWithValue("kind", item.Kind.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("originFieldsJson", JsonSerializer.Serialize(
                item.OriginFields.Select(field => field.ToString().ToLowerInvariant()).ToArray()));
            command.Parameters.AddWithValue("userLocked", item.UserLocked ? 1 : 0);
            command.Parameters.AddWithValue("status", item.Status);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task ValidateScopeEntityAsync(
        string scopeType,
        long scopeId,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        // The scope table is one of two fixed names, so the check is spelled out
        // per table rather than spliced from the caller's string.
        const string ArtistScopeSql = "SELECT 1 FROM artist WHERE id = @scopeId LIMIT 1;";
        const string AlbumScopeSql = "SELECT 1 FROM album WHERE id = @scopeId LIMIT 1;";
        var sql = scopeType == "artist" ? ArtistScopeSql : AlbumScopeSql;
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("scopeId", scopeId);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new ArgumentException($"Unknown {scopeType} ID '{scopeId}'.", nameof(scopeId));
        }
    }

    private static string NormalizeScopedLockType(string scopeType)
    {
        var normalized = (scopeType ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "artist" => "artist",
            "album" => "album",
            _ => throw new ArgumentException("Scoped Personal Genre locks support only 'artist' or 'album'.", nameof(scopeType))
        };
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;

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
CREATE TABLE IF NOT EXISTS personal_genre_autotag (
    job_id TEXT NOT NULL,
    file_path TEXT NOT NULL,
    evidence_json TEXT NOT NULL,
    track_id BIGINT NULL,
    resolution_json TEXT NULL,
    write_status TEXT NOT NULL DEFAULT 'pending',
    resolved_at_utc TEXT NULL,
    PRIMARY KEY (job_id, file_path)
);
-- Genre normalization is owned here rather than in the general application
-- settings. The alias rules and block list are stored as JSON rather than in
-- child tables because they are a single editable preference that is read and
-- written as a unit, and because their shape is user data that must survive
-- untouched through a migration.
CREATE TABLE IF NOT EXISTS personal_genre_settings (
    id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
    enabled INTEGER NOT NULL DEFAULT 1,
    max_genres INTEGER NOT NULL DEFAULT 3,
    preserve_provider_fallback INTEGER NOT NULL DEFAULT 1,
    include_parent_genres INTEGER NOT NULL DEFAULT 0,
    normalize_genre_tags INTEGER NOT NULL DEFAULT 0,
    genre_tag_alias_rules_json TEXT NOT NULL DEFAULT '[]',
    -- Nullable on purpose: NULL means "never configured", which is different from
    -- an empty list meaning "the user wants nothing blocked".
    genre_tag_block_list_json TEXT NULL,
    updated_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
INSERT OR IGNORE INTO personal_genre_settings (id) VALUES (1);

-- Records that the one-time move of genre normalization out of the general
-- application settings has already happened. Without this a later change made
-- here would be reverted on every startup by a fresh import of the old values.
CREATE TABLE IF NOT EXISTS personal_genre_migration (
    name TEXT NOT NULL PRIMARY KEY,
    applied_at_utc TEXT NOT NULL,
    detail TEXT
);

-- File-source architecture. The audio file is the only semantic input now, so a
-- snapshot records observed values and the field each was read from. `order`
-- preserves the position within its field; `origin` distinguishes a value the
-- AutoTag platforms wrote from one that was already in the file and survived.
--
-- weight/authority_cap are retained from the provider era and are no longer
-- written: a file tag has no authority to accumulate.
CREATE TABLE IF NOT EXISTS personal_genre_snapshot (
    job_id TEXT NOT NULL,
    file_path TEXT NOT NULL,
    stage TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    track_id BIGINT NULL,
    read_at_utc TEXT NOT NULL,
    PRIMARY KEY (job_id, file_path, stage)
);

CREATE TABLE IF NOT EXISTS personal_genre_taxon (
    id TEXT NOT NULL PRIMARY KEY,
    name TEXT NOT NULL,
    kind TEXT NOT NULL,
    parent_ids_json TEXT NOT NULL DEFAULT '[]',
    context_only INTEGER NOT NULL DEFAULT 0,
    aliases_json TEXT NOT NULL DEFAULT '[]',
    -- Nullable on purpose, for the same reason genre_tag_block_list_json is: NULL
    -- means "unscoped", which is how every universal term and every term created
    -- before regions existed is expressed. A default of '[]' would be equivalent
    -- for reading, but NULL keeps "never classified by region" distinguishable from
    -- an explicitly emptied selection.
    regions_json TEXT NULL,
    created_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX IF NOT EXISTS idx_personal_genre_taxon_kind_name
    ON personal_genre_taxon (kind, name COLLATE NOCASE);

-- A tag mapping answers "when this value is found in a file, how should it be
-- read?". `input_field` scopes the mapping to one file field (genre/style/
-- substyle/context/scene/language) or is NULL to match any field. It replaces
-- the provider `source` column, which is meaningless once the file is the input.
CREATE TABLE IF NOT EXISTS personal_genre_mapping (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    match_value TEXT NOT NULL,
    target_taxon_id TEXT NOT NULL,
    input_field TEXT,
    priority INTEGER NOT NULL DEFAULT 100,
    enabled INTEGER NOT NULL DEFAULT 1,
    action TEXT NOT NULL DEFAULT 'map',
    created_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
-- The mapping and rule indexes are created in MigrateSchemaAsync instead of
-- here: they reference input_field, which a provider-era table does not have
-- until that migration adds it.

CREATE TABLE IF NOT EXISTS personal_genre_rule (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    match_value TEXT NOT NULL,
    target_taxon_id TEXT NOT NULL,
    input_field TEXT,
    priority INTEGER NOT NULL DEFAULT 1000,
    enabled INTEGER NOT NULL DEFAULT 1,
    created_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);


CREATE TABLE IF NOT EXISTS personal_genre_lock (
    track_id BIGINT NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    taxon_id TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    updated_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (track_id, taxon_id)
);

CREATE TABLE IF NOT EXISTS personal_genre_scope_lock (
    scope_type TEXT NOT NULL,
    scope_id BIGINT NOT NULL,
    taxon_id TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    updated_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (scope_type, scope_id, taxon_id)
);
CREATE INDEX IF NOT EXISTS idx_personal_genre_scope_lock_scope
    ON personal_genre_scope_lock (scope_type, scope_id, enabled);

-- `preserved_json` holds values the taxonomy does not know. They are kept
-- verbatim, are not classified, and are written back to the field they came
-- from so personal tagging survives the round trip.
CREATE TABLE IF NOT EXISTS personal_genre_track (
    track_id BIGINT NOT NULL PRIMARY KEY REFERENCES track(id) ON DELETE CASCADE,
    primary_genre TEXT,
    genres_json TEXT NOT NULL DEFAULT '[]',
    styles_json TEXT NOT NULL DEFAULT '[]',
    substyles_json TEXT NOT NULL DEFAULT '[]',
    contexts_json TEXT NOT NULL DEFAULT '[]',
    scenes_json TEXT NOT NULL DEFAULT '[]',
    languages_json TEXT NOT NULL DEFAULT '[]',
    preserved_json TEXT NOT NULL DEFAULT '[]',
    classifications_json TEXT NOT NULL DEFAULT '[]',
    decisions_json TEXT NOT NULL DEFAULT '[]',
    applied_rule_ids_json TEXT NOT NULL DEFAULT '[]',
    observations_json TEXT NOT NULL DEFAULT '[]',
    pre_autotag_json TEXT NULL,
    post_autotag_json TEXT NULL,
    resolver_version TEXT NOT NULL,
    resolved_at_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_personal_genre_track_primary
    ON personal_genre_track (primary_genre);
CREATE TABLE IF NOT EXISTS personal_genre_resolution_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    track_id BIGINT NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    primary_genre TEXT,
    genres_json TEXT NOT NULL DEFAULT '[]',
    styles_json TEXT NOT NULL DEFAULT '[]',
    substyles_json TEXT NOT NULL DEFAULT '[]',
    contexts_json TEXT NOT NULL DEFAULT '[]',
    scenes_json TEXT NOT NULL DEFAULT '[]',
    languages_json TEXT NOT NULL DEFAULT '[]',
    preserved_json TEXT NOT NULL DEFAULT '[]',
    classifications_json TEXT NOT NULL DEFAULT '[]',
    decisions_json TEXT NOT NULL DEFAULT '[]',
    applied_rule_ids_json TEXT NOT NULL DEFAULT '[]',
    resolver_version TEXT NOT NULL,
    resolved_at_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_personal_genre_resolution_history_track
    ON personal_genre_resolution_history (track_id, id DESC);

-- One row per value that was read out of the file, in the order it appeared.
-- `input_field` is the tag it was read from; `origin` records whether a platform
-- wrote it or it was already present.
CREATE TABLE IF NOT EXISTS personal_genre_observation_history (
    resolution_id BIGINT NOT NULL REFERENCES personal_genre_resolution_history(id) ON DELETE CASCADE,
    sequence INTEGER NOT NULL,
    raw_value TEXT NOT NULL,
    input_field TEXT NOT NULL,
    origin TEXT NOT NULL DEFAULT 'post_platform',
    PRIMARY KEY (resolution_id, sequence)
);

-- `origin_fields_json` records which file fields a classification was seen in,
-- which is what makes a field correction auditable. `confidence` and
-- `sources_json` are provider-era columns: they stay in place so existing
-- history is not destroyed, and nothing writes them any more.
CREATE TABLE IF NOT EXISTS personal_genre_classification_history (
    resolution_id BIGINT NOT NULL REFERENCES personal_genre_resolution_history(id) ON DELETE CASCADE,
    taxon_id TEXT NOT NULL,
    name TEXT NOT NULL,
    kind TEXT NOT NULL,
    origin_fields_json TEXT NOT NULL DEFAULT '[]',
    user_locked INTEGER NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'suggested',
    PRIMARY KEY (resolution_id, taxon_id)
);

""";
            await using var command = new SqliteCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await MigrateSchemaAsync(connection, cancellationToken);
            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    /// <summary>
    /// Brings a database created by the earlier provider-based Genre Intelligence
    /// up to the file-source schema.
    ///
    /// The obsolete columns (provider <c>source</c>, <c>weight</c>,
    /// <c>authority_cap</c>, <c>confidence</c>, <c>sources_json</c>,
    /// <c>evidence_state</c>) are left in place. Dropping them would discard the
    /// record of what past resolutions believed, and SQLite column removal also
    /// rewrites every history table. Nothing writes them any more, so they cannot
    /// mislead a current result — they are simply history about history.
    ///
    /// Existing rows are preserved: the old evidence JSON is migrated into the
    /// snapshot table as a <c>legacy</c> stage so an interrupted run can still be
    /// read, and the settings flag is carried across so a user who had provider
    /// fallback enabled keeps the equivalent non-destructive behaviour.
    /// </summary>
    private static async Task MigrateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await AddColumnIfMissingAsync(connection, "personal_genre_mapping", "input_field", "TEXT", cancellationToken);
        await AddColumnIfMissingAsync(connection, "personal_genre_rule", "input_field", "TEXT", cancellationToken);
        await AddColumnIfMissingAsync(connection, "personal_genre_settings", "preserve_unmapped_tags", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await AddColumnIfMissingAsync(connection, "personal_genre_track", "preserved_json", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await AddColumnIfMissingAsync(connection, "personal_genre_track", "observations_json", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await AddColumnIfMissingAsync(connection, "personal_genre_track", "pre_autotag_json", "TEXT", cancellationToken);
        await AddColumnIfMissingAsync(connection, "personal_genre_track", "post_autotag_json", "TEXT", cancellationToken);
        await AddColumnIfMissingAsync(connection, "personal_genre_resolution_history", "preserved_json", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await AddColumnIfMissingAsync(connection, "personal_genre_classification_history", "origin_fields_json", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await AddColumnIfMissingAsync(connection, "personal_genre_observation_history", "origin", "TEXT NOT NULL DEFAULT 'post_platform'", cancellationToken);
        await AddColumnIfMissingAsync(connection, "personal_genre_settings", "normalize_genre_tags", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(connection, "personal_genre_settings", "genre_tag_alias_rules_json", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        // Added with no default on purpose. An existing row then reads as NULL,
        // which is the "never configured" state the migration expects to find, and
        // which is distinguishable from a user who deliberately emptied the list.
        // A default of '[]' here would collapse those two states into one.
        await AddColumnIfMissingAsync(connection, "personal_genre_settings", "genre_tag_block_list_json", "TEXT", cancellationToken);
        // Regional organization is additive metadata. An existing row reads as NULL,
        // which is unscoped, so no user-created term is hidden by this change and no
        // existing row has to be rewritten.
        await AddColumnIfMissingAsync(connection, "personal_genre_taxon", "regions_json", "TEXT", cancellationToken);

        // Only once the columns exist, so these cannot fail against a
        // provider-era table.
        await using (var command = new SqliteCommand("""
CREATE INDEX IF NOT EXISTS idx_personal_genre_mapping_match
    ON personal_genre_mapping (match_value, input_field, enabled, priority DESC);
CREATE INDEX IF NOT EXISTS idx_personal_genre_rule_match
    ON personal_genre_rule (match_value, input_field, enabled, priority DESC);
""", connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // A user who had unknown-provider-value preservation enabled keeps it: it
        // is the same intent, expressed without a provider.
        // `preserve_unmapped_tags` is added with DEFAULT 1, so a genuine "off"
        // and a never-set row are indistinguishable. Overwriting from the legacy
        // flag on every start would make the new setting impossible to turn off.
        // Carry across only while the new column still holds its default and the
        // user had explicitly disabled the old behaviour; afterwards the new
        // column is authoritative.
        await using (var command = new SqliteCommand(
            """
UPDATE personal_genre_settings
SET preserve_unmapped_tags = preserve_provider_fallback
WHERE preserve_unmapped_tags = 1 AND preserve_provider_fallback = 0;
""",
            connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // An in-flight run from the provider era still has its evidence under the
        // old column. Keep it readable as a legacy snapshot instead of dropping a
        // checkpoint a resumed run may still need.
        await using (var command = new SqliteCommand(
            """
INSERT OR IGNORE INTO personal_genre_snapshot (job_id, file_path, stage, snapshot_json, track_id, read_at_utc)
SELECT job_id, file_path, 'legacy', evidence_json, track_id, COALESCE(resolved_at_utc, CURRENT_TIMESTAMP)
FROM personal_genre_autotag
WHERE evidence_json IS NOT NULL AND evidence_json <> '[]';
""",
            connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
        // The read-then-ALTER ordering this helper used to spell out is handled
        // inside SqliteSchemaUtils (it probes through the parameterized
        // pragma_table_info form and validates both identifiers before altering).
        => await SqliteSchemaUtils.EnsureColumnAsync(connection, table, column, definition, cancellationToken);

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

    /// <summary>
    /// Rejects a custom taxon that would take a lookup key another taxon already owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The invariant is one normalized lookup key to exactly one semantic taxon,
    /// across the whole merged taxonomy. A value is matched by dropping punctuation and
    /// lowercasing, so "Thai Trap", "thai-trap" and "Thai-Trap" are one key, and a
    /// second taxon claiming any of them would make classification depend on precedence
    /// rather than on meaning.
    /// </para>
    /// <para>
    /// This is deliberately not settled at runtime by letting a custom term win. Two
    /// taxa answering to one value is a corrupted namespace, not a preference. The
    /// supported way to say something different about a value is a user rule, a mapping
    /// or a lock, each of which leaves the vocabulary itself intact.
    /// </para>
    /// <para>
    /// The protected side is the merged catalog, not the original built-in array.
    /// Checking only the built-ins would leave every researched term shadowable, which
    /// is the same blind spot that once made a guard read as if it were universal.
    /// </para>
    /// </remarks>
    private async Task ValidateCustomTaxonLookupAsync(
        PersonalGenreTaxon taxon,
        CancellationToken cancellationToken)
    {
        var candidates = LookupKeys(taxon);

        // A key already owned by the shared, read-only vocabulary. This is the
        // authoritative side of the invariant: the user cannot redefine what any term
        // DeezSpoTag ships means.
        foreach (var (key, owner) in ProtectedLookupKeys.Value)
        {
            if (candidates.ContainsKey(key))
            {
                ThrowCollision(candidates[key], owner, taxon.Id, "protected");
            }
        }

        // And a key another of the user's own taxa already owns. The taxon being
        // edited is skipped, so renaming, re-parenting and editing aliases remain
        // ordinary operations instead of colliding with the taxon's own previous state.
        var existing = await GetCustomTaxaAsync(cancellationToken);
        foreach (var other in existing.Where(item =>
            !string.Equals(item.Id, taxon.Id, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var (key, owner) in LookupKeys(other))
            {
                if (candidates.ContainsKey(key))
                {
                    ThrowCollision(candidates[key], other, taxon.Id, "custom");
                }
            }
        }
    }

    /// <summary>
    /// The lookup keys of the read-only vocabulary, computed once.
    /// </summary>
    private static readonly Lazy<Dictionary<string, PersonalGenreTaxon>> ProtectedLookupKeys =
        new(() => PersonalGenreCatalog.Default.Taxa
            .SelectMany(taxon => LookupKeys(taxon).Keys
                .Select(key => new KeyValuePair<string, PersonalGenreTaxon>(key, taxon)))
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal));

    /// <summary>
    /// The lookup keys a taxon answers to: its id, its display name and every alias,
    /// mapped to the value as the user would have typed it.
    /// </summary>
    private static Dictionary<string, string> LookupKeys(PersonalGenreTaxon taxon)
        => new[] { taxon.Id, taxon.Name }
            .Concat(taxon.Aliases ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => new
            {
                Key = PersonalGenreTaxonomy.Normalize(value),
                Value = value.Trim()
            })
            .Where(pair => pair.Key.Length > 0)
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);

    private static void ThrowCollision(
        string proposedValue,
        PersonalGenreTaxon existing,
        string proposedId,
        string existingKind)
    {
        throw new ArgumentException(
            $"A taxonomy term already resolves from '{proposedValue}': {existing.Name}. "
            + $"Custom taxon '{proposedId}' would share its lookup key with the {existingKind} term "
            + $"'{existing.Id}'. "
            + "Use a mapping, a user rule or a lock for an intentional override.",
            nameof(existing));
    }

    /// <summary>
    /// Whether an id belongs to a vocabulary term DeezSpoTag ships.
    /// </summary>
    /// <remarks>
    /// This deliberately asks the merged catalog rather than
    /// <see cref="PersonalGenreTaxonomy"/>. The built-in array alone knows only the
    /// original 156 terms, so a guard written against it would treat a researched term
    /// such as "rap" as unknown and let a user-defined term silently shadow it.
    /// Everything the user did not create must be refused, whichever vocabulary it came
    /// from.
    /// </remarks>
    private static bool IsShippedTaxonId(string? taxonId)
        => !string.IsNullOrWhiteSpace(taxonId)
           && PersonalGenreCatalog.Default.TryGetById(taxonId.Trim(), out _);

    private async Task ValidateTaxonAsync(
        string taxonId,
        CancellationToken cancellationToken)
    {
        var normalized = taxonId?.Trim() ?? string.Empty;
        if (IsShippedTaxonId(normalized))
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
            "scene" => PersonalGenreTaxonKind.Scene,
            "language" => PersonalGenreTaxonKind.Language,
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
        command.Parameters.AddWithValue("inputField", (object?)mapping.InputField?.ToString().ToLowerInvariant() ?? DBNull.Value);
        command.Parameters.AddWithValue("priority", mapping.Priority);
        command.Parameters.AddWithValue("enabled", mapping.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("action", mapping.Action.ToString().ToLowerInvariant());
    }

    private static void BindRule(SqliteCommand command, PersonalGenreRule rule)
    {
        command.Parameters.AddWithValue("matchValue", rule.MatchValue);
        command.Parameters.AddWithValue("targetTaxonId", rule.TargetTaxonId);
        command.Parameters.AddWithValue("inputField", (object?)rule.InputField?.ToString().ToLowerInvariant() ?? DBNull.Value);
        command.Parameters.AddWithValue("priority", rule.Priority);
        command.Parameters.AddWithValue("enabled", rule.Enabled ? 1 : 0);
    }

    /// <summary>
    /// Reads a stored input-field filter. A value left over from a provider-era
    /// row (a source name such as "audiomack") is not a file field, so it is
    /// discarded and the row becomes unconditional rather than silently never
    /// matching.
    /// </summary>
    private static PersonalGenreTaxonKind? TryParseInputField(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : Enum.TryParse<PersonalGenreTaxonKind>(value.Trim(), ignoreCase: true, out var parsed)
                ? parsed
                : null;

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

    private static IReadOnlyList<GenreTagObservation> ParseObservations(string json)
        => JsonSerializer.Deserialize<List<GenreTagObservation>>(json) ?? [];

    private static IReadOnlyList<PreservedTagValue> ParsePreserved(string json)
        => JsonSerializer.Deserialize<List<PreservedTagValue>>(json) ?? [];
}
