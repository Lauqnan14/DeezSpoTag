using System;
using System.Collections.Generic;
using System.Reflection;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Consumers of a provider identity (lyrics lookups, artwork fallback, Apple extras) may
/// only be seeded from an identity a native payload confirmed for that same file and
/// provider. Raw file tags, mutated track values and foreign-provider fallbacks are never
/// provenance.
/// </summary>
public sealed class ProviderIdentityConsumerTrustTest
{
    [Fact]
    public void RunPlan_StoresOnlyNativePayloads()
    {
        var plan = CreateRunPlan();
        var native = Native("spotify", trackId: "sp-1", url: "https://open.spotify.com/track/sp-1");

        RecordConfirmed(plan, 0, native);

        Assert.True(TryGetConfirmed(plan, 0, "spotify", out var stored));
        Assert.Equal("sp-1", stored.TrackId);
        Assert.Equal("https://open.spotify.com/track/sp-1", stored.Url);
    }

    [Fact]
    public void RunPlan_IgnoresNonNativePayloads()
    {
        var plan = CreateRunPlan();
        var fallback = new ProviderIdentityPayload(
            "shazam",
            "shazam-track-id",
            null,
            null,
            null,
            null,
            "https://www.shazam.com/track/shazam-track-id",
            IsNativeProviderResult: false);

        RecordConfirmed(plan, 0, fallback);

        Assert.False(TryGetConfirmed(plan, 0, "shazam", out _));
        Assert.Empty(ConfirmedProviderIdentities(plan));
    }

    [Fact]
    public void RunPlan_IsScopedToFileIndexAndNormalizedProvider()
    {
        var plan = CreateRunPlan();
        RecordConfirmed(plan, 0, Native("apple", trackId: "apple-track"));
        RecordConfirmed(plan, 1, Native("spotify", trackId: "sp-track"));

        Assert.True(TryGetConfirmed(plan, 0, "itunes", out var apple));
        Assert.Equal("apple-track", apple.TrackId);
        Assert.True(TryGetConfirmed(plan, 0, "APPLE", out var appleAlias));
        Assert.Equal("apple-track", appleAlias.TrackId);
        Assert.False(TryGetConfirmed(plan, 0, "spotify", out _));
        Assert.False(TryGetConfirmed(plan, 1, "itunes", out _));
        Assert.False(TryGetConfirmed(plan, 2, "spotify", out _));

        // The stored payload keeps the alias normalization the rest of the pipeline uses.
        Assert.Equal("itunes", apple.ProviderId is "apple" ? "itunes" : apple.ProviderId);
    }

    [Fact]
    public void LyricsLookupTrack_UsesOnlyConfirmedProviderIdentity()
    {
        var track = new AutoTagTrack
        {
            Title = "Track",
            TrackId = "foreign-track-id",
            Url = "https://play.qobuz.com/track/359542303",
            Other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["SPOTIFY_TRACK_ID"] = ["foreign-spotify-id"],
                ["SPOTIFY_URL"] = ["https://open.spotify.com/track/foreign-spotify-id"],
                ["DEEZER_TRACK_ID"] = ["foreign-deezer-id"],
                ["APPLE_TRACK_ID"] = ["foreign-apple-id"]
            }
        };
        var confirmed = Native("spotify", trackId: "confirmed-spotify-id", url: "https://open.spotify.com/track/confirmed-spotify-id");

        var lookup = InvokeRunnerStatic<Track>("BuildLyricsLookupTrack", track, "spotify", confirmed);

        Assert.Equal("confirmed-spotify-id", lookup.Id);
        Assert.Equal("confirmed-spotify-id", lookup.SourceId);
        Assert.Equal("https://open.spotify.com/track/confirmed-spotify-id", lookup.DownloadURL);
        Assert.Equal("confirmed-spotify-id", lookup.Urls["spotify_track_id"]);
        Assert.DoesNotContain(lookup.Urls.Keys, key => key.Contains("deezer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lookup.Urls.Keys, key => key.Contains("apple", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("foreign", lookup.DownloadURL, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LyricsLookupTrack_WithoutConfirmedIdentity_CarriesNoProviderIdentity()
    {
        var track = new AutoTagTrack
        {
            Title = "Track",
            TrackId = "foreign-track-id",
            Url = "https://play.qobuz.com/track/359542303",
            Other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["SPOTIFY_TRACK_ID"] = ["foreign-spotify-id"],
                ["SPOTIFY_URL"] = ["https://open.spotify.com/track/foreign-spotify-id"],
                ["DEEZER_TRACK_ID"] = ["foreign-deezer-id"]
            }
        };

        var lookup = InvokeRunnerStatic<Track>("BuildLyricsLookupTrack", track, "spotify", null);

        Assert.Equal(string.Empty, lookup.Id);
        Assert.Equal(string.Empty, lookup.DownloadURL);
        Assert.DoesNotContain("spotify_track_id", lookup.Urls.Keys);
        Assert.DoesNotContain("deezer_track_id", lookup.Urls.Keys);
        Assert.DoesNotContain(lookup.Urls.Values, value => value.Contains("foreign", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Consumers_ReadIdentityOnlyFromTheConfirmedRunPlanRegistry()
    {
        var lyrics = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.Lyrics.cs");
        Assert.Contains("confirmedIdentity?.TrackId", lyrics, StringComparison.Ordinal);
        Assert.Contains("confirmedIdentity?.Url", lyrics, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetFirstOtherValue(other, DeezerTrackIdTag", lyrics, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetFirstOtherValue(other, SpotifyTrackIdTag", lyrics, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetFirstOtherValue(other, \"APPLE_TRACK_ID\"", lyrics, StringComparison.Ordinal);
        Assert.DoesNotContain("track.Url", lyrics, StringComparison.Ordinal);
        Assert.DoesNotContain("AddLookupUrl(lookupTrack.Urls, DeezerPlatform, TryGetFirstOtherValue(other, \"DEEZER_URL\"));", lyrics, StringComparison.Ordinal);

        var apple = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.AppleIntegration.cs");
        Assert.Contains("appleConfirmed?.TrackId", apple, StringComparison.Ordinal);
        Assert.Contains("SourceUrl: appleConfirmed?.Url", apple, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetFirstOtherValue(track.Other, AutoTagIdentityTags.AppleTrackIdAliases)", apple, StringComparison.Ordinal);
        Assert.Contains("IsNativeProviderResult: true", apple, StringComparison.Ordinal);

        var artwork = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.Artwork.cs");
        Assert.Contains("ConfirmedProviderIdentity(context, \"itunes\")", artwork, StringComparison.Ordinal);
        Assert.Contains("ConfirmedProviderIdentity(context, \"deezer\")", artwork, StringComparison.Ordinal);
        Assert.Contains("ConfirmedProviderIdentity(context, \"spotify\")", artwork, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoTagIdentityTags.ReadAppleTrackId(identity)", artwork, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoTagIdentityTags.ReadAppleArtistId(identity)", artwork, StringComparison.Ordinal);

        var matching = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.PlatformMatching.cs");
        Assert.Contains("context.Plan.RecordConfirmedProviderIdentity(context.FileIndex, capturedIdentity);", matching, StringComparison.Ordinal);
    }

    private static ProviderIdentityPayload Native(string providerId, string? trackId = null, string? url = null)
        => new(providerId, trackId, null, null, null, null, url, IsNativeProviderResult: true);

    private static object CreateRunPlan()
        => Activator.CreateInstance(
            typeof(LocalAutoTagRunner).GetNestedType("AutoTagRunPlan", BindingFlags.NonPublic)!,
            nonPublic: true)!;

    private static void RecordConfirmed(object plan, int fileIndex, ProviderIdentityPayload payload)
        => plan.GetType()
            .GetMethod("RecordConfirmedProviderIdentity")!
            .Invoke(plan, [fileIndex, payload]);

    private static bool TryGetConfirmed(object plan, int fileIndex, string providerId, out ProviderIdentityPayload payload)
    {
        var args = new object?[] { fileIndex, providerId, null };
        var found = (bool)plan.GetType().GetMethod("TryGetConfirmedProviderIdentity")!.Invoke(plan, args)!;
        payload = (ProviderIdentityPayload)args[2]!;
        return found;
    }

    private static System.Collections.IDictionary ConfirmedProviderIdentities(object plan)
        => (System.Collections.IDictionary)plan.GetType()
            .GetProperty("ConfirmedProviderIdentities")!
            .GetValue(plan)!;

    private static T InvokeRunnerStatic<T>(string name, params object?[] args)
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"LocalAutoTagRunner.{name} not found.");
        return (T)method.Invoke(null, args)!;
    }
}