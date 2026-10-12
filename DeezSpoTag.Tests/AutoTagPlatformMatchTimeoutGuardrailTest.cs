using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services.AutoTag;
using DeezSpoTag.Web.Services.Audiomack;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagPlatformMatchTimeoutGuardrailTest
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OptionalArtistMetadataRequest_ContinuesUnlessTheJobWasCanceled(bool jobCanceled, bool synchronousFailure)
    {
        using var cancellation = new CancellationTokenSource();
        if (jobCanceled) cancellation.Cancel();
        var logs = new List<string>();
        var (runner, context) = CreateRunContext(cancellation.Token, logs);
        var method = typeof(LocalAutoTagRunner).GetMethod("RunBoundedOptionalStepAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Func<CancellationToken, Task> action = _ => synchronousFailure
            ? throw new TaskCanceledException("provider request timed out")
            : Task.FromException(new TaskCanceledException("provider request timed out"));
        var task = (Task)method.Invoke(runner, new object[] { context, "artist metadata", TimeSpan.FromSeconds(30), action })!;
        if (jobCanceled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
        else
        {
            await task;
            Assert.Contains(logs, line => line.Contains("continuing", StringComparison.Ordinal));
            Assert.False(cancellation.IsCancellationRequested);
            Func<CancellationToken, Task> nextStep = _ => Task.CompletedTask;
            await (Task)method.Invoke(runner, new object[] { context, "next file", TimeSpan.FromSeconds(30), nextStep })!;
            Assert.Contains(logs, line => line.Contains("next file completed", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AudiomackMatchRequest_ReportsProviderTimeoutWithoutCancelingTheJob(bool jobCanceled)
    {
        using var cancellation = new CancellationTokenSource();
        if (jobCanceled) cancellation.Cancel();
        var logs = new List<string>();
        var (runner, context) = CreateRunContext(cancellation.Token, logs);
        var factory = new CanceledRequestFactory();
        var api = new AudiomackApiClient(factory, new AudiomackWebCredentialsProvider(factory, NullLogger<AudiomackWebCredentialsProvider>.Instance), NullLogger<AudiomackApiClient>.Instance);
        typeof(LocalAutoTagRunner).GetField("_audiomackMatcher", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runner, new AudiomackMatcher(api, NullLogger<AudiomackMatcher>.Instance));
        var matchContextType = typeof(LocalAutoTagRunner).GetNestedType("PlatformMatchContext", BindingFlags.NonPublic)!;
        var matchContext = Activator.CreateInstance(matchContextType, nonPublic: true)!;
        var configType = typeof(LocalAutoTagRunner).GetNestedType("AutoTagRunnerConfig", BindingFlags.NonPublic)!;
        matchContextType.GetProperty("Config")!.SetValue(matchContext, Activator.CreateInstance(configType, nonPublic: true));
        matchContextType.GetProperty("Settings")!.SetValue(matchContext, new DeezSpoTagSettings());
        matchContextType.GetProperty("MatchingConfig")!.SetValue(matchContext, new AutoTagMatchingConfig());
        matchContextType.GetProperty("FilePath")!.SetValue(matchContext, "/downloads/song.flac");
        var method = typeof(LocalAutoTagRunner).GetMethod("RunPlatformMatchWithTimeoutAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(runner, new object[] { context, new AutoTagAudioInfo { Title = "Song", Artist = "Artist" }, matchContext })!;
        if (jobCanceled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else
        {
            await task;
            Assert.Equal("provider_timeout", context.GetType().GetProperty("MatchFailureOutcome")!.GetValue(context));
            Assert.False(cancellation.IsCancellationRequested);
            Assert.Contains(logs, line => line.Contains("continuing with later files", StringComparison.Ordinal));
        }
    }

    private static (LocalAutoTagRunner Runner, object Context) CreateRunContext(CancellationToken token, List<string> logs)
    {
        var runner = (LocalAutoTagRunner)RuntimeHelpers.GetUninitializedObject(typeof(LocalAutoTagRunner));
        typeof(LocalAutoTagRunner).GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runner, NullLogger<LocalAutoTagRunner>.Instance);
        var type = typeof(LocalAutoTagRunner).GetNestedType("AutoTagFileRunContext", BindingFlags.NonPublic)!;
        var context = Activator.CreateInstance(type, nonPublic: true)!;
        type.GetProperty("Platform")!.SetValue(context, "audiomack");
        type.GetProperty("File")!.SetValue(context, "/downloads/song.flac");
        type.GetProperty("Token")!.SetValue(context, token);
        type.GetProperty("LogCallback")!.SetValue(context, (Action<string>)logs.Add);
        var cacheType = typeof(LocalAutoTagRunner).GetNestedType("JobMatchCacheState", BindingFlags.NonPublic)!;
        type.GetProperty("JobMatchCache")!.SetValue(context, Activator.CreateInstance(cacheType, nonPublic: true));
        return (runner, context);
    }

    private sealed class CanceledRequestFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new CanceledRequestHandler());
        private sealed class CanceledRequestHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromException<HttpResponseMessage>(new TaskCanceledException("provider request timed out"));
        }
    }

    [Fact]
    public void LocalAutoTagRunner_OpensPerJobCircuitAfterConsecutiveMatchTimeouts()
    {
        var runner = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");

        Assert.Contains("HashSet<string> UnavailablePlatforms", runner, StringComparison.Ordinal);
        Assert.Contains("Dictionary<string, int> ConsecutiveTimeouts", runner, StringComparison.Ordinal);
        Assert.Contains("PlatformTimeoutCircuitThreshold = 3", runner, StringComparison.Ordinal);
        Assert.Contains("IsPlatformUnavailable(context.JobMatchCache, context.Platform)", runner, StringComparison.Ordinal);
        Assert.Contains("NotePlatformMatchTimeout(context.JobMatchCache, context.Platform)", runner, StringComparison.Ordinal);
        Assert.Contains("NotePlatformMatchSuccess(context.JobMatchCache, context.Platform)", runner, StringComparison.Ordinal);
        Assert.Contains("provider_unavailable", runner, StringComparison.Ordinal);
        Assert.Contains("provider_timeout", runner, StringComparison.Ordinal);
        Assert.Contains("skipped; platform unavailable after earlier match timeout", runner, StringComparison.Ordinal);
        Assert.Contains("continuing with later files", runner, StringComparison.Ordinal);
        Assert.Contains("skipping remaining {context.Platform} matches after consecutive timeouts", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("MarkPlatformUnavailable(context.JobMatchCache, context.Platform)", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("skipping remaining {context.Platform} matches in this run", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalAutoTagRunner_DoesNotTreatLaterTimeoutSkipsAsProviderErrors()
    {
        var runner = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        var resolveStart = runner.IndexOf("private async Task<AutoTagMatchResult?> ResolvePlatformMatchAsync", StringComparison.Ordinal);
        Assert.True(resolveStart >= 0, "Missing ResolvePlatformMatchAsync.");
        var resolveEnd = runner.IndexOf("private async Task<AutoTagMatchResult?> RunPlatformMatchWithTimeoutAsync", resolveStart, StringComparison.Ordinal);
        Assert.True(resolveEnd > resolveStart, "Missing RunPlatformMatchWithTimeoutAsync after ResolvePlatformMatchAsync.");
        var resolve = runner[resolveStart..resolveEnd];

        Assert.Contains("IsPlatformUnavailable", resolve, StringComparison.Ordinal);
        Assert.Contains("context.MatchFailureOutcome = \"provider_unavailable\"", resolve, StringComparison.Ordinal);
        var fileStart = runner.IndexOf("var match = await ResolvePlatformMatchAsync(context, matchInfo);", StringComparison.Ordinal);
        Assert.True(fileStart >= 0);
        var fileSlice = runner[fileStart..(fileStart + 900)];
        Assert.Contains("string.Equals(context.MatchFailureOutcome, \"provider_error\"", fileSlice, StringComparison.Ordinal);
        Assert.DoesNotContain("provider_unavailable\", StringComparison.Ordinal))", fileSlice, StringComparison.Ordinal);
    }

    [Fact]
    public void PathfinderQueries_FailFasterThanTheAutoTagMatchBudget()
    {
        var pathfinder = ReadSource("DeezSpoTag.Web", "Services", "SpotifyPathfinderMetadataClient.cs");

        Assert.Contains("private static readonly TimeSpan PathfinderRequestTimeout = TimeSpan.FromSeconds(8.0)", pathfinder, StringComparison.Ordinal);
        Assert.Contains("private static readonly TimeSpan PathfinderQueryTimeout = TimeSpan.FromSeconds(12.0)", pathfinder, StringComparison.Ordinal);
        Assert.Contains("queryTimeout.CancelAfter(PathfinderQueryTimeout)", pathfinder, StringComparison.Ordinal);
        Assert.Contains("requestTimeout.CancelAfter(PathfinderRequestTimeout)", pathfinder, StringComparison.Ordinal);
        Assert.Contains("Spotify Pathfinder request timed out after", pathfinder, StringComparison.Ordinal);
        Assert.Contains("Spotify Pathfinder query exceeded", pathfinder, StringComparison.Ordinal);
    }

    [Fact]
    public void LibrespotWorker_UsesShortTimeoutAndStartBackoff()
    {
        var blob = ReadSource("DeezSpoTag.Web", "Services", "SpotifyBlobService.cs");

        Assert.Contains("private static readonly TimeSpan LibrespotMetadataRequestTimeout = TimeSpan.FromSeconds(12)", blob, StringComparison.Ordinal);
        Assert.Contains("private static readonly TimeSpan LibrespotStartFailureBackoff = TimeSpan.FromMinutes(2)", blob, StringComparison.Ordinal);
        Assert.Contains("DateTimeOffset.UtcNow < _librespotStartRetryAfter", blob, StringComparison.Ordinal);
        Assert.Contains("MarkLibrespotStartFailure()", blob, StringComparison.Ordinal);
        Assert.Contains("worker start timed out after", blob, StringComparison.Ordinal);
        Assert.DoesNotContain("private static readonly TimeSpan LibrespotMetadataRequestTimeout = TimeSpan.FromSeconds(45)", blob, StringComparison.Ordinal);
    }

    [Fact]
    public void SpotifyIdMatch_DoesNotRequireEnrichmentToSucceed()
    {
        var matcher = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "SpotifyMatcher.cs");
        var client = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "SpotifyClient.cs");

        Assert.Contains("private static readonly TimeSpan IdEnrichmentTimeout = TimeSpan.FromSeconds(8)", matcher, StringComparison.Ordinal);
        Assert.Contains("EnrichOrKeepAsync", matcher, StringComparison.Ordinal);
        Assert.Contains("enrichmentTimeout.CancelAfter(IdEnrichmentTimeout)", matcher, StringComparison.Ordinal);
        Assert.Contains("A known Spotify ID is already a match", matcher, StringComparison.Ordinal);
        Assert.Contains("hydrateTracks: false", client, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "var enriched = await _client.EnrichTrackWithPathfinderAsync(seeded, cancellationToken);",
            matcher,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LibrespotInnerTimeout_IsShorterThanAutoTagMatchTimeout()
    {
        var runner = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        var blob = ReadSource("DeezSpoTag.Web", "Services", "SpotifyBlobService.cs");
        var pathfinder = ReadSource("DeezSpoTag.Web", "Services", "SpotifyPathfinderMetadataClient.cs");

        Assert.Contains("PlatformMatchTimeout = TimeSpan.FromSeconds(45)", runner, StringComparison.Ordinal);
        Assert.Contains("PathfinderQueryTimeout = TimeSpan.FromSeconds(12.0)", pathfinder, StringComparison.Ordinal);
        Assert.Contains("LibrespotMetadataRequestTimeout = TimeSpan.FromSeconds(12)", blob, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] relativeParts)
    {
        var repoRoot = ResolveRepoRoot();
        var path = Path.Join(repoRoot, Path.Join(relativeParts));
        Assert.True(File.Exists(path), $"Missing source: {path}");
        return File.ReadAllText(path);
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Join(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test output path.");
    }
}
