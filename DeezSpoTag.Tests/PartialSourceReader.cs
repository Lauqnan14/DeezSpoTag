using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DeezSpoTag.Tests;

/// <summary>
/// Reads the source text of a type that is spread across multiple partial-class files.
/// </summary>
/// <remarks>
/// Several guardrail tests assert on the *source text* of large services (they pin protocol
/// markers, shared constants and specific call shapes so a rename fails the build instead of
/// silently breaking behavior). A partial-class split moves members between files without
/// changing the type, so those tests must read the whole type surface rather than one file.
///
/// <para>
/// <see cref="ReadTypeSource"/> returns the primary file plus every satellite partial
/// (<c>Name.*.cs</c>) concatenated in deterministic order: primary first, then satellites
/// sorted by filename. Assertions that look for a statement anywhere in the type, or that
/// assert a statement appears nowhere in the type, keep their original meaning.
/// </para>
/// <para>
/// Use this instead of <c>File.ReadAllText</c> for any type that may be split into partials.
/// </para>
/// </remarks>
public static class PartialSourceReader
{
    public static string ReadTypeSource(params string[] relativeParts)
    {
        if (relativeParts.Length == 0)
        {
            throw new ArgumentException("At least one path part is required.", nameof(relativeParts));
        }

        var primaryPath = Path.Join(ResolveRepositoryRoot(), Path.Combine(relativeParts));
        return ReadTypeSourceFromFile(primaryPath);
    }

    /// <summary>
    /// Resolves the absolute path of a type's primary source file, for tests that need a path
    /// rather than the concatenated text.
    /// </summary>
    public static string ResolvePrimaryPath(params string[] relativeParts)
    {
        if (relativeParts is null || relativeParts.Length == 0)
        {
            throw new ArgumentException("At least one path part is required.", nameof(relativeParts));
        }

        return Path.Join(ResolveRepositoryRoot(), Path.Combine(relativeParts));
    }

    /// <summary>Reads an absolute primary path plus its satellite partials.</summary>
    public static string ReadTypeSourceFromFile(string primaryPath)
    {
        if (!File.Exists(primaryPath))
        {
            throw new FileNotFoundException($"Primary source file was not found: {primaryPath}", primaryPath);
        }

        var directory = Path.GetDirectoryName(primaryPath)!;
        var baseName = Path.GetFileNameWithoutExtension(primaryPath);

        var satellites = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, baseName + ".*.cs")
                .Where(path => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList()
            : new List<string>();

        var parts = new List<string> { File.ReadAllText(primaryPath) };
        parts.AddRange(satellites.Select(File.ReadAllText));
        return string.Join("\n", parts);
    }

    /// <summary>
    /// Returns the source of a single member, located by the start of its declaration.
    /// </summary>
    /// <remarks>
    /// A guardrail that asserts on the internals of one method must not depend on which partial
    /// file that method landed in, nor on what happens to follow it. This locates the member by
    /// declaration text and returns exactly its body, ending at the matching closing brace.
    /// </remarks>
    public static string ReadMemberSourceFromFile(string primaryPath, string declarationStart)
    {
        var source = ReadTypeSourceFromFile(primaryPath);
        var start = source.IndexOf(declarationStart, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"Member not found: {declarationStart}");
        }

        var open = source.IndexOf('{', start);
        if (open < 0)
        {
            throw new InvalidOperationException($"Member has no body: {declarationStart}");
        }

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(start, i - start + 1);
                }
            }
        }

        throw new InvalidOperationException($"Member body is unbalanced: {declarationStart}");
    }

    private static string ResolveRepositoryRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (Directory.Exists(Path.Join(directory, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(directory, "DeezSpoTag.Tests")))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
