using System;
using System.IO;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the durable verification record that verification-driven retry depends on.
/// </summary>
[Collection("Settings Config Isolation")]
public sealed class PublicApiSessionVerificationStoreTest : IDisposable
{
    private readonly string _tempRoot;
    private readonly TestConfigRootScope _configScope;

    public PublicApiSessionVerificationStoreTest()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-public-api-verify-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _configScope = new TestConfigRootScope(_tempRoot);
    }

    public void Dispose()
    {
        _configScope.Dispose();
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    private PublicApiSessionVerificationStore NewStore()
        => new(new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance));

    [Fact]
    public void RecordVerified_SurvivesAFreshStoreInstance()
    {
        var before = NewStore();
        Assert.True(before.IsUnverified("qobuz"));
        Assert.Null(before.GetLastVerifiedAtUtc("qobuz"));

        var verifiedAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        before.RecordVerified("qobuz", verifiedAt);

        // A fresh instance stands in for a process restart. Nothing is cached in memory, so the
        // record has to come back off disk or the mechanism is dead after every reboot.
        var afterRestart = NewStore();
        var recorded = afterRestart.GetLastVerifiedAtUtc("qobuz");

        Assert.NotNull(recorded);
        Assert.Equal(verifiedAt, recorded!.Value);
        Assert.False(afterRestart.IsUnverified("qobuz"));
    }

    [Fact]
    public void RecordVerified_IsPerProvider()
    {
        var store = NewStore();

        store.RecordVerified("qobuz");

        Assert.False(store.IsUnverified("qobuz"));
        Assert.True(store.IsUnverified("tidal"));
        Assert.True(store.IsUnverified("amazon"));
    }

    [Fact]
    public void RecordVerified_IgnoresNonPublicAndBlankSlugs()
    {
        var store = NewStore();

        store.RecordVerified("deezer");
        store.RecordVerified("soulseek");
        store.RecordVerified("   ");

        // None of these are public download APIs, so none may create a record.
        Assert.Null(store.GetLastVerifiedAtUtc("deezer"));
        Assert.Null(store.GetLastVerifiedAtUtc("soulseek"));
        Assert.True(store.HasAnyUnverifiedPublicApi());
    }

    [Fact]
    public void IsUnverified_TreatsNeverVerifiedAsUnverified()
    {
        var store = NewStore();

        // An absent record must never read as "probably fine": a session that was never verified
        // cannot serve a download, and the enqueue stamp depends on this being true.
        Assert.True(store.IsUnverified("tidal"));
        Assert.True(store.HasAnyUnverifiedPublicApi());
    }

    [Fact]
    public void HasAnyUnverifiedPublicApi_IsFalseOnlyWhenAllAreVerified()
    {
        var store = NewStore();
        store.RecordVerified("qobuz");
        store.RecordVerified("tidal");

        Assert.True(store.HasAnyUnverifiedPublicApi());

        store.RecordVerified("amazon");

        Assert.False(store.HasAnyUnverifiedPublicApi());
    }

    [Fact]
    public void HasAnyUnverifiedPublicApi_ConsidersOnlyTheRequestedSlugs()
    {
        var store = NewStore();
        store.RecordVerified("qobuz");
        store.RecordVerified("tidal");
        store.RecordVerified("amazon");

        Assert.False(store.HasAnyUnverifiedPublicApi(new[] { "qobuz", "tidal" }));
    }

    [Fact]
    public void WasVerifiedSince_ComparesAgainstTheStampTime()
    {
        var store = NewStore();
        var verifiedAt = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero);
        store.RecordVerified("qobuz", verifiedAt);

        Assert.True(store.WasVerifiedSince("qobuz", verifiedAt.AddMinutes(-5)));
        Assert.True(store.WasVerifiedSince("qobuz", verifiedAt));
        Assert.False(store.WasVerifiedSince("qobuz", verifiedAt.AddMinutes(5)));
        Assert.False(store.WasVerifiedSince("tidal", null));
    }

    [Theory]
    [InlineData("QOBUZ", "qobuz")]
    [InlineData("  tidal  ", "tidal")]
    [InlineData("Amazon", "amazon")]
    public void TryNormalizeSlug_AcceptsPublicApiSlugsCaseInsensitively(string input, string expected)
    {
        Assert.True(PublicApiSessionVerificationStore.TryNormalizeSlug(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("deezer")]
    [InlineData("soulseek")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryNormalizeSlug_RejectsAnythingElse(string? input)
    {
        Assert.False(PublicApiSessionVerificationStore.TryNormalizeSlug(input, out _));
    }
}
