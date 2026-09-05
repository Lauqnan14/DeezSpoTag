using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Utils;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Library;

/// <summary>A user-defined group of artist names that all refer to the same artist.</summary>
public sealed record ArtistAliasGroupDto(
    long Id,
    string PreferredName,
    IReadOnlyList<string> Aliases,
    string CreatedAt,
    string UpdatedAt);

/// <summary>
/// Persists user-defined artist alias groups ("Artist's Aliases" in Settings) in
/// the shared library SQLite DB (tables artist_alias_group / artist_alias), so
/// every component — web, workers, download pipeline, tagging — resolves the
/// same preferred artist name. The preferred name is the display name the user
/// picked; every other name in the group is an alias that resolves to it.
/// Follows the ArtistLocationOverrideStore self-ensuring-table pattern.
/// </summary>
public sealed class ArtistAliasService
{
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex WhitespaceCollapseRegex = new(
        @"\s+",
        RegexOptions.Compiled,
        RegexTimeout);

    private readonly IConfiguration _configuration;
    private readonly ILogger<ArtistAliasService> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _tableEnsured;

    private volatile AliasSnapshot? _snapshot;
    private long _snapshotLoadedUtcTicks;

    public ArtistAliasService(IConfiguration configuration, ILogger<ArtistAliasService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(GetConnectionString());

    /// <summary>Collapse whitespace, Unicode-normalize, case-fold. Empty string for null/blank.</summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var cleaned = name.Normalize(NormalizationForm.FormC).Trim();
        cleaned = WhitespaceCollapseRegex.Replace(cleaned, " ");
        return cleaned.ToLowerInvariant();
    }

    /// <summary>Returns all alias groups ordered by preferred name.</summary>
    public async Task<IReadOnlyList<ArtistAliasGroupDto>> GetAllGroupsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return Array.Empty<ArtistAliasGroupDto>();
        }

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var groups = new List<(long Id, string Preferred, string CreatedAt, string UpdatedAt)>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT id, preferred_name, created_at, updated_at
FROM artist_alias_group
ORDER BY preferred_name COLLATE NOCASE;";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    groups.Add((
                        reader.GetInt64(0),
                        reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
                }
            }

            var aliasesByGroup = new Dictionary<long, List<string>>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT group_id, name
FROM artist_alias
ORDER BY name COLLATE NOCASE;";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var groupId = reader.GetInt64(0);
                    if (!aliasesByGroup.TryGetValue(groupId, out var list))
                    {
                        list = new List<string>();
                        aliasesByGroup[groupId] = list;
                    }

                    list.Add(reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
                }
            }

            return groups
                .Select(group => new ArtistAliasGroupDto(
                    group.Id,
                    group.Preferred,
                    aliasesByGroup.TryGetValue(group.Id, out var aliases)
                        ? aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)).ToList()
                        : new List<string>(),
                    group.CreatedAt,
                    group.UpdatedAt))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Artist alias group listing failed");
            return Array.Empty<ArtistAliasGroupDto>();
        }
    }

    /// <summary>
    /// Saves a group (create or update) and absorbs any existing groups that
    /// share a name with the input, so merging two groups is a single save.
    /// Every non-preferred name — including absorbed groups' preferred names —
    /// becomes an alias. Throws ArgumentException on invalid input.
    /// </summary>
    public async Task<ArtistAliasGroupDto> SaveGroupAsync(
        string? preferredName,
        IEnumerable<string>? aliases,
        CancellationToken cancellationToken = default)
    {
        var preferred = (preferredName ?? string.Empty).Trim();
        if (preferred.Length == 0)
        {
            throw new ArgumentException("A preferred artist name is required.", nameof(preferredName));
        }

        var inputNames = new List<string> { preferred };
        foreach (var alias in aliases ?? Enumerable.Empty<string>())
        {
            var trimmed = (alias ?? string.Empty).Trim();
            if (trimmed.Length > 0)
            {
                inputNames.Add(trimmed);
            }
        }

        // Distinct by normalized name, keep first occurrence's display form.
        var distinctNames = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in inputNames)
        {
            var normalized = NormalizeName(name);
            if (normalized.Length == 0 || !seen.Add(normalized))
            {
                continue;
            }

            distinctNames.Add(name);
        }

        if (distinctNames.Count < 2)
        {
            throw new ArgumentException("A merge group needs at least two distinct artist names.", nameof(aliases));
        }

        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Library DB is not configured; artist aliases cannot be saved.");
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        long groupId;
        try
        {
            // Absorb every existing group that shares any normalized name with
            // the input set (this covers updating a group and merging groups).
            // Matching happens in code: an input name can collide either with a
            // group's preferred name or with one of its alias rows.
            var absorbedNames = new List<string>();
            var absorbedGroupIds = new List<long>();
            var inputNormalized = distinctNames
                .Select(NormalizeName)
                .ToHashSet(StringComparer.Ordinal);

            var existingGroups = new List<(long Id, string Preferred)>();
            await using (var selectCommand = connection.CreateCommand())
            {
                selectCommand.Transaction = transaction;
                selectCommand.CommandText = "SELECT id, preferred_name FROM artist_alias_group;";
                await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    existingGroups.Add((reader.GetInt64(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
                }
            }

            var aliasesByGroup = new Dictionary<long, List<(string Name, string Normalized)>>();
            await using (var selectCommand = connection.CreateCommand())
            {
                selectCommand.Transaction = transaction;
                selectCommand.CommandText = "SELECT group_id, name, normalized_name FROM artist_alias;";
                await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var aliasGroupId = reader.GetInt64(0);
                    var displayName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    var normalized = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    if (!aliasesByGroup.TryGetValue(aliasGroupId, out var list))
                    {
                        list = new List<(string, string)>();
                        aliasesByGroup[aliasGroupId] = list;
                    }

                    list.Add((displayName, normalized));
                }
            }

            foreach (var existingGroup in existingGroups)
            {
                var existingPreferredNormalized = NormalizeName(existingGroup.Preferred);
                var existingAliases = aliasesByGroup.TryGetValue(existingGroup.Id, out var rows)
                    ? rows
                    : new List<(string Name, string Normalized)>();
                var sharesName = (existingPreferredNormalized.Length > 0 && inputNormalized.Contains(existingPreferredNormalized))
                    || existingAliases.Any(row => row.Normalized.Length > 0 && inputNormalized.Contains(row.Normalized));
                if (sharesName)
                {
                    absorbedGroupIds.Add(existingGroup.Id);
                    absorbedNames.Add(existingGroup.Preferred);
                    // Preserve every alias of the absorbed group under the new group.
                    foreach (var row in existingAliases)
                    {
                        absorbedNames.Add(row.Name);
                    }
                }
            }

            foreach (var absorbedGroupId in absorbedGroupIds)
            {
                await using var deleteAliases = connection.CreateCommand();
                deleteAliases.Transaction = transaction;
                deleteAliases.CommandText = "DELETE FROM artist_alias WHERE group_id = $group_id;";
                deleteAliases.Parameters.AddWithValue("$group_id", absorbedGroupId);
                await deleteAliases.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await using var deleteGroup = connection.CreateCommand();
                deleteGroup.Transaction = transaction;
                deleteGroup.CommandText = "DELETE FROM artist_alias_group WHERE id = $group_id;";
                deleteGroup.Parameters.AddWithValue("$group_id", absorbedGroupId);
                await deleteGroup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Also absorb names that only exist as alias rows of groups we did
            // not match above (paranoia; the group match should cover them).
            var finalNames = new List<string>(distinctNames);
            var finalSeen = new HashSet<string>(inputNormalized, StringComparer.Ordinal);
            foreach (var absorbedName in absorbedNames)
            {
                var normalized = NormalizeName(absorbedName);
                if (normalized.Length > 0 && finalSeen.Add(normalized))
                {
                    finalNames.Add(absorbedName);
                }
            }

            var nowUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            await using var insertGroup = connection.CreateCommand();
            insertGroup.Transaction = transaction;
            insertGroup.CommandText = @"
INSERT INTO artist_alias_group (preferred_name, created_at, updated_at)
VALUES ($preferred_name, $now, $now);
SELECT last_insert_rowid();";
            insertGroup.Parameters.AddWithValue("$preferred_name", preferred);
            insertGroup.Parameters.AddWithValue("$now", nowUtc);
            groupId = Convert.ToInt64(await insertGroup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

            foreach (var name in finalNames)
            {
                var normalized = NormalizeName(name);
                if (normalized.Length == 0 || string.Equals(normalized, NormalizeName(preferred), StringComparison.Ordinal))
                {
                    continue;
                }

                await using var insertAlias = connection.CreateCommand();
                insertAlias.Transaction = transaction;
                insertAlias.CommandText = @"
INSERT INTO artist_alias (group_id, name, normalized_name)
VALUES ($group_id, $name, $normalized);";
                insertAlias.Parameters.AddWithValue("$group_id", groupId);
                insertAlias.Parameters.AddWithValue("$name", name);
                insertAlias.Parameters.AddWithValue("$normalized", normalized);
                await insertAlias.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ArgumentException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogWarning(ex, "Artist alias group save failed");
            throw new InvalidOperationException("Saving the artist alias group failed. Check that the library DB is writable.", ex);
        }

        InvalidateCache();
        var saved = (await GetAllGroupsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(group => group.Id == groupId);
        return saved ?? new ArtistAliasGroupDto(groupId, preferred, new List<string>(), string.Empty, string.Empty);
    }

    /// <summary>
    /// Deletes a group (un-merge). Stops future alias resolution; files already
    /// merged are NOT split back. Returns false when the group was not found.
    /// </summary>
    public async Task<bool> DeleteGroupAsync(long groupId, CancellationToken cancellationToken = default)
    {
        if (groupId <= 0)
        {
            return false;
        }

        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        long deleted;
        try
        {
            await using var deleteAliases = connection.CreateCommand();
            deleteAliases.Transaction = transaction;
            deleteAliases.CommandText = "DELETE FROM artist_alias WHERE group_id = $group_id;";
            deleteAliases.Parameters.AddWithValue("$group_id", groupId);
            await deleteAliases.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var deleteGroup = connection.CreateCommand();
            deleteGroup.Transaction = transaction;
            deleteGroup.CommandText = "DELETE FROM artist_alias_group WHERE id = $group_id;";
            deleteGroup.Parameters.AddWithValue("$group_id", groupId);
            deleted = await deleteGroup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogWarning(ex, "Artist alias group delete failed for {GroupId}", groupId);
            throw new InvalidOperationException("Deleting the artist alias group failed.", ex);
        }

        InvalidateCache();
        return deleted > 0;
    }

    /// <summary>
    /// Normalized alias name → preferred display name, from the cached snapshot
    /// (fresh after TTL or any write through this instance).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetAliasMapAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.AliasToPreferred;
    }

    /// <summary>
    /// Synchronous resolution through the cached snapshot: returns the preferred
    /// display name when <paramref name="name"/> is a known alias (or a
    /// differently-cased preferred name), otherwise the trimmed input.
    /// </summary>
    public string ResolvePreferred(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        var snapshot = _snapshot;
        if (snapshot == null || DateTimeOffset.UtcNow.Ticks - _snapshotLoadedUtcTicks > SnapshotTtl.Ticks)
        {
            // Fire-and-forget refresh; resolve against whatever we have (empty
            // on first call before any async warm-up — callers that can await
            // should use ResolvePreferredAsync first).
            _ = WarmUpAsync(CancellationToken.None);
            if (snapshot == null)
            {
                return trimmed;
            }
        }

        var normalized = NormalizeName(trimmed);
        return snapshot.AliasToPreferred.TryGetValue(normalized, out var preferred)
            ? preferred
            : trimmed;
    }

    /// <summary>Awaitable variant of <see cref="ResolvePreferred"/> that guarantees a loaded snapshot.</summary>
    public async Task<string> ResolvePreferredAsync(string? name, CancellationToken cancellationToken = default)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var normalized = NormalizeName(trimmed);
        return snapshot.AliasToPreferred.TryGetValue(normalized, out var preferred)
            ? preferred
            : trimmed;
    }

    /// <summary>
    /// Rewrites a combined artist credit (e.g. "A feat. B"): every artist part
    /// that is a known alias is replaced with its preferred name; parts and
    /// separators are preserved. Returns the input trimmed when nothing changes.
    /// </summary>
    public async Task<string> ResolveCreditAsync(string? credit, CancellationToken cancellationToken = default)
    {
        var trimmed = (credit ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return RewriteCreditWithSnapshot(snapshot, trimmed);
    }

    /// <summary>
    /// Synchronous credit rewrite through the cached snapshot. Returns the
    /// trimmed input when the snapshot has not loaded yet (it warms up in the
    /// background); use <see cref="ResolveCreditAsync"/> when awaiting is fine.
    /// </summary>
    public string ResolveCredit(string? credit)
    {
        var trimmed = (credit ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        var snapshot = _snapshot;
        if (snapshot == null)
        {
            _ = WarmUpAsync(CancellationToken.None);
            return trimmed;
        }

        return RewriteCreditWithSnapshot(snapshot, trimmed);
    }

    private static string RewriteCreditWithSnapshot(AliasSnapshot snapshot, string trimmed)
    {
        if (snapshot.AliasToPreferred.Count == 0)
        {
            return trimmed;
        }

        var changed = false;
        var parts = Core.Utils.ArtistNameNormalizer.SplitCombinedNameForRewrite(trimmed, out var separators);
        var rewritten = new List<string>(parts.Count);
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            var replacement = MatchAliasInPart(snapshot.AliasToPreferred, part);
            if (replacement == null)
            {
                rewritten.Add(part);
            }
            else
            {
                rewritten.Add(replacement.Value.Rewritten);
                changed |= !string.Equals(replacement.Value.Rewritten, part, StringComparison.Ordinal);
            }
        }

        if (!changed)
        {
            return trimmed;
        }

        var builder = new StringBuilder();
        for (var index = 0; index < rewritten.Count; index++)
        {
            if (index > 0 && index - 1 < separators.Count)
            {
                builder.Append(separators[index - 1]);
            }

            builder.Append(rewritten[index]);
        }

        return builder.ToString().Trim();
    }

    public void InvalidateCache()
    {
        _snapshot = null;
        _snapshotLoadedUtcTicks = 0;
    }

    private static readonly char[] PartTrimChars = { ' ', '.', ',', '(', ')', '[', ']', '-', '_', '&', '"', '\'' };

    /// <summary>
    /// Matches one credit part against the alias map. Tries the whole part, then
    /// a punctuation-trimmed variant (so "Alias)" in a title still matches).
    /// Returns null when the part is not an alias; otherwise the rewritten part.
    /// </summary>
    private static (string Rewritten, string MatchedPreferred)? MatchAliasInPart(
        IReadOnlyDictionary<string, string> aliasToPreferred,
        string part)
    {
        var direct = NormalizeName(part);
        if (direct.Length > 0 && aliasToPreferred.TryGetValue(direct, out var directPreferred))
        {
            return (directPreferred, directPreferred);
        }

        var trimmedPart = part.Trim(PartTrimChars);
        if (trimmedPart.Length == 0)
        {
            return null;
        }

        var trimmedNormalized = NormalizeName(trimmedPart);
        if (trimmedNormalized.Length == 0
            || string.Equals(trimmedNormalized, direct, StringComparison.Ordinal)
            || !aliasToPreferred.TryGetValue(trimmedNormalized, out var preferred))
        {
            return null;
        }

        // Replace only the trimmed token inside the part, keeping the
        // surrounding punctuation ("(" / ")") exactly where it was.
        var rewritten = part.Replace(
            trimmedPart,
            preferred,
            StringComparison.OrdinalIgnoreCase);
        return (rewritten, preferred);
    }

    private async Task<AliasSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = _snapshot;
        if (snapshot != null
            && DateTimeOffset.UtcNow.Ticks - _snapshotLoadedUtcTicks <= SnapshotTtl.Ticks)
        {
            return snapshot;
        }

        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var connectionString = GetConnectionString();
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            try
            {
                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT a.normalized_name, g.preferred_name
FROM artist_alias a
JOIN artist_alias_group g ON g.id = a.group_id;";
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var normalized = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                        var preferred = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                        if (normalized.Length > 0 && preferred.Length > 0)
                        {
                            map[normalized] = preferred;
                        }
                    }
                }

                // Map alternate casings of the preferred name to itself so
                // "ayra starr" resolves to the stored display form too.
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT preferred_name FROM artist_alias_group;";
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var preferred = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                        var normalized = NormalizeName(preferred);
                        if (normalized.Length > 0 && preferred.Length > 0)
                        {
                            map[normalized] = preferred;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Artist alias snapshot load failed");
            }
        }

        snapshot = new AliasSnapshot(map);
        _snapshot = snapshot;
        _snapshotLoadedUtcTicks = DateTimeOffset.UtcNow.Ticks;
        return snapshot;
    }

    private async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal; sync resolution falls back to the input name.
        }
    }

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_tableEnsured)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tableEnsured)
            {
                return;
            }

            var connectionString = GetConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return;
            }

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE IF NOT EXISTS artist_alias_group (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    preferred_name TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS artist_alias (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    group_id BIGINT NOT NULL REFERENCES artist_alias_group(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    normalized_name TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_artist_alias_group ON artist_alias (group_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_artist_alias_normalized ON artist_alias (normalized_name);";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _tableEnsured = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Artist alias table ensure failed");
        }
        finally
        {
            _initLock.Release();
        }
    }

    private string? GetConnectionString()
    {
        var rawConnection = Environment.GetEnvironmentVariable("LIBRARY_DB")
            ?? _configuration.GetConnectionString("Library");
        return SqliteConnectionStringResolver.Resolve(rawConnection, "deezspotag.db");
    }

    private sealed record AliasSnapshot(IReadOnlyDictionary<string, string> AliasToPreferred);
}
