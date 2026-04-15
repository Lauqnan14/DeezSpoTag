using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ReferencesIsolationTests
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".sonarqube",
        ".sonar-coverage",
        "References",
        "Data",
        "bin",
        "obj",
        ".venv",
        "venv",
        "node_modules",
        "reports",
        ".tmp",
        "Tools",
        "tmp-wrapper",
        "meloday-main"
    };

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".csproj",
        ".sln",
        ".props",
        ".targets",
        ".json",
        ".md",
        ".txt",
        ".yml",
        ".yaml",
        ".toml",
        ".py",
        ".sh",
        ".ps1",
        ".js",
        ".ts",
        ".css",
        ".html",
        ".cshtml",
        ".xml",
        ".config",
    };

    private static readonly HashSet<string> AllowedReferencesPathFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "scripts/scan.sh",
        "scripts/scan_keep.sh"
    };

    [Fact]
    public void NonReferenceFiles_DoNotContainReferencesFolderPaths()
    {
        var repoRoot = ResolveRepoRoot();
        var violations = new List<string>();

        foreach (var filePath in EnumerateCandidateFiles(repoRoot))
        {
            var relativePath = NormalizePath(Path.GetRelativePath(repoRoot, filePath));
            if (AllowedReferencesPathFiles.Contains(relativePath))
            {
                continue;
            }

            if (IsBinaryFile(filePath))
            {
                continue;
            }

            var lineNumber = 0;
            foreach (var line in File.ReadLines(filePath))
            {
                lineNumber++;
                if (!ContainsReferencesPath(line))
                {
                    continue;
                }

                violations.Add($"{relativePath}:{lineNumber}");
                if (violations.Count >= 25)
                {
                    break;
                }
            }

            if (violations.Count >= 25)
            {
                break;
            }
        }

        Assert.True(
            violations.Count == 0,
            "Found non-References files that still point to References folder:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Join(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test output path.");
    }

    private static IEnumerable<string> EnumerateCandidateFiles(string repoRoot)
    {
        var pending = new Stack<string>();
        pending.Push(repoRoot);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            IEnumerable<string> childDirectories;
            try
            {
                childDirectories = Directory.EnumerateDirectories(directory);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            foreach (var childDirectory in childDirectories)
            {
                var name = Path.GetFileName(childDirectory);
                if (ExcludedDirectories.Contains(name) || name.StartsWith(".jscpd", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                pending.Push(childDirectory);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            foreach (var filePath in files)
            {
                if (ShouldScanFile(filePath))
                {
                    yield return filePath;
                }
            }
        }
    }

    private static bool ShouldScanFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (fileName.Equals("ReferencesIsolationTests.cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(filePath);
        return string.IsNullOrEmpty(extension) || AllowedExtensions.Contains(extension);
    }

    private static bool ContainsReferencesPath(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var normalized = line.Replace('\\', '/');
        const string marker = "references/";
        var index = 0;

        while ((index = normalized.IndexOf(marker, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (index >= 3
                && normalized[index - 3] == '.'
                && normalized[index - 2] == '.'
                && normalized[index - 1] == '/')
            {
                return true;
            }

            if (index == 0)
            {
                return true;
            }

            var preceding = normalized[index - 1];
            if (preceding == '/'
                || preceding == '"'
                || preceding == '\''
                || preceding == '`'
                || preceding == '('
                || preceding == '['
                || preceding == ':'
                || preceding == '='
                || char.IsWhiteSpace(preceding))
            {
                return true;
            }

            index += marker.Length;
        }

        return false;
    }

    private static bool IsBinaryFile(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            Span<byte> buffer = stackalloc byte[512];
            var read = stream.Read(buffer);
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true;
        }
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }
}
