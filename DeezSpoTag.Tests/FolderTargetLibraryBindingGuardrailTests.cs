using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class FolderTargetLibraryBindingGuardrailTests
{
    [Fact]
    public void AutoTag_Folder_Modal_Maps_All_Meloday_Target_Libraries()
    {
        var view = Read("DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml");
        var script = Read("DeezSpoTag.Web", "wwwroot", "js", "library.js");

        Assert.Contains("folderPlexSectionId", view, StringComparison.Ordinal);
        Assert.Contains("folderJellyfinLibraryId", view, StringComparison.Ordinal);
        Assert.Contains("folderNavidromeLibraryId", view, StringComparison.Ordinal);
        Assert.Contains("/api/library/folders/target-libraries", script, StringComparison.Ordinal);
        Assert.Contains("normalizeTargetLibraryName", script, StringComparison.Ordinal);
        Assert.Contains("multiple exact name matches", script, StringComparison.Ordinal);
        Assert.Contains("plexSectionId: folderInput.plexSectionId", script, StringComparison.Ordinal);
        Assert.Contains("jellyfinLibraryId: folderInput.jellyfinLibraryId", script, StringComparison.Ordinal);
        Assert.Contains("navidromeLibraryId: folderInput.navidromeLibraryId", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Target_Library_Discovery_Is_Read_Only_And_Does_Not_Expose_Credentials()
    {
        var controller = Read("DeezSpoTag.Web", "Controllers", "Api", "LibraryFoldersApiController.cs");

        Assert.Contains("[HttpGet(\"target-libraries\")]", controller, StringComparison.Ordinal);
        Assert.Contains("DiscoverPlexLibrariesAsync", controller, StringComparison.Ordinal);
        Assert.Contains("DiscoverJellyfinLibrariesAsync", controller, StringComparison.Ordinal);
        Assert.Contains("DiscoverNavidromeLibrariesAsync", controller, StringComparison.Ordinal);
        Assert.Contains("TargetLibraryOption(string Id, string Name)", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("return Ok(auth)", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void Folder_Bindings_Have_An_Explicit_Endpoint_That_Can_Clear_A_Mapping()
    {
        var controller = Read("DeezSpoTag.Web", "Controllers", "Api", "LibraryFoldersApiController.cs");
        var script = Read("DeezSpoTag.Web", "wwwroot", "js", "library.js");

        Assert.Contains("[HttpPut(\"{id:long}/target-libraries\")]", controller, StringComparison.Ordinal);
        Assert.Contains("UpdateFolderTargetLibrariesAsync", controller, StringComparison.Ordinal);
        Assert.Contains("request.PlexSectionId", controller, StringComparison.Ordinal);
        Assert.Contains("persistFolderTargetLibraries(folder.id, folderInput)", script, StringComparison.Ordinal);
        Assert.Contains("method: 'PUT'", script, StringComparison.Ordinal);
    }

    private static string Read(params string[] segments)
        => File.ReadAllText(Path.Join([ResolveRepoRoot(), .. segments]));

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Web")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
