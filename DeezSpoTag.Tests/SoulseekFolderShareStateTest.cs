using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Services.Download.Soulseek;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Tests for share generation and the folder-row source of truth.
/// </summary>
/// <remarks>
///     The behaviour under test is the one the design fixes as non-negotiable: nothing is shared unless the
///     folder tab says so, the generated configuration never mutates the remote instance, and a path slskd
///     serves without an enabled folder behind it is reported as a problem.
/// </remarks>
public sealed class SoulseekFolderShareStateTest
{
    [Fact]
    public void NoEnabledFolder_GeneratesAnEmptyButValidBlock()
    {
        var yaml = SoulseekShareConfiguration.Generate([]);

        Assert.Contains("shares:", yaml);
        Assert.Contains("directories:", yaml);
        Assert.Contains("No folder is enabled for sharing", yaml);
    }

    [Fact]
    public void Share_IsGeneratedAsAnAbsoluteSlskdDirectoryEntry()
        => Assert.Contains("    - '/srv/music'", SoulseekShareConfiguration.Generate([new SoulseekDesiredShare(1, "/srv/music")]));

    [Fact]
    public void Alias_IsAppliedWithSlskdBracketSyntaxSoTheLocalFolderNameStaysHidden()
    {
        // slskd parses [Alias]\path; this is what hides "/srv/music" from remote peers.
        var yaml = SoulseekShareConfiguration.Generate([new SoulseekDesiredShare(1, "/srv/music", "Library")]);

        Assert.Contains("    - '[Library]/srv/music'", yaml);
    }

    [Fact]
    public void FoldersWithNoPath_AreSkippedRatherThanEmitted()
    {
        var yaml = SoulseekShareConfiguration.Generate(
        [
            new SoulseekDesiredShare(1, "   "),
            new SoulseekDesiredShare(2, "/srv/music")
        ]);

        Assert.Contains("'/srv/music'", yaml);
        Assert.DoesNotContain("''", yaml);
    }

    [Fact]
    public void ExcludeFilters_AreMergedIntoTheGlobalFilterList()
    {
        // slskd has no per-share filter, so excludes are projected into its single global list.
        var yaml = SoulseekShareConfiguration.Generate(
        [
            new SoulseekDesiredShare(1, "/srv/music", ExcludeFilters: ["\\.ini$", "Thumbs\\.db$"]),
            new SoulseekDesiredShare(2, "/srv/audio", ExcludeFilters: ["\\.ini$"])
        ]);

        Assert.Contains("  filters:", yaml);
        Assert.Contains("'\\.ini$'", yaml);
        Assert.Contains("'Thumbs\\.db$'", yaml);

        // The duplicate is merged, not repeated.
        Assert.Equal(1, CountOccurrences(yaml, "\\.ini$"));
    }

    [Fact]
    public void QuoteInAPath_IsEscapedSoTheGeneratedYamlStaysParseable()
    {
        var yaml = SoulseekShareConfiguration.Generate([new SoulseekDesiredShare(1, "/srv/it's music")]);

        Assert.Contains("it''s music", yaml);
    }

    [Fact]
    public void EnabledFolderThatSlskdDoesNotServe_IsReportedAsMissing()
    {
        var diff = SoulseekShareConfiguration.Diff(
            [new SoulseekDesiredShare(1, "/srv/music")],
            [new SoulseekActualShare("other", "/srv/other")]);

        var missing = Assert.Single(diff.MissingFromSlskd);
        Assert.Equal("/srv/music", missing.LocalPath);
        Assert.Contains(diff.Diagnostics, d => d.Code == "shares_missing_in_slskd");
    }

    [Fact]
    public void PathSlskdServesThatNoEnabledFolderAccountsFor_IsReportedAsUnexpected()
    {
        // This is the safety case: content exposed to the Soulseek network that the user never enabled here.
        var diff = SoulseekShareConfiguration.Diff(
            [new SoulseekDesiredShare(1, "/srv/music")],
            [
                new SoulseekActualShare("music", "/srv/music"),
                new SoulseekActualShare("private", "/home/user/Private")
            ]);

        var unexpected = Assert.Single(diff.UnexpectedlyShared);
        Assert.Equal("/home/user/Private", unexpected.LocalPath);

        var diagnostic = Assert.Single(diff.Diagnostics.Where(d => d.Code == "unexpected_share"));
        Assert.Equal(SoulseekShareDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("/home/user/Private", diagnostic.Message);
    }

    [Fact]
    public void ExcludedShareIsNotReportedAsUnexpected()
    {
        // An excluded share is not served, so it is not an exposure.
        var diff = SoulseekShareConfiguration.Diff(
            [new SoulseekDesiredShare(1, "/srv/music")],
            [
                new SoulseekActualShare("music", "/srv/music"),
                new SoulseekActualShare("scratch", "/srv/scratch", IsExcluded: true)
            ]);

        Assert.Empty(diff.UnexpectedlyShared);
    }

    [Fact]
    public void PathComparisonIgnoresSeparatorStyleAndTrailingSlash()
    {
        // The user writes one form and slskd reports the other; they must still compare equal, otherwise every
        // folder would look permanently missing.
        var diff = SoulseekShareConfiguration.Diff(
            [new SoulseekDesiredShare(1, "/srv/music/")],
            [new SoulseekActualShare("music", "\\srv\\music")]);

        Assert.Empty(diff.MissingFromSlskd);
        Assert.Empty(diff.UnexpectedlyShared);
    }

    [Fact]
    public void PathComparisonPreservesCaseSoRealDifferencesAreVisible()
    {
        // The filesystem may be case sensitive, so folding case could hide a genuine mismatch.
        var diff = SoulseekShareConfiguration.Diff(
            [new SoulseekDesiredShare(1, "/srv/Music")],
            [new SoulseekActualShare("music", "/srv/music")]);

        Assert.Single(diff.MissingFromSlskd);
        Assert.Single(diff.UnexpectedlyShared);
    }

    [Fact]
    public void WhenNothingIsEnabled_TheDiffIsEmptyAndSaysSo()
    {
        var diff = SoulseekShareConfiguration.Diff([], []);

        Assert.Empty(diff.MissingFromSlskd);
        Assert.Contains(diff.Diagnostics, d => d.Code == "no_shares_enabled");
    }

    [Fact]
    public void WhenSlskdIsUnavailable_TheDiffIsOneSidedAndSaysSo()
    {
        var diff = SoulseekShareConfiguration.Diff(
            [new SoulseekDesiredShare(1, "/srv/music")],
            actual: [],
            slskdUnavailable: true,
            unavailableMessage: "slskd is unavailable.");

        Assert.Single(diff.MissingFromSlskd);
        Assert.Empty(diff.UnexpectedlyShared);

        var diagnostic = Assert.Single(diff.Diagnostics.Where(d => d.Code == "slskd_unavailable"));
        Assert.Contains("slskd is unavailable.", diagnostic.Message);
    }

    [Fact]
    public void SkippedFolders_AreReported()
    {
        var diff = SoulseekShareConfiguration.Diff(
            [],
            [],
            skipped: [(7, "no_path")]);

        Assert.Contains(diff.Diagnostics, d => d.Code == "folders_skipped");
        Assert.Contains(diff.Diagnostics, d => d is { Code: "folder_skipped", FolderId: 7 });
    }

    [Fact]
    public void FolderRowRemainsTheOnlySourceOfTruthForShareState()
    {
        // The design forbids a second competing toggle. The folder-tab endpoint writes the same column the
        // Soulseek endpoints read, so there is one state and one place to change it.
        var foldersApi = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "LibraryFoldersApiController.cs");
        var shareApi = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekFolderShareApiController.cs");

        Assert.Contains("soulseek-share-enabled", foldersApi);
        Assert.Contains("UpdateFolderSoulseekShareEnabledAsync", foldersApi);

        // The Soulseek controller must not invent its own flag; it goes through the settings service.
        Assert.Contains("_settingsService.SetFolderShareEnabledAsync", shareApi);
        Assert.DoesNotContain("soulseek_share_enabled =", shareApi);
    }

    [Fact]
    public void SharingIsOptInAndCannotBeInheritedByAnExistingDatabase()
    {
        var dbService = ReadRepoFile("DeezSpoTag.Services", "Library", "LibraryDbService.cs");
        var schema = ReadRepoFile("DeezSpoTag.Services", "Library", "Schema", "library.sql");

        Assert.Contains(
            "EnsureColumnAsync(connection, FolderTable, \"soulseek_share_enabled\", $\"{IntegerType} DEFAULT 0\"",
            dbService);
        Assert.Contains("soulseek_share_enabled INTEGER NOT NULL DEFAULT 0", schema);
    }

    [Fact]
    public void ShareEndpointsAreBehindTheSameLocalAccessPolicyAsOtherSettings()
    {
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekFolderShareApiController.cs");

        Assert.Contains("[Authorize]", controller);
        Assert.Contains("[AutoValidateAntiforgeryToken]", controller);
        Assert.Contains("[ApiController]", controller);
        Assert.Contains("[Route(\"api/v1/soulseek/shares\")]", controller);
    }

    [Fact]
    public void WriteEndpointsAreRateLimited()
    {
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekFolderShareApiController.cs");

        // The three state-changing endpoints -- sync, scan and per-folder scan -- carry the tighter policy; the
        // class as a whole carries the default one.
        Assert.Equal(3, CountOccurrences(controller, "[EnableRateLimiting(\"SensitiveWrites\")]"));
        Assert.Contains("[EnableRateLimiting(\"DefaultApi\")]", controller);
    }

    [Fact]
    public void ShareSyncNeverMutatesTheRemoteConfiguration()
    {
        // slskd has no share-write API, so the service must not pretend to apply anything. It may only read
        // shares and ask for a rescan.
        var service = ReadRepoFile("DeezSpoTag.Services", "Download", "Soulseek", "SoulseekShareService.cs");
        var client = ReadRepoFile("DeezSpoTag.Integrations", "Soulseek", "ISlskdClient.cs");

        Assert.DoesNotContain("PutSharesAsync", service);
        Assert.DoesNotContain("SetShareAsync", service);
        Assert.DoesNotContain("UpdateShareAsync", service);
        Assert.DoesNotContain("DeleteShareAsync", service);

        // The adapter must not have grown a share-write operation either.
        Assert.DoesNotContain("HttpPut", client);
        Assert.Contains("RescanSharesAsync", client);
    }

    [Fact]
    public void FolderTabRendersTheShareSwitchAndCallsTheFolderEndpoint()
    {
        var script = ReadRepoFile("DeezSpoTag.Web", "wwwroot", "js", "library.js");
        var view = ReadRepoFile("DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml");

        Assert.Contains("data-folder-soulseek-share", script);
        Assert.Contains("setFolderSoulseekShareEnabled", script);
        Assert.Contains("/soulseek-share-enabled", script);
        Assert.Contains("Soulseek", view);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var root = ResolveRepoRoot();
        return System.IO.File.ReadAllText(System.IO.Path.Join(new[] { root }.Concat(parts).ToArray()));
    }

    private static string ResolveRepoRoot()
    {
        var current = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (System.IO.Directory.Exists(System.IO.Path.Join(current.FullName, "DeezSpoTag.Services")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
