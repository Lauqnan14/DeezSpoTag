using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Queue;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class QueuePreResolutionPayloadTests
{
    [Fact]
    public void ParseOrEmpty_ReturnsEmptyObjectForInvalidJson()
    {
        var payload = QueuePreResolutionPayload.ParseOrEmpty("{not valid");

        Assert.Empty(payload);
        Assert.Equal(QueuePreResolutionPayload.Pending, QueuePreResolutionPayload.ReadStatus(payload));
    }

    [Fact]
    public void MarkResolving_WritesStatusStartedAtAndClearsError()
    {
        var now = DateTimeOffset.Parse("2026-05-08T08:00:00Z");
        var payload = new JsonObject
        {
            ["ResolutionError"] = "old"
        };

        QueuePreResolutionPayload.MarkResolving(payload, now);

        Assert.True(QueuePreResolutionPayload.IsResolving(payload));
        Assert.Equal(string.Empty, payload["ResolutionError"]?.ToString());
        Assert.Equal(string.Empty, payload["resolutionError"]?.ToString());
        Assert.Equal(now, DateTimeOffset.Parse(payload["ResolutionStartedAtUtc"]?.ToString() ?? string.Empty));
    }

    [Fact]
    public void ApplyResolved_WritesEngineSourceQualityAutoIndexAndFallbackPlan()
    {
        var now = DateTimeOffset.Parse("2026-05-08T09:00:00Z");
        var payload = new JsonObject();
        var plan = new List<FallbackPlanStep>
        {
            new("step-0", "qobuz", "27", Array.Empty<string>(), "direct_url")
        };

        QueuePreResolutionPayload.ApplyResolved(
            payload,
            new QueuePreResolutionPayload.ResolutionResult(
                "qobuz",
                "https://play.qobuz.com/track/123",
                "27",
                2,
                plan,
                null,
                Isrc: "QT3F22565438",
                DeezerId: "359542303",
                SpotifyId: "spotify-track",
                AppleId: "apple-track",
                QobuzId: "123",
                TidalId: "456",
                AmazonId: "B0TEST1234",
                DurationMs: 205000,
                DestinationFolderId: 1,
                ContentType: "stereo"),
            now);

        Assert.True(QueuePreResolutionPayload.IsResolved(payload));
        Assert.Equal("qobuz", QueuePreResolutionPayload.ReadResolvedEngine(payload));
        Assert.Equal("qobuz", payload["Engine"]?.ToString());
        Assert.Equal("https://play.qobuz.com/track/123", QueuePreResolutionPayload.ReadResolvedSourceUrl(payload));
        Assert.Equal("https://play.qobuz.com/track/123", payload["SourceUrl"]?.ToString());
        Assert.Equal("27", payload["Quality"]?.ToString());
        Assert.Equal("QT3F22565438", payload["Isrc"]?.ToString());
        Assert.Equal("359542303", payload["DeezerId"]?.ToString());
        Assert.Equal("spotify-track", payload["SpotifyId"]?.ToString());
        Assert.Equal("apple-track", payload["AppleId"]?.ToString());
        Assert.Equal("123", payload["QobuzId"]?.ToString());
        Assert.Equal("123", payload["qobuzId"]?.ToString());
        Assert.Equal("456", payload["TidalId"]?.ToString());
        Assert.Equal("456", payload["tidalId"]?.ToString());
        Assert.Equal("B0TEST1234", payload["AmazonId"]?.ToString());
        Assert.Equal("B0TEST1234", payload["amazonId"]?.ToString());
        Assert.Equal("205000", payload["DurationMs"]?.ToString());
        Assert.Equal("205", payload["DurationSeconds"]?.ToString());
        Assert.Equal("205", payload["durationSeconds"]?.ToString());
        Assert.Equal("1", payload["DestinationFolderId"]?.ToString());
        Assert.Equal("stereo", payload["ContentType"]?.ToString());
        Assert.Equal("0", payload["AutoIndex"]?.ToString());
        Assert.IsType<JsonArray>(payload["FallbackPlan"]);
        Assert.IsType<JsonArray>(payload["fallbackPlan"]);
        Assert.Equal(now, QueuePreResolutionPayload.ReadResolvedAt(payload));
    }

    [Fact]
    public void ApplyResolved_RepairsQualityAndIndexToTheSamePersistedPlanStep()
    {
        var payload = new JsonObject();
        var plan = new List<FallbackPlanStep>
        {
            new("step-0", "qobuz", "7", [], "direct_url"),
            new("step-1", "tidal", "HI_RES", [], "direct_url"),
            new("step-2", "amazon", "ULTRA_HD_FLAC", [], "direct_url"),
            new("step-3", "qobuz", "6", [], "direct_url")
        };

        QueuePreResolutionPayload.ApplyResolved(
            payload,
            new QueuePreResolutionPayload.ResolutionResult(
                "qobuz",
                "https://play.qobuz.com/track/123",
                "6",
                0,
                plan,
                null),
            DateTimeOffset.UtcNow);

        Assert.Equal("6", payload["Quality"]?.ToString());
        Assert.Equal("3", payload["AutoIndex"]?.ToString());
        Assert.Equal("3", payload["ResolvedAutoIndex"]?.ToString());
    }

    [Theory]
    [InlineData("deezer")]
    [InlineData("apple")]
    [InlineData("tidal")]
    [InlineData("qobuz")]
    [InlineData("amazon")]
    public void ApplyResolved_PreservesCompleteResolvedMetadataForEveryEngine(string engine)
    {
        var payload = new JsonObject
        {
            ["Title"] = "Existing title",
            ["title"] = "Existing title",
            ["SourceService"] = "boomplay",
            ["sourceService"] = "boomplay"
        };
        var metadata = new QueuePreResolutionPayload.ResolvedMetadata(
            Title: "Resolved title",
            Artist: "Resolved artist",
            Album: "Resolved album",
            AlbumArtist: "Resolved album artist",
            Cover: "https://images.example.test/cover.jpg",
            Genres: new[] { "Afrobeats", "Pop" },
            Label: "Resolved label",
            Copyright: "Resolved copyright",
            Explicit: true,
            Composer: "Resolved composer",
            ReleaseDate: "2026-07-21",
            TrackNumber: 3,
            DiscNumber: 1,
            TrackTotal: 12,
            DiscTotal: 1,
            Url: "https://example.test/track",
            Barcode: "123456789012",
            Tempo: 110.5,
            MusicKey: "8A");

        QueuePreResolutionPayload.ApplyResolved(
            payload,
            new QueuePreResolutionPayload.ResolutionResult(
                engine,
                $"https://example.test/{engine}/track",
                "lossless",
                0,
                Array.Empty<FallbackPlanStep>(),
                null,
                Metadata: metadata),
            DateTimeOffset.Parse("2026-07-21T12:00:00Z"));

        Assert.Equal("Resolved title", payload["Title"]?.ToString());
        Assert.Equal("Resolved artist", payload["Artist"]?.ToString());
        Assert.Equal("Resolved album", payload["Album"]?.ToString());
        Assert.Equal("Resolved album artist", payload["AlbumArtist"]?.ToString());
        Assert.Equal("https://images.example.test/cover.jpg", payload["Cover"]?.ToString());
        Assert.Equal("https://images.example.test/cover.jpg", payload["cover"]?.ToString());
        Assert.Equal("Resolved label", payload["Label"]?.ToString());
        Assert.True(payload["Explicit"]?.GetValue<bool>());
        Assert.Equal("3", payload["TrackNumber"]?.ToString());
        Assert.Equal(110.5, payload["Tempo"]?.GetValue<double>());
        Assert.Equal("8A", payload["MusicKey"]?.ToString());
        Assert.Equal(2, payload["Genres"]?.AsArray().Count);
        Assert.Equal("boomplay", payload["SourceService"]?.ToString());
        Assert.Equal("boomplay", payload["sourceService"]?.ToString());
    }

    [Fact]
    public void ApplyResolved_DoesNotEraseExistingMetadataWhenResolvedValuesAreEmpty()
    {
        var payload = new JsonObject
        {
            ["Cover"] = "https://images.example.test/existing.jpg",
            ["cover"] = "https://images.example.test/existing.jpg",
            ["Label"] = "Existing label"
        };

        QueuePreResolutionPayload.ApplyResolved(
            payload,
            new QueuePreResolutionPayload.ResolutionResult(
                "deezer",
                "https://www.deezer.com/track/1",
                "lossless",
                0,
                Array.Empty<FallbackPlanStep>(),
                null,
                Metadata: new QueuePreResolutionPayload.ResolvedMetadata(Cover: "", Label: null)),
            DateTimeOffset.Parse("2026-07-21T12:00:00Z"));

        Assert.Equal("https://images.example.test/existing.jpg", payload["Cover"]?.ToString());
        Assert.Equal("Existing label", payload["Label"]?.ToString());
    }

    [Fact]
    public void ApplyFailed_EnforcesRetryCooldown()
    {
        var now = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
        var payload = new JsonObject();

        QueuePreResolutionPayload.ApplyFailed(payload, "temporary", now.AddSeconds(-30));

        Assert.Equal(QueuePreResolutionPayload.Failed, QueuePreResolutionPayload.ReadStatus(payload));
        Assert.True(QueuePreResolutionPayload.IsFailedOnCooldown(payload, TimeSpan.FromMinutes(2), now));
        Assert.False(QueuePreResolutionPayload.IsFailedOnCooldown(payload, TimeSpan.FromSeconds(10), now));
    }

}
