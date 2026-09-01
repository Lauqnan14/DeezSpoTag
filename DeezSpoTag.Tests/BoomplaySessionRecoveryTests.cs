using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class BoomplaySessionRecoveryTests
{
    private const string NumericPlaylistId = "6839559";
    private const string HarvestedUserAgent = "BoomplayHarvestBrowser/2.0";
    private const string HarvestedClearance = "cf_clearance=fresh-clearance";
    private const string SeededCookie = "sessionID=auth; cf_clearance=stale";
    private const string SeededUserAgent = "OldUA/1.0";

    // --- merge policy ---

    [Fact]
    public void Merge_ReplacesStaleClearanceAndPreservesLoginPairs()
    {
        var merged = BoomplaySessionCookieMerger.Merge(
            "sessionID=auth; cf_clearance=stale; __cf_bm=old",
            HarvestedClearance);

        Assert.Equal("sessionID=auth; __cf_bm=old; cf_clearance=fresh-clearance", merged);
    }

    [Fact]
    public void Merge_ReturnsExisting_WhenHarvestedPairIsNotACookie()
    {
        Assert.Equal("sessionID=auth", BoomplaySessionCookieMerger.Merge("sessionID=auth", "not-a-cookie"));
        Assert.Equal("sessionID=auth", BoomplaySessionCookieMerger.Merge("sessionID=auth", "  "));
    }

    // --- recovery service state machine ---

    [Fact]
    public async Task TryRecoverAsync_PersistsMergedSessionAndWakesWatchlist()
    {
        using var scope = CreateScope();
        await SeedSessionAsync(scope.Auth);
        var solver = new FakeBoomplayChallengeSolver();
        var sink = new RecordingNotificationSink();
        var signal = new WatchlistRunSignal();
        var service = CreateService(scope.Auth, solver, sink, signal);

        var recovered = await service.TryRecoverAsync("cloudflare_challenge", CancellationToken.None);

        Assert.True(recovered);
        var saved = (await scope.Auth.LoadAsync()).Boomplay!;
        Assert.Equal("sessionID=auth; cf_clearance=fresh-clearance", saved.Cookie);
        Assert.Equal(HarvestedUserAgent, saved.UserAgent);
        Assert.True(saved.SessionValid);
        Assert.Equal("recovered", saved.LastStatus);
        Assert.True(signal.IsPending);
        Assert.Contains(sink.Raised, entry => entry.Severity == "Info" && entry.DedupeKey == "boomplay-session-recovered");
        Assert.Contains(sink.Resolved, dedupeKey => dedupeKey == "boomplay-session-challenge");
    }

    [Fact]
    public async Task TryRecoverAsync_CooldownBlocksImmediateSecondAttempt()
    {
        using var scope = CreateScope();
        await SeedSessionAsync(scope.Auth);
        var solver = new FakeBoomplayChallengeSolver();
        var service = CreateService(scope.Auth, solver, new RecordingNotificationSink(), new WatchlistRunSignal());

        Assert.True(await service.TryRecoverAsync("cloudflare_challenge", CancellationToken.None));
        Assert.False(await service.TryRecoverAsync("cloudflare_challenge", CancellationToken.None));
        Assert.Equal(1, solver.Calls);
    }

    [Fact]
    public async Task TryRecoverAsync_ConcurrentTriggersCoalesceIntoOneSolve()
    {
        using var scope = CreateScope();
        await SeedSessionAsync(scope.Auth);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var solver = new FakeBoomplayChallengeSolver();
        solver.GateUntil(gate.Task);
        var service = CreateService(scope.Auth, solver, new RecordingNotificationSink(), new WatchlistRunSignal());

        var first = service.TryRecoverAsync("cloudflare_challenge", CancellationToken.None);
        while (solver.Calls == 0)
        {
            await Task.Delay(10);
        }

        var second = await service.TryRecoverAsync("cloudflare_challenge", CancellationToken.None);
        Assert.False(second);
        gate.TrySetResult();
        Assert.True(await first);
        Assert.Equal(1, solver.Calls);
    }

    [Fact]
    public async Task TryRecoverAsync_RepeatedFailuresDisableRecoveryAndNotify()
    {
        using var scope = CreateScope();
        await SeedSessionAsync(scope.Auth);
        var solver = new FakeBoomplayChallengeSolver { Result = null };
        var sink = new RecordingNotificationSink();
        var service = CreateService(
            scope.Auth,
            solver,
            sink,
            new WatchlistRunSignal(),
            new BoomplaySessionRecoveryOptions { CooldownMinutes = 0, MaxConsecutiveFailures = 3 });

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.False(await service.TryRecoverAsync("cloudflare_challenge", CancellationToken.None));
        }

        Assert.Equal(3, solver.Calls);
        // Recovery is disabled after the cap: the fourth attempt must not reach the solver.
        Assert.False(await service.TryRecoverAsync("cloudflare_challenge", CancellationToken.None));
        Assert.Equal(3, solver.Calls);
        Assert.Contains(
            sink.Raised,
            entry => entry.Severity == "Warning"
                     && entry.DedupeKey == "boomplay-session-challenge"
                     && entry.Title.Contains("disabled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TryRecoverAsync_DisabledByConfigurationNeverCallsSolver()
    {
        using var scope = CreateScope();
        await SeedSessionAsync(scope.Auth);
        var solver = new FakeBoomplayChallengeSolver();
        var service = CreateService(
            scope.Auth,
            solver,
            new RecordingNotificationSink(),
            new WatchlistRunSignal(),
            new BoomplaySessionRecoveryOptions { Enabled = false });

        Assert.False(await service.TryRecoverAsync("cloudflare_challenge", CancellationToken.None));
        Assert.Equal(0, solver.Calls);
    }

    // --- metadata service: challenge -> recover -> single retry ---

    [Fact]
    public async Task GetPlaylistAsync_ChallengeTriggersRecoveryAndSingleRetry()
    {
        using var scope = CreateScope();
        await SeedSessionAsync(scope.Auth);
        var challengeServed = false;
        var factory = CreateRoutingFactory(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host.Equals("www.boomplay.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith("/playlists", StringComparison.Ordinal))
            {
                if (!challengeServed)
                {
                    challengeServed = true;
                    var challenge = new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent("<html><title>Just a moment...</title></html>")
                    };
                    challenge.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
                    return challenge;
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"""
                        <html><head><title>Reggae Roots | Boomplay Music</title></head><body>
                          <main id="playlistsDetails" data-cid="{NumericPlaylistId}">
                            <li class="clearfix play_one" data-id="49847968"
                                data-data="49847968%40%2B%231%40%2B%23{NumericPlaylistId}">
                              <a class="songName" href="/songs/EQvCMAUEvpl7878KkkYwUFAt">Mother Matty</a>
                              <a class="artistName">Norris Cole</a>
                            </li>
                          </main>
                        </body></html>
                        """)
                };
            }

            if (uri.Host.Equals("api.boomplaymusic.com", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {
                          "detailCol": { "name": "Reggae Roots", "songCount": 1 },
                          "musics": [
                            {
                              "musicID": "49847968",
                              "name": "Mother Matty",
                              "singers": [{ "name": "Norris Cole" }],
                              "deaution": "188"
                            }
                          ]
                        }
                        """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        var recovery = new PersistingRecoveryService(scope.Auth);
        var service = new BoomplayMetadataService(
            factory,
            scope.Auth,
            NullLogger<BoomplayMetadataService>.Instance,
            recovery);

        var playlist = await service.GetPlaylistAsync(NumericPlaylistId, CancellationToken.None);

        Assert.NotNull(playlist);
        Assert.Equal(NumericPlaylistId, playlist!.Id);
        Assert.Equal("Reggae Roots", playlist.Title);
        Assert.Equal(1, recovery.Calls);
        var saved = (await scope.Auth.LoadAsync()).Boomplay!;
        Assert.Contains("cf_clearance=fresh-clearance", saved.Cookie, StringComparison.Ordinal);
        Assert.True(saved.SessionValid);
    }

    // --- helpers ---

    private sealed class PersistingRecoveryService(PlatformAuthService auth) : IBoomplaySessionRecoveryService
    {
        public int Calls { get; private set; }

        public async Task<bool> TryRecoverAsync(string reason, CancellationToken cancellationToken)
        {
            Calls++;
            var state = await auth.LoadAsync();
            state.Boomplay = new BoomplayAuth
            {
                Cookie = $"sessionID=auth; {HarvestedClearance}",
                UserAgent = HarvestedUserAgent,
                SessionValid = true,
                LastStatus = "recovered",
                SavedAt = DateTimeOffset.UtcNow
            };
            await auth.SaveAsync(state);
            return true;
        }
    }

    private sealed class FakeBoomplayChallengeSolver : IBoomplayChallengeSolver
    {
        private TaskCompletionSource? _gate;

        public int Calls { get; private set; }

        public BoomplayChallengeSolveResult? Result { get; set; } =
            new(HarvestedClearance, HarvestedUserAgent);

        public Exception? Throw { get; set; }

        public void GateUntil(Task gateTask) => _gate = TaskCompletionSourceFrom(gateTask);

        public async Task<BoomplayChallengeSolveResult?> SolveAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (_gate is not null)
            {
                await _gate.Task.WaitAsync(cancellationToken);
            }

            if (Throw is not null)
            {
                throw Throw;
            }

            return Result;
        }

        private static TaskCompletionSource TaskCompletionSourceFrom(Task gateTask)
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = gateTask.ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                    {
                        source.SetException(task.Exception!);
                    }
                    else if (task.IsCanceled)
                    {
                        source.SetCanceled();
                    }
                    else
                    {
                        source.SetResult();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return source;
        }
    }

    private sealed class RecordingNotificationSink : INotificationSink
    {
        public List<(string Kind, string Title, string Body, string Severity, string? DedupeKey)> Raised { get; } = new();

        public List<string> Resolved { get; } = new();

        public void Raise(
            string kind,
            string title,
            string body,
            string severity = "Info",
            string? dedupeKey = null,
            string? entityType = null,
            string? entityId = null,
            string? link = null)
            => Raised.Add((kind, title, body, severity, dedupeKey));

        public void Resolve(string dedupeKey, bool manuallyResolved, string? recoveryTitle = null, string? recoveryBody = null)
            => Resolved.Add(dedupeKey);
    }

    private static BoomplaySessionRecoveryService CreateService(
        PlatformAuthService auth,
        IBoomplayChallengeSolver solver,
        RecordingNotificationSink sink,
        WatchlistRunSignal signal,
        BoomplaySessionRecoveryOptions? options = null)
        => new(
            solver,
            auth,
            Options.Create(options ?? new BoomplaySessionRecoveryOptions()),
            NullLogger<BoomplaySessionRecoveryService>.Instance,
            sink,
            signal);

    private static async Task SeedSessionAsync(PlatformAuthService auth)
    {
        var state = await auth.LoadAsync();
        state.Boomplay = new BoomplayAuth
        {
            Cookie = SeededCookie,
            UserAgent = SeededUserAgent,
            SessionValid = true,
            SavedAt = DateTimeOffset.UtcNow
        };
        await auth.SaveAsync(state);
    }

    private static BoomplayRecoveryTestScope CreateScope()
    {
        var root = Path.Join(Path.GetTempPath(), $"deezspotag-boomplay-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var environment = new BoomplayRecoveryTestEnvironment(root);
        var auth = new PlatformAuthService(
            environment,
            NullLogger<PlatformAuthService>.Instance,
            DataProtectionProvider.Create(new DirectoryInfo(Path.Join(root, "keys"))));
        return new BoomplayRecoveryTestScope(root, auth);
    }

    private sealed class BoomplayRecoveryTestScope(string root, PlatformAuthService auth) : IDisposable
    {
        public PlatformAuthService Auth { get; } = auth;

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }

    private sealed class BoomplayRecoveryTestEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }

    private static BoomplayResponseHttpClientFactory CreateRoutingFactory(
        Func<HttpRequestMessage, HttpResponseMessage> route)
        => new(route);

    private sealed class BoomplayResponseHttpClientFactory(
        Func<HttpRequestMessage, HttpResponseMessage> route) : IHttpClientFactory
    {
        // A fresh client per call mirrors the real IHttpClientFactory contract:
        // BoomplayMetadataService.CreateClient mutates DefaultRequestHeaders, which is only
        // legal before a HttpClient's first request.
        public HttpClient CreateClient(string name)
            => new(new BoomplayResponseHandler(route)) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private sealed class BoomplayResponseHandler(
        Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(route(request));
    }
}
