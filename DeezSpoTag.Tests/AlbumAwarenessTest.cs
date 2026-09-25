using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Folder/album awareness: files of one album must converge onto identical album
/// identity tags across sessions, while a standard album and a deluxe edition stay
/// separate. Covers the edition-aware album title helpers, the edition-aware
/// consensus key, the registry, and the cross-run store.
/// </summary>
public sealed class AlbumAwarenessTest
{
    private static T InvokeRunnerStatic<T>(string name, params object?[] args)
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (T)method!.Invoke(null, args)!;
    }

    [Theory]
    [InlineData("Album (Deluxe Edition)", "album")]
    [InlineData("Album (Deluxe)", "album")]
    [InlineData("Album - Deluxe", "album")]
    [InlineData("Album [2011 Remaster]", "album")]
    [InlineData("Album", "album")]
    [InlineData("ALBUM  ", "album")]
    public void CoreTitle_StripsEditionSections(string title, string expected)
    {
        Assert.Equal(expected, AlbumTitleNormalizer.CoreTitle(title));
    }

    [Fact]
    public void CoreTitle_KeepsNonEditionSubtitles()
    {
        Assert.Equal("album part 2", AlbumTitleNormalizer.CoreTitle("Album - Part 2"));
        Assert.Equal("album live from nowhere", AlbumTitleNormalizer.CoreTitle("Album (Live from Nowhere)"));
    }

    [Fact]
    public void EditionIntent_DetectsEditions()
    {
        Assert.Empty(AlbumTitleNormalizer.EditionIntent("Album"));
        Assert.Contains("deluxe", AlbumTitleNormalizer.EditionIntent("Album (Deluxe Edition)"));
        Assert.Contains("remaster", AlbumTitleNormalizer.EditionIntent("Album [2011 Remaster]"));
    }

    [Fact]
    public void IsSameEdition_MatchesDifferentWordingOfTheSameEdition()
    {
        Assert.True(AlbumTitleNormalizer.IsSameEdition("Album (Deluxe)", "Album (Deluxe Edition)"));
        Assert.False(AlbumTitleNormalizer.IsSameEdition("Album (Deluxe)", "Album"));
        Assert.False(AlbumTitleNormalizer.IsSameEdition("Album", "Album (Deluxe)"));
        Assert.False(AlbumTitleNormalizer.IsSameEdition("Album One", "Album Two"));
    }

    [Fact]
    public void IsEditionConflict_DetectsSameAlbumDifferentEdition()
    {
        Assert.True(AlbumTitleNormalizer.IsEditionConflict("Album", "Album (Deluxe Edition)"));
        Assert.True(AlbumTitleNormalizer.IsEditionConflict("Album (Remastered)", "Album"));
        // Different albums entirely are not edition conflicts.
        Assert.False(AlbumTitleNormalizer.IsEditionConflict("Album One", "Album Two (Deluxe)"));
    }

    [Fact]
    public void BuildEditionAwareKey_ConvergesEditionWordingButNotEditions()
    {
        var withEdition = AlbumIdentity.BuildEditionAwareKey("artist", "Album (Deluxe)");
        var otherWording = AlbumIdentity.BuildEditionAwareKey("artist", "Album (Deluxe Edition)");
        var standard = AlbumIdentity.BuildEditionAwareKey("artist", "Album");
        var otherArtist = AlbumIdentity.BuildEditionAwareKey("other artist", "Album (Deluxe)");

        Assert.Equal(withEdition, otherWording);
        Assert.NotEqual(standard, withEdition);
        Assert.NotEqual(otherArtist, withEdition);
        Assert.Null(AlbumIdentity.BuildEditionAwareKey("artist", ""));
    }

    [Theory]
    [InlineData("Album", "Album (Deluxe Edition)")]
    [InlineData("Album (Expanded Edition)", "Album (Deluxe Edition)")]
    [InlineData("Album (2011 Remaster)", "Album (2021 Remaster)")]
    [InlineData("Album (Clean)", "Album (Explicit)")]
    [InlineData("Album (Mono)", "Album (Stereo)")]
    [InlineData("Album (Anniversary Edition)", "Album (Collector Edition)")]
    [InlineData("Album (Special Edition)", "Album (Limited Edition)")]
    [InlineData("Album (Bonus Edition)", "Album (Complete Edition)")]
    [InlineData("Album (Ultimate Edition)", "Album (Super Deluxe Edition)")]
    public void BuildEditionAwareKey_SeparatesExactAlbumVariants(string left, string right)
    {
        Assert.NotEqual(
            AlbumIdentity.BuildEditionAwareKey("artist", left),
            AlbumIdentity.BuildEditionAwareKey("artist", right));
    }

    [Theory]
    [InlineData("Album (Deluxe)", "Album - Deluxe Edition")]
    [InlineData("Album (Expanded)", "Album [Expanded Edition]")]
    [InlineData("Album (Remastered)", "Album - Remaster")]
    public void BuildEditionAwareKey_CanonicalizesEquivalentEditionWording(string left, string right)
    {
        Assert.Equal(
            AlbumIdentity.BuildEditionAwareKey("artist", left),
            AlbumIdentity.BuildEditionAwareKey("artist", right));
    }

    [Fact]
    public void BuildScopedEditionAwareKey_IsolatesLibrariesButRetainsEditionEquivalence()
    {
        var altB = AlbumIdentity.BuildScopedEditionAwareKey(
            "/music/AltB",
            "Artist",
            "Album (Deluxe)");
        var sameLibrary = AlbumIdentity.BuildScopedEditionAwareKey(
            "/music/AltB/",
            "artist",
            "Album - Deluxe Edition");
        var otherLibrary = AlbumIdentity.BuildScopedEditionAwareKey(
            "/music/Gold",
            "Artist",
            "Album (Deluxe)");

        Assert.Equal(altB, sameLibrary);
        Assert.NotEqual(altB, otherLibrary);
    }

    [Fact]
    public void BuildFolderScopedKey_NormalizesScopeAndAlbumPath()
    {
        var first = AlbumIdentity.BuildFolderScopedKey("/music/AltB/", "Artist\\Album/");
        var equivalent = AlbumIdentity.BuildFolderScopedKey("/MUSIC/ALTB", "artist/album");

        Assert.Equal(first, equivalent);
        Assert.NotEqual(first, AlbumIdentity.BuildFolderScopedKey("/music/Gold", "Artist/Album"));
        Assert.NotEqual(first, AlbumIdentity.BuildFolderScopedKey("/music/AltB", "Artist/Other Album"));
    }

    [Fact]
    public void BuildFolderScopedKey_DoesNotDependOnProviderAlbumWordingOrEdition()
    {
        var folderKey = AlbumIdentity.BuildFolderScopedKey("folder:7", "Artist/Album");

        Assert.Equal(folderKey, AlbumIdentity.BuildFolderScopedKey("folder:7", "Artist/Album"));
        Assert.NotNull(folderKey);
        Assert.Null(AlbumIdentity.BuildFolderScopedKey("folder:7", ""));
        Assert.Null(AlbumIdentity.BuildFolderScopedKey("", "Artist/Album"));
    }

    [Fact]
    public void AlbumReconciliation_DoesNotUseOrdinaryOverwriteChecksForAlbumIdentity()
    {
        var runner = PartialSourceReader.ReadTypeSource(
            "DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        var start = runner.IndexOf("private async Task ReconcileBatchAlbumIdentitiesAsync(", StringComparison.Ordinal);
        var end = runner.IndexOf("private static void WriteReconciledRawIdentity(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var body = runner[start..end];

        Assert.DoesNotContain("ShouldOverwriteTag(plan.Config, SupportedTag.Album)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ShouldOverwriteTag(plan.Config, SupportedTag.AlbumArtist)", body, StringComparison.Ordinal);
        Assert.Contains("enabled.Contains(AlbumTag)", body, StringComparison.Ordinal);
        Assert.Contains("enabled.Contains(AlbumArtistTag)", body, StringComparison.Ordinal);
    }

    private static Dictionary<string, ProviderAlbumIdentity> ProviderMap(
        params (string Provider, string? AlbumId, string? ReleaseId, string? AlbumArtistId)[] entries)
    {
        var map = new Dictionary<string, ProviderAlbumIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var (provider, albumId, releaseId, albumArtistId) in entries)
        {
            map[provider] = new ProviderAlbumIdentity(albumId, releaseId, albumArtistId);
        }

        return map;
    }

    [Fact]
    public void Registry_EstablishCoalescesAndTracksDirtyState()
    {
        var registry = new AlbumIdentityRegistry();
        Assert.False(registry.IsDirty);

        var first = registry.Establish("key", new AlbumIdentity(
            null,
            "mbid-1",
            null,
            ReleaseGroupId: "rg-1",
            ReleaseCountry: "US",
            Barcode: "123456789",
            ReleaseType: "album",
            ProviderIdentities: ProviderMap(("spotify", null, "sp-album-1", null))));
        Assert.Equal("mbid-1", first.AlbumId);
        Assert.Equal("rg-1", first.ReleaseGroupId);
        Assert.Equal("US", first.ReleaseCountry);
        Assert.Equal("123456789", first.Barcode);
        Assert.Equal("album", first.ReleaseType);
        Assert.Equal("sp-album-1", first.GetProviderIdentity("spotify")!.ReleaseId);
        Assert.True(registry.IsDirty);

        // Later candidates only fill gaps; they never overwrite established values.
        var second = registry.Establish("key", new AlbumIdentity(
            "2024-01-01",
            "mbid-2",
            "artist-2",
            ReleaseGroupId: "rg-2",
            ReleaseCountry: "GB",
            Barcode: "987654321",
            ReleaseType: "single",
            ProviderIdentities: ProviderMap(
                ("spotify", null, "sp-album-2", null),
                ("deezer", null, "dz-album-1", null))));
        Assert.Equal("mbid-1", second.AlbumId);
        Assert.Equal("artist-2", second.AlbumArtistId);
        Assert.Equal("2024-01-01", second.ReleaseDate);
        Assert.Equal("rg-1", second.ReleaseGroupId);
        Assert.Equal("US", second.ReleaseCountry);
        Assert.Equal("123456789", second.Barcode);
        Assert.Equal("album", second.ReleaseType);
        Assert.Equal("sp-album-1", second.GetProviderIdentity("spotify")!.ReleaseId);
        Assert.Equal("dz-album-1", second.GetProviderIdentity("deezer")!.ReleaseId);
    }

    [Fact]
    public void Registry_SeedPrePopulatesWithoutDirtyingAndCoalesces()
    {
        var registry = new AlbumIdentityRegistry();
        registry.Seed("key", new AlbumIdentity("2024-01-01", "mbid-1", null), DateTimeOffset.UtcNow);
        Assert.False(registry.IsDirty);

        var established = registry.Establish("key", new AlbumIdentity(null, "mbid-2", "artist-2"));
        Assert.Equal("2024-01-01", established.ReleaseDate);
        Assert.Equal("mbid-1", established.AlbumId);
    }

    [Fact]
    public void Registry_AcceptedProviderEvidenceOverridesOnlyUnconfirmedOrOverwriteEnabledIdentity()
    {
        // Only confirmed identity exists at runtime: a legacy version-2 value that was
        // never confirmed is dropped at load, so nothing can be "repaired" from it.
        var legacy = AlbumIdentity.Empty
            .WithProviderIdentity("deezer", new ProviderAlbumIdentity(null, "82411002", null), overwrite: true)
            .WithProviderIdentity("itunes", new ProviderAlbumIdentity(null, "82411002", null), overwrite: true);
        var registry = new AlbumIdentityRegistry();
        registry.Seed("key", legacy, DateTimeOffset.UtcNow);

        var preserved = registry.Establish(
            "key",
            AlbumIdentity.Empty,
            providerId: "itunes",
            providerIdentity: new ProviderAlbumIdentity(null, "1446918509", null),
            hasAuthoritativePlatformResult: true,
            overwriteAuthoritativeProviderIdentity: false);

        Assert.Equal("82411002", preserved.GetProviderIdentity("deezer")!.ReleaseId);
        Assert.Equal("82411002", preserved.GetProviderIdentity("itunes")!.ReleaseId);

        var overwritten = registry.Establish(
            "key",
            AlbumIdentity.Empty,
            providerId: "itunes",
            providerIdentity: new ProviderAlbumIdentity(null, "1446918509", null),
            hasAuthoritativePlatformResult: true,
            overwriteAuthoritativeProviderIdentity: true);
        Assert.Equal("1446918509", overwritten.GetProviderIdentity("itunes")!.ReleaseId);
        Assert.Equal("82411002", overwritten.GetProviderIdentity("deezer")!.ReleaseId);
    }

    [Fact]
    public void Registry_AuthoritativeAbsenceNeverDeletesAndDoesNotTouchOtherProviders()
    {
        var identity = AlbumIdentity.Empty
            .WithProviderIdentity("deezer", new ProviderAlbumIdentity(null, "82411002", null), overwrite: true)
            .WithProviderIdentity("shazam", new ProviderAlbumIdentity(null, "82411002", null), overwrite: true);
        var registry = new AlbumIdentityRegistry();
        registry.Seed("key", identity, DateTimeOffset.UtcNow);

        // A null member is an absence, never a deletion: the established entry survives
        // both a non-overwrite and an overwrite attempt that carries no value.
        var preserved = registry.Establish(
            "key",
            AlbumIdentity.Empty,
            providerId: "shazam",
            providerIdentity: new ProviderAlbumIdentity(null, null, null),
            hasAuthoritativePlatformResult: true,
            overwriteAuthoritativeProviderIdentity: false);
        Assert.Equal("82411002", preserved.GetProviderIdentity("shazam")!.ReleaseId);

        var afterOverwriteAttempt = registry.Establish(
            "key",
            AlbumIdentity.Empty,
            providerId: "shazam",
            providerIdentity: new ProviderAlbumIdentity(null, null, null),
            hasAuthoritativePlatformResult: true,
            overwriteAuthoritativeProviderIdentity: true);
        Assert.Equal("82411002", afterOverwriteAttempt.GetProviderIdentity("shazam")!.ReleaseId);
        Assert.Equal("82411002", afterOverwriteAttempt.GetProviderIdentity("deezer")!.ReleaseId);
    }

    [Fact]
    public void Registry_RepairsObservedCrossProviderContaminationWithoutMusicBrainzDependency()
    {
        var registry = new AlbumIdentityRegistry();
        registry.Seed(
            "release",
            AlbumIdentity.Empty
                .WithProviderIdentity("deezer", new ProviderAlbumIdentity(null, "82411002", null), overwrite: true)
                .WithProviderIdentity("itunes", new ProviderAlbumIdentity(null, "82411002", null), overwrite: true)
                .WithProviderIdentity("shazam", new ProviderAlbumIdentity(null, "82411002", null), overwrite: true)
                .WithProviderIdentity("audiomack", new ProviderAlbumIdentity(null, "82411002", null), overwrite: true)
                .WithProviderIdentity("discogs", new ProviderAlbumIdentity(null, "82411002", null), overwrite: true)
                with
                {
                    CanonicalAlbumTitle = "american dream",
                    CanonicalAlbumArtist = "21 Savage",
                    AlbumRelativePath = "21 Savage/american dream"
                },
            DateTimeOffset.UtcNow);

        var identity = registry.Establish("release", AlbumIdentity.Empty, null,
            "deezer", new ProviderAlbumIdentity(null, "82411002", null), true, true);
        identity = registry.Establish("release", AlbumIdentity.Empty, null,
            "itunes", new ProviderAlbumIdentity(null, "1446918509", null), true, true);
        identity = registry.Establish("release", AlbumIdentity.Empty, null,
            "audiomack", new ProviderAlbumIdentity(null, "943733001", null), true, true);
        identity = registry.Establish("release", AlbumIdentity.Empty, null,
            "discogs", new ProviderAlbumIdentity(null, "30123456", null), true, true);
        identity = registry.Establish("release", AlbumIdentity.Empty, null,
            "shazam", new ProviderAlbumIdentity(null, "82411002", null), true, true);

        Assert.Equal("82411002", identity.GetProviderIdentity("deezer")!.ReleaseId);
        Assert.Equal("1446918509", identity.GetProviderIdentity("itunes")!.ReleaseId);
        Assert.Equal("943733001", identity.GetProviderIdentity("audiomack")!.ReleaseId);
        Assert.Equal("30123456", identity.GetProviderIdentity("discogs")!.ReleaseId);
        Assert.Null(identity.AlbumId);
        Assert.Equal("american dream", identity.CanonicalAlbumTitle);
        Assert.Equal("21 Savage/american dream", identity.AlbumRelativePath);
    }

    [Fact]
    public void Registry_MusicBrainzAuthorityNeverBorrowsAProviderLocalId()
    {
        var registry = new AlbumIdentityRegistry();
        var identity = registry.Establish(
            "release",
            new AlbumIdentity(null, null, null),
            providerId: "deezer",
            providerIdentity: new ProviderAlbumIdentity(null, "82411002", null),
            hasAuthoritativePlatformResult: true,
            overwriteAuthoritativeProviderIdentity: true);

        Assert.Null(identity.AlbumId);
        Assert.Null(identity.GetProviderIdentity("musicbrainz"));

        const string mbid = "f67cd8b2-1ac6-4e21-8451-4d6a58eb0ee5";
        identity = registry.Establish(
            "release",
            AlbumIdentity.Empty,
            providerId: "musicbrainz",
            providerIdentity: new ProviderAlbumIdentity(mbid, mbid, null),
            hasAuthoritativePlatformResult: true,
            overwriteAuthoritativeProviderIdentity: true);

        Assert.Equal(mbid, identity.AlbumId);
        Assert.Equal("82411002", identity.GetProviderIdentity("deezer")!.ReleaseId);
        Assert.Equal(mbid, identity.GetProviderIdentity("musicbrainz")!.ReleaseId);
    }

    [Fact]
    public void ProviderIdentityConfirmation_ReplacesOnlyItsOwnNamespaceAndNeverDeletesOnAbsence()
    {
        var polluted = AlbumIdentity.Empty
            .WithProviderIdentity("deezer", new ProviderAlbumIdentity(null, "82411002", null), overwrite: true)
            .WithProviderIdentity("itunes", new ProviderAlbumIdentity(null, "82411002", null), overwrite: true)
            .WithProviderIdentity("shazam", new ProviderAlbumIdentity(null, "82411002", null), overwrite: true);

        var repaired = polluted.WithProviderIdentity(
            "itunes",
            new ProviderAlbumIdentity(null, "1446918509", null),
            overwrite: true);
        Assert.Equal("82411002", repaired.GetProviderIdentity("deezer")!.ReleaseId);
        Assert.Equal("1446918509", repaired.GetProviderIdentity("itunes")!.ReleaseId);

        var withoutShazam = repaired.WithProviderIdentity("shazam", ProviderAlbumIdentity.Empty, overwrite: true);
        Assert.Equal("82411002", withoutShazam.GetProviderIdentity("shazam")!.ReleaseId);
    }

    [Fact]
    public void ResolveAlbumRootDirectory_UnifiesDiscFoldersAndHonorsProspectiveAlbumRoot()
    {
        Assert.Equal(
            Path.GetFullPath("/library/Artist/Album"),
            InvokeRunnerStatic<string>(
                "ResolveAlbumRootDirectory",
                "/library/Artist/Album/CD2/track.flac",
                null));
        Assert.Equal(
            Path.GetFullPath("/library/Artist/Canonical Album"),
            InvokeRunnerStatic<string>(
                "ResolveAlbumRootDirectory",
                "/incoming/track.flac",
                "/library/Artist/Canonical Album"));
    }

    [Theory]
    [InlineData("Artist/Album", "/library/Artist/Album")]
    [InlineData("../outside", null)]
    [InlineData("/absolute/outside", null)]
    public void ResolvePersistedAlbumRoot_RejectsPathsOutsideLibrary(string relativePath, string? expected)
    {
        var actual = InvokeRunnerStatic<string?>(
            "TryResolvePersistedAlbumRoot",
            "/library",
            relativePath);
        Assert.Equal(expected == null ? null : Path.GetFullPath(expected), actual);
    }

    [Fact]
    public void ApplyConfirmedProviderReleaseIdHint_InjectsOnlyCurrentConfirmedProvider()
    {
        var identity = AlbumIdentity.Empty
            .WithProviderIdentity("itunes", new ProviderAlbumIdentity(null, "1446918509", null), overwrite: true);
        var info = new AutoTagAudioInfo
        {
            Title = "Track",
            Artist = "Artist",
            Album = "Album",
            Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ITUNES_RELEASE_ID"] = ["stale-value"],
                ["DEEZER_RELEASE_ID"] = ["82411002"],
                ["SHAZAM_RELEASE_ID"] = ["82411002"]
            }
        };

        var hinted = InvokeRunnerStatic<AutoTagAudioInfo>(
            "ApplyConfirmedProviderReleaseIdHint",
            info,
            identity,
            "itunes");

        Assert.Equal("1446918509", hinted.Tags["ITUNES_RELEASE_ID"][0]);
        // A multi-value release family still carries the confirmed value only.
        Assert.All(hinted.Tags["ITUNES_RELEASE_ID"], value => Assert.Equal("1446918509", value));
        Assert.False(hinted.Tags.ContainsKey("DEEZER_RELEASE_ID"));
        Assert.False(hinted.Tags.ContainsKey("SHAZAM_RELEASE_ID"));
        // Generic compatibility fields are never synthesized from a provider identity.
        Assert.False(hinted.Tags.ContainsKey("ALBUMID"));
        Assert.Equal("82411002", info.Tags["DEEZER_RELEASE_ID"][0]);
    }

    [Fact]
    public void Store_RoundTripsAndMergesSnapshots()
    {
        var path = Path.Combine(Path.GetTempPath(), $"album-identities-{Guid.NewGuid():N}.json");
        try
        {
            var store = new AlbumIdentityStore();
            store.Merge(new[]
            {
                ("key-a", new AlbumIdentity(
                    "2024-01-01",
                    "mbid-a",
                    "artist-a",
                    ReleaseGroupId: "rg-a",
                    ReleaseStatus: "official",
                    ReleaseCountry: "US",
                    Barcode: "123456789",
                    ReleaseType: "album",
                    ProviderIdentities: ProviderMap(("spotify", null, "sp-album-1", null)),
                    CanonicalAlbumTitle: "Canonical Album",
                    CanonicalAlbumArtist: "Canonical Artist",
                    AlbumRelativePath: "Canonical Artist/Canonical Album"), DateTimeOffset.UtcNow),
                ("key-b", new AlbumIdentity(null, "mbid-b", null), DateTimeOffset.UtcNow),
            });
            store.Save(path);

            var reloaded = AlbumIdentityStore.Load(path);
            Assert.Equal(2, reloaded.Entries.Count);
            Assert.Contains(reloaded.Entries, entry => entry.Key == "key-a" && entry.Identity.AlbumId == "mbid-a");
            Assert.Contains(reloaded.Entries, entry => entry.Identity.ReleaseGroupId == "rg-a");
            Assert.Contains(reloaded.Entries, entry => entry.Identity.ReleaseStatus == "official");
            Assert.Contains(reloaded.Entries, entry => entry.Identity.ReleaseCountry == "US");
            Assert.Contains(reloaded.Entries, entry => entry.Identity.Barcode == "123456789");
            Assert.Contains(reloaded.Entries, entry => entry.Identity.ReleaseType == "album");
            Assert.Contains(reloaded.Entries, entry => entry.Identity.GetProviderIdentity("spotify")?.ReleaseId == "sp-album-1");
            Assert.Contains(reloaded.Entries, entry => entry.Identity.CanonicalAlbumTitle == "Canonical Album");
            Assert.Contains(reloaded.Entries, entry => entry.Identity.CanonicalAlbumArtist == "Canonical Artist");
            Assert.Contains(reloaded.Entries, entry => entry.Identity.AlbumRelativePath == "Canonical Artist/Canonical Album");

            // A newer registry snapshot is complete and authoritative, including
            // deliberate replacements.
            reloaded.Merge(new[]
            {
                ("key-a", new AlbumIdentity(
                    "1999-01-01",
                    "mbid-overwrite",
                    null,
                    ProviderIdentities: ProviderMap(("itunes", null, "1446918509", null))),
                    DateTimeOffset.UtcNow.AddMinutes(1)),
            });
            var merged = Assert.Single(reloaded.Entries, entry => entry.Key == "key-a");
            Assert.Equal("mbid-overwrite", merged.Identity.AlbumId);
            Assert.Equal("1446918509", merged.Identity.GetProviderIdentity("itunes")!.ReleaseId);
            Assert.Null(merged.Identity.GetProviderIdentity("spotify"));

            store = AlbumIdentityStore.Load(path);
            store.Merge(new[]
            {
                ("key-a", merged.Identity, DateTimeOffset.UtcNow.AddMinutes(2))
            });
            store.Save(path);
            var roundTripped = Assert.Single(AlbumIdentityStore.Load(path).Entries, entry => entry.Key == "key-a");
            Assert.Equal("1446918509", roundTripped.Identity.GetProviderIdentity("itunes")!.ReleaseId);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Store_LoadMigratesLegacyScopedIdentityToFolderKey()
    {
        var path = Path.Combine(Path.GetTempPath(), $"album-identities-migrate-{Guid.NewGuid():N}.json");
        try
        {
            var legacyKey = AlbumIdentity.BuildScopedEditionAwareKey("/music/Gold", "Artist", "Album")!;
            var identity = new AlbumIdentity(
                "2024-01-01",
                "f67cd8b2-1ac6-4e21-8451-4d6a58eb0ee5",
                null,
                CanonicalAlbumTitle: "Album",
                CanonicalAlbumArtist: "Artist",
                AlbumRelativePath: "Artist/Album",
                ProviderIdentities: ProviderMap(("spotify", null, "spotify-release", null)));
            var store = new AlbumIdentityStore();
            store.Merge([(legacyKey, identity, DateTimeOffset.UtcNow)]);
            store.Save(path);

            var migrated = AlbumIdentityStore.Load(path);
            var expectedKey = AlbumIdentity.BuildFolderScopedKey("/music/Gold", "Artist/Album");
            var entry = Assert.Single(migrated.Entries);
            Assert.Equal(expectedKey, entry.Key);
            Assert.Equal("spotify-release", entry.Identity.GetProviderIdentity("spotify")?.ReleaseId);
            Assert.Equal("Album", entry.Identity.CanonicalAlbumTitle);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- artist ordering key ---------------------------------------------------------

    [Theory]
    [InlineData(new[] { "21 Savage", "Drake" }, "21 savage")]
    [InlineData(new[] { "Drake", "21 Savage" }, "21 savage")]
    [InlineData(new[] { "Beyoncé" }, "beyonce")]
    public void ResolveMainArtistKey_UsesAlphabeticallyFirstMainArtist(string[] artists, string expected)
    {
        Assert.Equal(expected, ArtistOrderKey.ResolveMainArtistKey(artists, null));
    }

    [Fact]
    public void ResolveMainArtistKey_FallsBackToProvidedArtist()
    {
        Assert.Equal("fleetwood mac", ArtistOrderKey.ResolveMainArtistKey(Array.Empty<string>(), "Fleetwood Mac"));
        Assert.Equal(string.Empty, ArtistOrderKey.ResolveMainArtistKey(Array.Empty<string>(), null));
    }

    [Fact]
    public void OrdinalSortPutsNumbersBeforeLetters()
    {
        var keys = new List<string> { "adele", "21 savage", "drake" };
        keys.Sort(StringComparer.Ordinal);
        Assert.Equal(new[] { "21 savage", "adele", "drake" }, keys);
    }
}
