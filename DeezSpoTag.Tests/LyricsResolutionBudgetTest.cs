using System.Linq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Shared lyrics resolution deadline regressions: each provider is bounded by its own budget,
///     a stalled provider advances to the next one, total exhaustion records operational
///     failures, and resolved content is retained. Provider order and capability rounds are
///     untouched by the bounds.
/// </summary>
public sealed class LyricsResolutionBudgetTest
{
    private const string ConfiguredToken = "eyJhbGciOiJIUzI1NiJ9.eyJ1c2VyIjoiY29uZmlndXJlZCJ9.c2lnbmF0dXJlQWJj";

    private sealed class BudgetHandler : HttpMessageHandler
    {
        public int AppleRequests;
        public int LrclibRequests;
        public TimeSpan AppleDelay = TimeSpan.Zero;
        public TimeSpan LrclibDelay = TimeSpan.Zero;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            if (host.Contains("apple", StringComparison.Ordinal) || host.Contains("paxsenix", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref AppleRequests);
                if (AppleDelay > TimeSpan.Zero) await Task.Delay(AppleDelay, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
                };
            }

            if (host.Contains("lrclib", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref LrclibRequests);
                if (LrclibDelay > TimeSpan.Zero) await Task.Delay(LrclibDelay, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"name\":\"Budget track\",\"artistName\":\"Budget artist\",\"plainLyrics\":\"Plain fallback\",\"syncedLyrics\":\"[00:01.00]Line\",\"instrumental\":false}",
                        Encoding.UTF8, "application/json")
                };
            }

            // The Apple public lyrics fallback host: never satisfy it in these tests.
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (LyricsService Service, BudgetHandler Handler) CreateService(string directory)
    {
        var configFolder = System.IO.Path.Join(directory, "config");
        System.IO.Directory.CreateDirectory(configFolder);
        System.IO.Directory.CreateDirectory(System.IO.Path.Join(directory, "autotag"));
        System.IO.File.WriteAllText(System.IO.Path.Join(directory, "autotag", "applemusic.json"),
            System.Text.Json.JsonSerializer.Serialize(new { authorizationToken = ConfiguredToken, mediaUserToken = "" }));
        var settings = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        foreach (var (field, value) in new[]
                 {
                     ("_configFolder", configFolder), ("_settingsFilePath", System.IO.Path.Join(configFolder, "config.json"))
                 })
        {
            typeof(DeezSpoTagSettingsService)
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(settings, value);
        }
        System.IO.File.WriteAllText(System.IO.Path.Join(configFolder, "config.json"),
            "{\"appleMusic\":{\"storefront\":\"us\",\"mediaUserToken\":\"\",\"authorizationToken\":\"" + ConfiguredToken + "\"}}");
        var handler = new BudgetHandler();
        var catalog = new AppleMusicCatalogService(new ClientFactory(handler), settings,
            NullLogger<AppleMusicCatalogService>.Instance, new MemoryCache(new MemoryCacheOptions()));
        var appleLyrics = new AppleLyricsService(catalog, new ClientFactory(handler), NullLogger<AppleLyricsService>.Instance);
        var lrclib = new LrclibLyricsService(new ClientFactory(handler), NullLogger<LrclibLyricsService>.Instance);
        var service = new LyricsService(
            NullLogger<LyricsService>.Instance,
            new ClientFactory(handler),
            (DeezSpoTag.Services.Utils.JwtTokenService)null!,
            (DeezSpoTag.Services.Download.AuthenticatedDeezerService)null!,
            appleLyrics,
            lrclib,
            (ISpotifyLyricsAuthProvider)null!);
        return (service, handler);
    }

    private static Track CreateTrack()
        => new()
        {
            Title = "Budget track",
            ISRC = Guid.NewGuid().ToString("N"),
            ArtistString = "Budget artist",
            Urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["apple_track_id"] = "1234567"
            }
        };

    private static DeezSpoTagSettings CreateSettings()
        => new()
        {
            SyncedLyrics = true, SaveLyrics = true,
            LrcType = "lyrics,syllable-lyrics,ttml-lyrics,unsynced-lyrics",
            LrcFormat = "lrc",
            LyricsFallbackEnabled = true,
            LyricsFallbackOrder = "apple,lrclib",
            AppleMusic = new AppleMusicSettings { PublicLyricsApiUrl = "https://apple.test/lyrics" }
        };

    private static async Task<LyricsResolutionResult> ResolveAsync(LyricsService service, TimeSpan totalBudget, TimeSpan providerBudget)
    {
        var method = typeof(LyricsService).GetMethod("ResolveLyricsCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return await ((Task<LyricsResolutionResult>)method.Invoke(service,
            [CreateTrack(), CreateSettings(), (LyricsProviderOptions?)null,
                (Func<LyricsResolutionProgress, CancellationToken, ValueTask>?)null,
                CancellationToken.None, totalBudget, providerBudget])!);
    }

    [Fact]
    public async Task PartialOperationalFailureIsRetainedAndNotCachedAsVerifiedSuccess()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("lyrics-partial-cache-").FullName;
        try
        {
            var (service, _) = CreateService(directory);
            var track = CreateTrack();
            var partial = new LyricsSource { UnsyncedLyrics = "Verified partial content" };
            partial.SetErrorMessage("Apple lyrics lookup deadline exceeded.");
            var calls = 0;
            Func<Task<LyricsBase?>> resolver = () => { calls++; return Task.FromResult<LyricsBase?>(partial); };
            var method = typeof(LyricsService).GetMethod("ResolveCachedProviderLyricsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = await (Task<LyricsBase?>)method.Invoke(service, ["lrclib", track, CreateSettings(), resolver])!;
                Assert.Equal(partial.UnsyncedLyrics, result!.UnsyncedLyrics);
                Assert.Contains("deadline", result.ErrorMessage!);
            }
            Assert.Equal(2, calls);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task StalledProviderAdvancesWithinItsOwnBudget()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("lyrics-budget-stall-").FullName;
        try
        {
            var (service, handler) = CreateService(directory);
            handler.AppleDelay = TimeSpan.FromSeconds(30);
            var started = DateTimeOffset.UtcNow;
            var result = await ResolveAsync(service, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(600));
            Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(8),
                "A stalled provider must not consume the whole resolution budget.");
            Assert.True(result.Lyrics?.IsLoaded() == true, "The next provider must still satisfy the request.");
            Assert.Contains("apple", result.ProvidersAttempted);
            Assert.Contains("lrclib", result.ProvidersAttempted);
            var appleOutcome = result.ProviderOutcomes?.FirstOrDefault(o => string.Equals(o.Provider, "apple", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(appleOutcome);
            Assert.True(appleOutcome!.IsOperationalFailure,
                "A provider that ran out of time must be recorded as an operational failure.");
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task TotalDeadlineExhaustionRecordsOperationalFailuresWithoutException()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("lyrics-budget-total-").FullName;
        try
        {
            var (service, handler) = CreateService(directory);
            handler.AppleDelay = TimeSpan.FromSeconds(30);
            handler.LrclibDelay = TimeSpan.FromSeconds(30);
            var started = DateTimeOffset.UtcNow;
            var result = await ResolveAsync(service, TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(600));
            Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(6),
                "Total exhaustion must complete inside the total budget.");
            Assert.False(result.Lyrics?.IsLoaded() == true);
            Assert.True(result.Incomplete);
            Assert.NotEmpty(result.ProviderOutcomes!);
            Assert.All(result.ProviderOutcomes?.Where(o => !string.Equals(o.Provider, "peer", StringComparison.OrdinalIgnoreCase)) ?? [],
                outcome => Assert.True(outcome.IsOperationalFailure));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CallerCancellationStillPropagates()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("lyrics-budget-cancel-").FullName;
        try
        {
            var (service, handler) = CreateService(directory);
            handler.AppleDelay = TimeSpan.FromSeconds(30);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ResolveLyricsWithDetailsAsync(
                CreateTrack(), CreateSettings(), null, null, cts.Token));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }
}
