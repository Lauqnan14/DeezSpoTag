using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Groups eligible music folders into library scopes for a Quality Checks run.
///
/// A checks run covers exactly one library — the profile that library is assigned to decides
/// what is switched on, and the folder scope is pinned to that library. Choosing "all libraries"
/// is therefore a queue of one run per library, never a single multi-library job. Folders that
/// carry no library row fall back to their own single-folder scope so they stay runnable.
/// </summary>
internal static class QualityChecksLibraryScope
{
    public sealed record LibraryScope(long? LibraryId, string LibraryName, IReadOnlyList<long> FolderIds);

    private sealed class Builder
    {
        public long? LibraryId { get; init; }
        public string LibraryName { get; init; } = string.Empty;
        public List<long> FolderIds { get; } = new();
    }

    /// <summary>
    /// Groups folders by library preserving the input order. The folder order inside a scope is
    /// preserved as well, so downstream folder resolution sees a stable selection.
    /// </summary>
    public static IReadOnlyList<LibraryScope> GroupByLibrary(IReadOnlyList<FolderDto> folders)
    {
        var builders = new List<Builder>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            if (folder.Id <= 0)
            {
                continue;
            }

            var key = ScopeKey(folder);
            if (!positions.TryGetValue(key, out var position))
            {
                positions[key] = position = builders.Count;
                builders.Add(new Builder
                {
                    LibraryId = folder.LibraryId is > 0 ? folder.LibraryId : null,
                    LibraryName = ResolveLibraryName(folder)
                });
            }

            builders[position].FolderIds.Add(folder.Id);
        }

        return builders
            .Select(builder => new LibraryScope(builder.LibraryId, builder.LibraryName, builder.FolderIds))
            .ToList();
    }

    /// <summary>
    /// Resolves the folder scope for one library, or null when no folder belongs to it.
    /// </summary>
    public static LibraryScope? Resolve(IReadOnlyList<FolderDto> folders, long? libraryId)
    {
        if (libraryId is not > 0)
        {
            return null;
        }

        return GroupByLibrary(folders).FirstOrDefault(scope => scope.LibraryId == libraryId);
    }

    /// <summary>
    /// True when the given folder set belongs to more than one library. A checks run must not span
    /// libraries: "all libraries" runs them one at a time, each as its own job.
    /// </summary>
    public static bool SpansMultipleLibraries(IReadOnlyList<FolderDto> folders)
    {
        var libraries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            if (folder.Id <= 0)
            {
                continue;
            }

            libraries.Add(ScopeKey(folder));
        }

        return libraries.Count > 1;
    }

    private static string ScopeKey(FolderDto folder)
        => folder.LibraryId is > 0 ? $"library:{folder.LibraryId.Value}" : $"folder:{folder.Id}";

    private static string ResolveLibraryName(FolderDto folder)
    {
        if (!string.IsNullOrWhiteSpace(folder.LibraryName))
        {
            return folder.LibraryName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(folder.DisplayName))
        {
            return folder.DisplayName.Trim();
        }

        return folder.LibraryId is > 0 ? $"Library {folder.LibraryId.Value}" : $"Folder {folder.Id}";
    }
}
