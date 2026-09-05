using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public class ArtistAliasServiceTests
{
    private static (ArtistAliasService Service, string DbPath) CreateService()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"artist-alias-{Guid.NewGuid():N}.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={dbPath}"
            })
            .Build();
        var service = new ArtistAliasService(
            configuration,
            NullLogger<ArtistAliasService>.Instance);
        return (service, dbPath);
    }

    private static void Cleanup(string dbPath)
    {
        try
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void NormalizeName_CollapsesWhitespaceAndCase()
    {
        Assert.Equal("ayra starr", ArtistAliasService.NormalizeName("  Ayra   Starr "));
        Assert.Equal(string.Empty, ArtistAliasService.NormalizeName("   "));
    }

    [Fact]
    public void SplitCombinedNameForRewrite_KeepsSeparators()
    {
        var parts = DeezSpoTag.Core.Utils.ArtistNameNormalizer.SplitCombinedNameForRewrite(
            "Song (feat. Old Alias) & Wizkid",
            out var separators);
        // The separator regex stops at the word boundary before the dot, so the
        // ". " of "feat. " stays attached to the following part; the alias
        // matching therefore trims surrounding punctuation.
        Assert.Equal(new[] { "Song (", ". Old Alias)", "Wizkid" }, parts);
        Assert.Equal(2, separators.Count);
        Assert.Equal("feat", separators[0]);
        Assert.Equal(" & ", separators[1]);
    }

    [Fact]
    public async Task SaveGroupAsync_RequiresTwoNames()
    {
        var (service, dbPath) = CreateService();
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.SaveGroupAsync("Only One", new[] { "Only One" }));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task SaveGroupAsync_PersistsGroupAndResolvesAliases()
    {
        var (service, dbPath) = CreateService();
        try
        {
            var group = await service.SaveGroupAsync("Ayra Starr", new[] { "ayra starr & co", "Ayra Star" });

            Assert.Equal("Ayra Starr", group.PreferredName);
            Assert.Contains("ayra starr & co", group.Aliases);

            var resolved = await service.ResolvePreferredAsync("AYRA STAR");
            Assert.Equal("Ayra Starr", resolved);

            var credit = await service.ResolveCreditAsync("Calm Down (feat. Ayra Star)");
            Assert.Equal("Calm Down (feat. Ayra Starr)", credit);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task SaveGroupAsync_AbsorbsExistingGroupSharingName()
    {
        var (service, dbPath) = CreateService();
        try
        {
            await service.SaveGroupAsync("Alias A", new[] { "Shared Name", "Extra One" });

            // Saving a new group whose preferred name is an alias of the first
            // group must fold both groups into one.
            var merged = await service.SaveGroupAsync("Preferred Final", new[] { "Alias B", "Alias A" });

            var groups = await service.GetAllGroupsAsync();
            var savedGroup = Assert.Single(groups);
            Assert.Equal(merged.Id, savedGroup.Id);
            Assert.Equal("Preferred Final", savedGroup.PreferredName);
            Assert.Contains("Shared Name", savedGroup.Aliases);
            Assert.Contains("Extra One", savedGroup.Aliases);
            Assert.Contains("Alias A", savedGroup.Aliases);
            Assert.Contains("Alias B", savedGroup.Aliases);

            var resolved = await service.ResolvePreferredAsync("extra one");
            Assert.Equal("Preferred Final", resolved);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task DeleteGroupAsync_StopsResolution()
    {
        var (service, dbPath) = CreateService();
        try
        {
            var group = await service.SaveGroupAsync("Preferred Name", new[] { "Removed Alias" });
            Assert.True(await service.DeleteGroupAsync(group.Id));

            var resolved = await service.ResolvePreferredAsync("Removed Alias");
            Assert.Equal("Removed Alias", resolved);

            Assert.False(await service.DeleteGroupAsync(group.Id));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }
}

public class ArtistAliasMergeServiceTests
{
    private static string CreateAudioFile(string directory, string fileName, long sizeBytes)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        return path;
    }

    private static async Task<string> CreateLibraryDbAsync(string rootPath, string secondRootPath)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"artist-merge-{Guid.NewGuid():N}.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={dbPath}",
                ["DataDirectory"] = Path.GetTempPath()
            })
            .Build();
        var dbService = new DeezSpoTag.Services.Library.LibraryDbService(
            configuration,
            NullLogger<DeezSpoTag.Services.Library.LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        async Task ExecuteAsync(string sql, params (string, object?)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            await command.ExecuteNonQueryAsync();
        }

        // Two separate library folders. The alias has audio in BOTH; the merge
        // must rename/merge within each root and never move files across.
        await ExecuteAsync("INSERT INTO folder (root_path, display_name, enabled) VALUES ($root, 'Test Folder', 1), ($root2, 'Second Folder', 1);",
            ("$root", rootPath),
            ("$root2", secondRootPath));
        await ExecuteAsync("INSERT INTO artist (name, preferred_image_path) VALUES ('Old Alias', $image);",
            ("$image", CreateAudioFile(Path.Combine(rootPath, "Old Alias"), "artist.jpg", 10)));
        await ExecuteAsync(@"
INSERT INTO album (artist_id, title, preferred_cover_path)
SELECT id, 'Test Album', $cover FROM artist WHERE name = 'Old Alias';",
            ("$cover", CreateAudioFile(Path.Combine(rootPath, "Old Alias", "Test Album"), "folder.jpg", 10)));
        await ExecuteAsync(@"
INSERT INTO track (album_id, title, tag_artist, tag_album_artist)
SELECT id, 'Test Song (feat. Old Alias)', 'Old Alias', 'Old Alias' FROM album WHERE title = 'Test Album';");
        await ExecuteAsync(@"
INSERT INTO audio_file (path, relative_path, folder_id, size, extension)
SELECT $path, 'Old Alias/Test Album/test song.flac', id, 100, '.flac' FROM folder WHERE root_path = $root;",
            ("$path", CreateAudioFile(Path.Combine(rootPath, "Old Alias", "Test Album"), "test song.flac", 100)),
            ("$root", rootPath));
        await ExecuteAsync(@"
INSERT INTO audio_file (path, relative_path, folder_id, size, extension)
SELECT $path, 'Old Alias/Test Album/second song.mp3', id, 50, '.mp3' FROM folder WHERE root_path = $root;",
            ("$path", CreateAudioFile(Path.Combine(secondRootPath, "Old Alias", "Test Album"), "second song.mp3", 50)),
            ("$root", secondRootPath));
        await ExecuteAsync(@"
INSERT INTO track_local (track_id, audio_file_id)
SELECT t.id, af.id
FROM track t, audio_file af
WHERE t.title LIKE 'Test Song%' AND (af.path LIKE '%test song.flac' OR af.path LIKE '%second song.mp3');");

        return dbPath;
    }

    private static (ArtistAliasService AliasService, ArtistAliasMergeService MergeService) CreateServices(string dbPath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={dbPath}"
            })
            .Build();
        var aliasService = new ArtistAliasService(
            configuration,
            NullLogger<ArtistAliasService>.Instance);

        // The settings service reads its config folder from the environment;
        // isolate it so tests never touch a real config directory.
        var configDir = Path.Combine(Path.GetTempPath(), $"artist-merge-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configDir);
        var previousConfigDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR");
        Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", configDir);
        try
        {
            var settingsService = new DeezSpoTagSettingsService(
                NullLogger<DeezSpoTagSettingsService>.Instance);
            var mergeService = new ArtistAliasMergeService(
                configuration,
                NullLogger<ArtistAliasMergeService>.Instance,
                aliasService,
                settingsService);
            return (aliasService, mergeService);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", previousConfigDir);
        }
    }

    [Fact]
    public async Task MergeGroupAsync_MovesFilesIntoPreferredFolderAndRewritesDb()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"artist-merge-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        var secondRootPath = Path.Combine(Path.GetTempPath(), $"artist-merge-root2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(secondRootPath);
        var dbPath = await CreateLibraryDbAsync(rootPath, secondRootPath);
        try
        {
            var (aliasService, mergeService) = CreateServices(dbPath);
            var group = await aliasService.SaveGroupAsync("Preferred Artist", new[] { "Old Alias" });

            var result = await mergeService.MergeGroupAsync(group.Id);

            Assert.Equal(2, result.FilesMoved);
            Assert.Equal(2, result.ArtworkFilesMoved);
            Assert.Equal(1, result.ArtistRowsMerged);
            Assert.Equal(1, result.TrackRowsRewritten);

            var movedPath = Path.Combine(rootPath, "Preferred Artist", "Test Album", "test song.flac");
            Assert.True(File.Exists(movedPath), $"Expected file at {movedPath}");
            Assert.False(File.Exists(Path.Combine(rootPath, "Old Alias", "Test Album", "test song.flac")));
            var oldAliasDir = Path.Combine(rootPath, "Old Alias");
            Assert.False(Directory.Exists(oldAliasDir),
                "Old Alias still exists with: " + (Directory.Exists(oldAliasDir)
                    ? string.Join(" | ", Directory.EnumerateFileSystemEntries(oldAliasDir, "*", SearchOption.AllDirectories))
                    : "(gone)"));
            Assert.Contains(movedPath, result.AffectedFilePaths, StringComparer.OrdinalIgnoreCase);

            // No criss-cross: the alias in the second library is renamed and
            // maintained entirely inside the second library root.
            var secondMovedPath = Path.Combine(secondRootPath, "Preferred Artist", "Test Album", "second song.mp3");
            Assert.True(File.Exists(secondMovedPath), $"Expected file at {secondMovedPath}");
            Assert.False(File.Exists(Path.Combine(secondRootPath, "Old Alias", "Test Album", "second song.mp3")));
            Assert.False(Directory.Exists(Path.Combine(secondRootPath, "Old Alias")));
            // Nothing from one library ever appears inside the other.
            Assert.False(File.Exists(Path.Combine(secondRootPath, "Preferred Artist", "Test Album", "test song.flac")));
            Assert.False(File.Exists(Path.Combine(rootPath, "Preferred Artist", "Test Album", "second song.mp3")));

            // Artist art and album cover sidecars followed into the preferred
            // artist folder, and the DB references were repointed.
            var movedArtistArt = Path.Combine(rootPath, "Preferred Artist", "artist.jpg");
            var movedCover = Path.Combine(rootPath, "Preferred Artist", "Test Album", "folder.jpg");
            Assert.True(File.Exists(movedArtistArt), $"Expected artist art at {movedArtistArt}");
            Assert.True(File.Exists(movedCover), $"Expected album cover at {movedCover}");

            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            await using var trackCommand = connection.CreateCommand();
            trackCommand.CommandText = "SELECT tag_artist, title FROM track LIMIT 1;";
            await using var reader = await trackCommand.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Preferred Artist", reader.GetString(0));
            Assert.Equal("Test Song (feat. Preferred Artist)", reader.GetString(1));

            await using var imageCommand = connection.CreateCommand();
            imageCommand.CommandText = "SELECT preferred_image_path FROM artist WHERE name = 'Preferred Artist';";
            var storedImagePath = (string?)(await imageCommand.ExecuteScalarAsync());
            Assert.Equal(movedArtistArt, storedImagePath);

            await using var coverCommand = connection.CreateCommand();
            coverCommand.CommandText = "SELECT preferred_cover_path FROM album WHERE title = 'Test Album';";
            var storedCoverPath = (string?)(await coverCommand.ExecuteScalarAsync());
            Assert.Equal(movedCover, storedCoverPath);

            // Every DB path stayed inside its own library root.
            await using var fileCommand = connection.CreateCommand();
            fileCommand.CommandText = "SELECT path FROM audio_file ORDER BY path;";
            await using var fileReader = await fileCommand.ExecuteReaderAsync();
            var storedPaths = new List<string>();
            while (await fileReader.ReadAsync())
            {
                storedPaths.Add(fileReader.GetString(0));
            }

            Assert.Equal(2, storedPaths.Count);
            Assert.Contains(movedPath, storedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(secondMovedPath, storedPaths, StringComparer.OrdinalIgnoreCase);

            await using var artistCommand = connection.CreateCommand();
            artistCommand.CommandText = "SELECT name FROM artist;";
            await using var artistReader = await artistCommand.ExecuteReaderAsync();
            var artistNames = new List<string>();
            while (await artistReader.ReadAsync())
            {
                artistNames.Add(artistReader.GetString(0));
            }

            Assert.DoesNotContain("Old Alias", artistNames);
            Assert.Contains("Preferred Artist", artistNames);
        }
        finally
        {
            foreach (var directory in new[] { rootPath, secondRootPath })
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException)
                {
                }
            }

            try
            {
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
