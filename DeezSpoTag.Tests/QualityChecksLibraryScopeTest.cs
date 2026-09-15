using System.Linq;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class QualityChecksLibraryScopeTest
{
    [Fact]
    public void GroupByLibrary_OneScopePerLibraryPreservingOrder()
    {
        var folders = new[]
        {
            Folder(1, libraryId: 10, libraryName: "Main"),
            Folder(2, libraryId: 20, libraryName: "Atmos"),
            Folder(3, libraryId: 10, libraryName: "Main")
        };

        var scopes = QualityChecksLibraryScope.GroupByLibrary(folders);

        Assert.Equal(2, scopes.Count);
        Assert.Equal(10, scopes[0].LibraryId);
        Assert.Equal("Main", scopes[0].LibraryName);
        Assert.Equal([1L, 3L], scopes[0].FolderIds);
        Assert.Equal(20, scopes[1].LibraryId);
        Assert.Equal([2L], scopes[1].FolderIds);
    }

    [Fact]
    public void GroupByLibrary_FolderWithoutALibraryGetsItsOwnScope()
    {
        var folders = new[]
        {
            Folder(1, libraryId: null, libraryName: null, displayName: "Loose"),
            Folder(2, libraryId: 20, libraryName: "Atmos")
        };

        var scopes = QualityChecksLibraryScope.GroupByLibrary(folders);

        Assert.Equal(2, scopes.Count);
        Assert.Null(scopes[0].LibraryId);
        Assert.Equal([1L], scopes[0].FolderIds);
        Assert.Equal("Loose", scopes[0].LibraryName);
    }

    [Fact]
    public void SpansMultipleLibraries_IsTrueForTwoLibrariesFalseForOne()
    {
        Assert.True(QualityChecksLibraryScope.SpansMultipleLibraries(
            [Folder(1, libraryId: 10), Folder(2, libraryId: 20)]));
        Assert.False(QualityChecksLibraryScope.SpansMultipleLibraries(
            [Folder(1, libraryId: 10), Folder(2, libraryId: 10)]));
        // A library-less folder is its own scope, so pairing it with a library spans two.
        Assert.True(QualityChecksLibraryScope.SpansMultipleLibraries(
            [Folder(1, libraryId: null), Folder(2, libraryId: 10)]));
    }

    [Fact]
    public void Resolve_ReturnsTheFolderScopeForOneLibrary()
    {
        var folders = new[]
        {
            Folder(1, libraryId: 10),
            Folder(2, libraryId: 20),
            Folder(3, libraryId: 20)
        };

        var scope = QualityChecksLibraryScope.Resolve(folders, 20);

        Assert.NotNull(scope);
        Assert.Equal([2L, 3L], scope!.FolderIds);
        Assert.Null(QualityChecksLibraryScope.Resolve(folders, 99));
        Assert.Null(QualityChecksLibraryScope.Resolve(folders, null));
    }

    private static FolderDto Folder(
        long id,
        long? libraryId,
        string? libraryName = "Library",
        string? displayName = null)
        => new(
            id,
            $"/music/{id}",
            displayName ?? $"Folder {id}",
            Enabled: true,
            LibraryId: libraryId,
            LibraryName: libraryName,
            DesiredQuality: "FLAC",
            AutoTagProfileId: "default",
            AutoTagEnabled: true,
            ConvertEnabled: false,
            ConvertFormat: null,
            ConvertBitrate: null);
}
