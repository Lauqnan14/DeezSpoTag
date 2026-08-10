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
public sealed class RepeatedTrackPrefixRepairTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly TestConfigRootScope _configScope;
    private readonly AutoTagLibraryOrganizer _organizer;

    public RepeatedTrackPrefixRepairTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-prefix-repair-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _configScope = new TestConfigRootScope(_tempRoot);

        var settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = settingsService.LoadSettings();
        settings.CreateArtistFolder = true;
        settings.CreateAlbumFolder = true;
        settings.AlbumTracknameTemplate = "%tracknumber% - %title%";
        settings.TracknameTemplate = "%tracknumber% - %title%";
        settings.Tags ??= new TagSettings();
        settings.Tags.SingleAlbumArtist = true;
        settingsService.SaveSettings(settings);

        var environment = new RepairStubWebHostEnvironment(_tempRoot);
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
    public async Task FolderUniformity_CollapsesRepeatedTrackNumberPrefixes()
    {
        var libraryRoot = Path.Join(_tempRoot, "library");
        var albumDir = Path.Join(libraryRoot, "Bee Thee Artiste", "Forget You");
        Directory.CreateDirectory(albumDir);

        const string mangledStem = "01 - 01 - 01 - 01 - 01 - Forget You";
        var audioPath = Path.Join(albumDir, mangledStem + ".flac");
        await WriteFlacWithoutTitleOrArtistAsync(audioPath);
        await File.WriteAllTextAsync(Path.Join(albumDir, mangledStem + ".lrc"), "[00:00.00] lyric");
        await File.WriteAllTextAsync(Path.Join(albumDir, mangledStem + ".ttml"), "<tt></tt>");

        await _organizer.OrganizePathAsync(
            libraryRoot,
            new AutoTagOrganizerOptions
            {
                IncludeSubfolders = true,
                MoveMisplacedFiles = true,
                RenameFilesToTemplate = true,
                TracknameTemplateOverride = "%tracknumber% - %title%",
                CreateArtistFolderOverride = true,
                CreateAlbumFolderOverride = true,
                ArtistNameTemplateOverride = "%artist%",
                AlbumNameTemplateOverride = "%album%"
            },
            null,
            CancellationToken.None);

        var audioFiles = Directory.GetFiles(libraryRoot, "*.flac", SearchOption.AllDirectories);
        var resulting = Assert.Single(audioFiles);
        var resultingStem = Path.GetFileNameWithoutExtension(resulting);

        Assert.DoesNotContain("01 - 01", resultingStem, StringComparison.Ordinal);
        Assert.Equal("01 - Forget You", resultingStem);
        Assert.Contains("Bee Thee Artiste", resulting, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown Artist", resulting, StringComparison.Ordinal);

        var albumFolder = Path.GetDirectoryName(resulting)!;
        Assert.True(File.Exists(Path.Join(albumFolder, "01 - Forget You.lrc")), "lrc sidecar was not renamed with the audio stem.");
        Assert.True(File.Exists(Path.Join(albumFolder, "01 - Forget You.ttml")), "ttml sidecar was not renamed with the audio stem.");
    }

    [Fact]
    public async Task FolderUniformity_RenameIsIdempotent()
    {
        var libraryRoot = Path.Join(_tempRoot, "library-idempotent");
        var albumDir = Path.Join(libraryRoot, "Bee Thee Artiste", "Forget You");
        Directory.CreateDirectory(albumDir);
        await WriteFlacWithoutTitleOrArtistAsync(Path.Join(albumDir, "01 - Forget You.flac"));

        var options = new AutoTagOrganizerOptions
        {
            IncludeSubfolders = true,
            MoveMisplacedFiles = true,
            RenameFilesToTemplate = true,
            TracknameTemplateOverride = "%tracknumber% - %title%",
            CreateArtistFolderOverride = true,
            CreateAlbumFolderOverride = true,
            ArtistNameTemplateOverride = "%artist%",
            AlbumNameTemplateOverride = "%album%"
        };

        await _organizer.OrganizePathAsync(libraryRoot, options, null, CancellationToken.None);
        await _organizer.OrganizePathAsync(libraryRoot, options, null, CancellationToken.None);

        var resulting = Assert.Single(Directory.GetFiles(libraryRoot, "*.flac", SearchOption.AllDirectories));
        Assert.Equal("01 - Forget You", Path.GetFileNameWithoutExtension(resulting));
    }

    [Fact]
    public async Task ShazamEnabled_StillRepairsFilesShazamCannotIdentify()
    {
        var libraryRoot = Path.Join(_tempRoot, "library-shazam");
        var albumDir = Path.Join(libraryRoot, "Bee Thee Artiste", "Forget You");
        Directory.CreateDirectory(albumDir);

        const string mangledStem = "01 - 01 - 01 - 01 - 01 - Forget You";
        await WriteFlacWithoutTitleOrArtistAsync(Path.Join(albumDir, mangledStem + ".flac"));

        await _organizer.OrganizePathAsync(
            libraryRoot,
            new AutoTagOrganizerOptions
            {
                IncludeSubfolders = true,
                MoveMisplacedFiles = true,
                RenameFilesToTemplate = true,
                UseShazamForUntaggedFiles = true,
                TracknameTemplateOverride = "%tracknumber% - %title%",
                CreateArtistFolderOverride = true,
                CreateAlbumFolderOverride = true,
                ArtistNameTemplateOverride = "%artist%",
                AlbumNameTemplateOverride = "%album%"
            },
            null,
            CancellationToken.None);

        var resulting = Assert.Single(Directory.GetFiles(libraryRoot, "*.flac", SearchOption.AllDirectories));
        Assert.Equal("01 - Forget You", Path.GetFileNameWithoutExtension(resulting));
        Assert.Contains("Bee Thee Artiste", resulting, StringComparison.Ordinal);
    }

    private static async Task WriteFlacWithoutTitleOrArtistAsync(string path)
    {
        await RunFfmpegAsync(
            "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo",
            "-t", "1",
            "-metadata", "album=Forget You",
            "-metadata", "track=1",
            "-metadata", "disc=1",
            "-c:a", "flac",
            path);
    }

    private sealed class RepairStubWebHostEnvironment : IWebHostEnvironment
    {
        public RepairStubWebHostEnvironment(string rootPath)
        {
            ContentRootPath = rootPath;
            ContentRootFileProvider = new PhysicalFileProvider(rootPath);
            WebRootPath = rootPath;
            WebRootFileProvider = new PhysicalFileProvider(rootPath);
        }

        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
    }

    private static async Task RunFfmpegAsync(params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo);
        Assert.NotNull(process);
        var stderr = process!.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stderr);
    }
}
