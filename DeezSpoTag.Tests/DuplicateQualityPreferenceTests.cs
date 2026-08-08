using System;
using System.IO;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using TagLib;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DuplicateQualityPreferenceTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "deezspotag-dupe-quality",
        Guid.NewGuid().ToString("N"));

    public DuplicateQualityPreferenceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
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

    [Fact]
    public async Task ShouldPreferIncoming_PrefersLosslessOverLossyEvenWhenTheLossyFileIsBetterTagged()
    {
        var lossless = Path.Join(_root, "lossless.flac");
        var lossy = Path.Join(_root, "lossy.mp3");
        await CreateAudioAsync(lossless, "-sample_fmt", "s16");
        await CreateAudioAsync(lossy, "-b:a", "128k");

        TagFile(lossless, tag => tag.Title = "Song");
        TagFile(lossy, tag =>
        {
            tag.Title = "Song";
            tag.Album = "Album";
            tag.ISRC = "USUM71234567";
            tag.Performers = ["Artist"];
            tag.AlbumArtists = ["Artist"];
            tag.Track = 3;
            tag.Disc = 1;
        });

        Assert.False(AudioCollisionDedupe.ShouldPreferIncoming(lossless, lossy));
        Assert.True(AudioCollisionDedupe.ShouldPreferIncoming(lossy, lossless));
    }

    [Fact]
    public async Task ShouldPreferIncoming_FallsBackToTagCompletenessWhenQualityTies()
    {
        var sparse = Path.Join(_root, "sparse.flac");
        var rich = Path.Join(_root, "rich.flac");
        await CreateAudioAsync(sparse, "-sample_fmt", "s16");
        await CreateAudioAsync(rich, "-sample_fmt", "s16");

        TagFile(sparse, tag => tag.Title = "Song");
        TagFile(rich, tag =>
        {
            tag.Title = "Song";
            tag.Album = "Album";
            tag.ISRC = "USUM71234567";
            tag.Performers = ["Artist"];
            tag.Track = 3;
        });

        Assert.True(AudioCollisionDedupe.ShouldPreferIncoming(sparse, rich));
        Assert.False(AudioCollisionDedupe.ShouldPreferIncoming(rich, sparse));
    }

    [Fact]
    public async Task ShouldPreferIncoming_PrefersHiResOverCdLossless()
    {
        var cd = Path.Join(_root, "cd.flac");
        var hiRes = Path.Join(_root, "hires.flac");
        await CreateAudioAsync(cd, "-sample_fmt", "s16", "-ar", "44100");
        await CreateAudioAsync(hiRes, "-sample_fmt", "s32", "-ar", "96000");

        Assert.True(AudioCollisionDedupe.ShouldPreferIncoming(cd, hiRes));
        Assert.False(AudioCollisionDedupe.ShouldPreferIncoming(hiRes, cd));
    }

    private static async Task CreateAudioAsync(string path, params string[] encodeArguments)
    {
        string[] baseArguments =
        [
            "-loglevel", "error",
            "-y",
            "-f", "lavfi",
            "-i", "sine=frequency=440:sample_rate=44100:duration=1",
            "-ac", "2"
        ];
        await RunProcessAsync("ffmpeg", [.. baseArguments, .. encodeArguments, path]);
        Assert.True(System.IO.File.Exists(path), $"ffmpeg did not produce {path}");
    }

    private static void TagFile(string path, Action<TagLib.Tag> configure)
    {
        using var file = TagLib.File.Create(path);
        configure(file.Tag);
        file.Save();
    }

    private static async Task RunProcessAsync(string executable, string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo);
        Assert.NotNull(process);
        await process!.WaitForExitAsync();
        var error = await process.StandardError.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, error);
    }
}
