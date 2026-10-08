namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     Decides whether a peer publishes the folder a search result named.
/// </summary>
/// <remarks>
///     <para>
///         A search result's path is not proof of a folder. slskd returns full nested paths for a file, but its
///         directory endpoint answers with leaf names only and no directory prefix, so a folder inferred from a
///         filename cannot be walked back to: the request returns nothing, and nothing is indistinguishable from
///         a folder that exists and is empty. That is how peers with tens of thousands of files were reported as
///         holding nothing.
///     </para>
///     <para>
///         This matches the requested path against the paths the peer actually publishes. The comparison is
///         deliberately exact once separators and whitespace are normalized. There is deliberately no nearest
///         match and no partial credit: substituting a folder the reader did not ask for would put someone
///         else's files under a release name, which is a far worse failure than saying the folder is not there.
///     </para>
/// </remarks>
public static class SoulseekFolderPath
{
    /// <summary>
    ///     Whether a published directory is the requested folder.
    /// </summary>
    /// <param name="published">One directory path as the peer publishes it.</param>
    /// <param name="requested">The folder the caller wants.</param>
    public static bool IsSameFolder(string? published, string? requested)
        => Normalize(published).Length > 0
            && string.Equals(Normalize(published), Normalize(requested), StringComparison.Ordinal);

    /// <summary>
    ///     The requested folder as the peer's own paths are written.
    /// </summary>
    /// <remarks>
    ///     Peers disagree about separators and about spacing, and both are noise for the question being asked:
    ///     <c>music\100 gecs (2019)</c> and <c>music/100 gecs\(2019)</c> are the same folder. Collapsing
    ///     separators to one character and collapsing runs of whitespace to a single space makes the comparison
    ///     answer the question instead of the peer's punctuation.
    /// </remarks>
    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var unified = SoulseekRemotePath.Normalize(path);
        var collapsed = new System.Text.StringBuilder(unified.Length);
        var pendingSpace = false;

        foreach (var character in unified)
        {
            // A run of whitespace, wherever it falls, becomes one space and is only emitted before more content.
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = collapsed.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                collapsed.Append(' ');
                pendingSpace = false;
            }

            collapsed.Append(char.ToLowerInvariant(character));
        }

        return collapsed.ToString();
    }
}