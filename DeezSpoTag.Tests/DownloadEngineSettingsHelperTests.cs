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
    public void ApplyResolvedProfileToSettings_ResolvesEngineSourceForApple()
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

        var resolved = DownloadEngineSettingsHelper.ApplyResolvedProfileToSettings(settings, profile, currentEngine: "apple");

        Assert.Equal(DownloadTagSourceHelper.AppleSource, resolved);
    }

    [Fact]
    public void ApplyResolvedProfileToSettings_DefaultsToDeezer_WhenCurrentEngineIsUnknown()
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

        var resolved = DownloadEngineSettingsHelper.ApplyResolvedProfileToSettings(settings, profile, currentEngine: "unknown-engine");

        Assert.Equal(DownloadTagSourceHelper.DeezerSource, resolved);
    }

    [Fact]
    public void ApplyResolvedProfileToSettings_DefaultsToDeezer_WhenProfileDownloadTagSourceIsMissing()
    {
        var settings = new DeezSpoTagSettings
        {
            Service = "auto"
        };
        var profile = new DownloadTagProfileSettings(
            TagSettings: new TagSettings(),
            DownloadTagSource: null,
            FolderStructure: null,
            Technical: null);

        var resolved = DownloadEngineSettingsHelper.ApplyResolvedProfileToSettings(settings, profile, currentEngine: "apple");

        Assert.Equal(DownloadTagSourceHelper.DeezerSource, resolved);
    }
}
