using System;
using System.IO;
using DeezSpoTag.Web.Controllers.Api;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class FolderTargetLibraryBindingGuardrailTests
{
    [Fact]
    public void Folder_Mutation_Endpoints_Require_Antiforgery_Validation()
    {
        var attributes = typeof(LibraryFoldersApiController).GetCustomAttributes(inherit: true);
        var tokenAwareFilter = Read("DeezSpoTag.Web", "Filters", "ApiTokenAwareAntiforgeryFilter.cs");

        Assert.Contains(attributes, attribute => attribute is AutoValidateAntiforgeryTokenAttribute);
        Assert.Contains("AutoValidateAntiforgeryTokenOrder + 1", tokenAwareFilter, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTag_Folders_Do_Not_Expose_Manual_Meloday_Mapping()
    {
        var view = Read("DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml");
        var script = Read("DeezSpoTag.Web", "wwwroot", "js", "library.js");
        var controller = Read("DeezSpoTag.Web", "Controllers", "Api", "LibraryFoldersApiController.cs");

        foreach (var obsolete in new[]
                 {
                     "folderPlexSectionId", "folderJellyfinLibraryId", "folderNavidromeLibraryId",
                     "/api/library/folders/target-libraries", "UpdateFolderTargetLibrariesAsync"
                 })
        {
            Assert.DoesNotContain(obsolete, view + script + controller, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Meloday_Uses_Internal_Automatic_Remote_Discovery()
    {
        var catalog = Read("DeezSpoTag.Web", "Services", "MelodayRemoteLibraryCatalog.cs");

        Assert.Contains("GetPlexAsync", catalog, StringComparison.Ordinal);
        Assert.Contains("GetJellyfinAsync", catalog, StringComparison.Ordinal);
        Assert.Contains("GetNavidromeAsync", catalog, StringComparison.Ordinal);
        Assert.Contains("CacheLifetime", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryFoldersApiController", catalog, StringComparison.Ordinal);
    }

    private static string Read(params string[] segments)
        => File.ReadAllText(Path.Join([ResolveRepoRoot(), .. segments]));

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Web"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
