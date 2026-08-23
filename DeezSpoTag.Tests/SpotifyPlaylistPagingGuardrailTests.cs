using System;
using System.IO;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyPlaylistPagingGuardrailTests
{
    [Fact]
    public void PlaylistPageFailure_IsExplicitAndNeverLooksAvailable()
    {
        var page = SpotifyPlaylistPage.Failed(
            100,
            "spotify_auth_client_token_failed",
            "incident-1",
            failureIsIncidentOrigin: false);

        Assert.False(page.IsComplete);
        Assert.False(page.HasMore);
        Assert.Empty(page.Tracks);
        Assert.Equal(100, page.NextOffset);
        Assert.Equal("spotify_auth_client_token_failed", page.FailureCode);
        Assert.Equal("incident-1", page.FailureIncidentId);
        Assert.False(page.FailureIsIncidentOrigin);
    }

    [Fact]
    public void PathfinderAuth_UsesPreciseStages_SharedRetries_AndRecoveryNotification()
    {
        var root = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Services", "SpotifyPathfinderMetadataClient.cs"));

        foreach (var code in new[]
                 {
                     "spotify_auth_blob_unavailable",
                     "spotify_auth_blob_invalid",
                     "spotify_auth_cookies_missing",
                     "spotify_auth_access_token_failed",
                     "spotify_auth_access_token_rejected",
                     "spotify_auth_access_token_rate_limited",
                     "spotify_auth_access_token_timeout",
                     "spotify_auth_access_token_request_failed",
                     "spotify_auth_client_version_failed",
                     "spotify_auth_client_id_missing",
                     "spotify_auth_client_token_failed",
                     "spotify_auth_build_timeout",
                     "spotify_auth_request_failed"
                 })
        {
            Assert.Contains(code, source, StringComparison.Ordinal);
        }

        Assert.Contains("AuthTransientBuildAttempts = 3", source, StringComparison.Ordinal);
        Assert.Contains("state.BuildTask is { IsCompleted: false }", source, StringComparison.Ordinal);
        Assert.Contains("IsIncidentOrigin = false", source, StringComparison.Ordinal);
        Assert.Contains("NotifyAuthenticationRecoveredAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("return FailedPlaylistPage(boundedOffset, \"spotify_auth_unavailable\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("return new(null, \"spotify_auth_unavailable\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PathfinderAuth_DiagnosticsNeverLogAuthenticationSecrets()
    {
        var root = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Services", "SpotifyPathfinderMetadataClient.cs"));
        var structuredLog = "Spotify Pathfinder auth context build completed in {ElapsedMilliseconds}ms with status {Status}, stage={Stage}, code={Code}, incident={IncidentId}.";
        var failureLog = "Spotify Pathfinder auth unavailable: stage={Stage} code={Code} transient={Transient} incident={IncidentId} diagnostic={Diagnostic}.";

        Assert.Contains(structuredLog, source, StringComparison.Ordinal);
        Assert.Contains(failureLog, source, StringComparison.Ordinal);
        Assert.Contains("LogSanitizer.OneLine", source, StringComparison.Ordinal);
        Assert.DoesNotContain("stage={Stage} code={Code} accessToken", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stage={Stage} code={Code} clientToken", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stage={Stage} code={Code} sp_dc", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tracklist_UsesOnePagedEndpointAndProviderNextOffset()
    {
        var root = ResolveRepoRoot();
        var controller = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Controllers", "Api", "SpotifyPlaylistTracklistApiController.cs"));
        var view = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml"));

        Assert.DoesNotContain("playlist/metadata", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist/metadata", view, StringComparison.Ordinal);
        Assert.Contains("playlist/tracks", view, StringComparison.Ordinal);
        Assert.Contains("nextOffset = page.NextOffset", controller, StringComparison.Ordinal);
        Assert.Contains("payload.nextOffset", view, StringComparison.Ordinal);
        Assert.DoesNotContain("trackSource === 'librespot' ? 1000 : 50", view, StringComparison.Ordinal);
    }

    [Fact]
    public void VisiblePageMatching_RemainsImmediateForPathfinderAndLibrespot()
    {
        var root = ResolveRepoRoot();
        var controller = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Controllers", "Api", "SpotifyPlaylistTracklistApiController.cs"));
        var view = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml"));

        Assert.Contains("StartVisibleTrackMatching", controller, StringComparison.Ordinal);
        Assert.Contains("ApplyStoredMatchesToTracks", controller, StringComparison.Ordinal);
        Assert.Contains("appendSpotifyTrackRows(tracks)", view, StringComparison.Ordinal);
        Assert.Contains("startSpotifyMatchPolling", view, StringComparison.Ordinal);
        Assert.Contains("void hydrateLibrespotTrackDetails(tracks)", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Watchlist_CompletenessUsesConsumedSourceItemsInsteadOfParsedTrackCount()
    {
        var root = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Services", "WatchlistEngine.cs"));

        Assert.Contains("sourceItemsConsumed += page.SourceItemCount", source, StringComparison.Ordinal);
        Assert.Contains("offset = page.NextOffset", source, StringComparison.Ordinal);
        Assert.Contains("sourceItemsConsumed < metadata.TotalTracks.Value", source, StringComparison.Ordinal);
        Assert.DoesNotContain("offset += page.Tracks.Count", source, StringComparison.Ordinal);
        Assert.DoesNotContain("candidates.Count < metadata.TotalTracks.Value", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PathfinderPageRequests_DoNotUseFullPlaylistExpansionOrCacheFailures()
    {
        var root = ResolveRepoRoot();
        var metadata = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Services", "SpotifyMetadataService.cs"));

        Assert.Contains("FetchPlaylistPageAsync(", metadata, StringComparison.Ordinal);
        Assert.Contains("if (page.IsComplete)", metadata, StringComparison.Ordinal);
        Assert.Contains("if (tracks.Count == 0)", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("FetchSpotiFlacPlaylistAsync", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPathfinderPlaylistTracksAsync", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistPages_ArePersistedAndInvalidatedBySnapshot()
    {
        var root = ResolveRepoRoot();
        var metadata = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Services", "SpotifyMetadataService.cs"));
        var repository = File.ReadAllText(Path.Join(root, "DeezSpoTag.Services", "Library", "SpotifyMetadataCacheRepository.cs"));

        Assert.Contains("spotify-playlist-page", metadata, StringComparison.Ordinal);
        Assert.Contains("spotify-playlist-snapshot", metadata, StringComparison.Ordinal);
        Assert.Contains("InvalidatePlaylistPagesWhenSnapshotChangesAsync", metadata, StringComparison.Ordinal);
        Assert.Contains("ClearBySourcePrefixAsync", metadata, StringComparison.Ordinal);
        Assert.Contains("substr(source_id, 1, length($source_id_prefix))", repository, StringComparison.Ordinal);
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Tests")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
