using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadTagSourceIdentityGuardTests
{
    private static readonly MethodInfo ResolveKnownDeezerIdMethod =
        typeof(EngineAudioPostDownloadHelper).GetMethod(
            "ResolveKnownDeezerId",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ResolveKnownDeezerId not found.");

    [Fact]
    public void ResolveKnownDeezerId_DoesNotTreatQobuzSourceUrlAsDeezerId()
    {
        var payload = new TestQueueItem
        {
            SourceUrl = "https://play.qobuz.com/track/418540522",
            Title = "PM",
            Artist = "Willy Paul",
            Album = "PM"
        };
        var track = new Track
        {
            Title = "PM",
            MainArtist = new Artist("0", "Willy Paul"),
            Album = new Album("0", "PM")
        };

        var resolved = ResolveKnownDeezerId(track, payload);

        Assert.Null(resolved);
        Assert.True(string.IsNullOrWhiteSpace(payload.DeezerId));
    }

    [Fact]
    public void ResolveKnownDeezerId_AllowsActualDeezerSourceUrl()
    {
        var payload = new TestQueueItem
        {
            SourceUrl = "https://www.deezer.com/track/123456",
            Title = "PM",
            Artist = "Willy Paul",
            Album = "PM"
        };
        var track = new Track
        {
            Title = "PM",
            MainArtist = new Artist("0", "Willy Paul"),
            Album = new Album("0", "PM")
        };

        var resolved = ResolveKnownDeezerId(track, payload);

        Assert.Equal("123456", resolved);
    }

    [Fact]
    public void ResolveKnownDeezerId_RejectsQobuzUrlStoredInDeezerId()
    {
        var payload = new TestQueueItem
        {
            DeezerId = "https://play.qobuz.com/track/418540522",
            Title = "PM",
            Artist = "Willy Paul",
            Album = "PM"
        };
        var track = new Track
        {
            Title = "PM",
            MainArtist = new Artist("0", "Willy Paul"),
            Album = new Album("0", "PM")
        };

        var resolved = ResolveKnownDeezerId(track, payload);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task ApplyProfileMetadataOverrideAsync_RejectsUnrelatedResolvedMetadataAndRestoresOriginalTrack()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMetadataResolver, WrongTrackResolver>();
        services.AddSingleton<IMetadataResolverRegistry, MetadataResolverRegistry>();
        var provider = services.BuildServiceProvider();
        var track = new Track
        {
            Title = "PM",
            ArtistString = "Willy Paul",
            ArtistsString = "Willy Paul",
            MainArtist = new Artist("0", "Willy Paul"),
            Album = new Album("0", "PM"),
            Duration = 180
        };
        var payload = new TestQueueItem
        {
            Title = "PM",
            Artist = "Willy Paul",
            Album = "PM",
            DurationSeconds = 180
        };

        var applied = await EngineAudioPostDownloadHelper.ApplyProfileMetadataOverrideAsync(
            new EngineAudioPostDownloadHelper.ProfileMetadataOverrideRequest(
                track,
                payload,
                new DeezSpoTagSettings(),
                provider,
                "qobuz",
                DownloadTagSourceHelper.DeezerSource,
                NullLogger.Instance,
                CancellationToken.None));

        Assert.False(applied);
        Assert.Equal("PM", track.Title);
        Assert.Equal("Willy Paul", track.MainArtist?.Name);
        Assert.Equal("PM", track.Album?.Title);
        Assert.True(string.IsNullOrWhiteSpace(payload.DeezerId));
    }

    [Fact]
    public async Task ApplyProfileMetadataOverrideAsync_KeepsLatinPayloadAlbumWhenResolverReturnsLocalizedAlbum()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMetadataResolver, LocalizedAlbumResolver>();
        services.AddSingleton<IMetadataResolverRegistry, MetadataResolverRegistry>();
        var provider = services.BuildServiceProvider();
        var track = new Track
        {
            Title = "Memories",
            ArtistString = "Maroon 5",
            ArtistsString = "Maroon 5",
            MainArtist = new Artist("0", "Maroon 5"),
            Album = new Album("0", "JORDI (Deluxe)"),
            ISRC = "USUM71913350",
            Duration = 189
        };
        var payload = new TestQueueItem
        {
            Title = "Memories",
            Artist = "Maroon 5",
            Album = "JORDI (Deluxe)",
            Isrc = "USUM71913350",
            DurationSeconds = 189,
            QobuzId = "123327344"
        };

        var applied = await EngineAudioPostDownloadHelper.ApplyProfileMetadataOverrideAsync(
            new EngineAudioPostDownloadHelper.ProfileMetadataOverrideRequest(
                track,
                payload,
                new DeezSpoTagSettings(),
                provider,
                "qobuz",
                DownloadTagSourceHelper.QobuzSource,
                NullLogger.Instance,
                CancellationToken.None));

        Assert.True(applied);
        Assert.Equal("Memories", track.Title);
        Assert.Equal("Maroon 5", track.MainArtist?.Name);
        Assert.Equal("JORDI (Deluxe)", track.Album?.Title);
        Assert.Equal("123327344", payload.QobuzId);
    }

    private static string? ResolveKnownDeezerId(Track track, EngineQueueItemBase payload)
        => ResolveKnownDeezerIdMethod.Invoke(null, new object[] { track, payload }) as string;

    private sealed class TestQueueItem : EngineQueueItemBase
    {
    }

    private sealed class WrongTrackResolver : IMetadataResolver
    {
        public string SourceKey => DownloadTagSourceHelper.DeezerSource;

        public Task ResolveTrackAsync(Track track, DeezSpoTagSettings settings, CancellationToken cancellationToken)
        {
            track.Title = "Love Spy / Back to Spy";
            track.MainArtist = new Artist("0", "Mike Mareen");
            track.ArtistString = "Mike Mareen";
            track.ArtistsString = "Mike Mareen";
            track.Artists = new List<string> { "Mike Mareen" };
            track.Album = new Album("0", "The Best Of");
            track.ISRC = "DEKB71500819";
            track.Duration = 300;
            return Task.CompletedTask;
        }
    }

    private sealed class LocalizedAlbumResolver : IMetadataResolver
    {
        public string SourceKey => DownloadTagSourceHelper.QobuzSource;

        public Task ResolveTrackAsync(Track track, DeezSpoTagSettings settings, CancellationToken cancellationToken)
        {
            track.Title = "Memories";
            track.MainArtist = new Artist("0", "Maroon 5");
            track.ArtistString = "Maroon 5";
            track.ArtistsString = "Maroon 5";
            track.Artists = new List<string> { "Maroon 5" };
            track.Album = new Album("0", "傷心情歌");
            track.ISRC = "USUM71913350";
            track.Duration = 189;
            return Task.CompletedTask;
        }
    }
}
