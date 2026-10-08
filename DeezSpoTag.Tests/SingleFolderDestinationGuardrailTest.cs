using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// A folder already knows what it is, so when exactly one folder can serve a kind of download
/// there is nothing for the user to choose. These guard the places that used to prompt for a
/// destination anyway, so the rule cannot quietly regress back into nagging.
///
/// A folder the user already picked always wins, and several folders of the same type still
/// prompt, because that choice genuinely belongs to the user.
/// </summary>
public sealed class SingleFolderDestinationGuardrailTest
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath));

    [Fact]
    public void DownloadRouter_UsesTheSharedSingleFolderResolverForEveryContentType()
    {
        var intent = ReadSource("DeezSpoTag.Web/Services/DownloadIntentService.cs");

        // Stereo used to return null here, which is what left the Tracklist and download bar
        // asking for a destination even when one stereo folder existed.
        Assert.Contains("FolderContentTypeResolver.ResolveDefaultFolderId", intent, StringComparison.Ordinal);
        Assert.Contains("return FolderContentRole.Stereo;", intent, StringComparison.Ordinal);

        // Several folders of a type must not resolve to whichever sorted first.
        Assert.DoesNotContain("FirstOrDefault(folder => IsFolderMode", intent, StringComparison.Ordinal);
        Assert.DoesNotContain("private static bool IsFolderMode(", intent, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadRouter_DualQualityRouting_LetsTheFoldersFillAnEmptySlot()
    {
        var intent = ReadSource("DeezSpoTag.Web/Services/DownloadIntentService.cs");

        // Dual quality reads the multi-quality settings directly, so it needs its own fallback or
        // it still fails with "Dual-quality routing requires a dedicated Atmos destination folder."
        Assert.Contains(
            "secondaryDestinationFolderId ??= await ResolveDefaultDestinationFolderIdAsync(",
            intent,
            StringComparison.Ordinal);
        Assert.Contains(
            "primaryDestinationFolderId ??= await ResolveDefaultDestinationFolderIdAsync(",
            intent,
            StringComparison.Ordinal);

        // The fallback must only ever fill a blank, never replace a destination the caller chose.
        Assert.Contains("??= await ResolveDefaultDestinationFolderIdAsync(", intent, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadRouter_AutomaticAtmosSecondary_LetsTheFoldersFillAnEmptySlot()
    {
        var intent = ReadSource("DeezSpoTag.Web/Services/DownloadIntentService.cs");

        // Otherwise the automatic Atmos copy is silently skipped with "secondary destination folder
        // is required for Atmos" even when a single Atmos folder exists.
        Assert.Contains(
            "secondaryDestinationFolderId ??= await ResolveDefaultDestinationFolderIdAsync(",
            intent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Watchlist_StopsDeferringWhenOnlyOneFolderCanServeTheDownload()
    {
        var watchlist = ReadSource("DeezSpoTag.Web/Services/WatchlistEngine.cs");

        // The deferral was driven by a settings pointer nothing in the app ever wrote, so a
        // watchlist with a perfectly good folder available still refused to plan downloads.
        Assert.Contains("FolderContentTypeResolver.ResolveDefaultFolderId", watchlist, StringComparison.Ordinal);
        Assert.Contains("ResolvePlaylistDestinationFolderId", watchlist, StringComparison.Ordinal);
        Assert.Contains("ResolveDefaultAtmosDestinationFolderId", watchlist, StringComparison.Ordinal);
    }

    [Fact]
    public void Tracklist_UsesTheOnlyFolderInsteadOfPrompting()
    {
        var tracklist = ReadSource("DeezSpoTag.Web/Views/Tracklist/Index.cshtml");

        Assert.Contains("function resolveSingleCandidateFolderId(select)", tracklist, StringComparison.Ordinal);
        Assert.Contains("value = resolveSingleCandidateFolderId(select);", tracklist, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadBar_UsesTheOnlyFolderInsteadOfPrompting()
    {
        var downloadClient = ReadSource("DeezSpoTag.Web/wwwroot/js/download-client.js");

        Assert.Contains("resolveSingleCandidateDestinationId(select)", downloadClient, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadSettings_SelectsTheOnlyAtmosFolderInsteadOfDemandingOne()
    {
        var settings = ReadSource("DeezSpoTag.Web/Views/Settings/Index.cshtml");

        // "Select an Atmos destination to enable the secondary download." is driven purely by an
        // empty dropdown, so the one obvious folder has to be preselected.
        Assert.Contains("!currentSecondary && sortedAtmos.length === 1", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleFolderRule_NeverPicksAmongSeveralFolders()
    {
        var resolver = ReadSource("DeezSpoTag.Services/Library/FolderContentTypeResolver.cs");

        // Take(2) then a count of exactly one is what makes "several folders" return nothing
        // instead of guessing the first.
        Assert.Contains("Take(2)", resolver, StringComparison.Ordinal);
        Assert.Contains("candidates.Count == 1 ? candidates[0].Id : null", resolver, StringComparison.Ordinal);
    }
}
