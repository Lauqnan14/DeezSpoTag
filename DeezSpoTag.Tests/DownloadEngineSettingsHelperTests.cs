using System;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadEngineSettingsHelperTests
{
    [Fact]
    public void ApplyResolvedProfileToSettings_UsesExplicitProfileDownloadTagSource()
    {
        var settings = new DeezSpoTagSettings
        {
            Service = "auto",
            Tags = new TagSettings
            {
                Title = false
            }
        };
        var profile = new DownloadTagProfileSettings(
            TagSettings: new TagSettings { Title = true },
            DownloadTagSource: DownloadTagSourceHelper.SpotifySource,
            FolderStructure: null,
            Technical: null);

        var resolved = DownloadEngineSettingsHelper.ApplyResolvedProfileToSettings(settings, profile, currentEngine: "deezer");

        Assert.Equal(DownloadTagSourceHelper.SpotifySource, resolved);
        Assert.True(settings.Tags.Title);
    }

    [Fact]
    public void ApplyResolvedProfileToSettings_ResolvesEngineSourceFromCurrentEngine()
    {
        var settings = new DeezSpoTagSettings
        {
            Service = "auto"
        };
        var profile = new DownloadTagProfileSettings(
            TagSettings: new TagSettings(),
            DownloadTagSource: DownloadTagSourceHelper.FollowDownloadEngineSource,
            FolderStructure: null,
            Technical: null);

        var resolved = DownloadEngineSettingsHelper.ApplyResolvedProfileToSettings(settings, profile, currentEngine: "qobuz");

        Assert.Equal(DownloadTagSourceHelper.QobuzSource, resolved);
    }

    [Fact]
    public void ApplyResolvedProfileToSettings_ThrowsClearError_WhenSourceCannotResolve()
    {
        var settings = new DeezSpoTagSettings
        {
            Service = "auto"
        };
        var profile = new DownloadTagProfileSettings(
            TagSettings: new TagSettings(),
            DownloadTagSource: DownloadTagSourceHelper.FollowDownloadEngineSource,
            FolderStructure: null,
            Technical: null);

        var ex = Assert.Throws<InvalidOperationException>(
            () => DownloadEngineSettingsHelper.ApplyResolvedProfileToSettings(settings, profile, currentEngine: "apple"));

        Assert.Contains("Download profile source resolution failed:", ex.Message);
    }
}
