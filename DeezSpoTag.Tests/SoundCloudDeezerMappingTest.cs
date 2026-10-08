using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Authentication;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Identity;
using DeezSpoTag.Services.Download.SoundCloud;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.LinkMapping;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Proves the SoundCloud to Deezer resolution order and that no weak fallback can override it.
/// </summary>
/// <remarks>
///     <para>
///         These tests drive the real <see cref="TrackIdentityResolver" /> and the real <see cref="DeezerClient" />
///         over a recording HTTP handler, so the observed request order is the production order rather than an
///         assertion about source text.
///     </para>
///     <para>
///         Only Deezer is a target platform, so the other six resolvers are passed as nulls: their branches
///         cannot run, and passing null keeps the harness small without weakening what is being tested.
///     </para>
/// </remarks>
public sealed class SoundCloudDeezerMappingTest
{
    private const string SoundCloudUrl = "https://soundcloud.com/example-artist/example-track";

    [Fact]
    public async Task IsrcPresent_ResolvesByIsrcAndNeverRunsAMetadataSearch()
    {
        var harness = new Harness
        {
            SoundCloudTrack = Track(isrc: "ABCDE1234567", durationMs: 200_000),
            DeezerIsrcResponse = DeezerTrack(id: "900", title: "Example", artist: "Example Artist", duration: 200, isrc: "ABCDE1234567")
        };

        var result = await harness.ResolveAsync();

        Assert.Equal("900", result.DeezerId);
        Assert.True(
            result.Candidates!.Any(c => c.Platform == "deezer" && c.Accepted),
            "The Deezer identity should have been accepted.");

        // ISRC first, and metadata never consulted.
        Assert.Equal(1, harness.DeezerIsrcRequests);
        Assert.Equal(0, harness.DeezerSearchRequests);
        Assert.Equal(1, harness.SoundCloudResolutions);

        // Hydration strictly precedes the Deezer lookup.
        Assert.True(
            harness.Calls.IndexOf("soundcloud:resolve") < harness.Calls.IndexOf("deezer:isrc"),
            $"Observed call order: {string.Join(", ", harness.Calls)}");
    }

    [Fact]
    public async Task NoIsrc_SkipsTheIsrcLookupAndSearchesByMetadata()
    {
        var harness = new Harness
        {
            SoundCloudTrack = Track(isrc: null, durationMs: 200_000),
            DeezerSearchResponse = DeezerSearch(DeezerTrack(id: "901", title: "Example", artist: "Example Artist", duration: 200))
        };

        var result = await harness.ResolveAsync();

        Assert.Equal("901", result.DeezerId);
        Assert.Equal(0, harness.DeezerIsrcRequests);
        Assert.True(harness.DeezerSearchRequests > 0, "The metadata fallback should have been attempted.");
    }

    [Fact]
    public async Task IsrcLookupMisses_ThenMetadataIsSearched()
    {
        var harness = new Harness
        {
            SoundCloudTrack = Track(isrc: "ABCDE1234567", durationMs: 200_000),
            // ISRC endpoint returns nothing.
            DeezerIsrcResponse = null,
            DeezerSearchResponse = DeezerSearch(DeezerTrack(id: "902", title: "Example", artist: "Example Artist", duration: 200))
        };

        var result = await harness.ResolveAsync();

        Assert.Equal("902", result.DeezerId);
        Assert.Equal(1, harness.DeezerIsrcRequests);
        Assert.True(harness.DeezerSearchRequests > 0);

        // The ordering that matters: the ISRC request precedes every search request.
        Assert.True(
            harness.Calls.IndexOf("deezer:isrc") < harness.Calls.IndexOf("deezer:search"),
            $"ISRC must be attempted before metadata. Observed: {string.Join(", ", harness.Calls)}");
    }

    [Fact]
    public async Task IsrcCandidateRejectedByValidation_ThenMetadataIsSearched()
    {
        var harness = new Harness
        {
            SoundCloudTrack = Track(isrc: "ABCDE1234567", durationMs: 200_000),
            // Same ISRC but a wildly different duration, which the existing validator rejects.
            DeezerIsrcResponse = DeezerTrack(id: "903", title: "Example", artist: "Example Artist", duration: 400, isrc: "ABCDE1234567"),
            DeezerSearchResponse = DeezerSearch(DeezerTrack(id: "904", title: "Example", artist: "Example Artist", duration: 200)),
            // The metadata path re-fetches each candidate by id before validating, so that endpoint has to
            // return the acceptable track too.
            DeezerTrackFetchResponse = DeezerTrack(id: "904", title: "Example", artist: "Example Artist", duration: 200)
        };

        var result = await harness.ResolveAsync();

        Assert.Equal(1, harness.DeezerIsrcRequests);
        Assert.True(harness.DeezerSearchRequests > 0, "A rejected ISRC candidate must fall through to metadata.");

        // The fallback result, not the rejected ISRC result, is what is used. The resolver does not record a
        // candidate row for a rejected lookup - it simply continues - so the accepted id is the evidence.
        Assert.Equal("904", result.DeezerId);
        Assert.True(
            result.Candidates!.All(c => c.Id != "903"),
            "The duration-rejected ISRC candidate must not be used.");
        Assert.True(
            result.Candidates!.Any(c => c.Platform == "deezer" && c.Accepted && c.Id == "904"),
            "The metadata fallback candidate should have been accepted.");
    }

    [Fact]
    public async Task MetadataFallbackRejected_ReportsUnresolvedRatherThanTakingTheClosestTitle()
    {
        var harness = new Harness
        {
            SoundCloudTrack = Track(isrc: null, durationMs: 200_000),
            // Similar text, wrong artist and a duration far outside the tolerance.
            DeezerSearchResponse = DeezerSearch(
                DeezerTrack(id: "905", title: "Example", artist: "Totally Different Artist", duration: 620))
        };

        var result = await harness.ResolveAsync();

        Assert.Null(result.DeezerId);
        Assert.Null(result.DeezerUrl);
        Assert.True(
            result.Candidates!.Any(c => c.Platform == "deezer" && !c.Accepted && c.Reason == "deezer-unresolved"),
            "Deezer should have been recorded as unresolved.");
    }

    [Fact]
    public async Task HydrationPopulatesTitleArtistIsrcAndDuration()
    {
        var harness = new Harness
        {
            SoundCloudTrack = new SoundCloudTrack
            {
                Id = 42,
                Urn = "soundcloud:tracks:42",
                Title = "Example",
                MetadataArtist = "Recording Artist",
                PublisherArtist = "Distributor Artist",
                UploaderUsername = "uploader-handle",
                Artist = "uploader-handle",
                Isrc = "ABCDE1234567",
                DurationMs = 200_000,
                PermalinkUrl = SoundCloudUrl
            }
        };

        var result = await harness.ResolveAsync();

        Assert.Equal("Example", result.Title);
        // metadata_artist outranks the distributor name and the uploader handle.
        Assert.Equal("Recording Artist", result.Artist);
        Assert.Equal("ABCDE1234567", result.Isrc);
        Assert.Equal(200_000, result.DurationMs);
    }

    /// <summary>
    ///     The permissive similarity fallback must not rescue a SoundCloud mapping the resolver rejected.
    /// </summary>
    /// <remarks>
    ///     The stub resolver returns source metadata with no Deezer identity, which is exactly the state that
    ///     arms the fallback for every other source. Without the guard this test fails, because the weak search
    ///     below would be accepted and a mapping invented out of an unrelated result.
    /// </remarks>
    [Fact]
    public async Task WeakGenericFallback_DoesNotRescueASoundCloudMappingTheResolverRejected()
    {
        var handler = new RecordingHandler { WhenSearches = DeezerSearch(WeakCandidate()) };
        var service = BuildLinkMappingService(handler, MetadataOnlyResolver());

        var result = await service.MapToDeezerAsync(SoundCloudUrl, CancellationToken.None);

        Assert.False(result.Available);
        Assert.Equal(string.Empty, result.DeezerId);
        Assert.Equal(string.Empty, result.DeezerUrl);
        Assert.Equal(0, handler.SearchRequests);
    }

    /// <summary>
    ///     The guard is narrow: another source still reaches the permissive fallback in the same setup.
    /// </summary>
    /// <remarks>
    ///     Without this control the guard could pass simply by having disabled the fallback everywhere.
    /// </remarks>
    [Fact]
    public async Task WeakGenericFallback_StillAppliesToOtherSourcesSoExistingBehaviourIsUnchanged()
    {
        var handler = new RecordingHandler { WhenSearches = DeezerSearch(WeakCandidate()) };
        var service = BuildLinkMappingService(handler, MetadataOnlyResolver());

        var result = await service.MapToDeezerAsync(
            "https://open.spotify.com/track/3AhXZa8sUQht0UEdBJgpGc",
            CancellationToken.None);

        Assert.True(handler.SearchRequests > 0, "A non-SoundCloud source must still reach the permissive search.");
        Assert.True(result.Available, "A non-SoundCloud source must keep its permissive fallback.");
        Assert.Equal("777", result.DeezerId);
    }

    /// <summary>
    ///     A search result that only a permissive matcher would accept: the text is similar enough to clear the
    ///     0.30 threshold, while the artist and duration are both wrong.
    /// </summary>
    private static string WeakCandidate()
        => DeezerTrack(id: "777", title: "Example", artist: "Example Artist", duration: 9);

    /// <summary>
    ///     Hydrates the source but resolves nothing, so the caller sees a non-null result carrying metadata
    ///     with no platform identity - the precise precondition for the permissive fallback.
    /// </summary>
    private static ITrackIdentityResolver MetadataOnlyResolver()
        => new StubResolver(_ => new TrackIdentityResolution(
            Title: "Example",
            Artist: "Example Artist",
            Album: null,
            Isrc: "ABCDE1234567",
            DurationMs: 200_000,
            SpotifyId: null,
            SpotifyUrl: null,
            DeezerId: null,
            DeezerUrl: null,
            AppleId: null,
            AppleUrl: null,
            AppleAlbumId: null,
            AppleAlbumName: null,
            AppleArtistName: null,
            AppleIsrc: null,
            AppleDurationMs: null,
            QobuzId: null,
            QobuzUrl: null,
            TidalId: null,
            TidalUrl: null,
            AmazonId: null,
            AmazonUrl: null,
            Candidates: []));

    private static SoundCloudTrack Track(string? isrc, int durationMs)
        => new()
        {
            Id = 1,
            Urn = "soundcloud:tracks:1",
            Title = "Example",
            MetadataArtist = "Example Artist",
            UploaderUsername = "example-artist",
            Artist = "example-artist",
            Isrc = isrc,
            DurationMs = durationMs,
            PermalinkUrl = SoundCloudUrl
        };

    private static string DeezerTrack(string id, string title, string artist, int duration, string isrc = "")
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            id,
            title,
            title_short = title,
            isrc,
            duration,
            link = "https://www.deezer.com/track/" + id,
            artist = new { id = "1", name = artist },
            album = new { id = "2", title = "Album" }
        });

    private static string DeezerSearch(string trackJson)
        => "{\"data\":[" + trackJson + "],\"total\":1}";

    private static ITrackIdentityResolver EmptyResolver()
        => new StubResolver(_ => TrackIdentityResolution.Empty(new TrackIdentityResolutionRequest(
            "soundcloud", SoundCloudUrl, null, null, null, null, null)));

    private static DeezerLinkMappingService BuildLinkMappingService(RecordingHandler handler, ITrackIdentityResolver resolver)
        => new(resolver, new StubHttpClientFactory(handler), NullLogger<DeezerLinkMappingService>.Instance);

    private sealed class Harness
    {
        private RecordingHandler? _handler;
        private StubSoundCloudClient? _soundCloud;

        public SoundCloudTrack SoundCloudTrack { get; init; } = new() { Title = "Example" };

        public string? DeezerIsrcResponse { get; init; }

        public string? DeezerSearchResponse { get; init; }

        public string? DeezerTrackFetchResponse { get; init; }

        public int DeezerIsrcRequests => _handler?.IsrcRequests ?? 0;

        public int DeezerSearchRequests => _handler?.SearchRequests ?? 0;

        public int SoundCloudResolutions => _soundCloud?.Resolutions ?? 0;

        public List<string> Calls => _handler?.Calls ?? new List<string>();

        public async Task<TrackIdentityResolution> ResolveAsync()
        {
            _handler = new RecordingHandler
            {
                WhenIsrc = DeezerIsrcResponse,
                WhenSearches = DeezerSearchResponse,
                WhenTrackFetch = DeezerTrackFetchResponse ?? DeezerIsrcResponse
            };
            _soundCloud = new StubSoundCloudClient(SoundCloudTrack, _handler);

            var session = new DeezerSessionManager(
                NullLogger<DeezerSessionManager>.Instance,
                () => null,
                () => _handler);
            // The resolver refuses to search without an authenticated Deezer session. The login state is set
            // directly because this harness tests mapping order, not the ARL login flow.
            typeof(DeezerSessionManager)
                .GetProperty(nameof(DeezerSessionManager.LoggedIn))!
                .SetValue(session, true);

            var client = new DeezerClient(NullLogger<DeezerClient>.Instance, session);
            var auth = new AuthenticatedDeezerService(
                NullLogger<AuthenticatedDeezerService>.Instance, client, new StubLoginStorage());

            var resolver = new TrackIdentityResolver(
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                auth,
                client,
                _soundCloud,
                NullLogger<TrackIdentityResolver>.Instance);

            return await resolver.ResolveAsync(
                new TrackIdentityResolutionRequest(
                    SourcePlatform: "soundcloud",
                    SourceUrl: SoundCloudUrl,
                    Title: null,
                    Artist: null,
                    Album: null,
                    Isrc: null,
                    DurationMs: null,
                    TargetPlatforms: new[] { "deezer" }),
                CancellationToken.None);
        }
    }

    private sealed class StubSoundCloudClient(SoundCloudTrack track, RecordingHandler calls) : ISoundCloudClient
    {
        public int Resolutions { get; private set; }

        public Task<SoundCloudTrack> ResolveTrackAsync(string url, CancellationToken cancellationToken)
        {
            Resolutions++;
            calls.Calls.Add("soundcloud:resolve");
            return Task.FromResult(track);
        }

        public Task<SoundCloudTrack?> ResolveTrackByIdAsync(string idOrUrn, CancellationToken cancellationToken)
            => Task.FromResult<SoundCloudTrack?>(track);

        public Task<SoundCloudSet> ResolveSetAsync(string url, CancellationToken cancellationToken)
            => Task.FromResult(new SoundCloudSet());

        public Task<IReadOnlyList<SoundCloudTrack>> SearchTracksAsync(string query, int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SoundCloudTrack>>([track]);

        public Task<SoundCloudStream> ResolveStreamAsync(SoundCloudTrack t, string? requestedQuality, CancellationToken cancellationToken)
            => Task.FromResult(new SoundCloudStream("https://example/playlist.m3u8", "sq"));

        public Task<bool> ValidateCredentialsAsync(string token, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }

    private sealed class StubResolver(Func<TrackIdentityResolutionRequest, TrackIdentityResolution> factory)
        : ITrackIdentityResolver
    {
        public Task<TrackIdentityResolution> ResolveAsync(TrackIdentityResolutionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(factory(request));
    }

    private sealed class StubLoginStorage : ILoginStorageService
    {
        public Task<LoginData?> LoadLoginCredentialsAsync() => Task.FromResult<LoginData?>(null);

        public Task SaveLoginCredentialsAsync(LoginData loginData) => Task.CompletedTask;

        public Task ResetLoginCredentialsAsync() => Task.CompletedTask;

        public Task ForceFixCorruptedFileAsync() => Task.CompletedTask;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>
    ///     Records the Deezer endpoints the resolver touches, in order, and answers with canned payloads.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<string> _calls = [];

        public List<string> Calls => _calls;

        public int IsrcRequests => _calls.Count(call => call == "deezer:isrc");

        public int SearchRequests => _calls.Count(call => call == "deezer:search");

        public string? WhenIsrc { get; set; }

        public string? WhenSearches { get; set; }

        public string? WhenTrackFetch { get; set; }

        public string? WhenAnyGet { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (url.Contains("isrc:", StringComparison.OrdinalIgnoreCase))
            {
                _calls.Add("deezer:isrc");
                return Reply(WhenIsrc);
            }

            if (path.Contains("/search/", StringComparison.OrdinalIgnoreCase))
            {
                _calls.Add("deezer:search");
                return Reply(WhenSearches);
            }

            if (path.Contains("/track/", StringComparison.OrdinalIgnoreCase))
            {
                _calls.Add("deezer:track");
                return Reply(WhenTrackFetch ?? WhenAnyGet);
            }

            _calls.Add($"deezer:other:{path}");
            return Reply(WhenAnyGet);
        }

        private Task<HttpResponseMessage> Reply(string? body)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json")
            });
    }
}