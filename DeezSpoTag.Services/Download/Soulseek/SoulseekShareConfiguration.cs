using System.Text;

namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     The result of comparing the desired shares against the shares slskd reports.
/// </summary>
/// <param name="MissingFromSlskd">Enabled folders that slskd is not serving.</param>
/// <param name="UnexpectedlyShared">Paths slskd serves that no enabled folder accounts for.</param>
/// <param name="Diagnostics">Findings for the UI.</param>
public sealed record SoulseekShareDiff(
    IReadOnlyList<SoulseekDesiredShare> MissingFromSlskd,
    IReadOnlyList<SoulseekActualShare> UnexpectedlyShared,
    IReadOnlyList<SoulseekShareDiagnostic> Diagnostics);

/// <summary>
///     Pure share comparison and configuration rendering.
/// </summary>
/// <remarks>
///     <para>
///         Kept free of I/O so the rules that matter most can be tested directly. In particular
///         <see cref="Diff"/> is where the "slskd is serving something no enabled folder accounts for" case is
///         decided, and that is a safety rule rather than a convenience.
///     </para>
/// </remarks>
public static class SoulseekShareConfiguration
{
    /// <summary>
    ///     Renders a <c>shares.directories</c> block for slskd's own configuration file.
    /// </summary>
    /// <remarks>
    ///     This is output for the user to apply, never a remote mutation: slskd has no share-write API, so the
    ///     only way to change what it serves is to edit its configuration. Entries use slskd's
    ///     <c>[Alias]\path</c> syntax so the local folder name is hidden from remote peers.
    /// </remarks>
    public static string Generate(IReadOnlyList<SoulseekDesiredShare> desiredShares)
    {
        ArgumentNullException.ThrowIfNull(desiredShares);

        var builder = new StringBuilder();
        builder.AppendLine("shares:");
        builder.AppendLine("  directories:");

        var any = false;
        foreach (var share in desiredShares)
        {
            if (string.IsNullOrWhiteSpace(share.LocalPath))
            {
                continue;
            }

            var entry = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(share.Alias))
            {
                entry.Append('[').Append(share.Alias).Append(']');
            }

            entry.Append(share.LocalPath);
            builder.Append("    - '").Append(Escape(entry.ToString())).AppendLine("'");
            any = true;
        }

        if (!any)
        {
            builder.AppendLine("    # No folder is enabled for sharing. Add entries here to expose one.");
        }

        var filters = CollectFilters(desiredShares);
        if (filters.Count > 0)
        {
            builder.AppendLine("  filters:");
            foreach (var filter in filters)
            {
                builder.Append("    - '").Append(Escape(filter)).AppendLine("'");
            }
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Compares the desired shares against the shares slskd reports.
    /// </summary>
    /// <param name="desired">The shares the user enabled in the folder tab.</param>
    /// <param name="actual">The shares slskd reported.</param>
    /// <param name="slskdUnavailable">
    ///     When set, the diff is one-sided: everything enabled is reported missing, because with no reading of
    ///     slskd there is no way to know.
    /// </param>
    /// <param name="skipped">Folders that were skipped, with the reason.</param>
    /// <param name="unavailableMessage">Why slskd could not be read, when it could not.</param>
    public static SoulseekShareDiff Diff(
        IReadOnlyList<SoulseekDesiredShare> desired,
        IReadOnlyList<SoulseekActualShare> actual,
        bool slskdUnavailable = false,
        IReadOnlyList<(long FolderId, string Reason)>? skipped = null,
        string? unavailableMessage = null)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(actual);

        var diagnostics = new List<SoulseekShareDiagnostic>();
        skipped ??= [];

        foreach (var entry in skipped)
        {
            diagnostics.Add(new SoulseekShareDiagnostic(
                "folder_skipped",
                SoulseekShareDiagnosticSeverity.Error,
                $"Folder {entry.FolderId} is enabled for sharing but was skipped: {entry.Reason}.",
                entry.FolderId));
        }

        if (slskdUnavailable)
        {
            diagnostics.Add(new SoulseekShareDiagnostic(
                "slskd_unavailable",
                SoulseekShareDiagnosticSeverity.Warning,
                $"Could not read the shares slskd is serving: {unavailableMessage ?? "slskd is unavailable."}"));

            return new SoulseekShareDiff(desired, [], diagnostics);
        }

        var desiredPaths = desired
            .Select(share => NormalizePath(share.LocalPath))
            .Where(path => path.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var missing = desired
            .Where(share => !actual.Any(entry => string.Equals(NormalizePath(entry.LocalPath ?? string.Empty), NormalizePath(share.LocalPath), StringComparison.Ordinal)))
            .ToList();

        // A path slskd serves that no enabled folder accounts for: local content exposed to the Soulseek
        // network without the user having enabled it here. An excluded share is not exposed, so it is fine.
        var unexpected = actual
            .Where(share => !share.IsExcluded)
            .Where(share => !string.IsNullOrWhiteSpace(share.LocalPath))
            .Where(share => !desiredPaths.Contains(NormalizePath(share.LocalPath!)))
            .ToList();

        diagnostics.Add(new SoulseekShareDiagnostic(
            desired.Count > 0 ? "shares_enabled" : "no_shares_enabled",
            SoulseekShareDiagnosticSeverity.Info,
            desired.Count > 0
                ? $"{desired.Count} folder(s) are enabled for Soulseek sharing."
                : "No folder is enabled for Soulseek sharing."));

        if (skipped.Count > 0)
        {
            diagnostics.Add(new SoulseekShareDiagnostic(
                "folders_skipped",
                SoulseekShareDiagnosticSeverity.Warning,
                $"{skipped.Count} enabled folder(s) were skipped: {string.Join(", ", skipped.Select(entry => entry.Reason))}."));
        }

        if (missing.Count > 0)
        {
            diagnostics.Add(new SoulseekShareDiagnostic(
                "shares_missing_in_slskd",
                SoulseekShareDiagnosticSeverity.Warning,
                $"{missing.Count} enabled folder(s) are not served by slskd yet. Apply the generated configuration "
                    + "and rescan to expose them."));
        }

        foreach (var share in unexpected)
        {
            diagnostics.Add(new SoulseekShareDiagnostic(
                "unexpected_share",
                SoulseekShareDiagnosticSeverity.Warning,
                $"slskd is serving \"{share.LocalPath}\", which no enabled folder accounts for. "
                    + "Remove it from the slskd configuration if that was not intended."));
        }

        return new SoulseekShareDiff(missing, unexpected, diagnostics);
    }

    /// <summary>
    ///     Normalizes a path for comparison.
    /// </summary>
    /// <remarks>
    ///     Separators and trailing slashes are normalized so the same folder written one way by the user and
    ///     another way by slskd compares equal. Case is preserved, because the underlying filesystem may be case
    ///     sensitive and folding case could hide a real difference.
    /// </remarks>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.Length > 1 && normalized.EndsWith('/'))
        {
            normalized = normalized[..^1];
        }

        return normalized;
    }

    /// <summary>
    ///     Rewrites the <c>shares.directories</c> list inside an slskd configuration document, leaving every
    ///     other line of that document exactly as it was.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         slskd exposes no endpoint for setting shared directories, so this is the only way to change them:
    ///         the file has to be rewritten and slskd's own web UI rewrites it the same way. A general YAML
    ///         round-trip would reformat and reorder the user's whole configuration, so only the one sequence is
    ///         touched and the rest of the bytes are preserved.
    ///     </para>
    ///     <para>
    ///         Anything this cannot read with certainty is refused rather than guessed at. Overwriting a
    ///         configuration file is not recoverable by the caller, and a config that parses but has lost a
    ///         setting is worse than a refused edit.
    ///     </para>
    /// </remarks>
    /// <exception cref="SoulseekShareConfigurationException">
    ///     The document is not shaped the way slskd's configuration is, so rewriting it would be a guess.
    /// </exception>
    public static string ApplyDirectories(string yaml, IReadOnlyList<string> directories)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(directories);

        var newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

        var sharesKey = FindTopLevelKey(lines, "shares");
        if (sharesKey is null)
        {
            // No shares block at all: append one, keeping the document's trailing newline convention.
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.RemoveAt(lines.Count - 1);
            }

            lines.Add("shares:");
            lines.Add("  directories:");
            foreach (var entry in RenderDirectories(directories))
            {
                // One level deeper than the key, which is what makes it a block sequence.
                lines.Add("    " + entry);
            }

            return string.Join(newline, lines) + newline;
        }

        var blockIndent = IndentOf(lines[sharesKey.Value]);
        var blockEnd = EndOfBlock(lines, sharesKey.Value, blockIndent);

        var directoriesKey = FindKey(lines, "directories", sharesKey.Value + 1, blockEnd, blockIndent);
        if (directoriesKey is null)
        {
            // shares exists but has no directories list. The key goes in as its first child and the
            // sequence below that, one level deeper, because a sequence level with the same indent as its
            // key is not the block list this reader recognises.
            var childIndent = blockIndent + 2;
            var inserted = new List<string> { $"{new string(' ', childIndent)}directories:" };
            inserted.AddRange(RenderDirectories(directories).Select(entry => $"{new string(' ', childIndent + 2)}{entry}"));
            lines.InsertRange(sharesKey.Value + 1, inserted);

            return string.Join(newline, lines);
        }

        var listIndent = IndentOf(lines[directoriesKey.Value]);
        if (listIndent <= blockIndent || KeyValue(lines[directoriesKey.Value]).Length > 0)
        {
            // Either the key is not nested, or it carries its list inline ("directories: ['/music']" or
            // "directories: /music"). Both are legal YAML and neither is the block list this editor
            // rewrites, so guessing at the author's intent here could silently change what is shared.
            throw new SoulseekShareConfigurationException(
                "The slskd configuration has a shares.directories key that is not a nested list, so it cannot be rewritten safely.");
        }

        // The sequence runs until the next line indented no further than the key itself. Comments and blank
        // lines inside that span belong to the list being replaced, so they go with it.
        var listEnd = directoriesKey.Value + 1;
        var entryIndent = listIndent + 2;
        while (listEnd < lines.Count)
        {
            var line = lines[listEnd];
            if (string.IsNullOrWhiteSpace(line))
            {
                listEnd++;
                continue;
            }

            var indent = IndentOf(line);
            if (indent <= listIndent)
            {
                break;
            }

            if (line.TrimStart().StartsWith('-'))
            {
                // Keep whatever column the author already used for the sequence, so rewriting a share does
                // not reindent their file.
                entryIndent = indent;
            }

            listEnd++;
        }

        lines.RemoveRange(directoriesKey.Value + 1, listEnd - directoriesKey.Value - 1);

        // Insert past the end of the block being built, not at a fixed offset: inserting each entry at the
        // same index would push every one of them in front of the last and reverse the list.
        var insertAt = directoriesKey.Value + 1;
        foreach (var entry in RenderDirectories(directories))
        {
            lines.Insert(insertAt++, $"{new string(' ', entryIndent)}{entry}");
        }

        return string.Join(newline, lines);
    }

    /// <summary>
    ///     Reads <c>shares.directories</c> back out of a configuration document.
    /// </summary>
    /// <remarks>
    ///     Used to report what slskd is actually serving rather than what was last asked for, so an edit
    ///     made outside DeezSpoTag is visible instead of being silently overwritten on the next toggle.
    /// </remarks>
    public static IReadOnlyList<string> ReadDirectories(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sharesKey = FindTopLevelKey(lines.ToList(), "shares");
        if (sharesKey is null)
        {
            return [];
        }

        var blockIndent = IndentOf(lines[sharesKey.Value]);
        var blockEnd = EndOfBlock(lines.ToList(), sharesKey.Value, blockIndent);
        var directoriesKey = FindKey(lines.ToList(), "directories", sharesKey.Value + 1, blockEnd, blockIndent);
        if (directoriesKey is null)
        {
            return [];
        }

        var listIndent = IndentOf(lines[directoriesKey.Value]);
        var found = new List<string>();
        for (var i = directoriesKey.Value + 1; i < lines.Length && i < blockEnd; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (IndentOf(line) <= listIndent)
            {
                break;
            }

            var value = line.Trim();
            if (value.StartsWith('-'))
            {
                value = value[1..].Trim();
            }

            if (value.Length == 0)
            {
                continue;
            }

            // slskd renders these single-quoted; YAML escapes an embedded quote by doubling it.
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            {
                value = value[1..^1].Replace("''", "'", StringComparison.Ordinal);
            }

            found.Add(value);
        }

        return found;
    }

    private static IEnumerable<string> RenderDirectories(IReadOnlyList<string> directories)
    {
        var any = false;
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            yield return $"- '{Escape(directory.Trim())}'";
            any = true;
        }

        if (!any)
        {
            yield return "# No shared directories.";
        }
    }

    private static int IndentOf(string line) => line.Length - line.TrimStart().Length;

    private static bool IsKeyLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length > 0
            && trimmed[0] != '#'
            && trimmed[0] != '-'
            && trimmed.Contains(':');
    }

    private static int? FindTopLevelKey(List<string> lines, string key)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (IndentOf(lines[i]) == 0 && IsKeyLine(lines[i]) && KeyName(lines[i]) == key)
            {
                return i;
            }
        }

        return null;
    }

    private static int? FindKey(List<string> lines, string key, int from, int to, int parentIndent)
    {
        // Indentation width is not fixed. YAML authors use two spaces, four spaces or tabs, so the key is
        // matched on being nested deeper than its parent rather than on one exact column.
        for (var i = from; i < to && i < lines.Count; i++)
        {
            if (IndentOf(lines[i]) > parentIndent && IsKeyLine(lines[i]) && KeyName(lines[i]) == key)
            {
                return i;
            }
        }

        return null;
    }

    private static string KeyValue(string line)
    {
        var trimmed = line.TrimStart();
        var colon = trimmed.IndexOf(':');
        return colon < 0 ? string.Empty : trimmed[(colon + 1)..].Trim();
    }

    private static int EndOfBlock(List<string> lines, int start, int indent)
    {
        var i = start + 1;
        while (i < lines.Count)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]) && IndentOf(lines[i]) <= indent)
            {
                break;
            }

            i++;
        }

        return i;
    }

    private static string KeyName(string line)
    {
        var trimmed = line.TrimStart();
        var colon = trimmed.IndexOf(':');
        return colon < 0 ? trimmed : trimmed[..colon].Trim();
    }

    private static List<string> CollectFilters(IReadOnlyList<SoulseekDesiredShare> desiredShares)
    {
        // slskd only supports global regex filters, so per-folder excludes are merged into one list. They are
        // advisory on the slskd side; DeezSpoTag applies them itself when previewing.
        var filters = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var share in desiredShares)
        {
            foreach (var filter in (share.ExcludeFilters ?? [])
                         .Where(candidate => !string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate.Trim())))
            {
                filters.Add(filter.Trim());
            }
        }

        return filters;
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
