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
        Assert.Equal("2", payload["AutoIndex"]?.ToString());
        Assert.IsType<JsonArray>(payload["FallbackPlan"]);
        Assert.IsType<JsonArray>(payload["fallbackPlan"]);
        Assert.Equal(now, QueuePreResolutionPayload.ReadResolvedAt(payload));
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
