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
        "References",
        "Data",
        "bin",
        "obj",
        ".venv",
        "venv",
        "node_modules",
        "reports",
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

    [Fact]
    public void NonReferenceFiles_DoNotContainReferencesFolderPaths()
    {
        var repoRoot = ResolveRepoRoot();
        var violations = new List<string>();

        foreach (var filePath in EnumerateCandidateFiles(repoRoot))
        {
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

                var relativePath = Path.GetRelativePath(repoRoot, filePath);
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
            if (File.Exists(Path.Join(current.FullName, "src.sln")))
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
            catch (Exception ex) when (ex is not OperationCanceledException) {
                continue;
            }

            foreach (var childDirectory in childDirectories)
            {
                var name = Path.GetFileName(childDirectory);
                if (ExcludedDirectories.Contains(name))
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
            catch (Exception ex) when (ex is not OperationCanceledException) {
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

        return line.Contains("../References/", StringComparison.OrdinalIgnoreCase)
            || line.Contains("..\\References\\", StringComparison.OrdinalIgnoreCase)
            || line.Contains("References/", StringComparison.OrdinalIgnoreCase)
            || line.Contains("References\\", StringComparison.OrdinalIgnoreCase);
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
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return true;
        }
    }
}
