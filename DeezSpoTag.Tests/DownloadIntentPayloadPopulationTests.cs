using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DeezSpoTag.Services.Download.Deezer;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadIntentPayloadPopulationTests
{
    private static readonly MethodInfo PopulateStandardQueuePayloadMethod =
        typeof(DownloadIntentService).GetMethod(
            "PopulateStandardQueuePayload",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DownloadIntentService.PopulateStandardQueuePayload not found.");

    private static readonly Type StandardPayloadContextType =
        typeof(DownloadIntentService).GetNestedType("StandardPayloadContext", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("DownloadIntentService.StandardPayloadContext not found.");

    private static readonly Type PayloadIdentityType =
        typeof(DownloadIntentService).GetNestedType("PayloadIdentity", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("DownloadIntentService.PayloadIdentity not found.");

    private static readonly Type EnqueueItemContextType =
        typeof(DownloadIntentService).GetNestedType("EnqueueItemContext", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("DownloadIntentService.EnqueueItemContext not found.");

    private static readonly MethodInfo TryValidateResolvedQueuePayloadMethod =
        typeof(DownloadIntentService).GetMethod(
            "TryValidateResolvedQueuePayload",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DownloadIntentService.TryValidateResolvedQueuePayload not found.");

    private static readonly MethodInfo CreateManualParityQueueIntentMethod =
        typeof(WatchlistEngine).GetMethod(
            "CreateManualParityQueueIntent",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("WatchlistEngine.CreateManualParityQueueIntent not found.");

    [Fact]
    public void ManualVisiblePreResolutionQueueItems_AreInsertedAsQueuedNotResolving()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Services/DownloadIntentService.cs"));

        Assert.DoesNotContain("initialStatus: \"resolving\"", source, StringComparison.Ordinal);
        Assert.Contains("initialStatus: \"queued\"", source, StringComparison.Ordinal);
        Assert.Contains("IsPreResolutionPayload(payload)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PopulateStandardQueuePayload_PreservesPreResolvedDeezerId()
    {
        var payload = new DeezerQueueItem
        {
            DeezerId = "3466216111"
        };
        var intent = new DownloadIntent
        {
            DeezerId = string.Empty,
            Title = "Hot Body",
            Artist = "Ayra Starr"
        };

        InvokePopulateStandardQueuePayload(payload, intent);

        Assert.Equal("3466216111", payload.DeezerId);
    }

    [Fact]
    public void PopulateStandardQueuePayload_UsesIntentDeezerId_WhenPayloadIsEmpty()
    {
        var payload = new DeezerQueueItem();
        var intent = new DownloadIntent
        {
            DeezerId = "3947111201",
            Title = "Ahere",
            Artist = "Willy Paul"
        };

        InvokePopulateStandardQueuePayload(payload, intent);

        Assert.Equal("3947111201", payload.DeezerId);
    }

    [Fact]
    public void PopulateStandardQueuePayload_PreservesCompleteEngineIdentityMatrix()
    {
        var payload = new DeezerQueueItem();
        var intent = new DownloadIntent
        {
            Isrc = "USSM12503434",
            SpotifyId = "4gMgiXfqyzZLMhsksGmbQV",
            DeezerId = "359542303",
            AppleId = "1447452170",
            TidalId = "451457120",
            QobuzId = "123456789",
            AmazonId = "B087654321",
            Title = "break da law",
            Artist = "21 Savage"
        };

        InvokePopulateStandardQueuePayload(payload, intent);

        Assert.Equal(intent.Isrc, payload.Isrc);
        Assert.Equal(intent.SpotifyId, payload.SpotifyId);
        Assert.Equal(intent.DeezerId, payload.DeezerId);
        Assert.Equal(intent.AppleId, payload.AppleId);
        Assert.Equal(intent.TidalId, payload.TidalId);
        Assert.Equal(intent.QobuzId, payload.QobuzId);
        Assert.Equal(intent.AmazonId, payload.AmazonId);
    }

    [Fact]
    public void PopulateStandardQueuePayload_DoesNotPersistPreviewLikeMusicDuration()
    {
        var payload = new DeezerQueueItem();
        var intent = new DownloadIntent
        {
            DeezerId = "2370825735",
            Title = "Enjoy",
            Artist = "Jux",
            DurationMs = 51121,
            AppleDurationMs = 51121
        };

        InvokePopulateStandardQueuePayload(payload, intent, durationSeconds: 51);

        Assert.Equal(0, payload.DurationSeconds);
    }

    [Fact]
    public void CreateManualParityQueueIntent_PreservesResolvedMetadata()
    {
        var intent = new DownloadIntent
        {
            SourceService = "apple",
            SourceUrl = "https://music.apple.com/us/song/example/123456789?i=123456789",
            AppleId = "123456789",
            QobuzId = "987654321",
            TidalId = "456789123",
            AmazonId = "B012345678",
            Title = "Resolved Title",
            Artist = "Resolved Artist",
            Album = "Resolved Album",
            AlbumArtist = "Resolved Album Artist",
            Cover = "https://example.test/cover.jpg",
            DurationMs = 185000,
            TrackNumber = 4,
            DiscNumber = 1,
            ReleaseDate = "2024-01-01",
            Genres = new List<string> { "Soul" },
            Composer = "Resolved Composer"
        };

        var result = InvokeCreateManualParityQueueIntent(intent);

        Assert.Equal(intent.Title, result.Title);
        Assert.Equal(intent.Artist, result.Artist);
        Assert.Equal(intent.Album, result.Album);
        Assert.Equal(intent.AlbumArtist, result.AlbumArtist);
        Assert.Equal(intent.Cover, result.Cover);
        Assert.Equal(intent.DurationMs, result.DurationMs);
        Assert.Equal(intent.TrackNumber, result.TrackNumber);
        Assert.Equal(intent.DiscNumber, result.DiscNumber);
        Assert.Equal(intent.ReleaseDate, result.ReleaseDate);
        Assert.Equal(intent.Genres, result.Genres);
        Assert.Equal(intent.Composer, result.Composer);
        Assert.Equal(intent.AppleId, result.AppleId);
        Assert.Equal(intent.QobuzId, result.QobuzId);
        Assert.Equal(intent.TidalId, result.TidalId);
        Assert.Equal(intent.AmazonId, result.AmazonId);
    }

    [Fact]
    public void TryValidateResolvedQueuePayload_RejectsMusicWithoutMetadata()
    {
        var payload = new AppleQueueItem
        {
            AppleId = "205608871",
            SourceUrl = "https://music.apple.com/us/song/example/205608871?i=205608871",
            ContentType = "stereo"
        };
        var context = CreateEnqueueItemContext(
            engine: "apple",
            contentType: "stereo",
            title: string.Empty,
            artist: string.Empty,
            appleTrackId: "205608871");

        var decision = InvokeTryValidateResolvedQueuePayload(payload, context);

        Assert.NotNull(decision);
        Assert.Equal("unresolved_metadata", ReadDecisionProperty(decision!, "ReasonCode"));
    }

    [Fact]
    public void TryValidateResolvedQueuePayload_RejectsDeezerPayloadWithoutDeezerIdentity()
    {
        var payload = new DeezerQueueItem
        {
            Title = "Resolved Title",
            Artist = "Resolved Artist",
            AppleId = "206518504",
            SourceUrl = "https://music.apple.com/us/song/example/206518504?i=206518504",
            ContentType = "stereo"
        };
        var context = CreateEnqueueItemContext(
            engine: "deezer",
            contentType: "stereo",
            title: "Resolved Title",
            artist: "Resolved Artist",
            appleTrackId: "206518504");

        var decision = InvokeTryValidateResolvedQueuePayload(payload, context);

        Assert.NotNull(decision);
        Assert.Equal("unresolved_engine_identity", ReadDecisionProperty(decision!, "ReasonCode"));
    }

    private static void InvokePopulateStandardQueuePayload(
        DeezerQueueItem payload,
        DownloadIntent intent,
        int durationSeconds = 0)
    {
        var context = Activator.CreateInstance(
            StandardPayloadContextType,
            "https://www.deezer.com/track/3466216111",
            "album",
            "stereo",
            new List<string> { "deezer|1" },
            0,
            new List<FallbackPlanStep>(),
            string.Empty,
            durationSeconds,
            null,
            string.Empty);

        Assert.NotNull(context);
        PopulateStandardQueuePayloadMethod.Invoke(null, new object?[] { payload, intent, context });
    }

    private static DownloadIntent InvokeCreateManualParityQueueIntent(DownloadIntent intent)
    {
        var result = CreateManualParityQueueIntentMethod.Invoke(null, new object?[] { intent });
        return Assert.IsType<DownloadIntent>(result);
    }

    private static object CreateEnqueueItemContext(
        string engine,
        string contentType,
        string title,
        string artist,
        string? deezerTrackId = null,
        string? appleTrackId = null)
    {
        var identity = Activator.CreateInstance(
            PayloadIdentityType,
            null,
            deezerTrackId,
            null,
            null,
            null,
            null,
            null,
            appleTrackId,
            null,
            null,
            null,
            null,
            null,
            engine,
            contentType,
            null,
            title,
            artist,
            artist,
            null,
            Array.Empty<string>(),
            null,
            null,
            null,
            null,
            contentType,
            null);
        Assert.NotNull(identity);

        var context = Activator.CreateInstance(
            EnqueueItemContextType,
            identity,
            new DeezSpoTagSettings(),
            null,
            false,
            null,
            false,
            int.MinValue,
            null,
            false);
        Assert.NotNull(context);
        return context;
    }

    private static object? InvokeTryValidateResolvedQueuePayload<TPayload>(TPayload payload, object context)
        where TPayload : class
    {
        var method = TryValidateResolvedQueuePayloadMethod.MakeGenericMethod(typeof(TPayload));
        return method.Invoke(null, new[] { payload, context, true });
    }

    private static string? ReadDecisionProperty(object decision, string propertyName)
    {
        return decision.GetType().GetProperty(propertyName)?.GetValue(decision) as string;
    }
}
