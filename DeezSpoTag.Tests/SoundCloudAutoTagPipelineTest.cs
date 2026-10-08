using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.SoundCloud;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Drives the real AutoTag pipeline with SoundCloud as the only platform, against real audio files.
/// </summary>
/// <remarks>
///     <para>
///         The matcher unit tests prove what <see cref="SoundcloudMatcher" /> returns. These prove the part
///         that actually matters and cannot be asserted in isolation: that the returned metadata reaches the
///         file through the existing writer, obeying overwrite policy, and that a SoundCloud pass touches only
///         SoundCloud's own identity fields.
///     </para>
///     <para>
///         Built the way the repository's existing end-to-end AutoTag test builds a runner: only the
///         collaborators the soundcloud pass needs are supplied. <c>PlatformRegistry</c> is left null, which
///         means the per-platform supported-tag intersection cannot narrow the configured tag list.
///     </para>
/// </remarks>
public sealed class SoundCloudAutoTagPipelineTest
{
    [Fact]
    public async Task ASoundCloudPassWritesItsOwnIdentityFieldsToTheFile()
    {
        await WithAudioFile("flac", async (path, directory) =>
        {
            await RunAsync(directory, path, overwrite: true,
                tags: new[] { "title", "artist", "genre", "url", "trackId" });

            Assert.Equal("Example", ReadTitle(path));
            Assert.Equal("soundcloud:tracks:1", ReadIdentity(path, "SOUNDCLOUD_TRACK_ID"));
            Assert.Equal("https://soundcloud.com/some-artist/some-track", ReadIdentity(path, "SOUNDCLOUD_URL"));
        });
    }

    [Fact]
    public async Task ASoundCloudPassDoesNotWriteIdentityFieldsThatWereNotRequested()
    {
        await WithAudioFile("flac", async (path, directory) =>
        {
            // trackId and url are absent from the requested set, so neither identity field may be written.
            await RunAsync(directory, path, overwrite: true, tags: new[] { "title", "artist" });

            Assert.Equal("Example", ReadTitle(path));
            Assert.Null(ReadIdentity(path, "SOUNDCLOUD_TRACK_ID"));
            Assert.Null(ReadIdentity(path, "SOUNDCLOUD_URL"));
        });
    }

    [Fact]
    public async Task AnExistingGenreIsPreservedWhenOverwriteIsOff()
    {
        await WithAudioFile("flac", async (path, directory) =>
        {
            SetGenres(path, new[] { "Personal Choice" });

            // overwrite=false and genre is not in overwriteTags, so the reader's own genre must survive even
            // though SoundCloud advertises one. The seeded title/artist are unchanged by this run.
            await RunAsync(directory, path, overwrite: false,
                tags: new[] { "title", "artist", "genre" },
                overwriteTags: Array.Empty<string>());

            Assert.Equal(new[] { "Personal Choice" }, ReadGenres(path));
        });
    }

    [Fact]
    public async Task TheGenreIsWrittenWhenTheUserOptsIntoOverwritingIt()
    {
        await WithAudioFile("flac", async (path, directory) =>
        {
            SetGenres(path, new[] { "Personal Choice" });

            // mergeGenres is off so an opted-in overwrite replaces rather than merging. On the default
            // merge behaviour SoundCloud's genre is added alongside the existing one, which the preceding
            // test covers.
            await RunAsync(directory, path, overwrite: false,
                tags: new[] { "title", "artist", "genre" },
                overwriteTags: new[] { "genre" },
                mergeGenres: false);

            Assert.Equal(new[] { "Hip-Hop" }, ReadGenres(path));
        });
    }

    [Fact]
    public async Task ASoundCloudPassNeverRemovesAnotherProvidersIdentityFields()
    {
        await WithAudioFile("flac", async (path, directory) =>
        {
            // Seed the identity fields of several other providers, exactly as earlier AutoTag runs would.
            SetIdentity(path, "SPOTIFY_TRACK_ID", "3AhXZa8sUQht0UEdBJgpGc");
            SetIdentity(path, "DEEZER_TRACK_ID", "14477354");
            SetIdentity(path, "TIDAL_TRACK_ID", "148583382");
            SetIdentity(path, "AUDIOMACK_TRACK_ID", "audiomack-id");
            SetIdentity(path, "SPOTIFY_URL", "https://open.spotify.com/track/3AhXZa8sUQht0UEdBJgpGc");

            await RunAsync(directory, path, overwrite: true,
                tags: new[] { "title", "artist", "url", "trackId" });

            // SoundCloud wrote its own field...
            Assert.Equal("soundcloud:tracks:1", ReadIdentity(path, "SOUNDCLOUD_TRACK_ID"));

            // ...and left every other provider's untouched.
            Assert.Equal("3AhXZa8sUQht0UEdBJgpGc", ReadIdentity(path, "SPOTIFY_TRACK_ID"));
            Assert.Equal("14477354", ReadIdentity(path, "DEEZER_TRACK_ID"));
            Assert.Equal("148583382", ReadIdentity(path, "TIDAL_TRACK_ID"));
            Assert.Equal("audiomack-id", ReadIdentity(path, "AUDIOMACK_TRACK_ID"));
            Assert.Equal("https://open.spotify.com/track/3AhXZa8sUQht0UEdBJgpGc", ReadIdentity(path, "SPOTIFY_URL"));
        });
    }

    [Fact]
    public async Task APassThatReturnsNoIdentityLeavesAnExistingSoundCloudIdentityIntact()
    {
        await WithAudioFile("flac", async (path, directory) =>
        {
            SetIdentity(path, "SOUNDCLOUD_TRACK_ID", "soundcloud:tracks:1");

            // The client resolves nothing, so the matcher returns no match and nothing should be written -
            // and in particular the identity already on the file must not be cleaned up.
            await RunAsync(directory, path, overwrite: true,
                tags: new[] { "title", "artist", "url", "trackId" },
                resolveReturnsNull: true);

            Assert.Equal("soundcloud:tracks:1", ReadIdentity(path, "SOUNDCLOUD_TRACK_ID"));
        });
    }

    /// <summary>Runs the real pipeline with SoundCloud as the only platform.</summary>
    private static async Task RunAsync(
        string directory,
        string path,
        bool overwrite,
        string[] tags,
        string[]? overwriteTags = null,
        bool resolveReturnsNull = false,
        bool mergeGenres = true)
    {
        var client = new StubClient
        {
            SearchResults = resolveReturnsNull
                ? Array.Empty<SoundCloudTrack>()
                : new List<SoundCloudTrack> { Track() }
        };

        var collaborators =
            (LocalAutoTagRunner.LocalAutoTagRunnerCollaborators)
                Activator.CreateInstance(typeof(LocalAutoTagRunner.LocalAutoTagRunnerCollaborators))!;

        void Set(string name, object value)
            => collaborators.GetType().GetProperty(name)!.SetValue(collaborators, value);

        // The runner converts any exception into a Failed result, so a recording logger is the only way to
        // see the stack when the harness itself is at fault.
        var exceptions = new List<Exception>();
        Set("Logger", new RecordingLogger<LocalAutoTagRunner>(exceptions));
        Set("SoundcloudMatcher", new SoundcloudMatcher(client, NullLogger<SoundcloudMatcher>.Instance));
        Set("ShazamRecognitionService",
            new ShazamRecognitionService(null!, null!, NullLogger<ShazamRecognitionService>.Instance));

        var runner = new LocalAutoTagRunner(collaborators);

        var configPath = Path.Combine(directory, "config.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            platforms = new[] { "soundcloud" },
            targetFiles = new[] { path },
            tags,
            overwrite,
            overwriteTags = overwriteTags ?? Array.Empty<string>(),
            mergeGenres,
            enableShazam = false,
            parseFilename = false,
            // Kept off so the run cannot fan out to the genre-intelligence stage, which needs collaborators
            // this harness deliberately does not provide.
            genreIntelligence = new { enabled = false }
        }));

        var logs = new List<string>();
        var outcome = await runner.RunAsync(
            "job",
            directory,
            configPath,
            _ => { },
            logs.Add,
            null,
            null,
            CancellationToken.None);

        Assert.NotNull(outcome);
        if (!outcome!.Success)
        {
            throw new InvalidOperationException(
                "AutoTag run failed. " + outcome.Error + " || logs: " + string.Join(" ~ ", logs)
                + " || captured: " + string.Join(" ~ ", exceptions.Select(e => e.ToString())));
        }
    }

    private static SoundCloudTrack Track() => new()
    {
        Id = 1,
        Urn = "soundcloud:tracks:1",
        Title = "Example",
        MetadataArtist = "Example Artist",
        UploaderUsername = "some-artist",
        Artist = "some-artist",
        Isrc = "ABCDE1234567",
        DurationMs = 200_000,
        Genre = "Hip-Hop",
        PermalinkUrl = "https://soundcloud.com/some-artist/some-track"
    };

    private static async Task WithAudioFile(string extension, Func<string, string, Task> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), "soundcloud-autotag-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "track." + extension);
            CreateSilentAudio(path);

            // Seed the embedded title and artist the run will look for. Besides being the realistic case -
            // a library file with basic tags being enriched - a trusted identity is what stops the pipeline
            // from invoking the Shazam recogniser, which this harness deliberately does not wire up.
            using (var file = TagLib.File.Create(path))
            {
                file.Tag.Title = "Example";
                file.Tag.Performers = ["Example Artist"];
                file.Save();
            }

            await body(path, directory);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private static void CreateSilentAudio(string path)
    {
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (var argument in new[]
        {
            "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "0.1", path
        })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    private static string? ReadTitle(string path)
    {
        using var file = TagLib.File.Create(path);
        return file.Tag.Title;
    }

    private static void SetGenres(string path, string[] genres)
    {
        using var file = TagLib.File.Create(path);
        file.Tag.Genres = genres;
        file.Save();
    }

    private static string[] ReadGenres(string path)
    {
        using var file = TagLib.File.Create(path);
        return file.Tag.Genres;
    }

    private static void SetIdentity(string path, string name, string value)
        => LocalAutoTagRunner.SetSemanticRawTagForTest(path, name, [value]);

    private static string? ReadIdentity(string path, string name)
        => LocalAutoTagRunner.ReadRawIdentityValue(path, name);

    /// <summary>Captures the exceptions the runner logs, which it otherwise turns into a Failed result.</summary>
    private sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        private readonly List<Exception> _exceptions;

        public RecordingLogger(List<Exception> exceptions) => _exceptions = exceptions;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                _exceptions.Add(exception);
            }
        }
    }

    private sealed class StubClient : ISoundCloudClient
    {
        public IReadOnlyList<SoundCloudTrack> SearchResults { get; init; } = Array.Empty<SoundCloudTrack>();

        public bool ResolveReturnsNull { get; init; }

        public Task<SoundCloudTrack?> ResolveTrackAsync(string url, CancellationToken cancellationToken)
            => Task.FromResult<SoundCloudTrack?>(ResolveReturnsNull ? null : Track());

        public Task<SoundCloudTrack?> ResolveTrackByIdAsync(string idOrUrn, CancellationToken cancellationToken)
            => Task.FromResult<SoundCloudTrack?>(ResolveReturnsNull ? null : Track());

        public Task<SoundCloudSet> ResolveSetAsync(string url, CancellationToken cancellationToken)
            => Task.FromResult(new SoundCloudSet());

        public Task<IReadOnlyList<SoundCloudTrack>> SearchTracksAsync(
            string query, int limit, CancellationToken cancellationToken)
            => Task.FromResult(SearchResults);

        public Task<SoundCloudStream> ResolveStreamAsync(
            SoundCloudTrack track, string? requestedQuality, CancellationToken cancellationToken)
            => Task.FromResult(new SoundCloudStream("https://example/playlist.m3u8", "sq"));

        public Task<bool> ValidateCredentialsAsync(string token, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }
}