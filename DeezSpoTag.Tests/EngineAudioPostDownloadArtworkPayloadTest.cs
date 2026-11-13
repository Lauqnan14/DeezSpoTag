using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class EngineAudioPostDownloadArtworkPayloadTest : IDisposable
{
    private readonly string _tempRoot = Path.Join(
        Path.GetTempPath(),
        $"deezspotag-artwork-payload-{Guid.NewGuid():N}");

    [Fact]
    public async System.Threading.Tasks.Task ConsecutiveResumedFilesWaitForLyricsAndKeepArtworkAndTags()
    {
        Directory.CreateDirectory(_tempRoot);
        var seed = Path.Join(_tempRoot, "seed.flac");
        var command = new System.Diagnostics.ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (var argument in new[] { "-loglevel", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "0.1", seed }) command.ArgumentList.Add(argument);
        using (var process = System.Diagnostics.Process.Start(command)!)
        {
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, error);
        }
        var helper = typeof(EngineAudioPostDownloadHelper);
        var checkpoint = helper.Assembly.GetType("DeezSpoTag.Services.Download.Shared.DownloadLifecycleCheckpoint")!;
        var gates = helper.GetField("PrefetchGates", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var gateType = helper.GetNestedType("PrefetchGateState", BindingFlags.NonPublic)!;
        var resultType = helper.GetNestedType("PrefetchCompletionResult", BindingFlags.NonPublic)!;
        var settingsService = new DeezSpoTag.Services.Settings.DeezSpoTagSettingsService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>.Instance);
        using var services = new ServiceCollection().AddSingleton(new DeezSpoTag.Services.Download.Utils.AudioTagger(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Download.Utils.AudioTagger>.Instance, settingsService)).BuildServiceProvider();
        for (var index = 0; index < 2; index++)
        {
            var id = Guid.NewGuid().ToString("N");
            var output = Path.Join(_tempRoot, $"Track{index}.flac");
            File.Copy(seed, output);
            var cover = Path.Join(_tempRoot, $"cover{index}.jpg");
            using (var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(32, 32)) image.SaveAsJpeg(cover);
            var payload = new QobuzQueueItem { Title = $"Track{index}", Artist = "Artist", Album = "Album", AlbumArtist = "Artist", Engine = "qobuz", Quality = "6", AudioAcquired = true, AcquiredAudioPath = output, AcquiredRequestedQuality = "6", AcquiredEngine = "qobuz" };
            // Reload the persisted checkpoint, as a restarted worker would do.
            payload = System.Text.Json.JsonSerializer.Deserialize<QobuzQueueItem>(System.Text.Json.JsonSerializer.Serialize(payload))!;
            object?[] resume = [payload, null];
            Assert.True((bool)checkpoint.GetMethod("TryResume")!.Invoke(null, resume)!);
            Assert.Equal(output, resume[1]);
            var track = new DeezSpoTag.Core.Models.Track { Title = payload.Title, MainArtist = new DeezSpoTag.Core.Models.Artist("Artist"), Album = new DeezSpoTag.Core.Models.Album("Album") };
            var paths = new PathGenerationResult { FilePath = _tempRoot, Filename = $"Track{index}", WritePath = output, CoverPath = _tempRoot, ArtistPath = _tempRoot, ExtrasPath = _tempRoot };
            var context = new EngineAudioPostDownloadHelper.EngineTrackContext(track, paths, _tempRoot, "%title%");
            var settings = new DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings { SyncedLyrics = true,
                Tags = new DeezSpoTag.Core.Models.Settings.TagSettings { Cover = true, Lyrics = true, SyncedLyrics = true } };
            var gate = Activator.CreateInstance(gateType, [true])!;
            gates.GetType().GetMethod("TryAdd")!.Invoke(gates, [id, gate]);
            try
            {
                var ready = Activator.CreateInstance(resultType, [true, null, cover]);
                var artwork = gateType.GetProperty("ArtworkCompletion")!.GetValue(gate)!;
                artwork.GetType().GetMethod("TrySetResult")!.Invoke(artwork, [ready]);
                var finalizing = EngineAudioPostDownloadHelper.ApplyPostDownloadSettingsAsync(
                    new(id, context, payload, output, settings, services, "qobuz", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
                Assert.False(finalizing.IsCompleted);
                await File.WriteAllTextAsync(Path.ChangeExtension(output, ".lrc"), "[00:01.00]Verified lyrics");
                ((System.Threading.Tasks.TaskCompletionSource)gateType.GetProperty("LyricsCompletion")!.GetValue(gate)!).TrySetResult();
                await finalizing.WaitAsync(TimeSpan.FromSeconds(15));
                var all = gateType.GetProperty("Completion")!.GetValue(gate)!;
                all.GetType().GetMethod("TrySetResult")!.Invoke(all, [ready]);
                await EngineAudioPostDownloadHelper.AwaitRemainingPrefetchAsync(id);
                using var tagged = TagLib.File.Create(output);
                Assert.Equal(payload.Title, tagged.Tag.Title);
                Assert.NotEmpty(tagged.Tag.Pictures);
                Assert.Contains("Verified lyrics", tagged.Tag.Lyrics);
                Assert.True(payload.AudioAcquired);
                Assert.Contains(payload.Files, file => file.TryGetValue("path", out var path) && path?.ToString() == output);
            }
            finally { EngineAudioPostDownloadHelper.ClearPrefetchState(id); }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task FailedArtworkReleasesSettledGateSoFinalizationCanRetry()
    {
        var helper = typeof(EngineAudioPostDownloadHelper);
        var gateType = helper.GetNestedType("PrefetchGateState", BindingFlags.NonPublic)!;
        var resultType = helper.GetNestedType("PrefetchCompletionResult", BindingFlags.NonPublic)!;
        var gates = helper.GetField("PrefetchGates", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var id = Guid.NewGuid().ToString("N");
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var gate = Activator.CreateInstance(gateType, [true])!;
                Assert.True((bool)gates.GetType().GetMethod("TryAdd")!.Invoke(gates, [id, gate])!);
                var result = Activator.CreateInstance(resultType, [attempt == 1, attempt == 0 ? "Network failed" : null, attempt == 1 ? "ready-cover.jpg" : null]);
                foreach (var property in new[] { "ArtworkCompletion", "Completion" })
                {
                    var completion = gateType.GetProperty(property)!.GetValue(gate)!;
                    completion.GetType().GetMethod("TrySetResult")!.Invoke(completion, [result]);
                }
                var wait = (System.Threading.Tasks.Task)helper.GetMethod("AwaitRequiredArtworkBeforeTaggingAsync", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, [id, System.Threading.CancellationToken.None])!;
                await wait.WaitAsync(TimeSpan.FromSeconds(2));
                var value = wait.GetType().GetProperty("Result")!.GetValue(wait)!;
                Assert.Equal(attempt == 0 ? "Network failed" : null, value.GetType().GetProperty("FailureReason")!.GetValue(value));
                if (attempt == 1) await EngineAudioPostDownloadHelper.AwaitRemainingPrefetchAsync(id);
                Assert.False((bool)gates.GetType().GetMethod("ContainsKey")!.Invoke(gates, [id])!);
            }
        }
        finally { EngineAudioPostDownloadHelper.ClearPrefetchState(id); }
    }

    [Fact]
    public async System.Threading.Tasks.Task ArtworkReadinessDoesNotWaitForLyricsOrLoseCompletionGate()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        var helper = typeof(EngineAudioPostDownloadHelper);
        var gateType = helper.GetNestedType("PrefetchGateState", BindingFlags.NonPublic)!;
        var resultType = helper.GetNestedType("PrefetchCompletionResult", BindingFlags.NonPublic)!;
        var gate = Activator.CreateInstance(gateType, [true])!;
        var gates = helper.GetField("PrefetchGates", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var id = Guid.NewGuid().ToString("N");
        gates.GetType().GetMethod("TryAdd")!.Invoke(gates, [id, gate]);
        try
        {
            var ready = Activator.CreateInstance(resultType, [true, null, "ready-cover.jpg"]);
            var artworkCompletion = gateType.GetProperty("ArtworkCompletion", flags);
            Assert.NotNull(artworkCompletion);
            var artwork = artworkCompletion!.GetValue(gate)!;
            artwork.GetType().GetMethod("TrySetResult")!.Invoke(artwork, [ready]);
            var wait = (System.Threading.Tasks.Task)helper.GetMethod("AwaitRequiredArtworkBeforeTaggingAsync",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [id, System.Threading.CancellationToken.None])!;
            await wait.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True((bool)gates.GetType().GetMethod("ContainsKey")!.Invoke(gates, [id])!);
            var remaining = EngineAudioPostDownloadHelper.AwaitRemainingPrefetchAsync(id);
            Assert.False(remaining.IsCompleted);
            var completion = gateType.GetProperty("Completion", flags)!.GetValue(gate)!;
            completion.GetType().GetMethod("TrySetResult")!.Invoke(completion, [ready]);
            await remaining.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False((bool)gates.GetType().GetMethod("ContainsKey")!.Invoke(gates, [id])!);
        }
        finally { EngineAudioPostDownloadHelper.ClearPrefetchState(id); }
    }

    [Fact]
    public void UpdateAudioPayloadFiles_PreservesGeneratedArtworkWithConfiguredFilename()
    {
        var albumPath = Path.Join(_tempRoot, "Artist", "Album");
        var outputPath = Path.Join(albumPath, "Track.flac");
        var configuredArtworkPath = Path.Join(albumPath, "Artist - Album.png");
        Directory.CreateDirectory(albumPath);
        File.WriteAllText(outputPath, "audio");
        File.WriteAllText(configuredArtworkPath, "artwork");
        var payload = new QobuzQueueItem
        {
            Files =
            [
                new Dictionary<string, object>
                {
                    ["path"] = configuredArtworkPath,
                    ["albumPath"] = albumPath,
                    ["artistPath"] = Path.GetDirectoryName(albumPath)!,
                    ["type"] = "artwork"
                }
            ]
        };

        EngineAudioPostDownloadHelper.UpdateAudioPayloadFiles(
            payload,
            new PathGenerationResult
            {
                FilePath = albumPath,
                ArtistPath = Path.GetDirectoryName(albumPath)
            },
            outputPath);

        Assert.Contains(payload.Files, file =>
            string.Equals(file["path"].ToString(), outputPath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(payload.Files, file =>
            string.Equals(file["path"].ToString(), configuredArtworkPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(file["type"].ToString(), "artwork", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UpdateAudioPayloadFiles_DoesNotClaimUntrackedAlbumImage()
    {
        var albumPath = Path.Join(_tempRoot, "Artist", "Album");
        var outputPath = Path.Join(albumPath, "Track.flac");
        var unrelatedImagePath = Path.Join(albumPath, "untracked.jpg");
        Directory.CreateDirectory(albumPath);
        File.WriteAllText(outputPath, "audio");
        File.WriteAllText(unrelatedImagePath, "image");
        var payload = new QobuzQueueItem();

        EngineAudioPostDownloadHelper.UpdateAudioPayloadFiles(
            payload,
            new PathGenerationResult
            {
                FilePath = albumPath,
                ArtistPath = Path.GetDirectoryName(albumPath)
            },
            outputPath);

        Assert.DoesNotContain(payload.Files, file =>
            string.Equals(file["path"].ToString(), unrelatedImagePath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UpdateAudioPayloadFiles_UsesRecordedLyricsArtifactPathInsteadOfGuessingFromAudioName()
    {
        var albumPath = Path.Join(_tempRoot, "Artist", "Album");
        var outputPath = Path.Join(albumPath, "01 - Final Track.flac");
        var prefetchedLyricsPath = Path.Join(_tempRoot, "staging", "provider-name.lrc");
        var payload = new QobuzQueueItem
        {
            LyricsArtifacts = new LyricsArtifactState
            {
                FilesByFormat = new Dictionary<string, string> { ["lrc"] = prefetchedLyricsPath }
            }
        };

        EngineAudioPostDownloadHelper.UpdateAudioPayloadFiles(
            payload,
            new PathGenerationResult
            {
                FilePath = albumPath,
                ArtistPath = Path.GetDirectoryName(albumPath)
            },
            outputPath);

        Assert.Contains(payload.Files, file =>
            string.Equals(file["path"].ToString(), prefetchedLyricsPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AddGeneratedSidecars_MarksConfiguredArtworkForFinalization()
    {
        var albumPath = Path.Join(_tempRoot, "Artist", "Album");
        var configuredArtworkPath = Path.Join(albumPath, "Artist - Album.png");
        var files = new List<Dictionary<string, object>>();
        var method = GetPrivateStaticMethod("AddGeneratedSidecars");

        method.Invoke(null,
        [
            files,
            new[] { configuredArtworkPath },
            Array.Empty<string>(),
            new PathGenerationResult
            {
                FilePath = albumPath,
                ArtistPath = Path.GetDirectoryName(albumPath)
            }
        ]);

        var artwork = Assert.Single(files);
        Assert.Equal(configuredArtworkPath, artwork["path"]);
        Assert.Equal("artwork", artwork["type"]);
    }

    [Fact]
    public void CleanupTemporaryEmbeddedArtwork_RemovesOnlyItsQueueDirectory()
    {
        var buildDirectory = GetPrivateStaticMethod("BuildTemporaryEmbeddedArtworkDirectory");
        var cleanup = GetPrivateStaticMethod("CleanupTemporaryEmbeddedArtwork");
        var temporaryDirectory = Assert.IsType<string>(buildDirectory.Invoke(null, [Guid.NewGuid().ToString("N")]));
        var temporaryArtwork = Path.Join(temporaryDirectory, "cover.jpg");
        Directory.CreateDirectory(temporaryDirectory);
        File.WriteAllText(temporaryArtwork, "artwork");

        cleanup.Invoke(null, [temporaryArtwork]);

        Assert.False(Directory.Exists(temporaryDirectory));
    }

    private static MethodInfo GetPrivateStaticMethod(string name)
    {
        return typeof(EngineAudioPostDownloadHelper).GetMethod(
                   name,
                   BindingFlags.NonPublic | BindingFlags.Static)
               ?? throw new InvalidOperationException($"{name} was not found.");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}
