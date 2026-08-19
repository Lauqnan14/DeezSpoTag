using System;
using System.Text.Json;
using DeezSpoTag.Web.Services.Notifications;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class NotificationTransportAdapterTests
{
    [Fact]
    public void UniversalCompatibility_RendersTitleAndBodyIntoOneAppriseBody()
    {
        var payload = NotificationTransportAdapter.BuildPayload(
            Entry("Download Completed", "Thriller was tagged successfully."),
            Preferences(NotificationTransportProvider.Apprise, ApprisePayloadMode.UniversalCompatibility));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = document.RootElement;
        Assert.Equal(
            "*Download Completed* Thriller was tagged successfully.",
            root.GetProperty("body").GetString());
        Assert.False(root.TryGetProperty("title", out _));
        Assert.False(root.TryGetProperty("type", out _));
        Assert.False(root.TryGetProperty("entityId", out _));
    }

    [Fact]
    public void NativeTitleBody_MergesTitleIntoBodyAndSendsTypeWithoutASeparateTitle()
    {
        var payload = NotificationTransportAdapter.BuildPayload(
            Entry("Download Completed", "Thriller was tagged successfully.", NotificationKinds.RunCompleted),
            Preferences(NotificationTransportProvider.Apprise, ApprisePayloadMode.NativeTitleBody));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = document.RootElement;
        Assert.Equal(
            "*Download Completed* Thriller was tagged successfully.",
            root.GetProperty("body").GetString());
        Assert.Equal("success", root.GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("title", out _));
        Assert.False(root.TryGetProperty("entityId", out _));
    }

    [Fact]
    public void GenericWebhook_PreservesTheRichInternalModel()
    {
        var payload = NotificationTransportAdapter.BuildPayload(
            Entry("Download Completed", "Thriller was tagged successfully.", NotificationKinds.RunCompleted, "track-1"),
            Preferences(NotificationTransportProvider.GenericWebhook, ApprisePayloadMode.UniversalCompatibility));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = document.RootElement;
        Assert.Equal("Download Completed", root.GetProperty("title").GetString());
        Assert.Equal("Thriller was tagged successfully.", root.GetProperty("body").GetString());
        Assert.Equal("success", root.GetProperty("type").GetString());
        Assert.Equal("track-1", root.GetProperty("entityId").GetString());
        Assert.Equal(NotificationKinds.RunCompleted, root.GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("Download Completed", "", "*Download Completed*")]
    [InlineData("Download Completed", "Download Completed", "*Download Completed*")]
    [InlineData("", "Thriller was tagged successfully.", "Thriller was tagged successfully.")]
    public void UniversalBody_DoesNotDuplicateAnEmptyOrIdenticalTitle(string title, string body, string expected)
    {
        Assert.Equal(expected, NotificationTransportAdapter.RenderUniversalBody(title, body));
    }

    [Fact]
    public void ExistingWebhookUrlWithoutProvider_StaysGenericSoCurrentPayloadsKeepWorking()
    {
        var preferences = new NotificationPreferences { WebhookUrl = "https://ntfy.sh/topic" };
        preferences.EnsureDefaults();

        Assert.Equal(NotificationTransportProvider.GenericWebhook, preferences.ResolvedProvider);
        Assert.Equal(ApprisePayloadMode.UniversalCompatibility, preferences.ResolvedApprisePayloadMode);
    }

    [Fact]
    public void ExistingAppriseNotifyUrlWithoutProvider_UsesAppriseSoTitleIsMergedIntoBody()
    {
        var preferences = new NotificationPreferences { WebhookUrl = "http://192.168.1.10:8180/notify/deezspotag" };
        preferences.EnsureDefaults();

        Assert.Equal(NotificationTransportProvider.Apprise, preferences.ResolvedProvider);
        var payload = NotificationTransportAdapter.BuildPayload(
            Entry("Download Completed", "Thriller was tagged successfully."),
            preferences);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        Assert.Equal(
            "*Download Completed* Thriller was tagged successfully.",
            document.RootElement.GetProperty("body").GetString());
        Assert.False(document.RootElement.TryGetProperty("title", out _));
    }

    [Fact]
    public void NewPreferences_DefaultToAppriseUniversalCompatibility()
    {
        var preferences = NotificationPreferences.CreateDefault();

        Assert.Equal(NotificationTransportProvider.Apprise, preferences.ResolvedProvider);
        Assert.Equal(ApprisePayloadMode.UniversalCompatibility, preferences.ResolvedApprisePayloadMode);
    }

    private static NotificationPreferences Preferences(
        NotificationTransportProvider provider,
        ApprisePayloadMode mode)
        => new()
        {
            Provider = provider,
            AppriseMode = mode
        };

    private static NotificationEntry Entry(
        string title,
        string body,
        string kind = NotificationKinds.RunCompleted,
        string? entityId = null)
        => new()
        {
            Id = "id-1",
            Kind = kind,
            DedupeKey = "test",
            Title = title,
            Body = body,
            EntityId = entityId,
            LastSeenUtc = DateTimeOffset.Parse("2026-08-19T12:00:00Z")
        };
}
