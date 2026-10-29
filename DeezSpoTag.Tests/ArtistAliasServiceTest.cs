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

[Collection("ArtistAliasGateway")]
public class ArtistAliasServiceTest
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

            // Sync resolution must work immediately after save so a follow-up
            // enhancement run can rewrite on-disk aliases without waiting.
            Assert.Equal("Ayra Starr", service.ResolvePreferred("Ayra Star"));
            Assert.Equal("Calm Down (feat. Ayra Starr)", service.ResolveCredit("Calm Down (feat. Ayra Star)"));
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
    public async Task DownloadAliasPreference_PreservesLiteralTitlesAndIdentifiers()
    {
        var (service, dbPath) = CreateService();
        var gatewayField = typeof(ArtistAliasGateway).GetField("_service", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var previousGateway = gatewayField.GetValue(null);
        try
        {
            await service.SaveGroupAsync("Abbas Kubaff", new[] { "Abbas Doobeez" });
            ArtistAliasGateway.Configure(service);
            await service.GetAliasMapAsync();
            var track = new DeezSpoTag.Core.Models.Track
            {
                Title = "Abbas Doobeez",
                MainArtist = new DeezSpoTag.Core.Models.Artist("spotify-id", "Abbas Doobeez"),
                Album = new DeezSpoTag.Core.Models.Album("album-id", "Abbas Doobeez"),
                Artists = new List<string> { "Other Artist", "Abbas Doobeez" }
            };
            var apply = typeof(DeezSpoTag.Services.Download.Utils.AudioTagger).GetMethod(
                "ApplyArtistAliasPreference", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            apply.Invoke(null, new object[] { track });
            Assert.Equal("Abbas Kubaff", track.MainArtist.Name);
            Assert.Equal("spotify-id", track.MainArtist.Id);
            Assert.Equal("Abbas Doobeez", track.Title);
            Assert.Equal("Abbas Doobeez", track.Album.Title);
            Assert.Equal(new[] { "Other Artist", "Abbas Kubaff" }, track.Artists);
            track.Title = "Song (feat. Abbas Doobeez)";
            apply.Invoke(null, new object[] { track });
            Assert.Equal("Song (feat. Abbas Kubaff)", track.Title);
        }
        finally { gatewayField.SetValue(null, previousGateway); Cleanup(dbPath); }
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    [InlineData("m4a")]
    public async Task DownloadTags_WritePreferredMainAndFeaturedCreditsToAudioFile(string extension)
    {
        var (service, dbPath) = CreateService();
        var gatewayField = typeof(ArtistAliasGateway).GetField("_service", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var previousGateway = gatewayField.GetValue(null);
        var audioPath = Path.Combine(Path.GetTempPath(), $"alias-audio-{Guid.NewGuid():N}.{extension}");
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "0.2", "-y", audioPath })
                start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, error);
            await service.SaveGroupAsync("Abbas Kubaff", new[] { "Abbas Doobeez" });
            ArtistAliasGateway.Configure(service);
            service.InvalidateCache();
            var tagger = new DeezSpoTag.Services.Download.Utils.AudioTagger(
                NullLogger<DeezSpoTag.Services.Download.Utils.AudioTagger>.Instance, null!);
            typeof(DeezSpoTag.Services.Download.Utils.AudioTagger).GetField("_genreAliasMap", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(tagger, new Dictionary<string,string>());
            var track = new DeezSpoTag.Core.Models.Track
            {
                Title = "Song (feat. Abbas Doobeez)",
                MainArtist = new DeezSpoTag.Core.Models.Artist("artist-id", "Abbas Doobeez"),
                Album = new DeezSpoTag.Core.Models.Album("album-id", "Album")
                { MainArtist = new DeezSpoTag.Core.Models.Artist("artist-id", "Abbas Doobeez") },
                Artists = new List<string> { "Other Artist", "Abbas Doobeez" }
            };
            await tagger.TagTrackAsync(extension, audioPath, track, new DeezSpoTag.Core.Models.Settings.TagSettings
            { Title = true, Artist = true, AlbumArtist = true, Cover = false, Genre = false, Lyrics = false, Year = false, Date = false });
            using var audio = TagLib.File.Create(audioPath);
            Assert.Equal("Song (feat. Abbas Kubaff)", audio.Tag.Title);
            Assert.Contains("Abbas Kubaff", string.Join(";",audio.Tag.Performers));
            Assert.Contains("Other Artist", string.Join(";",audio.Tag.Performers));
            Assert.Contains("Abbas Kubaff", string.Join(";",audio.Tag.AlbumArtists));
            Assert.DoesNotContain("Abbas Doobeez", string.Join(";",audio.Tag.Performers));
            audio.Dispose();
            await service.SaveGroupAsync("Abbas Doobeez", new[] { "Abbas Kubaff" });
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Library"] = $"Data Source={dbPath}" }).Build();
            ArtistAliasGateway.Configure(new ArtistAliasService(config, NullLogger<ArtistAliasService>.Instance));
            await tagger.TagTrackAsync(extension, audioPath, track, new DeezSpoTag.Core.Models.Settings.TagSettings
            { Title = true, Artist = true, AlbumArtist = true, Cover = false, Genre = false, Lyrics = false, Year = false, Date = false });
            using var retagged = TagLib.File.Create(audioPath);
            Assert.Equal("Song (feat. Abbas Doobeez)", retagged.Tag.Title);
            Assert.Contains("Abbas Doobeez", string.Join(";", retagged.Tag.Performers));
            Assert.Contains("Other Artist", string.Join(";", retagged.Tag.Performers));
            Assert.Contains("Abbas Doobeez", string.Join(";", retagged.Tag.AlbumArtists));
        }
        finally
        {
            gatewayField.SetValue(null, previousGateway);
            if (File.Exists(audioPath)) File.Delete(audioPath);
            Cleanup(dbPath);
        }
    }

    [Theory]
    [InlineData("flac",false)]
    [InlineData("flac",true)]
    [InlineData("mp3",false)]
    [InlineData("mp3",true)]
    [InlineData("m4a",false)]
    [InlineData("m4a",true)]
    public async Task ExistingAlbumArtistAliasOverridesDisabledOverwriteAndWritesPreferredTag(string extension, bool verifyPlanner)
    {
        var (aliases, db) = CreateService();
        var gateway = typeof(ArtistAliasGateway).GetField("_service", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var previous = gateway.GetValue(null);
        var path = Path.Combine(Path.GetTempPath(), $"alias-retained-{Guid.NewGuid():N}.{extension}");
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "-v","error","-f","lavfi","-i","anullsrc=r=44100:cl=stereo","-t","0.2","-y",path }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, stderr);
            await aliases.SaveGroupAsync("Abbas Doobeez", new[] { "Abbas" });
            ArtistAliasGateway.Configure(aliases); await ArtistAliasGateway.EnsureLoadedAsync();
            using (var seed = TagLib.File.Create(path))
            { seed.Tag.Performers = new[] { "Abbas Doobeez" }; seed.Tag.AlbumArtists = new[] { "Abbas" }; seed.Save(); }
            if (verifyPlanner)
            {
                var runner = typeof(DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner);
                var configType = runner.GetNestedType("AutoTagRunnerConfig",System.Reflection.BindingFlags.NonPublic)!;
                var config = Activator.CreateInstance(configType)!;
                configType.GetProperty("Tags")!.SetValue(config,new List<string> { "albumArtist" });
                var planType = runner.GetNestedType("AutoTagRunPlan",System.Reflection.BindingFlags.NonPublic)!;
                var plan = Activator.CreateInstance(planType,true)!;
                planType.GetProperty("Config")!.SetValue(plan,config);
                planType.GetProperty("PlatformSupportedTags")!.SetValue(plan,new Dictionary<string,HashSet<DeezSpoTag.Web.Services.AutoTag.SupportedTag>>());
                var contextType = runner.GetNestedType("AutoTagFileRunContext",System.Reflection.BindingFlags.NonPublic)!;
                var context = Activator.CreateInstance(contextType,true)!;
                contextType.GetProperty("Plan")!.SetValue(context,plan);
                contextType.GetProperty("File")!.SetValue(context,path);
                contextType.GetProperty("Platform")!.SetValue(context,"spotify");
                var result = runner.GetMethod("BuildProviderTagPlan",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.Invoke(null,new[] { context })!;
                var eligible = (HashSet<DeezSpoTag.Web.Services.AutoTag.SupportedTag>)result.GetType().GetProperty("Eligible")!.GetValue(result)!;
                Assert.Contains(DeezSpoTag.Web.Services.AutoTag.SupportedTag.AlbumArtist,eligible);
            }
            var settings = new DeezSpoTag.Core.Models.Settings.TagSettings { Artist=false,Artists=false,AlbumArtist=false,Title=false };
            var track = new DeezSpoTag.Web.Services.AutoTag.AutoTagTrack
            { Artists = new() { "Abbas Doobeez" }, AlbumArtists = new() { "Abbas Doobeez" } };
            using (var file = TagLib.File.Create(path))
                typeof(DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner).GetMethod("ApplyPreferenceAwareOverwriteGuards",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(null,new object[] { settings,track,new DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings(),file,"spotify" });
            Assert.True(settings.AlbumArtist);
            Assert.Equal("Abbas Doobeez", Assert.Single(track.AlbumArtists));
            var collaborators = (DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner.LocalAutoTagRunnerCollaborators)Activator.CreateInstance(
                typeof(DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner.LocalAutoTagRunnerCollaborators))!;
            collaborators.GetType().GetProperty("Logger")!.SetValue(collaborators,
                NullLogger<DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner>.Instance);
            var writer = new DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner(collaborators);
            var runtime = new DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings();
            var writeConfig = new DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner.AutoTagRunnerConfig
            { Tags = new() { "artist", "albumArtist" } };
            await writer.WriteTagsOnetaggerStyleAsync(
                new DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner.TagWriteRequest
                {
                    FilePath=path, SourceTrack=track,
                    CoreTrack=DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner.BuildCoreTrack(track,";",false,runtime),
                    EffectiveTagSettings=settings, Config=writeConfig, Settings=runtime,
                    PlatformId="spotify", Separator=";"
                },System.Threading.CancellationToken.None);
            using var written = TagLib.File.Create(path);
            Assert.Equal("Abbas Doobeez",Assert.Single(written.Tag.AlbumArtists));
            Assert.Equal("Abbas Doobeez",Assert.Single(written.Tag.Performers));
        }
        finally { gateway.SetValue(null,previous); File.Delete(path); Cleanup(db); }
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

public class ArtistAliasMergeServiceTest
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

            aliasService.InvalidateCache(); // A merge must also work after a cold host start.
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

    [Fact]
    public async Task MergeGroupAsync_FeaturedOnlyCreditStaysInTheOtherMainArtistsFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"alias-featured-folder-{Guid.NewGuid():N}");
        var second = Path.Combine(root, "second-library");
        var dbPath = await CreateLibraryDbAsync(root, second);
        var featured = CreateAudioFile(Path.Combine(root, "Other Artist", "Other Album"), "featured.flac", 20);
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                await connection.OpenAsync();
                await using var seed = connection.CreateCommand();
                seed.CommandText = """
                    INSERT INTO artist(name) VALUES('Other Artist');
                    INSERT INTO album(artist_id,title) SELECT id,'Other Album' FROM artist WHERE name='Other Artist';
                    INSERT INTO track(album_id,title,tag_artist,tag_album_artist)
                    SELECT id,'Song (feat. Old Alias)','Other Artist feat. Old Alias','Other Artist' FROM album WHERE title='Other Album';
                    INSERT INTO audio_file(path,folder_id,extension) SELECT $path,MIN(id),'.flac' FROM folder;
                    INSERT INTO track_local(track_id,audio_file_id)
                    SELECT t.id,f.id FROM track t,audio_file f WHERE t.title='Song (feat. Old Alias)' AND f.path=$path;
                    """;
                seed.Parameters.AddWithValue("$path", featured);
                await seed.ExecuteNonQueryAsync();
            }
            var (aliases, merge) = CreateServices(dbPath);
            var group = await aliases.SaveGroupAsync("Preferred Artist", new[] { "Old Alias" });
            var result = await merge.MergeGroupAsync(group.Id);
            Assert.Empty(result.Errors);
            Assert.Contains(featured, result.AffectedFilePaths);
            Assert.True(File.Exists(featured));
            Assert.False(File.Exists(Path.Combine(root, "Preferred Artist", "Other Album", "featured.flac")));
            await using var check = new SqliteConnection($"Data Source={dbPath}");
            await check.OpenAsync();
            await using var command = check.CreateCommand();
            command.CommandText = "SELECT tag_artist || ':' || tag_album_artist || ':' || title FROM track WHERE title LIKE 'Song%';";
            Assert.Equal("Other Artist feat. Preferred Artist:Other Artist:Song (feat. Preferred Artist)", await command.ExecuteScalarAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MergeGroupAsync_RetainsDistinctProviderIdentitiesAndOwnedMetadata()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"artist-identity-merge-{Guid.NewGuid():N}.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={dbPath}"
            })
            .Build();
        var dbService = new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();
        var repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = @"
INSERT INTO artist (name) VALUES ('Preferred Artist'), ('Old Alias');
INSERT INTO artist_source (artist_id, source, source_id, is_primary, verification_state)
SELECT id, 'spotify', 'spotpreferred111111111', 1, 'verified' FROM artist WHERE name = 'Preferred Artist';
INSERT INTO artist_source (artist_id, source, source_id, native_name, alias_name, is_primary, verification_state)
SELECT id, 'spotify', 'spotalias22222222222222', 'Old Alias', 'Old Alias', 1, 'verified' FROM artist WHERE name = 'Old Alias';
INSERT INTO artist_biography_cache (artist_id, source, source_id, biography, selected)
SELECT id, 'spotify', 'spotpreferred111111111', 'Preferred bio', 1 FROM artist WHERE name = 'Preferred Artist';
INSERT INTO artist_biography_cache (artist_id, source, source_id, biography, native_name, alias_name, selected)
SELECT id, 'spotify', 'spotalias22222222222222', 'Alias bio', 'Old Alias', 'Old Alias', 1 FROM artist WHERE name = 'Old Alias';
INSERT INTO artist_metadata_policy (artist_id, sync_blocked, ocr_text_art_blocking_enabled, selected_targets_json)
SELECT id, 1, 1, '[""plex""]' FROM artist WHERE name = 'Old Alias';
INSERT INTO artist_location_override (artist_id, city, country, country_code)
SELECT id, 'Accra', 'Ghana', 'GH' FROM artist WHERE name = 'Old Alias';
INSERT INTO artist_artwork_cache (artist_id, role, identity, source, user_blocked)
SELECT id, 'avatar', 'alias-art', 'spotify', 1 FROM artist WHERE name = 'Old Alias';
INSERT INTO artist_visual_usage (artist_id, slot, content_hash)
SELECT id, 'avatar', 'hash-alias' FROM artist WHERE name = 'Old Alias';";
                await command.ExecuteNonQueryAsync();
            }

            var preferredId = await repository.FindArtistIdByNameAsync("Preferred Artist");
            Assert.NotNull(preferredId);
            await repository.UpsertArtistSourceIdAsync(preferredId.Value, "spotify", "spotnew3333333333333333");
            var identitiesBeforeDelete = await ReadSpotifyIdsAsync(dbPath, preferredId.Value);
            Assert.Contains("spotpreferred111111111", identitiesBeforeDelete);
            Assert.Contains("spotnew3333333333333333", identitiesBeforeDelete);

            await repository.UpsertArtistBiographyCacheAsync(preferredId.Value, "spotify", "   ", selected: false);
            var preferredBio = await ReadBiographyAsync(dbPath, "spotpreferred111111111");
            Assert.Equal("Preferred bio", preferredBio);

            var (aliasService, mergeService) = CreateServices(dbPath);
            var group = await aliasService.SaveGroupAsync("Preferred Artist", new[] { "Old Alias" });
            await mergeService.MergeGroupAsync(group.Id);

            var survivingIds = await ReadSpotifyIdsAsync(dbPath, preferredId.Value);
            Assert.Contains("spotpreferred111111111", survivingIds);
            Assert.Contains("spotalias22222222222222", survivingIds);
            Assert.Contains("spotnew3333333333333333", survivingIds);
            Assert.Equal("Alias bio", await ReadBiographyAsync(dbPath, "spotalias22222222222222"));
            Assert.Equal("Preferred bio", await ReadBiographyAsync(dbPath, "spotpreferred111111111"));
            var mergedIdentities = await repository.GetArtistSourceIdentitiesAsync(preferredId.Value, "spotify");
            Assert.Single(mergedIdentities.Where(identity => identity.IsPrimary));

            await using var check = new SqliteConnection($"Data Source={dbPath}");
            await check.OpenAsync();
            await using var policy = check.CreateCommand();
            policy.CommandText = "SELECT sync_blocked, selected_targets_json FROM artist_metadata_policy WHERE artist_id = $id;";
            policy.Parameters.AddWithValue("$id", preferredId.Value);
            await using var policyReader = await policy.ExecuteReaderAsync();
            Assert.True(await policyReader.ReadAsync());
            Assert.Equal(1, policyReader.GetInt32(0));
            Assert.Equal("[\"plex\"]", policyReader.GetString(1));
            await policyReader.DisposeAsync();

            await using var location = check.CreateCommand();
            location.CommandText = "SELECT city FROM artist_location_override WHERE artist_id = $id;";
            location.Parameters.AddWithValue("$id", preferredId.Value);
            Assert.Equal("Accra", await location.ExecuteScalarAsync());

            await using var art = check.CreateCommand();
            art.CommandText = "SELECT user_blocked FROM artist_artwork_cache WHERE artist_id = $id AND identity = 'alias-art';";
            art.Parameters.AddWithValue("$id", preferredId.Value);
            Assert.Equal(1L, Convert.ToInt64(await art.ExecuteScalarAsync()));

            await using var usage = check.CreateCommand();
            usage.CommandText = "SELECT COUNT(*) FROM artist_visual_usage WHERE artist_id = $id AND content_hash = 'hash-alias';";
            usage.Parameters.AddWithValue("$id", preferredId.Value);
            Assert.Equal(1L, Convert.ToInt64(await usage.ExecuteScalarAsync()));

            await using var absorbed = check.CreateCommand();
            absorbed.CommandText = "SELECT COUNT(*) FROM artist WHERE name = 'Old Alias';";
            Assert.Equal(0L, Convert.ToInt64(await absorbed.ExecuteScalarAsync()));
        }
        finally
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
    }

    [Fact]
    public async Task MergeGroupAsync_KeepsOneBiographyRotationWhenSeveralAliasesHaveOne()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"artist-rotation-merge-{Guid.NewGuid():N}.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={dbPath}"
            })
            .Build();
        await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                await connection.OpenAsync();
                await using var seed = connection.CreateCommand();
                seed.CommandText = @"
INSERT INTO artist (name) VALUES ('Preferred Artist'), ('Old Alias'), ('Second Alias');
INSERT INTO artist_biography_rotation (artist_id, last_source, used_at)
SELECT id, 'lastfm', '2026-09-01' FROM artist WHERE name = 'Old Alias';
INSERT INTO artist_biography_rotation (artist_id, last_source, used_at)
SELECT id, 'spotify', '2026-09-02' FROM artist WHERE name = 'Second Alias';
INSERT INTO artist_biography_cache(artist_id,source,source_id,biography,selected)
SELECT id,'lastfm','old-page','Older text',1 FROM artist WHERE name='Old Alias';
INSERT INTO artist_biography_cache(artist_id,source,source_id,biography,selected)
SELECT id,'spotify','sibling-id','Newer text',1 FROM artist WHERE name='Second Alias';
UPDATE artist_biography_rotation SET profile_cursors_json='{""lastfm"":""old-page"",""qobuz"":""missing""}' WHERE last_source='lastfm';
UPDATE artist_biography_rotation SET last_source_id='sibling-id',profile_cursors_json='{""spotify"":""sibling-id""}' WHERE last_source='spotify';";
                await seed.ExecuteNonQueryAsync();
            }

            var (aliasService, mergeService) = CreateServices(dbPath);
            var group = await aliasService.SaveGroupAsync("Preferred Artist", new[] { "Old Alias", "Second Alias" });
            var result = await mergeService.MergeGroupAsync(group.Id);
            Assert.Empty(result.Errors);

            await using var check = new SqliteConnection($"Data Source={dbPath}");
            await check.OpenAsync();
            await using var command = check.CreateCommand();
            command.CommandText = @"
SELECT last_source
FROM artist_biography_rotation
WHERE artist_id = (SELECT id FROM artist WHERE name = 'Preferred Artist');";
            Assert.Equal("spotify", await command.ExecuteScalarAsync());
            command.CommandText = "SELECT COUNT(*) FROM artist_biography_rotation;";
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            command.CommandText = "SELECT profile_cursors_json FROM artist_biography_rotation;";
            var cursors = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,string>>((string)(await command.ExecuteScalarAsync())!)!;
            Assert.Equal("old-page", cursors["lastfm"]);
            Assert.Equal("sibling-id", cursors["spotify"]);
            Assert.False(cursors.ContainsKey("qobuz"));
            command.CommandText = "SELECT COUNT(*) FROM artist_biography_cache WHERE selected=1;";
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
        finally
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
    }

    [Fact]
    public async Task BiographyRotationMigration_IsAdditiveAndRepeatable()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"alias-rotation-migration-{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:Library"] = $"Data Source={dbPath}" }).Build();
        try
        {
            var db = new LibraryDbService(config, NullLogger<LibraryDbService>.Instance);
            await db.EnsureSchemaAsync();
            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE artist_biography_rotation; CREATE TABLE artist_biography_rotation(artist_id INTEGER PRIMARY KEY,last_source TEXT NOT NULL,used_at TEXT); INSERT INTO artist(name) VALUES ('Abbas Kubaff'); INSERT INTO artist_biography_rotation(artist_id,last_source) SELECT id,'spotify' FROM artist;";
            await command.ExecuteNonQueryAsync();
            await db.EnsureSchemaAsync();
            await db.EnsureSchemaAsync();
            command.CommandText = "SELECT last_source_id || ':' || profile_cursors_json FROM artist_biography_rotation;";
            Assert.Equal(":{}", await command.ExecuteScalarAsync());
            command.CommandText = "SELECT last_source FROM artist_biography_rotation;";
            Assert.Equal("spotify", await command.ExecuteScalarAsync());
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(dbPath)) File.Delete(dbPath); }
    }

    [Fact]
    public async Task BiographyRotation_SelectsOneProfileAndVisitsBothWithinProvider()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"alias-profile-rotation-{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:Library"] = $"Data Source={dbPath}" }).Build();
        try
        {
            await new LibraryDbService(config, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO artist(id,name) VALUES (1,'Abbas Kubaff'); INSERT INTO artist_biography_cache(artist_id,source,source_id,biography) VALUES (1,'spotify','kubaff','First biography'),(1,'spotify','doobeez','Second biography');";
            await command.ExecuteNonQueryAsync();
            var repository = new LibraryRepository(config, NullLogger<LibraryRepository>.Instance);
            await repository.SelectArtistBiographySourceAsync(1, "spotify");
            command.CommandText = "SELECT COUNT(*) FROM artist_biography_cache WHERE selected=1;";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT source_id FROM artist_biography_cache WHERE selected=1;";
            var first = await command.ExecuteScalarAsync();
            await repository.SelectArtistBiographySourceAsync(1, "spotify");
            Assert.NotEqual(first, await command.ExecuteScalarAsync());
            await repository.SelectArtistBiographySourceAsync(1, "spotify");
            Assert.Equal(first, await command.ExecuteScalarAsync());
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(dbPath)) File.Delete(dbPath); }
    }

    [Fact]
    public async Task BiographyRotation_PreservesProviderFairnessAndExactSelectionAcrossRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alias-fair-rotation-{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:Library"] = $"Data Source={path}" }).Build();
        try
        {
            await new LibraryDbService(config, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO artist(id,name) VALUES(1,'Abbas Kubaff');
                INSERT INTO artist_biography_cache(artist_id,source,source_id,biography) VALUES
                (1,'spotify','a','First'),(1,'spotify','b','Second'),(1,'spotify','c','First  '),
                (1,'lastfm','Abbas Doobeez','Third'),(1,'audiomack','abbas-kubaff','Fourth');
                INSERT INTO artist_biography_rotation(artist_id,last_source,profile_cursors_json) VALUES(1,'','malformed');
                """;
            await command.ExecuteNonQueryAsync();
            var expected = new[] { "spotify:a", "lastfm:Abbas Doobeez", "audiomack:abbas-kubaff",
                "spotify:b", "lastfm:Abbas Doobeez", "audiomack:abbas-kubaff", "spotify:a" };
            command.CommandText = "SELECT source || ':' || source_id FROM artist_biography_cache WHERE selected=1;";
            foreach (var identity in expected)
            {
                // Recreate the repository between turns: the cursor belongs to the database.
                var repo = new LibraryRepository(config, NullLogger<LibraryRepository>.Instance);
                await repo.SelectArtistBiographySourceAsync(1, null);
                Assert.Equal(identity, await command.ExecuteScalarAsync());
                Assert.Single((await repo.GetArtistBiographyRowsAsync(1)).Where(row => row.Selected));
            }
            var repository = new LibraryRepository(config, NullLogger<LibraryRepository>.Instance);
            await repository.SelectArtistBiographySourceAsync(1, "spotify", sourceId: "b", advanceRotation: false);
            Assert.Equal("spotify:b", await command.ExecuteScalarAsync());
            await repository.GetArtistBiographyCacheAsync(1, null, allowFallback: true);
            await repository.SelectArtistBiographySourceAsync(1, "spotify", advanceRotation: false);
            Assert.Equal("spotify:b", await command.ExecuteScalarAsync());
            await repository.SelectArtistBiographySourceAsync(1, "lastfm", sourceId: "b", advanceRotation: false);
            Assert.Equal("spotify:b", await command.ExecuteScalarAsync());
            await repository.UpsertArtistBiographyCacheAsync(1, "spotify", "b", null, false, null, null);
            Assert.Contains(await repository.GetArtistBiographyRowsAsync(1), row => row.SourceId == "b" && row.Biography == "Second");
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task MergeGroupAsync_OverlappingAliasDoesNotRewritePreferredPathAgain()
    {
        var root = Path.Combine(Path.GetTempPath(), $"alias-repeat-{Guid.NewGuid():N}");
        var other = root + "-other";
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(other);
        var db = await CreateLibraryDbAsync(root, other);
        try
        {
            foreach (var directory in new[] { root, other })
            {
                Directory.Move(Path.Combine(directory, "Old Alias"), Path.Combine(directory, "Abbas"));
                Directory.Move(Path.Combine(directory, "Abbas", "Test Album"), Path.Combine(directory, "Abbas", "Mister Abbas Doobeez"));
            }
            await using var connection = new SqliteConnection($"Data Source={db}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE artist SET name='Abbas' WHERE name='Old Alias';
                UPDATE artist SET name='Abbas Doobeez' WHERE name='Preferred Artist';
                UPDATE audio_file SET path=replace(replace(path,'Old Alias','Abbas'),'Test Album','Mister Abbas Doobeez');
                UPDATE track SET tag_artist='Abbas',tag_album_artist='Abbas';
                """;
            await command.ExecuteNonQueryAsync();
            var (aliases, merge) = CreateServices(db);
            var group = await aliases.SaveGroupAsync("Abbas Doobeez", new[] { "Abbas" });
            await merge.MergeGroupAsync(group.Id);
            await merge.MergeGroupAsync(group.Id);
            Assert.True(File.Exists(Path.Combine(root, "Abbas Doobeez", "Mister Abbas Doobeez", "test song.flac")));
            Assert.DoesNotContain(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories), path => path.Contains("Doobeez Doobeez", StringComparison.Ordinal));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true); Directory.Delete(other, true); File.Delete(db);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MergeGroupAsync_SharedAbsorbedMetadataPreservesTextAndBlocks(bool withBiography)
    {
        var db = Path.Combine(Path.GetTempPath(), $"alias-collision-{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["ConnectionStrings:Library"] = $"Data Source={db}" }).Build();
        try
        {
            await new LibraryDbService(config, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
            await using var connection = new SqliteConnection($"Data Source={db}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO artist(id,name) VALUES(1,'Preferred'),(2,'Alias One'),(3,'Alias Two');
                INSERT INTO artist_artwork_cache(artist_id,role,identity,user_blocked,text_art_blocked)
                VALUES(2,'avatar','same',0,0),(3,'avatar','same',1,1);
                """;
            if (withBiography) command.CommandText += """
                INSERT INTO artist_biography_cache(artist_id,source,source_id,biography,native_name)
                VALUES(2,'lastfm','shared','',NULL),(3,'lastfm','shared','Keep this biography','Alias Two');
                """;
            await command.ExecuteNonQueryAsync();
            var (aliases, merge) = CreateServices(db);
            var group = await aliases.SaveGroupAsync("Preferred", new[] { "Alias One", "Alias Two" });
            var result = await merge.MergeGroupAsync(group.Id);
            Assert.Empty(result.Errors);
            command.CommandText = "SELECT user_blocked+text_art_blocked FROM artist_artwork_cache WHERE artist_id=1;";
            Assert.Equal(2L, await command.ExecuteScalarAsync());
            if (withBiography)
            {
                command.CommandText = "SELECT biography FROM artist_biography_cache WHERE artist_id=1;";
                Assert.Equal("Keep this biography", await command.ExecuteScalarAsync());
                command.CommandText = "SELECT native_name FROM artist_biography_cache WHERE artist_id=1;";
                Assert.Equal("Alias Two", await command.ExecuteScalarAsync());
            }
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(db); }
    }

    [Fact]
    public async Task AutomaticIdentityUpsertCannotTakeAnotherArtistsProfile()
    {
        var db = Path.Combine(Path.GetTempPath(), $"alias-owner-{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["ConnectionStrings:Library"] = $"Data Source={db}" }).Build();
        try
        {
            await new LibraryDbService(config, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
            await using var connection = new SqliteConnection($"Data Source={db}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO artist(id,name) VALUES(1,'Owner'),(2,'Lookalike');";
            await command.ExecuteNonQueryAsync();
            var repo = new LibraryRepository(config, NullLogger<LibraryRepository>.Instance);
            await repo.UpsertArtistSourceIdAsync(1,"qobuz","shared");
            await Assert.ThrowsAsync<InvalidOperationException>(() => repo.UpsertArtistSourceIdAsync(2,"qobuz","shared"));
            Assert.Equal("shared", await repo.GetArtistSourceIdAsync(1,"qobuz"));
            Assert.Null(await repo.GetArtistSourceIdAsync(2,"qobuz"));
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(db); }
    }

    [Theory]
    [InlineData(0, 0, false, "completed")]
    [InlineData(1, 0, false, "partial")]
    [InlineData(1, 1, false, "tagging_queued")]
    [InlineData(1, 1, true, "partial")]
    [InlineData(0, 0, true, "partial")]
    public void MergeSubmissionStatusDoesNotClaimTagsAreWritten(int files, int jobs, bool hasErrors, string expected)
    {
        var result = new ArtistAliasMergeResult(1,"Preferred",files,0,0,0,0,0,
            files == 0 ? Array.Empty<string>() : new[] { "file.flac" }, Array.Empty<long>(),
            hasErrors ? new[] { "File move failed" } : Array.Empty<string>());
        Assert.Equal(expected, result.GetSubmissionStatus(jobs));
    }

    private static async Task<List<string>> ReadSpotifyIdsAsync(string dbPath, long artistId)
    {
        var ids = new List<string>();
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_id FROM artist_source WHERE artist_id = $id AND source = 'spotify' ORDER BY source_id;";
        command.Parameters.AddWithValue("$id", artistId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static async Task<string?> ReadBiographyAsync(string dbPath, string sourceId)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT biography FROM artist_biography_cache WHERE source_id = $id;";
        command.Parameters.AddWithValue("$id", sourceId);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToString(result);
    }
}
