using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Library;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// A folder knows what it is, so when only one folder can serve a kind of download there is
/// nothing for the user to choose and the app should use it. These cover that rule, and the
/// equally important part: when the user has already chosen, or has more than one folder, the app
/// must not decide for them.
/// </summary>
public sealed class FolderContentTypeResolverTest
{
    private static FolderDto Folder(long id, string? desiredQuality, bool enabled = true)
        => new FolderDto(
            Id: id,
            RootPath: $"/music/{id}",
            DisplayName: $"Folder {id}",
            Enabled: enabled,
            LibraryId: null,
            LibraryName: null,
            DesiredQuality: desiredQuality ?? string.Empty,
            AutoTagProfileId: "profile-1",
            AutoTagEnabled: true,
            ConvertEnabled: false,
            ConvertFormat: null,
            ConvertBitrate: null);

    [Theory]
    [InlineData("atmos", FolderContentRole.Atmos)]
    [InlineData("ATMOS", FolderContentRole.Atmos)]
    [InlineData("5", FolderContentRole.Atmos)]
    [InlineData("video", FolderContentRole.Video)]
    [InlineData("podcast", FolderContentRole.Podcast)]
    [InlineData("flac", FolderContentRole.Stereo)]
    [InlineData("max_hires_192", FolderContentRole.Stereo)]
    [InlineData("mp3_320", FolderContentRole.Stereo)]
    [InlineData("", FolderContentRole.Stereo)]
    public void ResolveRole_ReadsTheRoleOffTheFoldersOwnType(string desiredQuality, FolderContentRole expected)
        => Assert.Equal(expected, FolderContentTypeResolver.ResolveRole(Folder(1, desiredQuality)));

    [Fact]
    public void ResolveRole_TreatsMissingFolderAsStereo()
        => Assert.Equal(FolderContentRole.Stereo, FolderContentTypeResolver.ResolveRole(null));

    [Fact]
    public void SingleAtmosFolder_IsTheDefaultWithoutBeingConfigured()
    {
        var folders = new[]
        {
            Folder(1, QualityCatalog.MaxHiRes192),
            Folder(2, "atmos")
        };

        Assert.Equal(2, FolderContentTypeResolver.ResolveDefaultFolderId(folders, FolderContentRole.Atmos));
    }

    [Fact]
    public void SingleStereoFolder_IsTheDefaultWithoutBeingConfigured()
    {
        var folders = new[]
        {
            Folder(1, "atmos"),
            Folder(2, QualityCatalog.Flac)
        };

        Assert.Equal(2, FolderContentTypeResolver.ResolveDefaultFolderId(folders, FolderContentRole.Stereo));
    }

    [Fact]
    public void NoFolderOfThatType_LeavesTheChoiceToTheUser()
    {
        var folders = new[] { Folder(1, "atmos"), Folder(2, "video") };

        Assert.Null(FolderContentTypeResolver.ResolveDefaultFolderId(folders, FolderContentRole.Stereo));
        Assert.Null(FolderContentTypeResolver.ResolveDefaultFolderId(folders, FolderContentRole.Podcast));
    }

    [Fact]
    public void SeveralFoldersOfThatType_LeavesTheChoiceToTheUser()
    {
        var folders = new[]
        {
            Folder(1, "atmos"),
            Folder(2, "ATMOS"),
            Folder(3, QualityCatalog.MaxHiRes192)
        };

        // Guessing here is what the app used to do, and it silently routed downloads to whichever
        // folder happened to sort first.
        Assert.Null(FolderContentTypeResolver.ResolveDefaultFolderId(folders, FolderContentRole.Atmos));
    }

    [Fact]
    public void ConfiguredFolder_WinsOverTheSingleFolderRule()
    {
        var folders = new[]
        {
            Folder(1, "atmos"),
            Folder(2, "atmos"),
            Folder(3, QualityCatalog.Flac)
        };

        Assert.Equal(2, FolderContentTypeResolver.ResolveDefaultFolderId(folders, FolderContentRole.Atmos, 2));
        Assert.Equal(1, FolderContentTypeResolver.ResolveDefaultFolderId(folders, FolderContentRole.Atmos, 1));
    }

    [Fact]
    public void ConfiguredFolderOfTheWrongRole_IsIgnored()
    {
        var folders = new[] { Folder(1, "atmos"), Folder(2, QualityCatalog.Flac) };

        // A stereo pointer must not be handed out for an Atmos download, so it is dropped and the
        // Atmos single-folder rule answers instead.
        Assert.Equal(1, FolderContentTypeResolver.ResolveDefaultFolderId(folders, FolderContentRole.Atmos, 2));
        Assert.Equal(2, FolderContentTypeResolver.ResolveDefaultFolderId(folders, FolderContentRole.Stereo, 1));
    }

    [Fact]
    public void ConfiguredFolderOfTheWrongRole_LeavesNoDefaultWhenNothingElseQualifies()
    {
        var folders = new[] { Folder(1, "atmos"), Folder(2, QualityCatalog.Flac), Folder(3, QualityCatalog.MaxHiRes192) };

        // Two stereo folders, so dropping the wrong-role pointer leaves a real choice.
        Assert.Null(FolderContentTypeResolver.ResolveDefaultFolderId(folders, FolderContentRole.Stereo, 1));
    }

    [Fact]
    public void ConfiguredFolderThatNoLongerExists_FallsBackToTheSingleFolder()
    {
        var folders = new[] { Folder(7, "atmos"), Folder(8, QualityCatalog.Flac) };

        Assert.Equal(7, FolderContentTypeResolver.ResolveDefaultFolderId(folders, FolderContentRole.Atmos, 999));
    }

    [Fact]
    public void DisabledFolderIsNeverADestination()
    {
        var folders = new[]
        {
            Folder(1, "atmos", enabled: false),
            Folder(2, "atmos", enabled: false)
        };

        Assert.Null(FolderContentTypeResolver.ResolveDefaultFolderId(folders, FolderContentRole.Atmos));
        Assert.Empty(FolderContentTypeResolver.SelectEnabledByRole(folders, FolderContentRole.Atmos));
    }

    [Fact]
    public void SingleEnabledFolder_IsUsedEvenWhenDisabledFoldersOfTheSameTypeExist()
    {
        var folders = new[]
        {
            Folder(1, "atmos", enabled: false),
            Folder(2, "atmos")
        };

        Assert.Equal(2, FolderContentTypeResolver.ResolveDefaultFolderId(folders, FolderContentRole.Atmos));
    }

    [Fact]
    public void MissingFolderList_LeavesTheChoiceToTheUser()
    {
        Assert.Null(FolderContentTypeResolver.ResolveDefaultFolderId(null, FolderContentRole.Stereo));
        Assert.Empty(FolderContentTypeResolver.SelectEnabledByRole(null, FolderContentRole.Stereo));
    }

    [Theory]
    [InlineData("atmos", FolderContentRole.Atmos)]
    [InlineData("video", FolderContentRole.Video)]
    [InlineData("podcast", FolderContentRole.Podcast)]
    [InlineData("music", FolderContentRole.Stereo)]
    [InlineData("stereo", FolderContentRole.Stereo)]
    [InlineData(null, FolderContentRole.Stereo)]
    [InlineData("", FolderContentRole.Stereo)]
    public void ParseRole_MapsContentTypeNamesOntoRoles(string? contentType, FolderContentRole expected)
        => Assert.Equal(expected, FolderContentTypeResolver.ParseRole(contentType));

    [Fact]
    public void SelectEnabledByRole_KeepsRolesApart()
    {
        var folders = new[]
        {
            Folder(1, "atmos"),
            Folder(2, "video"),
            Folder(3, "podcast"),
            Folder(4, QualityCatalog.MaxHiRes192)
        };

        Assert.Equal(new long[] { 4 }, FolderContentTypeResolver.SelectEnabledByRole(folders, FolderContentRole.Stereo).Select(f => f.Id));
        Assert.Equal(new long[] { 1 }, FolderContentTypeResolver.SelectEnabledByRole(folders, FolderContentRole.Atmos).Select(f => f.Id));
        Assert.Equal(new long[] { 2 }, FolderContentTypeResolver.SelectEnabledByRole(folders, FolderContentRole.Video).Select(f => f.Id));
        Assert.Equal(new long[] { 3 }, FolderContentTypeResolver.SelectEnabledByRole(folders, FolderContentRole.Podcast).Select(f => f.Id));
    }
}
