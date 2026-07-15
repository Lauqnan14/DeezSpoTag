using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagAppleIdentityTests
{
    [Fact]
    public void ItunesPlatform_AdvertisesAppleCatalogAndAtmosMetadataFields()
    {
        var supported = ItunesPlatform.SharedDownloadParityTags();

        Assert.Contains(SupportedTag.Copyright, supported);
        Assert.Contains(SupportedTag.Composer, supported);
        Assert.Contains(SupportedTag.InvolvedPeople, supported);
        Assert.Contains(SupportedTag.OtherTags, supported);
    }

    [Fact]
    public void AppleCatalogMetadata_SupplementsExactItunesResultWithSelectedFields()
    {
        var config = CreateRunnerConfig(
            "genre", "isrc", "label", "copyright", "composer", "involvedPeople", "otherTags", "explicit");
        var track = new AutoTagTrack
        {
            Genres = ["Afrobeats"],
            Other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        };
        using var payload = JsonDocument.Parse("""
        {
          "genreNames": ["Music", "Afrobeats", "African"],
          "isrc": "QZWA32202168",
          "recordLabel": "Encore Recordings",
          "composerName": "Anthony Ebuka Victor; John Doe",
          "copyright": "2026 Encore Recordings",
          "contentRating": "explicit",
          "audioTraits": ["atmos", "lossless"]
        }
        """);

        ApplyAppleCatalogMetadata(track, config, payload.RootElement);

        Assert.Equal(["Afrobeats", "African"], track.Genres);
        Assert.Equal("QZWA32202168", track.Isrc);
        Assert.Equal("Encore Recordings", track.Label);
        Assert.Equal("2026 Encore Recordings", Assert.Single(track.Other["copyright"]));
        Assert.Equal(["Anthony Ebuka Victor", "John Doe"], track.Other["composer"]);
        Assert.Equal(["Composer: Anthony Ebuka Victor", "Composer: John Doe"], track.Other["involvedPeople"]);
        Assert.Equal(["atmos", "lossless"], track.Other["APPLE_AUDIO_TRAITS"]);
        Assert.Equal("1", Assert.Single(track.Other["APPLE_IS_ATMOS"]));
        Assert.True(track.Explicit);
    }

    [Fact]
    public void AppleCatalogMetadata_PreservesItunesGenreWhenCatalogHasNoGenres()
    {
        var config = CreateRunnerConfig("genre");
        var track = new AutoTagTrack
        {
            Genres = ["Afrobeats"],
            Other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        };
        using var payload = JsonDocument.Parse("""{ "genreNames": [] }""");

        ApplyAppleCatalogMetadata(track, config, payload.RootElement);

        Assert.Equal(["Afrobeats"], track.Genres);
    }

    [Theory]
    [InlineData("APPLE_TRACK_ID")]
    [InlineData("APPLE_TRACKID")]
    [InlineData("APPLE_MUSIC_TRACK_ID")]
    [InlineData("ITUNES_TRACK_ID")]
    [InlineData("ITUNESCATALOGID")]
    public async Task ItunesMatcher_UsesCanonicalAppleIdAliasesForExactLookup(string tagName)
    {
        var handler = new CapturingJsonHandler("""
        {
          "resultCount": 1,
          "results": [{
            "wrapperType": "track",
            "kind": "song",
            "artistId": 6786449700,
            "collectionId": 6786449711,
            "trackId": 6786449714,
            "artistName": "Victony",
            "collectionName": "SLICK - Single",
            "trackName": "SLICK",
            "trackTimeMillis": 106909,
            "primaryGenreName": "Afrobeats",
            "isrc": "QZWA32202168",
            "trackExplicitness": "explicit",
            "copyright": "2026 Encore Recordings",
            "releaseDate": "2026-07-08T12:00:00Z"
          }]
        }
        """);
        using var client = new HttpClient(handler, disposeHandler: true);
        var itunesClient = new ItunesClient(client, NullLogger<ItunesClient>.Instance);
        itunesClient.SetRateLimit(-1);
        var matcher = new ItunesMatcher(itunesClient);
        var info = new AutoTagAudioInfo
        {
            Title = "SLICK",
            Artist = "Victony",
            Artists = ["Victony"],
            Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [tagName] = ["6786449714"]
            }
        };

        var match = await matcher.MatchAsync(
            info,
            new AutoTagMatchingConfig(),
            new ItunesMatchConfig { MatchById = true, Country = "ke" },
            CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal("id", match!.MatchStrategy);
        Assert.Equal("6786449714", match.Track.TrackId);
        Assert.Equal("Afrobeats", Assert.Single(match.Track.Genres));
        Assert.Equal("6786449714", Assert.Single(match.Track.Other["APPLE_TRACK_ID"]));
        Assert.Equal("2026 Encore Recordings", Assert.Single(match.Track.Other["copyright"]));
        Assert.Contains("/lookup", handler.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("id=6786449714", handler.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("country=ke", handler.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItunesMatcher_DoesNotTextSearchWhenAuthoritativeAppleIdIsUnresolved()
    {
        var handler = new CapturingJsonHandler("""
        {
          "resultCount": 0,
          "results": []
        }
        """);
        using var client = new HttpClient(handler, disposeHandler: true);
        var itunesClient = new ItunesClient(client, NullLogger<ItunesClient>.Instance);
        itunesClient.SetRateLimit(-1);
        var matcher = new ItunesMatcher(itunesClient);
        var info = new AutoTagAudioInfo
        {
            Title = "SLICK",
            Artist = "Victony",
            Artists = ["Victony"],
            Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["APPLE_TRACK_ID"] = ["6786449714"]
            }
        };

        var match = await matcher.MatchAsync(
            info,
            new AutoTagMatchingConfig(),
            new ItunesMatchConfig { MatchById = true, Country = "ke" },
            CancellationToken.None);

        Assert.Null(match);
        Assert.Single(handler.RequestUris);
        Assert.Equal("/lookup", handler.RequestUris[0].AbsolutePath);
    }

    private sealed class CapturingJsonHandler(string payload) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }

    private static object CreateRunnerConfig(params string[] tags)
    {
        var type = typeof(LocalAutoTagRunner).GetNestedType("AutoTagRunnerConfig", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AutoTagRunnerConfig was not found.");
        var config = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("AutoTagRunnerConfig could not be created.");
        type.GetProperty("Tags")!.SetValue(config, tags.ToList());
        return config;
    }

    private static void ApplyAppleCatalogMetadata(AutoTagTrack track, object config, JsonElement attributes)
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "ApplyAppleCatalogMetadata",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ApplyAppleCatalogMetadata was not found.");
        method.Invoke(null, [track, config, attributes]);
    }
}
