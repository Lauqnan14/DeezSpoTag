using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class MelodayGuardrailTests
{
    [Fact]
    public void Meloday_Service_Syncs_All_Configured_Targets_Including_Navidrome()
    {
        var source = ReadMelodayService();

        Assert.Contains("ResolveTargetServers", source, StringComparison.Ordinal);
        Assert.Contains("effective.SyncTargets", source, StringComparison.Ordinal);
        Assert.Contains("auth.Navidrome", source, StringComparison.Ordinal);
        Assert.Contains("SyncMelodayToNavidromeAsync", source, StringComparison.Ordinal);
        Assert.Contains("foreach (var target in context.SyncTargets)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Plex or Jellyfin auth missing.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Schedule_Still_Uses_Original_Dayparts()
    {
        var source = ReadMelodayService();

        Assert.Contains("DawnPeriodName", source, StringComparison.Ordinal);
        Assert.Contains("EarlyMorningPeriodName", source, StringComparison.Ordinal);
        Assert.Contains("MorningPeriodName", source, StringComparison.Ordinal);
        Assert.Contains("AfternoonPeriodName", source, StringComparison.Ordinal);
        Assert.Contains("EveningPeriodName", source, StringComparison.Ordinal);
        Assert.Contains("NightPeriodName", source, StringComparison.Ordinal);
        Assert.Contains("LateNightPeriodName", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Uses_Stable_Library_Specific_App_Mix_Identity()
    {
        var source = ReadMelodayService();

        Assert.Contains("MelodayAppUserId", source, StringComparison.Ordinal);
        Assert.Contains("EnsureMelodayAppUserAsync", source, StringComparison.Ordinal);
        Assert.Contains("BuildMelodayMixId(mode, context.Library.Id)", source, StringComparison.Ordinal);
        Assert.Contains("BuildMelodayMixId(string mode, long libraryId)", source, StringComparison.Ordinal);
        Assert.Contains("=> $\"meloday-{MelodayModes.Normalize(mode)}-{libraryId}\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildMelodayMixId(mode),", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildMelodayMixId(mode, context.HistoryTarget.Service)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("meloday-{targetService}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Tracklist_Rows_Are_Local_And_Not_Deezer_Matched_External_Rows()
    {
        var view = ReadTracklistView();

        Assert.Contains("tracklistSource === 'mix' || tracklistSource === 'meloday'", view, StringComparison.Ordinal);
        Assert.Contains("function isLocalTracklistSource(source)", view, StringComparison.Ordinal);
        Assert.Contains("normalized === 'meloday'", view, StringComparison.Ordinal);
        Assert.Contains("source: 'meloday'", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Settings_Render_Target_Server_Checkboxes()
    {
        var view = ReadActivitiesView();
        var script = ReadMelodayScript();

        Assert.Contains("data-meloday-target=\"plex\"", view, StringComparison.Ordinal);
        Assert.Contains("data-meloday-target=\"jellyfin\"", view, StringComparison.Ordinal);
        Assert.Contains("data-meloday-target=\"navidrome\"", view, StringComparison.Ordinal);
        Assert.Contains("function melodayGetSyncTargets()", script, StringComparison.Ordinal);
        Assert.Contains("syncTargets: melodayGetSyncTargets()", script, StringComparison.Ordinal);
        Assert.Contains("melodaySetSyncTargets(settings.syncTargets)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Playlists_Can_Be_Listed_And_Deleted_As_App_Owned_Mixes()
    {
        var controller = ReadMixesController();
        var repository = ReadLibraryRepository();
        var script = ReadAutoPlaylistsScript();

        Assert.Contains("EnsureMelodayAppUserAsync", controller, StringComparison.Ordinal);
        Assert.Contains("mixes.AddRange(requestedLibraryId > 0", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpDelete(\"{id}\")]", controller, StringComparison.Ordinal);
        Assert.Contains("DeleteGeneratedMixCacheAsync", repository, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM mix_item WHERE mix_cache_id", repository, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM mix_cache WHERE id", repository, StringComparison.Ordinal);
        Assert.Contains("className = \"meloday-playlist-delete\"", script, StringComparison.Ordinal);
        Assert.Contains("headers.set(\"X-CSRF-TOKEN\", csrfToken)", script, StringComparison.Ordinal);
        Assert.Contains("credentials: \"same-origin\"", script, StringComparison.Ordinal);
        Assert.Contains("method: \"DELETE\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Generates_A_Distinct_Mix_For_Every_Nonempty_Library()
    {
        var source = ReadMelodayService();
        var resolverBody = ExtractMethodBody(source, "private static IReadOnlyList<LibraryDto> ResolveMelodayLibraries");
        var runBody = ExtractMethodBody(source, "public async Task<MelodayRunResult> RunAsync");

        Assert.Contains("string.IsNullOrWhiteSpace(libraryName)", resolverBody, StringComparison.Ordinal);
        Assert.Contains("folder.LibraryId.HasValue", resolverBody, StringComparison.Ordinal);
        Assert.Contains("folder.LibraryName", resolverBody, StringComparison.Ordinal);
        Assert.Contains("GroupBy(folder => folder.LibraryId", resolverBody, StringComparison.Ordinal);
        Assert.Contains("GetConfiguredEnabledMusicFoldersAsync", runBody, StringComparison.Ordinal);
        Assert.Contains("folder.PlexSectionId", source, StringComparison.Ordinal);
        Assert.Contains("folder.JellyfinLibraryId", source, StringComparison.Ordinal);
        Assert.Contains("folder.NavidromeLibraryId", source, StringComparison.Ordinal);
        Assert.Contains("FolderSupportsAnyTarget(folder, targets)", runBody, StringComparison.Ordinal);
        Assert.Contains("folder.Id", runBody, StringComparison.Ordinal);
        Assert.Contains("foreach (var library in libraries)", runBody, StringComparison.Ordinal);
        Assert.Contains("BuildMelodayMixId(mode, context.Library.Id)", source, StringComparison.Ordinal);
        Assert.Contains("context.Library.Name} {GetModeLabel(mode)}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectLibraryAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Artwork_Can_Be_Uploaded_From_Local_File_When_BaseUrl_Is_Not_Configured()
    {
        var source = ReadMelodayService();

        Assert.Contains("GeneratedMelodayCover", source, StringComparison.Ordinal);
        Assert.Contains("TryResolveStaticCoverPath", source, StringComparison.Ordinal);
        Assert.Contains("UpdatePlaylistPosterFromFileAsync", source, StringComparison.Ordinal);
        Assert.Contains("UpdateItemPrimaryImageFromFileAsync", source, StringComparison.Ordinal);
        Assert.Contains("images\", \"meloday", source, StringComparison.Ordinal);
        Assert.DoesNotContain("if (string.IsNullOrWhiteSpace(options.BaseUrl))\n        {\n            return null;\n        }", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RenderCoverAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CoversPath", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Artwork_Is_Deterministic_Per_Library_Period_And_Mode()
    {
        var source = ReadMelodayService();

        Assert.Contains("GetArtworkIndex(periodName, libraryId, mode", source, StringComparison.Ordinal);
        Assert.Contains("context.Library.Id", source, StringComparison.Ordinal);
        Assert.Contains("libraryId * 7L", source, StringComparison.Ordinal);
        Assert.Contains("MelodayModes.Sonic", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveStaticCoverFile", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Direct_And_Sonic_Modes_Use_FolderScoped_Vibe_Results()
    {
        var service = ReadMelodayService();
        var selector = ReadMelodayVibeSelector();
        var directBody = ExtractMethodBody(service, "private async Task<List<long>> BuildDirectTrackSelectionAsync");
        var sonicBody = ExtractMethodBody(service, "private async Task<List<long>> BuildSonicTrackSelectionAsync");
        var vibeBody = ExtractMethodBody(service, "private async Task<List<long>> BuildVibeDrivenTrackSelectionAsync");

        Assert.Contains("BuildVibeDrivenTrackSelectionAsync", directBody, StringComparison.Ordinal);
        Assert.Contains("BuildVibeDrivenTrackSelectionAsync", sonicBody, StringComparison.Ordinal);
        Assert.Contains("GetTrackAnalysisByTrackIdsAsync", vibeBody, StringComparison.Ordinal);
        Assert.Contains("context.AllowedTrackIds", vibeBody, StringComparison.Ordinal);
        Assert.Contains("context.Options.SonicSimilarityDistance", vibeBody, StringComparison.Ordinal);
        Assert.Contains("historicalTrackIds", vibeBody, StringComparison.Ordinal);
        Assert.Contains("outputExclusions.UnionWith(historyTrackIds)", vibeBody, StringComparison.Ordinal);
        Assert.Contains("allowedTrackIds.Contains", selector, StringComparison.Ordinal);
        Assert.Contains("excludedTrackIds.Contains", selector, StringComparison.Ordinal);
        Assert.DoesNotContain("GetTrackAnalysisCandidatesAsync", vibeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("GetRandomTrackIdsAsync", vibeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("FillWithRandomTracksAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveFallbackVibeSeedIndex", service, StringComparison.Ordinal);
        Assert.Contains("ResolveAnalyzedHistoryTrackIds", vibeBody, StringComparison.Ordinal);
        Assert.Contains("ApplyPlexRatingFiltersAsync(candidatePool", vibeBody, StringComparison.Ordinal);
        Assert.Contains("HasMeaningfulFeatureCoverage", selector, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Folders_And_History_Are_Scoped_To_Selected_Targets()
    {
        var service = ReadMelodayService();

        Assert.Contains("FolderSupportsAnyTarget(folder, targets)", service, StringComparison.Ordinal);
        Assert.Contains("target.IsPlex && !string.IsNullOrWhiteSpace(folder.PlexSectionId)", service, StringComparison.Ordinal);
        Assert.Contains("target.IsJellyfin && !string.IsNullOrWhiteSpace(folder.JellyfinLibraryId)", service, StringComparison.Ordinal);
        Assert.Contains("target.IsNavidrome && !string.IsNullOrWhiteSpace(folder.NavidromeLibraryId)", service, StringComparison.Ordinal);
        Assert.Contains("AllDayHours", service, StringComparison.Ordinal);
        Assert.Contains("exact-folder all-day fallback", service, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Uses_Local_Daypart_And_Vibe_Tags_For_Evolving_Metadata()
    {
        var service = ReadMelodayService();

        Assert.Contains("cancellationToken, folder.Id, now.Offset", service, StringComparison.Ordinal);
        Assert.Contains("context.TrackAnalysesByTrackId", service, StringComparison.Ordinal);
        Assert.Contains("analysis.MoodTags", service, StringComparison.Ordinal);
        Assert.Contains("analysis.EssentiaGenres", service, StringComparison.Ordinal);
        Assert.Contains("context.PeriodName", service, StringComparison.Ordinal);
        Assert.Contains("ToDisplayLabel(mostCommonMood)", service, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Vibe_Results_Are_Not_Truncated_By_A_Final_Genre_Quota()
    {
        var source = ReadMelodayService();
        var filterBody = ExtractMethodBody(source, "private static bool TryIncludeTrack");

        Assert.DoesNotContain("GenreCountByName", filterBody, StringComparison.Ordinal);
        Assert.DoesNotContain("GenreLimit", filterBody, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPrimaryGenre", source, StringComparison.Ordinal);
    }

    private static string ReadMelodayService()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "MelodayService.cs"));
    }

    private static string ReadMelodayVibeSelector()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "MelodayVibeSelector.cs"));
    }

    private static string ReadTracklistView()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml"));
    }

    private static string ReadActivitiesView()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml"));
    }

    private static string ReadMelodayScript()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "meloday.js"));
    }

    private static string ReadMixesController()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "MixesApiController.cs"));
    }

    private static string ReadLibraryRepository()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));
    }

    private static string ReadAutoPlaylistsScript()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "auto-playlists.js"));
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Join(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test output path.");
    }

    private static string ExtractMethodBody(string source, string methodMarker)
    {
        var methodIndex = source.IndexOf(methodMarker, StringComparison.Ordinal);
        Assert.True(methodIndex >= 0, $"Missing method marker: {methodMarker}");

        var bodyStart = source.IndexOf('{', methodIndex);
        Assert.True(bodyStart >= 0, $"Missing method body start for: {methodMarker}");

        var depth = 0;
        for (var i = bodyStart; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(bodyStart, i - bodyStart + 1);
                }
            }
        }

        throw new InvalidOperationException($"Unable to extract method body for {methodMarker}.");
    }
}
