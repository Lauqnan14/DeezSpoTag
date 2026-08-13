using System;
using System.Collections.Generic;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class RecentDownloadEnhancementPolicyTests
{
    [Theory]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 5)]
    [InlineData(4, 5)]
    [InlineData(5, 5)]
    [InlineData(10, 10)]
    public void NormalizeDays_DisablesOrJumpsToMinimum(int input, int expected)
    {
        Assert.Equal(expected, RecentDownloadEnhancementPolicy.NormalizeDays(input));
    }

    [Fact]
    public void NormalizeTime_DefaultsToFiveAm()
    {
        Assert.Equal(new TimeOnly(5, 0), RecentDownloadEnhancementPolicy.NormalizeTime(null));
        Assert.Equal(new TimeOnly(5, 0), RecentDownloadEnhancementPolicy.NormalizeTime(""));
        Assert.Equal(new TimeOnly(5, 0), RecentDownloadEnhancementPolicy.NormalizeTime("nope"));
        Assert.Equal(new TimeOnly(5, 30), RecentDownloadEnhancementPolicy.NormalizeTime("05:30"));
    }

    [Fact]
    public void IsScheduleDue_WaitsForLocalTimeAndDoesNotRepeatSameDay()
    {
        var now = new DateTimeOffset(2026, 8, 13, 4, 59, 0, TimeSpan.FromHours(-4));
        Assert.False(RecentDownloadEnhancementPolicy.IsScheduleDue(new TimeOnly(5, 0), null, now));

        var atFive = now.AddMinutes(1);
        Assert.True(RecentDownloadEnhancementPolicy.IsScheduleDue(new TimeOnly(5, 0), null, atFive));
        Assert.False(RecentDownloadEnhancementPolicy.IsScheduleDue(
            new TimeOnly(5, 0),
            DateOnly.FromDateTime(atFive.DateTime),
            atFive));
    }

    [Fact]
    public void IsDownloadDue_IncludesExactDayAndOlderCatchUp()
    {
        var now = new DateTimeOffset(2026, 8, 13, 5, 0, 0, TimeSpan.Zero);
        var exactlyTenDays = new DateTimeOffset(2026, 8, 3, 18, 0, 0, TimeSpan.Zero);
        var nineDays = new DateTimeOffset(2026, 8, 4, 18, 0, 0, TimeSpan.Zero);
        var elevenDays = new DateTimeOffset(2026, 8, 2, 8, 0, 0, TimeSpan.Zero);

        Assert.True(RecentDownloadEnhancementPolicy.IsDownloadDue(exactlyTenDays, 10, now));
        Assert.False(RecentDownloadEnhancementPolicy.IsDownloadDue(nineDays, 10, now));
        Assert.True(RecentDownloadEnhancementPolicy.IsDownloadDue(elevenDays, 10, now));
        Assert.False(RecentDownloadEnhancementPolicy.IsDownloadDue(exactlyTenDays, 0, now));
    }

    [Fact]
    public void ReadSettings_UsesProfileExtras()
    {
        var profile = new TaggingProfile
        {
            AutoTag = new AutoTagSettings
            {
                Data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["recentDownloadWindowDays"] = JsonSerializer.SerializeToElement(10),
                    ["recentDownloadEnhancementTime"] = JsonSerializer.SerializeToElement("06:15")
                }
            }
        };

        var settings = RecentDownloadEnhancementPolicy.ReadSettings(profile);
        Assert.Equal(10, settings.Days);
        Assert.Equal(new TimeOnly(6, 15), settings.LocalTime);
    }
}
