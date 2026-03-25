using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed record AutoTagFolderScopeDependencies(
    LibraryRepository LibraryRepository,
    LibraryConfigStore LibraryConfigStore);
