using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class DuplicateCleanerBatchScopeTest : IDisposable
{
    private readonly string _root;
    private readonly TestConfigRootScope _configScope;
    private readonly DuplicateCleanerService _service;

    public DuplicateCleanerBatchScopeTest()
    {
        _root = Path.Join(Path.GetTempPath(), "deezspotag-dedupe-batch-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _configScope = new TestConfigRootScope(_root);
        var settings = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var environment = new StubWebHostEnvironment { ContentRootPath = _root, WebRootPath = _root };
        var discovery = new ShazamDiscoveryService(new HttpClient(), NullLogger<ShazamDiscoveryService>.Instance, environment);
        var recognition = new ShazamRecognitionService(environment, discovery, NullLogger<ShazamRecognitionService>.Instance);
        _service = new DuplicateCleanerService(recognition, settings, NullLogger<DuplicateCleanerService>.Instance);
    }

    [Fact]
    public async Task ScanFilesAsync_NeverEnumeratesAnIdenticalSiblingOutsideTheBatch()
    {
        var album = Path.Join(_root, "Artist", "Album One");
        var secondAlbum = Path.Join(_root, "Artist", "Album Two");
        var outsideAlbum = Path.Join(_root, "Artist", "Outside");
        Directory.CreateDirectory(album);
        Directory.CreateDirectory(secondAlbum);
        Directory.CreateDirectory(outsideAlbum);
        var first = Path.Join(album, "track.flac");
        var second = Path.Join(secondAlbum, "track.flac");
        var outside = Path.Join(outsideAlbum, "track.flac");
        await CreateAudioAsync(first);
        File.Copy(first, second);
        File.Copy(first, outside);

        var result = await _service.ScanFilesAsync(
            [BuildFolder()],
            [first, second],
            new DuplicateCleanerOptions { UseDuplicatesFolder = true },
            CancellationToken.None);

        Assert.Equal(2, result.FilesScanned);
        var modification = Assert.Single(result.Modifications);
        Assert.Equal("quarantined", modification.OperationKind);
        Assert.Contains(modification.SourcePath, new[] { first, second });
        Assert.NotNull(modification.DestinationPath);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task ScanAsync_StillEnumeratesTheWholeFolder()
    {
        var first = Path.Join(_root, "Artist", "Album", "track.flac");
        var outside = Path.Join(_root, "Artist", "Outside", "track.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        await CreateAudioAsync(first);
        File.Copy(first, outside);

        var result = await _service.ScanAsync(
            [BuildFolder()],
            new DuplicateCleanerOptions { UseDuplicatesFolder = true },
            CancellationToken.None);

        Assert.Equal(2, result.FilesScanned);
        Assert.Single(result.Modifications);
    }

    private FolderDto BuildFolder()
        => new(1, _root, "Test", true, null, null, "FLAC", "default", true, false, null, null);

    private static async Task CreateAudioAsync(string path)
    {
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
                 {
                     "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                     "sine=frequency=440:sample_rate=44100:duration=1", "-ac", "2", "-sample_fmt", "s16", path
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        await process!.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
    }

    public void Dispose()
    {
        _configScope.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
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
