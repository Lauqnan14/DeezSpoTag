namespace DeezSpoTag.Web.Services;

public interface ILibraryRuntimeSnapshotProvider
{
    Task<LibraryRuntimeSnapshotService.LibraryRuntimeSnapshotDto> BuildSnapshotAsync(long? folderId, CancellationToken cancellationToken);
}
