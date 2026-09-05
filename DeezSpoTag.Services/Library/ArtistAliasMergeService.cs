using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Utils;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Library;

/// <summary>Per-file outcome of a merge.</summary>
public sealed record ArtistAliasMergeFileResult(
    string SourcePath,
    string DestinationPath,
    bool Moved,
    bool ConflictResolved,
    string? Error = null);

/// <summary>Aggregate outcome of merging one alias group on disk and in the library DB.</summary>
public sealed record ArtistAliasMergeResult(
    long GroupId,
    string PreferredName,
    int FilesScanned,
    int FilesMoved,
    int ConflictsResolved,
    int ArtistRowsMerged,
    int TrackRowsRewritten,
    int ArtworkFilesMoved,
    IReadOnlyList<string> AffectedFilePaths,
    IReadOnlyList<long> AffectedFolderIds,
    IReadOnlyList<string> Errors);

/// <summary>
/// Executes a user-confirmed artist alias merge:
/// 1. re-points library DB artist/album rows to the preferred artist,
/// 2. rewrites artist credits and "feat." title strings in track rows,
/// 3. moves/renames the affected files on disk (folder merge + name rewrite),
/// 4. updates audio_file paths, and
/// 5. reports the affected final file paths so a targeted enhancement run can
///    re-tag the embedded metadata.
/// Affected files are resolved from the library DB only — no folder scanning.
/// </summary>
public sealed class ArtistAliasMergeService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ArtistAliasMergeService> _logger;
    private readonly ArtistAliasService _aliasService;
    private readonly DeezSpoTagSettingsService _settingsService;

    public ArtistAliasMergeService(
        IConfiguration configuration,
        ILogger<ArtistAliasMergeService> logger,
        ArtistAliasService aliasService,
        DeezSpoTagSettingsService settingsService)
    {
        _configuration = configuration;
        _logger = logger;
        _aliasService = aliasService;
        _settingsService = settingsService;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(GetConnectionString());

    public async Task<ArtistAliasMergeResult> MergeGroupAsync(long groupId, CancellationToken cancellationToken = default)
    {
        var group = (await _aliasService.GetAllGroupsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.Id == groupId)
            ?? throw new InvalidOperationException($"Artist alias group {groupId} was not found.");

        var aliasNormalized = group.Aliases
            .Select(ArtistAliasService.NormalizeName)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var preferredNormalized = ArtistAliasService.NormalizeName(group.PreferredName);
        var settings = _settingsService.LoadSettings() ?? new DeezSpoTagSettings();

        var errors = new List<string>();
        var affectedPaths = new List<string>();
        var affectedFolderIds = new HashSet<long>();
        var filesScanned = 0;
        var filesMoved = 0;
        var conflictsResolved = 0;
        var artistRowsMerged = 0;
        var trackRowsRewritten = 0;
        var artworkFilesMoved = 0;
        var movedPathMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var aliasArtistDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Library DB is not configured; merge cannot run.");
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // ---- 1. Resolve affected DB rows and files (read-only) -------------
        var (preferredArtistId, absorbedArtistIds) = await ResolveArtistRowsAsync(
            connection, group.PreferredName, aliasNormalized, cancellationToken).ConfigureAwait(false);
        var mainArtistFileRows = await ResolveMainArtistFilesAsync(
            connection, absorbedArtistIds.Append(preferredArtistId).ToList(), cancellationToken).ConfigureAwait(false);
        var featuredFileRows = await ResolveFeaturedFilesAsync(
            connection, aliasNormalized, cancellationToken).ConfigureAwait(false);
        var candidateTrackRows = await ResolveCandidateTrackRowsAsync(
            connection, aliasNormalized, cancellationToken).ConfigureAwait(false);

        var fileRows = mainArtistFileRows
            .Concat(featuredFileRows)
            .GroupBy(row => row.Id)
            .Select(groupBy => groupBy.First())
            .ToList();

        // ---- 2. DB transaction: re-point artists + rewrite track strings ---
        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (absorbedArtistIds.Count > 0)
                {
                    // Read the absorbed artists' artwork references before the
                    // rows are deleted, so the preferred artist inherits them.
                    var absorbedImagePaths = new List<(string? Image, string? Background)>();
                    await using var readImages = connection.CreateCommand();
                    readImages.Transaction = transaction;
                    readImages.CommandText = @"
SELECT preferred_image_path, preferred_background_path
FROM artist
WHERE id IN (" + BuildPlaceholders(absorbedArtistIds.Count, "a") + @");";
                    for (var index = 0; index < absorbedArtistIds.Count; index++)
                    {
                        readImages.Parameters.AddWithValue($"$a{index}", absorbedArtistIds[index]);
                    }

                    await using (var imageReader = await readImages.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (await imageReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            absorbedImagePaths.Add((
                                imageReader.IsDBNull(0) ? null : imageReader.GetString(0),
                                imageReader.IsDBNull(1) ? null : imageReader.GetString(1)));
                        }
                    }

                    await using var rePoint = connection.CreateCommand();
                    rePoint.Transaction = transaction;
                    rePoint.CommandText = @"
UPDATE album
SET artist_id = $preferred, updated_at = CURRENT_TIMESTAMP
WHERE artist_id IN (" + BuildPlaceholders(absorbedArtistIds.Count, "a") + @");";
                    rePoint.Parameters.AddWithValue("$preferred", preferredArtistId);
                    for (var index = 0; index < absorbedArtistIds.Count; index++)
                    {
                        rePoint.Parameters.AddWithValue($"$a{index}", absorbedArtistIds[index]);
                    }

                    artistRowsMerged = await rePoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                    await using var deleteArtists = connection.CreateCommand();
                    deleteArtists.Transaction = transaction;
                    deleteArtists.CommandText = @"
DELETE FROM artist
WHERE id IN (" + BuildPlaceholders(absorbedArtistIds.Count, "a") + @");";
                    for (var index = 0; index < absorbedArtistIds.Count; index++)
                    {
                        deleteArtists.Parameters.AddWithValue($"$a{index}", absorbedArtistIds[index]);
                    }

                    await deleteArtists.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                    // Inherit artwork references from the absorbed rows when
                    // the preferred artist does not have its own yet.
                    var inheritedImage = absorbedImagePaths
                        .Select(pair => pair.Image)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                    var inheritedBackground = absorbedImagePaths
                        .Select(pair => pair.Background)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                    if (inheritedImage != null || inheritedBackground != null)
                    {
                        await using var carryImages = connection.CreateCommand();
                        carryImages.Transaction = transaction;
                        carryImages.CommandText = @"
UPDATE artist
SET preferred_image_path = COALESCE(NULLIF(preferred_image_path, ''), $image),
    preferred_background_path = COALESCE(NULLIF(preferred_background_path, ''), $background)
WHERE id = $id
  AND ((NULLIF(preferred_image_path, '') IS NULL AND $image IS NOT NULL)
    OR (NULLIF(preferred_background_path, '') IS NULL AND $background IS NOT NULL));";
                        carryImages.Parameters.AddWithValue("$image", (object?)inheritedImage ?? DBNull.Value);
                        carryImages.Parameters.AddWithValue("$background", (object?)inheritedBackground ?? DBNull.Value);
                        carryImages.Parameters.AddWithValue("$id", preferredArtistId);
                        await carryImages.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                // Enforce the preferred display name on the surviving artist row.
                await using var normalizeName = connection.CreateCommand();
                normalizeName.Transaction = transaction;
                normalizeName.CommandText = @"
UPDATE artist
SET name = $preferred, updated_at = CURRENT_TIMESTAMP
WHERE id = $id AND name <> $preferred;";
                normalizeName.Parameters.AddWithValue("$preferred", group.PreferredName);
                normalizeName.Parameters.AddWithValue("$id", preferredArtistId);
                await normalizeName.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                // Rewrite tag_artist / tag_album_artist / title strings.
                foreach (var trackRow in candidateTrackRows)
                {
                    var newArtist = RewriteCredit(trackRow.TagArtist, aliasNormalized, group.PreferredName);
                    var newAlbumArtist = RewriteCredit(trackRow.TagAlbumArtist, aliasNormalized, group.PreferredName);
                    var newTitle = RewriteCredit(trackRow.Title, aliasNormalized, group.PreferredName);
                    if (string.Equals(newArtist, trackRow.TagArtist, StringComparison.Ordinal)
                        && string.Equals(newAlbumArtist, trackRow.TagAlbumArtist, StringComparison.Ordinal)
                        && string.Equals(newTitle, trackRow.Title, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    await using var updateTrack = connection.CreateCommand();
                    updateTrack.Transaction = transaction;
                    updateTrack.CommandText = @"
UPDATE track
SET tag_artist = $tag_artist,
    tag_album_artist = $tag_album_artist,
    title = $title,
    updated_at = CURRENT_TIMESTAMP
WHERE id = $id;";
                    updateTrack.Parameters.AddWithValue("$tag_artist", (object?)newArtist ?? DBNull.Value);
                    updateTrack.Parameters.AddWithValue("$tag_album_artist", (object?)newAlbumArtist ?? DBNull.Value);
                    updateTrack.Parameters.AddWithValue("$title", (object?)newTitle ?? DBNull.Value);
                    updateTrack.Parameters.AddWithValue("$id", trackRow.Id);
                    await updateTrack.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    trackRowsRewritten++;
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                _logger.LogWarning(ex, "Artist alias merge DB phase failed for group {GroupId}", groupId);
                throw new InvalidOperationException("The library DB phase of the merge failed; no files were moved.", ex);
            }
        }

        // ---- 3. Filesystem pass -------------------------------------------
        var artistFolderName = BuildArtistFolderName(group.PreferredName, settings);
        var createArtistFolder = settings.CreateArtistFolder;
        foreach (var fileRow in fileRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            filesScanned++;
            affectedFolderIds.Add(fileRow.FolderId);

            var sourceArtistDir = createArtistFolder ? GetSourceArtistDirectory(fileRow) : null;
            if (sourceArtistDir != null
                && aliasNormalized.Contains(ArtistAliasService.NormalizeName(Path.GetFileName(sourceArtistDir))))
            {
                aliasArtistDirs.Add(sourceArtistDir);
            }

            try
            {
                var outcome = await MoveFileAsync(
                    fileRow, artistFolderName, createArtistFolder, aliasNormalized, group.PreferredName,
                    cancellationToken).ConfigureAwait(false);
                if (outcome == null)
                {
                    affectedPaths.Add(fileRow.Path);
                    continue;
                }

                if (outcome.Moved)
                {
                    filesMoved++;
                    movedPathMap[fileRow.Path] = outcome.DestinationPath;
                    if (outcome.ConflictResolved)
                    {
                        conflictsResolved++;
                    }

                    await UpdateAudioFilePathAsync(
                        connection, fileRow.Id, fileRow.Path, outcome.DestinationPath, fileRow.RootPath,
                        cancellationToken).ConfigureAwait(false);
                    affectedPaths.Add(outcome.DestinationPath);
                }
                else
                {
                    affectedPaths.Add(fileRow.Path);
                }

                if (!string.IsNullOrWhiteSpace(outcome.Error))
                {
                    errors.Add(outcome.Error);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Artist alias merge file move failed for {Path}", fileRow.Path);
                errors.Add($"{fileRow.Path}: {ex.Message}");
                affectedPaths.Add(fileRow.Path);
            }
        }

        // ---- 3b. Artwork / sidecar pass -------------------------------------
        // Artist art (artist.jpg, fanart.jpg, …), album covers, and lyric
        // sidecars are not audio_file rows, so the audio pass leaves them
        // behind. Move any leftover non-audio file from a pure-alias artist
        // folder into the preferred artist folder (same relative subpath),
        // then clean up the emptied alias tree.
        foreach (var aliasArtistDir in aliasArtistDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(aliasArtistDir))
            {
                continue;
            }

            var root = Path.GetDirectoryName(aliasArtistDir)!;
            var preferredArtistDir = Path.Combine(root, artistFolderName);
            foreach (var leftover in Directory.EnumerateFiles(aliasArtistDir, "*", SearchOption.AllDirectories))
            {
                if (IsAudioExtension(leftover))
                {
                    // Audio that the library DB does not know about is never
                    // touched or deleted — reported instead.
                    errors.Add($"Untracked audio file left in place: {leftover}");
                    continue;
                }

                try
                {
                    var destination = Path.Combine(preferredArtistDir, Path.GetRelativePath(aliasArtistDir, leftover));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (File.Exists(destination))
                    {
                        destination = BuildAvailablePath(destination);
                    }

                    File.Move(leftover, destination);
                    movedPathMap[Path.GetFullPath(leftover)] = Path.GetFullPath(destination);
                    artworkFilesMoved++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Artist alias merge could not move artwork {Path}", leftover);
                    errors.Add($"{leftover}: {ex.Message}");
                }
            }

            await CleanUpDirectoryTreeIfEmptyAsync(aliasArtistDir, root, cancellationToken).ConfigureAwait(false);
        }

        // ---- 3c. Repoint DB references that pointed at moved paths ----------
        foreach (var pair in movedPathMap)
        {
            await using var updateRefs = connection.CreateCommand();
            updateRefs.CommandText = @"
UPDATE artist SET preferred_image_path = $new WHERE preferred_image_path = $old;
UPDATE artist SET preferred_background_path = $new WHERE preferred_background_path = $old;
UPDATE album SET preferred_cover_path = $new WHERE preferred_cover_path = $old;";
            updateRefs.Parameters.AddWithValue("$new", pair.Value);
            updateRefs.Parameters.AddWithValue("$old", pair.Key);
            await updateRefs.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Artist alias merge for group {GroupId} ({Preferred}): scanned {Scanned}, moved {Moved}, artwork/sidecars {Artwork}, conflicts {Conflicts}, artist rows merged {Artists}, track rows rewritten {Tracks}.",
            groupId, group.PreferredName, filesScanned, filesMoved, artworkFilesMoved, conflictsResolved, artistRowsMerged, trackRowsRewritten);

        return new ArtistAliasMergeResult(
            groupId,
            group.PreferredName,
            filesScanned,
            filesMoved,
            conflictsResolved,
            artistRowsMerged,
            trackRowsRewritten,
            artworkFilesMoved,
            affectedPaths,
            affectedFolderIds.ToList(),
            errors);
    }

    // ---- DB resolution -----------------------------------------------------

    private async Task<(long PreferredArtistId, List<long> AbsorbedArtistIds)> ResolveArtistRowsAsync(
        SqliteConnection connection,
        string preferredName,
        IReadOnlySet<string> aliasNormalized,
        CancellationToken cancellationToken)
    {
        var preferredNormalized = ArtistAliasService.NormalizeName(preferredName);
        var artists = new List<(long Id, string Name)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name FROM artist;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                artists.Add((reader.GetInt64(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
            }
        }

        long preferredArtistId = 0;
        var absorbed = new List<long>();
        foreach (var artist in artists)
        {
            var normalized = ArtistAliasService.NormalizeName(artist.Name);
            if (normalized.Length == 0)
            {
                continue;
            }

            if (string.Equals(normalized, preferredNormalized, StringComparison.Ordinal))
            {
                preferredArtistId = artist.Id;
            }
            else if (aliasNormalized.Contains(normalized))
            {
                absorbed.Add(artist.Id);
            }
        }

        if (preferredArtistId == 0)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = @"
INSERT INTO artist (name) VALUES ($name);
SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$name", preferredName);
            preferredArtistId = Convert.ToInt64(
                await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        return (preferredArtistId, absorbed);
    }

    private sealed record AudioFileRow(long Id, string Path, long FolderId, string RootPath, int? QualityRank);

    private async Task<List<AudioFileRow>> ResolveMainArtistFilesAsync(
        SqliteConnection connection,
        IReadOnlyList<long> artistIds,
        CancellationToken cancellationToken)
    {
        if (artistIds.Count == 0)
        {
            return new List<AudioFileRow>();
        }

        const string sql = @"
SELECT af.id, af.path, af.folder_id, f.root_path, af.quality_rank
FROM audio_file af
JOIN folder f ON f.id = af.folder_id
WHERE af.id IN (
    SELECT tl.audio_file_id
    FROM track_local tl
    JOIN track t ON t.id = tl.track_id
    JOIN album al ON al.id = t.album_id
    WHERE al.artist_id IN (" + "{ARTISTS}" + @")
);";
        return await ExecuteFileQueryAsync(connection, sql, artistIds, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<AudioFileRow>> ResolveFeaturedFilesAsync(
        SqliteConnection connection,
        IReadOnlySet<string> aliasNormalized,
        CancellationToken cancellationToken)
    {
        if (aliasNormalized.Count == 0)
        {
            return new List<AudioFileRow>();
        }

        var conditions = new StringBuilder();
        var index = 0;
        foreach (var alias in aliasNormalized)
        {
            if (conditions.Length > 0)
            {
                conditions.Append(" OR ");
            }

            var pattern = $"%{EscapeLike(alias)}%";
            conditions.Append($"(t.tag_artist LIKE $like{index} ESCAPE '\\' OR t.tag_album_artist LIKE $like{index} ESCAPE '\\' OR t.title LIKE $like{index} ESCAPE '\\')");
            index++;
        }

        var sql = $@"
SELECT DISTINCT af.id, af.path, af.folder_id, f.root_path, af.quality_rank
FROM track t
JOIN track_local tl ON tl.track_id = t.id
JOIN audio_file af ON af.id = tl.audio_file_id
JOIN folder f ON f.id = af.folder_id
WHERE {conditions};";

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        index = 0;
        foreach (var alias in aliasNormalized)
        {
            command.Parameters.AddWithValue($"$like{index}", $"%{EscapeLike(alias)}%");
            index++;
        }

        var rows = new List<AudioFileRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadFileRow(reader));
        }

        return rows;
    }

    private sealed record TrackRow(long Id, string? TagArtist, string? TagAlbumArtist, string? Title);

    private async Task<List<TrackRow>> ResolveCandidateTrackRowsAsync(
        SqliteConnection connection,
        IReadOnlySet<string> aliasNormalized,
        CancellationToken cancellationToken)
    {
        if (aliasNormalized.Count == 0)
        {
            return new List<TrackRow>();
        }

        var conditions = new StringBuilder();
        var index = 0;
        foreach (var alias in aliasNormalized)
        {
            if (conditions.Length > 0)
            {
                conditions.Append(" OR ");
            }

            conditions.Append($"(IFNULL(tag_artist,'') LIKE $like{index} ESCAPE '\\' OR IFNULL(tag_album_artist,'') LIKE $like{index} ESCAPE '\\' OR IFNULL(title,'') LIKE $like{index} ESCAPE '\\')");
            index++;
        }

        var sql = $"SELECT id, tag_artist, tag_album_artist, title FROM track WHERE {conditions};";
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        index = 0;
        foreach (var alias in aliasNormalized)
        {
            command.Parameters.AddWithValue($"$like{index}", $"%{EscapeLike(alias)}%");
            index++;
        }

        var rows = new List<TrackRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new TrackRow(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    private async Task<List<AudioFileRow>> ExecuteFileQueryAsync(
        SqliteConnection connection,
        string sqlTemplate,
        IReadOnlyList<long> artistIds,
        CancellationToken cancellationToken)
    {
        var sql = sqlTemplate.Replace(
            "{ARTISTS}",
            string.Join(", ", artistIds.Select((_, index) => $"$a{index}")));
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var index = 0; index < artistIds.Count; index++)
        {
            command.Parameters.AddWithValue($"$a{index}", artistIds[index]);
        }

        var rows = new List<AudioFileRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadFileRow(reader));
        }

        return rows;
    }

    private static AudioFileRow ReadFileRow(SqliteDataReader reader)
    {
        return new AudioFileRow(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4));
    }

    // ---- DB updates after a successful move --------------------------------

    private async Task UpdateAudioFilePathAsync(
        SqliteConnection connection,
        long audioFileId,
        string oldPath,
        string newPath,
        string rootPath,
        CancellationToken cancellationToken)
    {
        var relativePath = ComputeRelativePath(rootPath, newPath);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE audio_file
SET path = $path, relative_path = $relative, updated_at = CURRENT_TIMESTAMP
WHERE id = $id;";
        command.Parameters.AddWithValue("$path", newPath);
        command.Parameters.AddWithValue("$relative", relativePath);
        command.Parameters.AddWithValue("$id", audioFileId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Keep the Shazam cache's file pointer aligned with the moved file;
        // other path-bearing caches (media server metadata) are refreshed by
        // the next library scan.
        await using var shazam = connection.CreateCommand();
        shazam.CommandText = @"
UPDATE track_shazam_cache
SET file_path = $path
WHERE file_path IS NOT NULL AND file_path = $old;";
        shazam.Parameters.AddWithValue("$path", newPath);
        shazam.Parameters.AddWithValue("$old", oldPath);
        await shazam.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ComputeRelativePath(string rootPath, string filePath)
    {
        var root = rootPath ?? string.Empty;
        var full = Path.GetFullPath(filePath);
        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var relative = full.StartsWith(trimmedRoot, StringComparison.OrdinalIgnoreCase)
            ? full[trimmedRoot.Length..]
            : Path.GetFileName(full);
        return relative.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
    }

    // ---- Filesystem --------------------------------------------------------

    private async Task<ArtistAliasMergeFileResult?> MoveFileAsync(
        AudioFileRow fileRow,
        string artistFolderName,
        bool createArtistFolder,
        IReadOnlySet<string> aliasNormalized,
        string preferredName,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.GetFullPath(fileRow.Path);
        if (!File.Exists(sourcePath))
        {
            return new ArtistAliasMergeFileResult(sourcePath, sourcePath, false, false, null);
        }

        var root = Path.GetFullPath(fileRow.RootPath ?? string.Empty)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length == 0 || !sourcePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null; // File outside a library root: nothing to relocate.
        }

        var relative = sourcePath[(root.Length + 1)..];
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Length == 0)
        {
            return null;
        }

        var changed = false;
        if (createArtistFolder && segments.Length > 1)
        {
            var desired = artistFolderName;
            if (!string.Equals(segments[0], desired, StringComparison.Ordinal))
            {
                segments[0] = desired;
                changed = true;
            }
        }

        for (var index = 1; index < segments.Length; index++)
        {
            foreach (var alias in aliasNormalized)
            {
                if (segments[index].Contains(alias, StringComparison.OrdinalIgnoreCase))
                {
                    segments[index] = segments[index].Replace(alias, preferredName, StringComparison.OrdinalIgnoreCase);
                    changed = true;
                }
            }

            segments[index] = SanitizeSegment(segments[index]);
        }

        var destinationPath = Path.Combine(root, Path.Combine(segments));
        if (!changed || string.Equals(destinationPath, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return new ArtistAliasMergeFileResult(sourcePath, sourcePath, false, false, null);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        var conflictResolved = false;
        var finalDestination = destinationPath;
        if (File.Exists(finalDestination))
        {
            conflictResolved = true;
            var sourceRank = fileRow.QualityRank ?? AudioFileQualityRanker.EstimateRankFromExtension(sourcePath);
            var destinationRank = await GetStoredQualityRankAsync(finalDestination, cancellationToken).ConfigureAwait(false)
                ?? AudioFileQualityRanker.EstimateRankFromExtension(finalDestination);
            var sourceWins = (sourceRank ?? 0) >= (destinationRank ?? 0);
            if (sourceWins)
            {
                // Existing file loses the canonical name; move it aside first.
                var displaced = BuildAvailablePath(finalDestination);
                File.Move(finalDestination, displaced);
                _logger.LogInformation(
                    "Alias merge duplicate: existing {Existing} (rank {ExistingRank}) displaced to {Displaced} by {Incoming} (rank {IncomingRank}).",
                    finalDestination, destinationRank, displaced, sourcePath, sourceRank);
            }
            else
            {
                // Incoming file loses; keep it under a non-canonical name.
                finalDestination = BuildAvailablePath(finalDestination);
                _logger.LogInformation(
                    "Alias merge duplicate: incoming {Incoming} (rank {IncomingRank}) kept as {Final} because existing {Existing} (rank {ExistingRank}) is better.",
                    sourcePath, sourceRank, finalDestination, destinationPath, destinationRank);
            }
        }

        File.Move(sourcePath, finalDestination);
        await CleanUpEmptyDirectoriesAsync(Path.GetDirectoryName(sourcePath), root, cancellationToken).ConfigureAwait(false);

        return new ArtistAliasMergeFileResult(sourcePath, finalDestination, true, conflictResolved, null);
    }

    private async Task<int?> GetStoredQualityRankAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var connectionString = GetConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return null;
            }

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT quality_rank FROM audio_file WHERE path = $path LIMIT 1;";
            command.Parameters.AddWithValue("$path", path);
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result == null || result == DBNull.Value ? null : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string BuildAvailablePath(string occupiedPath)
    {
        var directory = Path.GetDirectoryName(occupiedPath)!;
        var fileName = Path.GetFileNameWithoutExtension(occupiedPath);
        var extension = Path.GetExtension(occupiedPath);
        for (var counter = 2; counter < 1000; counter++)
        {
            var candidate = Path.Combine(directory, $"{fileName} ({counter}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Could not find a free duplicate name for {occupiedPath}.");
    }

    private static Task CleanUpDirectoryTreeIfEmptyAsync(string directory, string root, CancellationToken cancellationToken)
    {
        CleanUpDirectoryTree(directory, root);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes `directory` and every empty sub-directory beneath it
    /// (bottom-up), stopping at the library root. Used after the leftover
    /// artwork pass so the emptied alias artist tree disappears.
    /// </summary>
    private static void CleanUpDirectoryTree(string directory, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full;
        try
        {
            full = Path.GetFullPath(directory);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return;
        }

        if (string.Equals(full, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || !full.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(full))
            {
                CleanUpDirectoryTree(sub, root);
            }

            if (!Directory.EnumerateFileSystemEntries(full).Any())
            {
                Directory.Delete(full, recursive: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leave whatever could not be deleted in place; logged upstream.
        }
    }

    private static Task CleanUpEmptyDirectoriesAsync(string? startDirectory, string root, CancellationToken cancellationToken)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = startDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var full = Path.GetFullPath(current);
            if (string.Equals(full, normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || !full.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                break; // Reached (or left) the library root — stop deleting.
            }

            try
            {
                if (Directory.EnumerateFileSystemEntries(full).Any())
                {
                    break;
                }

                Directory.Delete(full, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                break;
            }

            current = Path.GetDirectoryName(full);
        }

        return Task.CompletedTask;
    }

    // ---- Naming helpers ------------------------------------------------------

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".m4b", ".aac", ".ogg", ".opus", ".wav",
        ".wma", ".aiff", ".aif", ".alac", ".ape", ".dsf", ".dff"
    };

    private static bool IsAudioExtension(string path)
        => AudioExtensions.Contains(Path.GetExtension(path));

    /// <summary>The artist-level directory of a library file (root's first segment), or null.</summary>
    private static string? GetSourceArtistDirectory(AudioFileRow fileRow)
    {
        var sourcePath = Path.GetFullPath(fileRow.Path);
        var root = Path.GetFullPath(fileRow.RootPath ?? string.Empty)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length == 0
            || !sourcePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = sourcePath[(root.Length + 1)..];
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Length <= 1 ? null : Path.Combine(root, segments[0]);
    }

    private string BuildArtistFolderName(string preferredName, DeezSpoTagSettings settings)
    {
        var sanitized = SanitizeSegment(preferredName);
        var template = settings.ArtistNameTemplate;
        if (string.IsNullOrWhiteSpace(template) || !template.Contains("%artist%", StringComparison.OrdinalIgnoreCase))
        {
            return sanitized;
        }

        var built = template.Replace("%artist%", sanitized, StringComparison.OrdinalIgnoreCase);
        return built.Contains('%') ? sanitized : built;
    }

    private static string SanitizeSegment(string? segment)
    {
        var value = segment ?? string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? "_" : cleaned.TrimEnd('.');
    }

    // ---- Credit rewriting ----------------------------------------------------

    /// <summary>
    /// Rewrites a credit/title string so every alias part becomes the preferred
    /// name; separators and surrounding punctuation are preserved. Returns the
    /// input unchanged when no alias part is present.
    /// </summary>
    private static string? RewriteCredit(string? credit, IReadOnlySet<string> aliasNormalized, string preferredName)
    {
        if (string.IsNullOrWhiteSpace(credit))
        {
            return credit;
        }

        var trimmed = credit.Trim();
        var parts = DeezSpoTag.Core.Utils.ArtistNameNormalizer.SplitCombinedNameForRewrite(trimmed, out var separators);
        if (parts.Count == 0)
        {
            return credit;
        }

        var changed = false;
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            var direct = ArtistAliasService.NormalizeName(part);
            if (direct.Length > 0 && aliasNormalized.Contains(direct))
            {
                parts[index] = preferredName;
                changed = true;
                continue;
            }

            var trimmedPart = part.Trim(' ', '.', ',', '(', ')', '[', ']', '-', '_', '&', '"', '\'');
            var trimmedNormalized = ArtistAliasService.NormalizeName(trimmedPart);
            if (trimmedNormalized.Length > 0
                && !string.Equals(trimmedNormalized, direct, StringComparison.Ordinal)
                && aliasNormalized.Contains(trimmedNormalized))
            {
                parts[index] = part.Replace(trimmedPart, preferredName, StringComparison.OrdinalIgnoreCase);
                changed = true;
            }
        }

        if (!changed)
        {
            return credit;
        }

        var builder = new StringBuilder();
        for (var index = 0; index < parts.Count; index++)
        {
            if (index > 0 && index - 1 < separators.Count)
            {
                builder.Append(separators[index - 1]);
            }

            builder.Append(parts[index]);
        }

        return builder.ToString().Trim();
    }

    // ---- Plumbing ------------------------------------------------------------

    private string? GetConnectionString()
    {
        var rawConnection = Environment.GetEnvironmentVariable("LIBRARY_DB")
            ?? _configuration.GetConnectionString("Library");
        return SqliteConnectionStringResolver.Resolve(rawConnection, "deezspotag.db");
    }

    private static string EscapeLike(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '%' or '_' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string BuildPlaceholders(int count, string prefix)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append('$').Append(prefix).Append(index);
        }

        return builder.ToString();
    }
}
