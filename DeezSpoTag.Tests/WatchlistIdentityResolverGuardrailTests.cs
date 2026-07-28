using System;
using System.Collections.Generic;
using System.IO;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Core.Models.Settings;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class WatchlistIdentityResolverGuardrailTests
{
    [Fact]
    public void Ambiguous_identity_uses_one_shazam_assisted_ranker_in_required_order()
    {
        var source = File.ReadAllText(Path.Join(
            FindSourceRoot(),
            "DeezSpoTag.Web",
            "Services",
            "WatchlistLocalIdentityResolver.cs"));

        Assert.Contains("if (!initial.IsAmbiguous", source, StringComparison.Ordinal);
        Assert.Contains("_shazam.RecognizeAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetShazamTrackCacheByTrackIdsAsync", source, StringComparison.Ordinal);
        Assert.Contains(".OrderByDescending(static candidate => candidate.SourceIdentityScore)", source, StringComparison.Ordinal);
        Assert.Contains(".ThenByDescending(static candidate => candidate.VariantScore)", source, StringComparison.Ordinal);
        Assert.Contains(".ThenByDescending(static candidate => candidate.ReleaseScore)", source, StringComparison.Ordinal);
        Assert.Contains(".ThenByDescending(static candidate => candidate.Candidate.QualityRank)", source, StringComparison.Ordinal);
        Assert.Contains(".ThenByDescending(static candidate => candidate.ProfileTagScore)", source, StringComparison.Ordinal);
        Assert.Contains(".ThenByDescending(static candidate => candidate.Candidate.MetadataRichness)", source, StringComparison.Ordinal);
        Assert.Contains(".ThenBy(static candidate => candidate.Candidate.TrackId)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Confirmed_duplicates_use_existing_enhancement_quarantine_and_preserve_release_variants()
    {
        var sourceRoot = FindSourceRoot();
        var resolver = File.ReadAllText(Path.Join(
            sourceRoot,
            "DeezSpoTag.Web",
            "Services",
            "WatchlistLocalIdentityResolver.cs"));
        var organizer = File.ReadAllText(Path.Join(
            sourceRoot,
            "DeezSpoTag.Web",
            "Services",
            "AutoTagLibraryOrganizer.cs"));

        Assert.Contains("ResolveRecordingIdentity(duplicate)", resolver, StringComparison.Ordinal);
        Assert.Contains("duplicate.Candidate.Album", resolver, StringComparison.Ordinal);
        Assert.Contains("ResolveDuplicatesFolderName", resolver, StringComparison.Ordinal);
        Assert.Contains("_libraryOrganizer.QuarantineConfirmedDuplicateAsync", resolver, StringComparison.Ordinal);
        Assert.Contains("AddLocalDuplicateResolutionEventAsync", resolver, StringComparison.Ordinal);
        Assert.Contains("MoveAssociatedDuplicateSidecarsToQuarantine", organizer, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete", resolver, StringComparison.Ordinal);
    }

    [Fact]
    public void Shazam_cache_is_bound_to_the_exact_file_and_retains_cross_platform_identity()
    {
        var sourceRoot = FindSourceRoot();
        var resolver = File.ReadAllText(Path.Join(
            sourceRoot,
            "DeezSpoTag.Web",
            "Services",
            "WatchlistLocalIdentityResolver.cs"));
        var schema = File.ReadAllText(Path.Join(
            sourceRoot,
            "DeezSpoTag.Services",
            "Library",
            "Schema",
            "library.sql"));

        Assert.Contains("cache.FilePath", resolver, StringComparison.Ordinal);
        Assert.Contains("cache.FileSize == file.Length", resolver, StringComparison.Ordinal);
        Assert.Contains("cache.FileModifiedUtc", resolver, StringComparison.Ordinal);
        Assert.Contains("evidence?.SpotifyId", resolver, StringComparison.Ordinal);
        Assert.Contains("evidence?.AppleId", resolver, StringComparison.Ordinal);
        Assert.Contains("evidence?.DeezerId", resolver, StringComparison.Ordinal);
        Assert.Contains("file_modified_utc TEXT", schema, StringComparison.Ordinal);
        Assert.Contains("spotify_id TEXT", schema, StringComparison.Ordinal);
        Assert.Contains("apple_id TEXT", schema, StringComparison.Ordinal);
        Assert.Contains("deezer_id TEXT", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void Watchlist_sync_finalization_and_download_dedupe_share_the_resolver()
    {
        var sourceRoot = FindSourceRoot();
        var watchlist = File.ReadAllText(Path.Join(sourceRoot, "DeezSpoTag.Web", "Services", "WatchlistEngine.cs"));
        var finalization = File.ReadAllText(Path.Join(sourceRoot, "DeezSpoTag.Web", "Services", "WatchlistFinalizationService.cs"));
        var sync = File.ReadAllText(Path.Join(sourceRoot, "DeezSpoTag.Web", "Services", "PlaylistSyncService.cs"));
        var dedupe = File.ReadAllText(Path.Join(sourceRoot, "DeezSpoTag.Services", "Download", "DownloadDedupeService.cs"));

        Assert.Contains("_localIdentityResolver.ResolveAsync", watchlist, StringComparison.Ordinal);
        Assert.Contains("_localIdentityResolver.ResolveAsync", finalization, StringComparison.Ordinal);
        Assert.Contains("_localIdentityResolver.ResolveAsync", sync, StringComparison.Ordinal);
        Assert.Contains("_ambiguityResolver.ResolveAsync", dedupe, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Song (Live)", "Song (Live)", null, null, 100)]
    [InlineData("Song (Live)", "Song", null, null, 0)]
    [InlineData("Song (Remix)", "Song (Acoustic)", null, null, 0)]
    [InlineData("Song", "Song", true, false, 0)]
    [InlineData("Song", "Song", true, true, 100)]
    public void Variant_resolution_preserves_recording_variants(
        string requested,
        string candidate,
        bool? requestedExplicit,
        bool? candidateExplicit,
        int expected)
    {
        Assert.Equal(
            expected,
            WatchlistLocalIdentityResolver.ScoreVariant(
                requested,
                candidate,
                requestedExplicit,
                candidateExplicit));
    }

    [Theory]
    [InlineData("Album (Deluxe)", "Album (Deluxe)", 150)]
    [InlineData("Album (Deluxe)", "Album", 0)]
    [InlineData("Album - EP", "Album - EP", 150)]
    [InlineData("Album (Anniversary)", "Album (Remaster)", 0)]
    public void Release_resolution_preserves_editions(string requested, string candidate, int expected)
    {
        Assert.Equal(expected, WatchlistLocalIdentityResolver.ScoreRelease(requested, candidate));
    }

    [Fact]
    public void Cross_platform_identity_extracts_spotify_apple_and_deezer_track_ids()
    {
        var ids = WatchlistLocalIdentityResolver.ExtractPlatformIds(
            "https://open.spotify.com/track/4abcXYZ",
            "https://music.apple.com/us/song/title/123456789?i=123456789",
            "https://www.deezer.com/track/987654321");

        Assert.Equal("4abcXYZ", ids.SpotifyId);
        Assert.Equal("123456789", ids.AppleId);
        Assert.Equal("987654321", ids.DeezerId);
    }

    [Fact]
    public void Shazam_cache_is_invalidated_when_the_file_changes()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "first");
            var file = new FileInfo(path);
            var cache = new ShazamTrackCacheDto(
                1,
                "matched",
                "shazam-1",
                "Title",
                "Artist",
                "ISRC",
                Array.Empty<RecommendationTrackDto>(),
                DateTimeOffset.UtcNow,
                null,
                file.FullName,
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
            Assert.True(WatchlistLocalIdentityResolver.CacheMatchesFile(cache, file));

            File.AppendAllText(path, "-changed");
            file.Refresh();
            Assert.False(WatchlistLocalIdentityResolver.CacheMatchesFile(cache, file));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Metadata_tiebreaker_counts_only_tags_enabled_by_the_folder_profile()
    {
        var config = new UnifiedTagConfig();
        foreach (var property in typeof(UnifiedTagConfig).GetProperties())
        {
            if (property.PropertyType == typeof(TagSource))
            {
                property.SetValue(config, TagSource.None);
            }
        }
        config.Title = TagSource.DownloadSource;
        config.AlbumArtist = TagSource.AutoTagPlatform;
        config.Genre = TagSource.Both;

        var score = WatchlistLocalIdentityResolver.ScorePopulatedProfileTags(
            config,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Title",
                "AlbumArtist",
                "Bpm"
            });

        Assert.Equal(2, score);
    }

    private static string FindSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Services")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("DeezSpoTag source root was not found.");
    }
}
