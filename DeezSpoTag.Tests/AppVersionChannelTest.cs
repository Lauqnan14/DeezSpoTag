using DeezSpoTag.Web.Services.Updates;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the branch-to-channel mapping and the version comparison that decide whether a release
/// counts as an update. The channel mapping is the guardrail that stops a branch from ever being
/// told about another branch's releases.
/// </summary>
public sealed class AppVersionChannelTest
{
    [Theory]
    [InlineData("main", AppVersionChannel.Prerelease)]
    [InlineData("MAIN", AppVersionChannel.Prerelease)]
    [InlineData("  main  ", AppVersionChannel.Prerelease)]
    [InlineData("stable", AppVersionChannel.Stable)]
    [InlineData("Stable", AppVersionChannel.Stable)]
    public void TryResolveChannel_MapsKnownBranches(string branch, AppVersionChannel expected)
    {
        Assert.True(AppVersionOptions.TryResolveChannel(branch, out var channel));
        Assert.Equal(expected, channel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("develop")]
    [InlineData("feature/personal-genre")]
    [InlineData("master")]
    public void TryResolveChannel_RefusesUnknownBranches(string? branch)
    {
        // An unrecognised branch must not resolve to any channel, so the caller performs no
        // check rather than falling back to another branch's releases.
        Assert.False(AppVersionOptions.TryResolveChannel(branch, out var channel));
        Assert.Equal(AppVersionChannel.Unknown, channel);
    }

    [Theory]
    [InlineData("0.1.27.5", "v0.1.27.6-pre", true)]
    [InlineData("v0.1.27.5", "v0.1.27.6-pre", true)]
    [InlineData("v0.1.27.5", "0.1.27.6", true)]
    [InlineData("v0.1.27.5", "v0.1.27.5-pre", false)]
    [InlineData("v0.1.27.5", "v0.1.27.5", false)]
    [InlineData("v0.1.27.6", "v0.1.27.5-pre", false)]
    [InlineData("v0.1.27.5", "v0.1.28.0", true)]
    [InlineData("v0.1.27.5", "v1.0.0.0", true)]
    [InlineData("v0.2.0.0", "v0.1.99.99", false)]
    public void IsNewer_ComparesFourPartVersions(string current, string candidate, bool expected)
    {
        Assert.Equal(expected, AppVersionComparison.IsNewer(current, candidate));
    }

    [Theory]
    [InlineData("unknown", "v0.1.27.6")]
    [InlineData("v0.1.27.5", "unknown")]
    [InlineData("v0.1.27.5", "")]
    [InlineData(null, "v0.1.27.6")]
    [InlineData("v0.1.27.5", null)]
    [InlineData("v1.2.3", "v0.1.27.6")]
    public void IsNewer_NeverReportsAnUpdateForUnparsableInput(string? current, string? candidate)
    {
        // An unrecognisable value on either side must not fabricate an "update available".
        Assert.False(AppVersionComparison.IsNewer(current, candidate));
    }

    [Theory]
    [InlineData("v0.1.27.5", "0.1.27.5")]
    [InlineData("0.1.27.5", "0.1.27.5")]
    [InlineData("v0.1.27.6-pre", "0.1.27.6")]
    [InlineData("0.1.27.6+build.7", "0.1.27.6")]
    [InlineData("v1.2.3", null)]
    [InlineData("v0.1.27.5-rc.1", "0.1.27.5")]
    [InlineData("v0.1.27", null)]
    [InlineData("", null)]
    [InlineData("not-a-version", null)]
    public void TryParse_ReadsTheFourPartCore(string? value, string? expected)
    {
        var parsed = AppVersionComparison.TryParse(value);
        if (expected is null)
        {
            Assert.Null(parsed);
            return;
        }

        Assert.NotNull(parsed);
        var text = $"{parsed.Value.Major}.{parsed.Value.Minor}.{parsed.Value.Patch}.{parsed.Value.Revision}";
        Assert.Equal(expected, text);
    }
}
