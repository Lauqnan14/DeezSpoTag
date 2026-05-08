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
                null),
            now);

        Assert.True(QueuePreResolutionPayload.IsResolved(payload));
        Assert.Equal("qobuz", QueuePreResolutionPayload.ReadResolvedEngine(payload));
        Assert.Equal("qobuz", payload["Engine"]?.ToString());
        Assert.Equal("https://play.qobuz.com/track/123", QueuePreResolutionPayload.ReadResolvedSourceUrl(payload));
        Assert.Equal("https://play.qobuz.com/track/123", payload["SourceUrl"]?.ToString());
        Assert.Equal("27", payload["Quality"]?.ToString());
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
