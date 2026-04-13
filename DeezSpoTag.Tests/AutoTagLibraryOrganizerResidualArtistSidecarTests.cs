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
public sealed class AutoTagLibraryOrganizerResidualArtistSidecarTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly TestConfigRootScope _configScope;
    private readonly AutoTagLibraryOrganizer _organizer;

    public AutoTagLibraryOrganizerResidualArtistSidecarTests()
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

        var shazamDiscovery = new ShazamDiscoveryService(new HttpClient(), NullLogger<ShazamDiscoveryService>.Instance);
        var shazamRecognition = new ShazamRecognitionService(
            new StubWebHostEnvironment(_tempRoot),
            shazamDiscovery,
            NullLogger<ShazamRecognitionService>.Instance);

        _organizer = new AutoTagLibraryOrganizer(
            NullLogger<AutoTagLibraryOrganizer>.Instance,
            NullLoggerFactory.Instance,
            settingsService,
            shazamRecognition);
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
