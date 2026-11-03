using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class AutoTagLibraryOrganizerResidualArtistSidecarTest : IDisposable
{
    private readonly string _tempRoot;
    private readonly TestConfigRootScope _configScope;
    private readonly AutoTagLibraryOrganizer _organizer;

    public AutoTagLibraryOrganizerResidualArtistSidecarTest()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-organizer-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _configScope = new TestConfigRootScope(_tempRoot);

        var settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = settingsService.LoadSettings();
        settings.CreateArtistFolder = true;
        settings.CreateAlbumFolder = true;
        settings.Tags ??= new TagSettings();
        settings.Tags.SingleAlbumArtist = true;
        settingsService.SaveSettings(settings);

        var environment = new StubWebHostEnvironment(_tempRoot);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FinalTagsApplyTheSavedAlbumAndAudioTemplatesTogether(bool sourceFoldersAreProvisional)
    {
        var staging = Path.Join(_tempRoot, "staging");
        var oldAlbum = Path.Join(staging, "Artist", "Provisional Album");
        Directory.CreateDirectory(oldAlbum);
        var audio = Path.Join(oldAlbum, "old.wav");
        DeliveredAudioQualityGuardTest.WriteWave(audio, 16);
        using (var file = TagLib.File.Create(audio))
        {
            file.Tag.Title = "Final Title"; file.Tag.Performers = ["Artist"];
            file.Tag.AlbumArtists = ["Artist"]; file.Tag.Album = "Final Album";
            file.Tag.Year = 1997; file.Tag.Track = 7; file.Tag.TrackCount = 12; file.Save();
        }
        await File.WriteAllTextAsync(Path.ChangeExtension(audio, ".lrc"), "lyrics");
        var options = new AutoTagOrganizerOptions
        {
            BatchScopedFilesOnly = true, MoveMisplacedFiles = true, RenameFilesToTemplate = true,
            SourceFoldersAreProvisional = sourceFoldersAreProvisional, OnlyMoveWhenTagged = true,
            CreateArtistFolderOverride = true, ArtistNameTemplateOverride = "%artist%",
            CreateAlbumFolderOverride = true, AlbumNameTemplateOverride = "%year% - %album%",
            TracknameTemplateOverride = "%tracknumber% - %title%"
        };
        var report = await _organizer.OrganizeFilesWithReportAsync(staging, [audio], options, null, default);
        var expected = Path.Join(staging, "Artist", "1997 - Final Album", "07 - Final Title.wav");
        if (!sourceFoldersAreProvisional)
        {
            Assert.True(File.Exists(audio));
            Assert.False(File.Exists(expected));
            Assert.True(File.Exists(Path.ChangeExtension(audio, ".lrc")));
            return;
        }
        Assert.True(File.Exists(expected), string.Join(Environment.NewLine, report.Entries));
        Assert.True(File.Exists(Path.ChangeExtension(expected, ".lrc")));
        Assert.False(File.Exists(audio));
        using var result = TagLib.File.Create(expected);
        Assert.Equal("Final Album", result.Tag.Album);
        Assert.Equal("Final Title", result.Tag.Title);
    }

    [Fact]
    public async Task OrganizePathAsync_MovesResidualArtistArtworkAndDeletesOldArtistFolder()
    {
        var libraryRoot = Path.Join(_tempRoot, "library");
        var sourceArtistDir = Path.Join(libraryRoot, "Alpha & Beta");
        var sourceAlbumDir = Path.Join(sourceArtistDir, "Greatest Hits");
        Directory.CreateDirectory(sourceAlbumDir);

        var audioPath = Path.Join(sourceAlbumDir, "01 - Anthem.mp3");
        var artistArtworkPath = Path.Join(sourceArtistDir, "artist.jpg");
        await File.WriteAllTextAsync(audioPath, "not-real-audio");
        await File.WriteAllTextAsync(artistArtworkPath, "fake-jpg");

        var options = new AutoTagOrganizerOptions
        {
            MoveMisplacedFiles = true,
            RenameFilesToTemplate = true,
            RemoveEmptyFolders = true,
            UsePrimaryArtistFoldersOverride = true,
            CreateArtistFolderOverride = true,
            CreateAlbumFolderOverride = true
        };

        await _organizer.OrganizePathAsync(libraryRoot, options);

        var destinationArtistDir = Path.Join(libraryRoot, "Alpha");
        Assert.False(Directory.Exists(sourceArtistDir));
        Assert.True(Directory.Exists(destinationArtistDir));
        Assert.True(File.Exists(Path.Join(destinationArtistDir, "artist.jpg")));
    }

    [Fact]
    public async Task OrganizePathAsync_MergesNoAudioArtistVariantFolderIntoMatchingArtistFolder()
    {
        var libraryRoot = Path.Join(_tempRoot, "library-variant");
        var canonicalArtistDir = Path.Join(libraryRoot, "Dantez 254", "Ala - Single");
        var legacyArtistDir = Path.Join(libraryRoot, "Dantez254");
        Directory.CreateDirectory(canonicalArtistDir);
        Directory.CreateDirectory(legacyArtistDir);

        var canonicalTrackPath = Path.Join(canonicalArtistDir, "01 - Ala.mp3");
        var legacyArtworkPath = Path.Join(legacyArtistDir, "folder.jpg");
        await File.WriteAllTextAsync(canonicalTrackPath, "not-real-audio");
        await File.WriteAllTextAsync(legacyArtworkPath, "legacy-artwork");

        var options = new AutoTagOrganizerOptions
        {
            IncludeSubfolders = false,
            MoveMisplacedFiles = true,
            RenameFilesToTemplate = true,
            RemoveEmptyFolders = true,
            UsePrimaryArtistFoldersOverride = true,
            CreateArtistFolderOverride = true,
            CreateAlbumFolderOverride = true
        };

        await _organizer.OrganizePathAsync(libraryRoot, options);

        Assert.False(Directory.Exists(legacyArtistDir));
        Assert.True(File.Exists(Path.Join(libraryRoot, "Dantez 254", "folder.jpg")));
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
        catch
        {
            // Best-effort cleanup.
        }
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public StubWebHostEnvironment(string rootPath)
        {
            ContentRootPath = rootPath;
            ContentRootFileProvider = new PhysicalFileProvider(rootPath);
            WebRootPath = rootPath;
            WebRootFileProvider = new PhysicalFileProvider(rootPath);
        }

        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
    }
}
