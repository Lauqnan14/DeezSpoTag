using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TrackAnalysisRepositoryOrderingTest : IAsyncLifetime
{
    private string _root = string.Empty;
    private string _dbPath = string.Empty;
    private LibraryRepository _repository = default!;

    public async Task InitializeAsync()
    {
        _root = Path.Join(Path.GetTempPath(), "deezspotag-vibe-order-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _dbPath = Path.Join(_root, "library.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={_dbPath}"
            })
            .Build();
        await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
        _repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Snapshot_UsesLibraryArtistAlbumDiscTrackOrderAndPreservesVariantPreference()
    {
        var firstFolder = await AddFolderAsync("First Library", "first");
        var secondFolder = await AddFolderAsync("Second Library", "second");
        var d = await AddTrackAsync(firstFolder.Id, "D Artist", "Later", "D", 1, 1);
        var b2 = await AddTrackAsync(firstFolder.Id, "B Artist", "Album", "B Two", 2, 1);
        var b1 = await AddTrackAsync(firstFolder.Id, "B Artist", "Album", "B One", 1, 2);
        var accented = await AddTrackAsync(secondFolder.Id, "Éclair", "Album", "Accent", 1, 1);
        var numeric = await AddTrackAsync(secondFolder.Id, "Drake, 21 Savage", "Album", "Numeric", 1, 1);
        await AddVariantAsync(b1, firstFolder.Id, "B Artist/Album/02.opus", "opus", ".opus", "stereo", 50);
        await AddVariantAsync(b1, firstFolder.Id, "B Artist/Album/02-atmos.ec3", "eac3", ".ec3", "atmos", 100);

        var snapshot = await _repository.GetTracksForAnalysisAsync(
            100,
            orderedLibraryIds: [firstFolder.Id, secondFolder.Id]);

        Assert.Equal([b1, b2, d, numeric, accented], snapshot.Select(static item => item.TrackId));
        var selected = Assert.Single(snapshot, item => item.TrackId == b1);
        Assert.EndsWith("02.flac", selected.FilePath, StringComparison.Ordinal);
        Assert.Equal(2, selected.AlternateFilePaths?.Count);
        Assert.EndsWith("02.opus", selected.AlternateFilePaths![0], StringComparison.Ordinal);
        Assert.EndsWith("02-atmos.ec3", selected.AlternateFilePaths[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_ExcludesAttemptedAndCompletedEnhancedTracks()
    {
        var folder = await AddFolderAsync("Library", "library");
        var missing = await AddTrackAsync(folder.Id, "Artist", "Album", "Missing", 1, 1);
        var failed = await AddTrackAsync(folder.Id, "Artist", "Album", "Failed", 1, 2);
        var complete = await AddTrackAsync(folder.Id, "Artist", "Album", "Complete", 1, 3);
        await SetAnalysisAsync(failed, "failed", "enhanced", DateTimeOffset.UtcNow);
        await SetAnalysisAsync(complete, "completed", "enhanced", DateTimeOffset.UtcNow);

        var snapshot = await _repository.GetTracksForAnalysisAsync(
            100,
            orderedLibraryIds: [folder.Id],
            excludedTrackIds: new HashSet<long> { missing });

        Assert.Equal([failed], snapshot.Select(static item => item.TrackId));
    }

    private async Task<FolderDto> AddFolderAsync(string name, string suffix)
        => await _repository.AddFolderAsync(new LibraryRepository.FolderUpsertInput(
            Path.Join(_root, suffix), name, true, name, "flac", false, null, null, "test-profile"));

    private async Task<long> AddTrackAsync(long folderId, string artist, string album, string title, int disc, int track)
    {
        var relativePath = Path.Join(artist, album, $"{track:00}.flac");
        var fullPath = Path.Join(_root, folderId.ToString(), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "audio");
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO artist(name) VALUES(@artist);
INSERT INTO album(artist_id,title) VALUES(last_insert_rowid(),@album);
INSERT INTO track(album_id,title,duration_ms,disc,track_no,tag_artist,tag_album,tag_disc,tag_track_no)
VALUES(last_insert_rowid(),@title,180000,@disc,@track,@artist,@album,@disc,@track);
SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("artist", artist);
        command.Parameters.AddWithValue("album", album);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("disc", disc);
        command.Parameters.AddWithValue("track", track);
        var trackId = Convert.ToInt64(await command.ExecuteScalarAsync());
        await AddVariantAsync(trackId, folderId, relativePath, "flac", ".flac", "stereo", 10);
        return trackId;
    }

    private async Task AddVariantAsync(long trackId, long folderId, string relativePath, string codec, string extension, string variant, int quality)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO audio_file(path,relative_path,folder_id,size,duration_ms,codec,extension,sample_rate_hz,channels,quality_rank,audio_variant)
VALUES(@path,@relative,@folder,1000,180000,@codec,@extension,48000,2,@quality,@variant);
INSERT INTO track_local(track_id,audio_file_id) VALUES(@track,last_insert_rowid());";
        command.Parameters.AddWithValue("path", Path.Join(_root, folderId.ToString(), relativePath));
        command.Parameters.AddWithValue("relative", relativePath);
        command.Parameters.AddWithValue("folder", folderId);
        command.Parameters.AddWithValue("codec", codec);
        command.Parameters.AddWithValue("extension", extension);
        command.Parameters.AddWithValue("quality", quality);
        command.Parameters.AddWithValue("variant", variant);
        command.Parameters.AddWithValue("track", trackId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SetAnalysisAsync(long trackId, string status, string mode, DateTimeOffset analyzedAt)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO track_analysis(track_id,status,analysis_mode,analyzed_at_utc) VALUES(@track,@status,@mode,@at);";
        command.Parameters.AddWithValue("track", trackId);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("mode", mode);
        command.Parameters.AddWithValue("at", analyzedAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }
}
