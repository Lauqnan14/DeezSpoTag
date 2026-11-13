using System;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Apple lyrics lookup deadline regressions. The complete lookup (account checks, token
///     wait, authenticated requests, response bodies and both rounds) must respect one shared
///     deadline, account checks must respect a smaller combined budget, and partially resolved
///     candidates must be retained instead of discarded.
/// </summary>
public sealed class AppleLyricsLookupBudgetTest
{
    private const string ConfiguredToken = "eyJhbGciOiJIUzI1NiJ9.eyJ1c2VyIjoiY29uZmlndXJlZCJ9.c2lnbmF0dXJlQWJj";
    private const string MediaUserToken = "media-user-token-0123456789-0123456789-0123456789-0123456789";
    private const string TtmlNs = "http://www.w3.org/ns/ttml";
    private const string ItunesNs = "http://music.apple.com/lyric-ttml-internal";

    private static string WordTtml => $"<tt xmlns=\"{TtmlNs}\" xmlns:itunes=\"{ItunesNs}\" itunes:timing=\"Word\" xml:lang=\"en\">"
        + "<body dur=\"9:59.999\"><div><p begin=\"0.000\" end=\"5.000\"><span begin=\"0.000\" end=\"5.000\">Line</span></p></div></body></tt>";

    private sealed class LyricsHandler : HttpMessageHandler
    {
        public int AccountRequests;
        public int LyricsRequests;
        private Func<HttpResponseMessage>? _nextLyricsResponse;
        public TimeSpan AccountDelay = TimeSpan.Zero;
        public bool StallBody;
        public bool LineThenStall;

        public void EnqueueLyricsResponse(Func<HttpResponseMessage> response) => _nextLyricsResponse = response;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/account")
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { music_user_token = MediaUserToken })) };
            if (request.RequestUri!.AbsolutePath.Contains("/v1/me/account", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref AccountRequests);
                if (AccountDelay > TimeSpan.Zero) await Task.Delay(AccountDelay, cancellationToken);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"meta\":{\"subscription\":{\"storefront\":\"us\",\"active\":true}}}",
                        Encoding.UTF8, "application/json")
                };
            }

            Interlocked.Increment(ref LyricsRequests);
            if (StallBody || (LineThenStall && LyricsRequests > 1))
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) };
            if (LineThenStall) return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("{\"data\":[{\"attributes\":{\"ttml\":" + System.Text.Json.JsonSerializer.Serialize(WordTtml.Replace("itunes:timing=\"Word\"", "itunes:timing=\"Line\"").Replace("<span begin=\"0.000\" end=\"5.000\">Line</span>", "Line")) + "}}]}") };
                        if (_nextLyricsResponse is null)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"data\":[{\"attributes\":{\"ttml\":" + System.Text.Json.JsonSerializer.Serialize(WordTtml) + "}}]}",
                        Encoding.UTF8, "application/json")
                };
            }
            return _nextLyricsResponse();
        }
    }

    private static (AppleLyricsService Service, LyricsHandler Handler) CreateService(
        string directory, LyricsHandler? handler = null)
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
            "{\"appleMusic\":{\"storefront\":\"us\",\"mediaUserToken\":\"" + MediaUserToken + "\",\"authorizationToken\":\""
            + ConfiguredToken + "\"},\"tags\":{\"lyrics\":true,\"syncedLyrics\":true}}");
        var probe = handler ?? new LyricsHandler();
        var catalog = new AppleMusicCatalogService(
            new ClientFactory(probe), settings, NullLogger<AppleMusicCatalogService>.Instance,
            new MemoryCache(new MemoryCacheOptions()));
        var service = new AppleLyricsService(catalog, new ClientFactory(probe), NullLogger<AppleLyricsService>.Instance);
        return (service, probe);
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static Task<LyricsBase> ResolveAsync(AppleLyricsService service, TimeSpan lookupBudget, TimeSpan accountBudget, bool useWrapper = false)
    {
        var settings = new DeezSpoTagSettings { SynthesizeLrcFromTtml = true, LrcType = "syllable-lyrics,lyrics", AppleMusic = new AppleMusicSettings { MediaUserToken = useWrapper ? "" : MediaUserToken, Storefront = "us" } };
        var method = typeof(AppleLyricsService).GetMethod("ResolveLyricsCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task<LyricsBase>)method.Invoke(service,
            ["song-1", settings, CancellationToken.None, lookupBudget, accountBudget, null])!;
    }

    [Fact]
    public async Task StalledResponseBodyIsBoundedByTheLookupDeadline()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-lyrics-stall-").FullName;
        try
        {
            var (service, handler) = CreateService(directory);
            handler.StallBody = true;
            var started = DateTimeOffset.UtcNow;
            var result = await ResolveAsync(service, TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(200));
            Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10),
                "The complete lookup must respect its deadline instead of hanging on a stalled body.");
            Assert.False(result.IsLoaded(), "A lookup that ran out of time must not report success.");
            Assert.Contains("deadline", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PartialCandidatesAreRetainedWhenTheDeadlineExpires()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-lyrics-partial-").FullName;
        try
        {
            var (service, handler) = CreateService(directory);
            handler.LineThenStall = true;
            var result = await ResolveAsync(service, TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(200));
            Assert.True(result.IsLoaded(), "Successfully resolved candidates must be retained despite the deadline.");
            Assert.False(AppleLyricsService.IsWordSyncedTtml(result.TtmlLyrics));
            Assert.True(handler.LyricsRequests >= 2);
            Assert.Contains("deadline", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AccountChecksShareACombinedBudgetAndTheLookupContinues()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-lyrics-account-").FullName;
        try
        {
            var (service, handler) = CreateService(directory);
            handler.AccountDelay = TimeSpan.FromSeconds(30); // account endpoints hang
            handler.EnqueueLyricsResponse(() => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(
                    "{\"data\":[{\"attributes\":{\"ttml\":" + System.Text.Json.JsonSerializer.Serialize(WordTtml) + "}}]}",
                    Encoding.UTF8, "application/json")
            });

            var started = DateTimeOffset.UtcNow;
            var result = await ResolveAsync(service, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(300));
            Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(4),
                "Combined account/storefront checks must not exceed their budget.");
            Assert.True(result.IsLoaded(), "The lookup must continue with the remaining budget.");
            Assert.Equal(1, handler.AccountRequests);
            Assert.True(handler.LyricsRequests > 0);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SharedProviderDeadlineRetainsNativePartialLyrics()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-provider-partial-").FullName;
        try
        {
            var (apple, handler) = CreateService(directory);
            handler.LineThenStall = true;
            var service = new DeezSpoTag.Services.Download.Utils.LyricsService(
                NullLogger<DeezSpoTag.Services.Download.Utils.LyricsService>.Instance,
                new ClientFactory(handler), null!, null!, apple, null!, null!);
            var settings = new DeezSpoTagSettings
            {
                AppleMusic = new AppleMusicSettings { MediaUserToken = MediaUserToken, Storefront = "us" },
                SyncedLyrics = true, SynthesizeLrcFromTtml = true,
                LrcType = "lyrics,syllable-lyrics,ttml-lyrics", LrcFormat = "lrc,ttml",
                LyricsFallbackEnabled = true, LyricsFallbackOrder = "apple"
            };
            var track = new Track { Title = "Partial track", ArtistString = "Partial artist",
                Urls = new System.Collections.Generic.Dictionary<string,string> { ["apple_track_id"] = DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
            var method = service.GetType().GetMethod("ResolveLyricsCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var result = await (Task<DeezSpoTag.Services.Download.Utils.LyricsResolutionResult>)method.Invoke(service,
                [track, settings, null, null, CancellationToken.None, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500)])!;
            Assert.True(result.Lyrics?.IsLoaded());
            Assert.True(result.Incomplete);
            Assert.Contains(result.ProviderOutcomes!, outcome => outcome.Provider == "apple" && outcome.IsOperationalFailure);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task WrapperDerivedTokenStillUsesTheCombinedAccountBudget()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-wrapper-budget-").FullName;
        try
        {
            var (service, handler) = CreateService(directory);
            handler.AccountDelay = TimeSpan.FromSeconds(30);
            var result = await ResolveAsync(service, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(150), useWrapper: true);
            Assert.True(result.IsLoaded());
            Assert.Equal(1, handler.AccountRequests);
            Assert.True(handler.LyricsRequests > 0);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    private sealed class StalledStream : System.IO.Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
