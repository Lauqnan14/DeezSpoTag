using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class FolderUniformityRemoveEmptyFoldersTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly TestConfigRootScope _configScope;
    private readonly AutoTagLibraryOrganizer _organizer;

    public FolderUniformityRemoveEmptyFoldersTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-fu-empty-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _configScope = new TestConfigRootScope(_tempRoot);

        var settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = settingsService.LoadSettings();
        settings.CreateArtistFolder = true;
        settings.CreateAlbumFolder = true;
        settings.Tags ??= new TagSettings();
        settings.Tags.SingleAlbumArtist = true;
        settingsService.SaveSettings(settings);

        var environment = new StubWebHostEnvironment { ContentRootPath = _tempRoot, WebRootPath = _tempRoot };
        var shazamDiscovery = new ShazamDiscoveryService(
            new HttpClient(),
            NullLogger<ShazamDiscoveryService>.Instance,
            environment);
        var shazamRecognition = new ShazamRecognitionService(
            environment,
            shazamDiscovery,
            NullLogger<ShazamRecognitionService>.Instance);

        _organizer = new AutoTagLibraryOrganizer(
            NullLogger<AutoTagLibraryOrganizer>.Instance,
            NullLoggerFactory.Instance,
            settingsService,
            shazamRecognition);
    }

    public void Dispose()
    {
        _configScope.Dispose();
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task OrganizePathAsync_RemovesEmptyArtistFolder_WhenRemoveEmptyFoldersIsEnabled()
    {
        var (libraryRoot, emptyArtistDir) = await BuildLibraryAsync("nonbatch");

        await _organizer.OrganizePathAsync(
            libraryRoot,
            new AutoTagOrganizerOptions { RemoveEmptyFolders = true },
            log: null,
            CancellationToken.None);

        Assert.False(
            Directory.Exists(emptyArtistDir),
            "the non-batch path honours RemoveEmptyFolders");
    }

    [Fact]
    public async Task OrganizePathInBatchesAsync_RemovesEmptyArtistFolder_WhenRemoveEmptyFoldersIsEnabled()
    {
        var (libraryRoot, emptyArtistDir) = await BuildLibraryAsync("batch");

        await _organizer.OrganizePathInBatchesAsync(
            libraryRoot,
            new AutoTagOrganizerOptions { RemoveEmptyFolders = true },
            batchSize: 40,
            log: null,
            CancellationToken.None);

        Assert.False(
            Directory.Exists(emptyArtistDir),
            "folder uniformity runs in batch mode and must still honour RemoveEmptyFolders");
    }

    [Fact]
    public async Task OrganizePathInBatchesAsync_KeepsEmptyArtistFolder_WhenRemoveEmptyFoldersIsDisabled()
    {
        var (libraryRoot, emptyArtistDir) = await BuildLibraryAsync("batch-off");

        await _organizer.OrganizePathInBatchesAsync(
            libraryRoot,
            new AutoTagOrganizerOptions { RemoveEmptyFolders = false },
            batchSize: 40,
            log: null,
            CancellationToken.None);

        Assert.True(Directory.Exists(emptyArtistDir));
    }

    [Fact]
    public async Task OrganizePathInBatchesAsync_DoesNotQuarantineNonAudioFolders_ByDefault()
    {
        var (libraryRoot, _) = await BuildLibraryAsync("scans");
        var scansDir = Path.Join(libraryRoot, "Artist A", "Album A", "Scans");
        Directory.CreateDirectory(scansDir);
        var bookletPath = Path.Join(scansDir, "booklet.txt");
        await System.IO.File.WriteAllTextAsync(bookletPath, "booklet");

        await _organizer.OrganizePathInBatchesAsync(
            libraryRoot,
            new AutoTagOrganizerOptions { RemoveEmptyFolders = true },
            batchSize: 40,
            log: null,
            CancellationToken.None);

        Assert.True(
            System.IO.File.Exists(bookletPath),
            "artwork/booklet folders must not be swept into the duplicates folder unless explicitly enabled");
    }

    [Fact]
    public async Task OrganizePathInBatchesAsync_MovesAlbumLeftoversAfterTheFinalBatch()
    {
        var libraryRoot = Path.Join(_tempRoot, "leftovers");
        var artistDir = Path.Join(libraryRoot, "Artist A");
        Directory.CreateDirectory(artistDir);

        var trackPath = Path.Join(artistDir, "track.flac");
        await CreateAudioAsync(trackPath);
        using (var file = TagLib.File.Create(trackPath))
        {
            file.Tag.Title = "Track A";
            file.Tag.Album = "Album A";
            file.Tag.Performers = ["Artist A"];
            file.Tag.AlbumArtists = ["Artist A"];
            file.Tag.Track = 1;
            file.Save();
        }

        var coverPath = Path.Join(artistDir, "cover.jpg");
        await System.IO.File.WriteAllTextAsync(coverPath, "cover");

        await _organizer.OrganizePathInBatchesAsync(
            libraryRoot,
            new AutoTagOrganizerOptions { RemoveEmptyFolders = true, MoveMisplacedFiles = true },
            batchSize: 40,
            log: null,
            CancellationToken.None);

        var movedCover = Path.Join(artistDir, "Album A", "cover.jpg");
        Assert.True(
            System.IO.File.Exists(movedCover),
            "leftover album files must follow the audio once the final batch has run");
        Assert.False(System.IO.File.Exists(coverPath));
    }

    [Fact]
    public async Task OrganizePathInBatchesAsync_MovesConfiguredArtworkSidecarsWithRenamedAlbum()
    {
        var settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = settingsService.LoadSettings();
        settings.CoverImageTemplate = "%artist% - %album%";
        settings.ArtistImageTemplate = "%artist%";
        settings.AnimatedArtworkSquareFileName = "motion";
        settings.AnimatedArtworkTallFileName = "motion_tall";
        settingsService.SaveSettings(settings);

        var libraryRoot = Path.Join(_tempRoot, "configured-artwork");
        var sourceArtistDir = Path.Join(libraryRoot, "artist a");
        var sourceAlbumDir = Path.Join(sourceArtistDir, "album a");
        Directory.CreateDirectory(sourceAlbumDir);

        var trackPath = Path.Join(sourceAlbumDir, "bad-name.flac");
        await CreateAudioAsync(trackPath);
        using (var file = TagLib.File.Create(trackPath))
        {
            file.Tag.Title = "Track A";
            file.Tag.Album = "Album A";
            file.Tag.Performers = ["Artist A"];
            file.Tag.AlbumArtists = ["Artist A"];
            file.Tag.Track = 1;
            file.Save();
        }

        var configuredCover = Path.Join(sourceAlbumDir, "Artist A - Album A.jpg");
        var configuredAnimatedSquare = Path.Join(sourceAlbumDir, "motion.webp");
        var configuredAnimatedTall = Path.Join(sourceAlbumDir, "motion_tall.gif");
        var configuredArtistArt = Path.Join(sourceArtistDir, "Artist A.png");
        await System.IO.File.WriteAllTextAsync(configuredCover, "cover");
        await System.IO.File.WriteAllTextAsync(configuredAnimatedSquare, "square");
        await System.IO.File.WriteAllTextAsync(configuredAnimatedTall, "tall");
        await System.IO.File.WriteAllTextAsync(configuredArtistArt, "artist");

        await _organizer.OrganizePathInBatchesAsync(
            libraryRoot,
            new AutoTagOrganizerOptions
            {
                RemoveEmptyFolders = true,
                MoveMisplacedFiles = true,
                RenameFilesToTemplate = true
            },
            batchSize: 40,
            log: null,
            CancellationToken.None);

        var movedTrack = Directory.EnumerateFiles(libraryRoot, "*.flac", SearchOption.AllDirectories)
            .Single(path => !string.Equals(path, trackPath, StringComparison.OrdinalIgnoreCase));
        var destinationAlbumDir = Path.GetDirectoryName(movedTrack)!;
        var destinationArtistDir = Directory.GetParent(destinationAlbumDir)!.FullName;
        Assert.True(System.IO.File.Exists(Path.Join(destinationAlbumDir, "Artist A - Album A.jpg")));
        Assert.True(System.IO.File.Exists(Path.Join(destinationAlbumDir, "motion.webp")));
        Assert.True(System.IO.File.Exists(Path.Join(destinationAlbumDir, "motion_tall.gif")));
        Assert.True(System.IO.File.Exists(Path.Join(destinationArtistDir, "Artist A.png")));
        Assert.False(System.IO.File.Exists(configuredCover));
        Assert.False(System.IO.File.Exists(configuredAnimatedSquare));
        Assert.False(System.IO.File.Exists(configuredAnimatedTall));
        Assert.False(System.IO.File.Exists(configuredArtistArt));
    }

    [Fact]
    public async Task OrganizePathInBatchesAsync_NeverSweepsTheLibraryRootIntoAnAlbumFolder()
    {
        var libraryRoot = Path.Join(_tempRoot, "rootsweep");
        Directory.CreateDirectory(libraryRoot);

        var trackPath = Path.Join(libraryRoot, "track.flac");
        await CreateAudioAsync(trackPath);
        using (var file = TagLib.File.Create(trackPath))
        {
            file.Tag.Title = "Track A";
            file.Tag.Album = "Album A";
            file.Tag.Performers = ["Artist A"];
            file.Tag.AlbumArtists = ["Artist A"];
            file.Tag.Track = 1;
            file.Save();
        }

        var rootFilePath = Path.Join(libraryRoot, "library-notes.txt");
        await System.IO.File.WriteAllTextAsync(rootFilePath, "notes");

        await _organizer.OrganizePathInBatchesAsync(
            libraryRoot,
            new AutoTagOrganizerOptions { RemoveEmptyFolders = true, MoveMisplacedFiles = true },
            batchSize: 40,
            log: null,
            CancellationToken.None);

        Assert.True(
            System.IO.File.Exists(rootFilePath),
            "files sitting at the library root must never be swept into an album folder");
    }

    [Fact]
    public async Task OrganizeFilesAsync_DoesNotRunRootWideCleanup_ForBatchScopedCallers()
    {
        var (libraryRoot, emptyArtistDir) = await BuildLibraryAsync("scoped");
        var files = Directory.EnumerateFiles(libraryRoot, "*.flac", SearchOption.AllDirectories).ToList();

        await _organizer.OrganizeFilesAsync(
            libraryRoot,
            files,
            new AutoTagOrganizerOptions { RemoveEmptyFolders = true, BatchScopedFilesOnly = true },
            log: null,
            CancellationToken.None);

        Assert.True(
            Directory.Exists(emptyArtistDir),
            "batch-scoped callers such as download moves must never sweep the whole library root");
    }

    private async Task<(string LibraryRoot, string EmptyArtistDir)> BuildLibraryAsync(string name)
    {
        var libraryRoot = Path.Join(_tempRoot, name);
        var albumDir = Path.Join(libraryRoot, "Artist A", "Album A");
        Directory.CreateDirectory(albumDir);

        var trackPath = Path.Join(albumDir, "track.flac");
        await CreateAudioAsync(trackPath);
        using (var file = TagLib.File.Create(trackPath))
        {
            file.Tag.Title = "Track A";
            file.Tag.Album = "Album A";
            file.Tag.Performers = ["Artist A"];
            file.Tag.AlbumArtists = ["Artist A"];
            file.Tag.Track = 1;
            file.Save();
        }

        var emptyArtistDir = Path.Join(libraryRoot, "Empty Artist");
        Directory.CreateDirectory(emptyArtistDir);
        return (libraryRoot, emptyArtistDir);
    }

    private static async Task CreateAudioAsync(string path)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
                 {
                     "-loglevel", "error", "-y",
                     "-f", "lavfi",
                     "-i", "sine=frequency=440:sample_rate=44100:duration=1",
                     "-ac", "2", "-sample_fmt", "s16",
                     path
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo);
        Assert.NotNull(process);
        await process!.WaitForExitAsync();
        var error = await process.StandardError.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
