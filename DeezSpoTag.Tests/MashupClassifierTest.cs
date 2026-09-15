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
using TagLib;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class MashupClassifierTest
{
    [Theory]
    [InlineData("Song A vs Song B")]
    [InlineData("Song A VS. Song B")]
    [InlineData("Best Mashup Ever")]
    [InlineData("mash-up of two hits")]
    [InlineData("Summer DJ Mix 2024")]
    [InlineData("dj-mix set")]
    [InlineData("Bootleg Remix")]
    [InlineData("Abbey Road Medley")]
    [InlineData("Artist \u00d7 Artist")]
    [InlineData("Artist \u2715 Artist")]
    public void IsMashupTitle_RecognisesMashupStyleReleases(string title)
    {
        Assert.True(MashupClassifier.IsMashupTitle(title));
    }

    [Theory]
    [InlineData("Ordinary Song")]
    [InlineData("Bassline")]
    [InlineData("Overture")]
    [InlineData("Miss You")]
    [InlineData("")]
    [InlineData(null)]
    public void IsMashupTitle_LeavesOrdinaryTitlesAlone(string? title)
    {
        Assert.False(MashupClassifier.IsMashupTitle(title));
    }

    [Fact]
    public void IsMashupFile_ClassifiesFromTheFileNameWhenTagsAreMissing()
    {
        Assert.True(MashupClassifier.IsMashupFile("/music/unsorted/Two Hits Mashup.flac"));
        Assert.False(MashupClassifier.IsMashupFile("/music/unsorted/Ordinary Track.flac"));
    }
}

[Collection("Settings Config Isolation")]
public sealed class MashupDedupeBehaviourTest : IDisposable
{
    private readonly string _root;
    private readonly TestConfigRootScope _configScope;
    private readonly DuplicateCleanerService _service;

    public MashupDedupeBehaviourTest()
    {
        _root = Path.Join(Path.GetTempPath(), "deezspotag-mashup-dedupe-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _configScope = new TestConfigRootScope(_root);
        var settings = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var environment = new StubWebHostEnvironment { ContentRootPath = _root, WebRootPath = _root };
        var discovery = new ShazamDiscoveryService(new HttpClient(), NullLogger<ShazamDiscoveryService>.Instance, environment);
        var recognition = new ShazamRecognitionService(environment, discovery, NullLogger<ShazamRecognitionService>.Instance);
        _service = new DuplicateCleanerService(recognition, settings, NullLogger<DuplicateCleanerService>.Instance);
    }

    [Fact]
    public async Task ScanAsync_ClustersOrdinaryFilesOnWeakTitleIdentity()
    {
        var first = Path.Join(_root, "Artist", "Album One", "Same Song.flac");
        var second = Path.Join(_root, "Artist", "Album Two", "Same Song.flac");
        await CreateAudioAsync(first, 440);
        await CreateAudioAsync(second, 660);
        TagIdentity(first, "Same Song", "Artist");
        TagIdentity(second, "Same Song", "Artist");

        var result = await _service.ScanAsync(
            [BuildFolder()],
            new DuplicateCleanerOptions { UseDuplicatesFolder = true },
            CancellationToken.None);

        Assert.Equal(2, result.FilesScanned);
        Assert.Single(result.Modifications);
    }

    [Fact]
    public async Task ScanAsync_NeverClustersMashupsOnWeakIdentityOnly()
    {
        var first = Path.Join(_root, "Artist", "Album One", "Song A vs Song B.flac");
        var second = Path.Join(_root, "Artist", "Album Two", "Song A vs Song B.flac");
        await CreateAudioAsync(first, 440);
        await CreateAudioAsync(second, 660);
        TagIdentity(first, "Song A vs Song B", "Artist");
        TagIdentity(second, "Song A vs Song B", "Artist");

        var result = await _service.ScanAsync(
            [BuildFolder()],
            new DuplicateCleanerOptions { UseDuplicatesFolder = true },
            CancellationToken.None);

        Assert.Equal(2, result.FilesScanned);
        Assert.Empty(result.Modifications);
    }

    [Fact]
    public async Task ScanAsync_StillClustersIdenticalMashupsOnStrongIdentity()
    {
        var first = Path.Join(_root, "Artist", "Album One", "Song A vs Song B.flac");
        var second = Path.Join(_root, "Artist", "Album Two", "Song A vs Song B.flac");
        await CreateAudioAsync(first, 440);
        TagIdentity(first, "Song A vs Song B", "Artist");
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        System.IO.File.Copy(first, second);

        var result = await _service.ScanAsync(
            [BuildFolder()],
            new DuplicateCleanerOptions { UseDuplicatesFolder = true },
            CancellationToken.None);

        Assert.Equal(2, result.FilesScanned);
        Assert.Single(result.Modifications);
    }

    private FolderDto BuildFolder()
        => new(1, _root, "Test", true, null, null, "FLAC", "default", true, false, null, null);

    private static void TagIdentity(string path, string title, string artist)
    {
        using var file = TagLib.File.Create(path);
        file.Tag.Title = title;
        file.Tag.Performers = [artist];
        file.Save();
    }

    private static async Task CreateAudioAsync(string path, int frequency)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
                 {
                     "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                     $"sine=frequency={frequency}:sample_rate=44100:duration=1", "-ac", "2", "-sample_fmt", "s16", path
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
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
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
