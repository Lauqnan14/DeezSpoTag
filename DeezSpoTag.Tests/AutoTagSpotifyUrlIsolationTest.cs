using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagSpotifyUrlIsolationTest
{
    private const string SpotifyTrackId = "0VjIjW4GlUZAMYd2vXMi3b";
    private const string CanonicalSpotifyUrl = "https://open.spotify.com/track/0VjIjW4GlUZAMYd2vXMi3b";

    [Theory]
    [InlineData("https://play.qobuz.com/track/359542303")]
    [InlineData("https://www.deezer.com/track/359542303")]
    [InlineData("https://music.apple.com/song/example/123?i=123")]
    [InlineData("https://open.spotify.com/album/0VjIjW4GlUZAMYd2vXMi3b")]
    public void NormalizeSpotifyTrackUrl_RejectsNonSpotifyTrackUrls(string value)
    {
        Assert.Null(NormalizeSpotifyTrackUrl(value));
    }

    [Theory]
    [InlineData(SpotifyTrackId)]
    [InlineData("spotify:track:0VjIjW4GlUZAMYd2vXMi3b")]
    [InlineData("https://play.spotify.com/track/0VjIjW4GlUZAMYd2vXMi3b")]
    [InlineData("https://open.spotify.com/track/0VjIjW4GlUZAMYd2vXMi3b?si=test")]
    public void NormalizeSpotifyTrackUrl_CanonicalizesSpotifyTrackIdentity(string value)
    {
        Assert.Equal(CanonicalSpotifyUrl, NormalizeSpotifyTrackUrl(value));
    }

    [Fact]
    public void ProviderMatches_DoNotReceiveCentrallyResolvedCrossPlatformTags()
    {
        Assert.Null(typeof(LocalAutoTagRunner).GetMethod(
            "ApplyResolvedIdentityTagsToTrack",
            BindingFlags.NonPublic | BindingFlags.Static));
    }

    [Fact]
    public void SpotifyMatcher_WritesOnlyCanonicalSpotifyTrackUrl()
    {
        var track = new SpotifyTrackInfo
        {
            TrackId = SpotifyTrackId,
            Url = "https://play.qobuz.com/track/359542303"
        };

        var mapped = (AutoTagTrack)typeof(SpotifyMatcher)
            .GetMethod("ToAutoTagTrack", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [track])!;

        Assert.Equal(CanonicalSpotifyUrl, mapped.Url);
        Assert.Equal(CanonicalSpotifyUrl, Assert.Single(mapped.Other["SPOTIFY_URL"]));
    }

    [Fact]
    public void ShazamIdFirstMatch_DoesNotExposeResolverProviderIdentity()
    {
        var match = new AutoTagMatchResult
        {
            Track = new AutoTagTrack
            {
                Url = CanonicalSpotifyUrl,
                TrackId = SpotifyTrackId,
                ReleaseId = "spotify-album-id",
                Other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SPOTIFY_URL"] = [CanonicalSpotifyUrl],
                    ["SPOTIFY_TRACK_ID"] = [SpotifyTrackId],
                    ["SOURCE"] = ["SPOTIFY"]
                }
            }
        };
        var source = new AutoTagAudioInfo
        {
            Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["SHAZAM_TRACK_ID"] = ["shazam-track-id"],
                ["SHAZAM_URL"] = ["https://www.shazam.com/track/shazam-track-id"]
            }
        };

        var mapped = (AutoTagMatchResult)RunnerMethod("PrepareShazamIdFirstMatch")
            .Invoke(null, [match, "spotify", source])!;

        Assert.Equal("https://www.shazam.com/track/shazam-track-id", mapped.Track.Url);
        Assert.Equal("shazam-track-id", mapped.Track.TrackId);
        Assert.Null(mapped.Track.ReleaseId);
        Assert.DoesNotContain(mapped.Track.Other.Keys, key => key.Contains("SPOTIFY", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mapped.Track.Other.Keys, key => key.Equals("SOURCE", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("shazam-track-id", Assert.Single(mapped.Track.Other["SHAZAM_TRACK_ID"]));
    }

    [Fact]
    public void SpotifyUrlWriter_IsOwnedByTheProviderIdentityContract()
    {
        var source = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        var writerStart = source.IndexOf("private static Task<ProviderIdentityWriteResult> WriteProviderIdentityAsync", StringComparison.Ordinal);
        Assert.True(writerStart >= 0, "the single provider identity writer was not found");
        var writerEnd = source.IndexOf("private sealed class ProviderIdentityTagSession", writerStart, StringComparison.Ordinal);
        var writer = source[writerStart..writerEnd];

        Assert.Contains("AutoTagIdentityTags.ResolveFamily(payload.ProviderId, field)", writer, StringComparison.Ordinal);
        Assert.Contains("ShouldOverwriteTag(config, family.SupportedTag)", writer, StringComparison.Ordinal);
        Assert.DoesNotContain("private static void WriteUrlTag", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private static void WriteTrackIdTag", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WwwAudioFileTag, track.Url", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderIdentityConsumers_TrustOnlyConfirmedRunPlanIdentity()
    {
        var source = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.Lyrics.cs");
        Assert.Contains("ProviderIdentityPayload? confirmedIdentity = null", source, StringComparison.Ordinal);
        var matching = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.PlatformMatching.cs");
        Assert.Contains("context.Plan.TryGetConfirmedProviderIdentity(", matching, StringComparison.Ordinal);
        Assert.Contains("RecordConfirmedProviderIdentity(context.FileIndex, capturedIdentity)", matching, StringComparison.Ordinal);
    }

    [Fact]
    public void ShazamFallbackThroughDeezer_DoesNotAttributeResolverIdentityToShazam()
    {
        var match = new AutoTagMatchResult
        {
            Track = new AutoTagTrack
            {
                Url = CanonicalSpotifyUrl,
                TrackId = SpotifyTrackId,
                ReleaseId = "spotify-album-id",
                Other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SHAZAM_MATCH_PROVIDER"] = ["DEEZER"],
                    ["SPOTIFY_URL"] = [CanonicalSpotifyUrl],
                    ["SPOTIFY_TRACK_ID"] = [SpotifyTrackId]
                }
            }
        };

        var payload = CaptureProviderIdentity("shazam", match);

        Assert.False(payload.IsNativeProviderResult);
        Assert.Null(payload.TrackId);
        Assert.Null(payload.ReleaseId);
        Assert.Null(payload.Url);
    }

    [Fact]
    public void SpotifyMatch_CapturesOnlySpotifyIdentity()
    {
        var match = new AutoTagMatchResult
        {
            Track = new AutoTagTrack
            {
                Url = CanonicalSpotifyUrl,
                TrackId = SpotifyTrackId,
                AlbumId = "spotify-album-id",
                ReleaseId = "spotify-album-id",
                ArtistId = "spotify-artist-id",
                AlbumArtistId = "spotify-album-artist-id"
            }
        };

        var payload = CaptureProviderIdentity("spotify", match);

        Assert.True(payload.IsNativeProviderResult);
        Assert.Equal("spotify", payload.ProviderId);
        Assert.Equal(SpotifyTrackId, payload.TrackId);
        Assert.Equal("spotify-album-id", payload.AlbumId);
        Assert.Equal("spotify-album-id", payload.ReleaseId);
        Assert.Equal("spotify-artist-id", payload.ArtistId);
        Assert.Equal("spotify-album-artist-id", payload.AlbumArtistId);
        Assert.Equal(CanonicalSpotifyUrl, payload.Url);
    }

    private static ProviderIdentityPayload CaptureProviderIdentity(string platform, AutoTagMatchResult match)
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "CaptureProviderIdentity",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<ProviderIdentityPayload>(method!.Invoke(null, [platform, match]));
    }

    private static string? NormalizeSpotifyTrackUrl(string value)
        => (string?)RunnerMethod("NormalizeSpotifyTrackUrl").Invoke(null, [value]);

    private static MethodInfo RunnerMethod(string name)
        => typeof(LocalAutoTagRunner).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException($"LocalAutoTagRunner.{name} not found.");

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !Directory.Exists(Path.Combine(current.FullName, "DeezSpoTag.Web")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
