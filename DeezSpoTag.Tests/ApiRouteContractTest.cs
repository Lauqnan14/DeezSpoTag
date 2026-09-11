using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Guards the frontend↔backend API contract: every literal /api/… path referenced by
/// the browser scripts must exist on the backend controllers. This is the net that
/// catches dead endpoints (the missing lastfm-biography route being the example).
/// </summary>
public sealed class ApiRouteContractTest
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "DeezSpoTag.Web", "Controllers", "Api")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static readonly string[] FrontendSearchRoots =
    [
        Path.Combine("DeezSpoTag.Web", "wwwroot", "js"),
        Path.Combine("DeezSpoTag.Web", "Views")
    ];

    private static readonly Regex ApiLiteralPattern = new(
        @"/api/[A-Za-z0-9\-_.${}/]*",
        RegexOptions.Compiled);

    [Fact]
    public void FrontendApiLiterals_ExistOnBackend()
    {
        var root = RepositoryRoot();
        var backendRoutes = CollectBackendRoutes(root)
            .Select(NormalizeRoute)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(backendRoutes);

        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in CollectFrontendFiles(root))
        {
            var content = File.ReadAllText(file);
            foreach (Match match in ApiLiteralPattern.Matches(content))
            {
                // Strip sentence punctuation that glues to the literal in comments/strings.
                var literal = match.Value.TrimEnd('.', ',', ')', ';', '!');
                var normalized = NormalizeRoute(literal);
                if (normalized.Split('/').LastOrDefault() is not ("{p}" or "api")
                    && !BackendRouteCovers(backendRoutes, normalized))
                {
                    missing.Add($"{Path.GetRelativePath(root, file)} -> {literal}");
                }
            }
        }

        Assert.True(missing.Count == 0, $"Frontend API calls without a backend route:{Environment.NewLine}{string.Join(Environment.NewLine, missing)}");
    }

    private static IEnumerable<string> CollectBackendRoutes(string root)
    {
        var controllersDirectory = Path.Combine(root, "DeezSpoTag.Web", "Controllers");
        var controllerFiles = Directory.EnumerateFiles(controllersDirectory, "*.cs", SearchOption.AllDirectories).ToList();

        var classRoutePattern = new Regex(@"\[Route\(""(?<template>[^""]+)""\)\]");
        var methodRoutePattern = new Regex(@"\[Http(?:Get|Post|Put|Delete|Patch)(?:\(""(?<template>[^""]+)""\))?\]");
        var httpMethodPattern = new Regex(@"\[Http(?:Get|Post|Put|Delete|Patch)");

        string IsApiBase(string template)
            => template.Equals("api", StringComparison.OrdinalIgnoreCase)
               || template.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
                   ? template
                   : string.Empty;

        var allBases = controllerFiles
            .SelectMany(file => classRoutePattern.Matches(File.ReadAllText(file)))
            .Select(match => IsApiBase(match.Groups["template"].Value))
            .Where(baseTemplate => baseTemplate.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in controllerFiles)
        {
            var content = File.ReadAllText(file);

            // A controller may declare multiple [Route] bases, and partial controllers
            // may split the [Route] file from the actions file: when this file has no
            // base of its own, fall back to the union of all controller bases so
            // actions still resolve.
            var fileBases = classRoutePattern.Matches(content)
                .Select(match => IsApiBase(match.Groups["template"].Value))
                .Where(baseTemplate => baseTemplate.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var bases = (fileBases.Count > 0 ? fileBases : allBases)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var methodMatches = methodRoutePattern.Matches(content);
            foreach (Match match in httpMethodPattern.Matches(content))
            {
                var ownMatch = methodMatches
                    .FirstOrDefault(candidate => candidate.Index == match.Index);
                var template = ownMatch is not null && ownMatch.Groups["template"].Success
                    ? ownMatch.Groups["template"].Value
                    : string.Empty;
                if (template.StartsWith("~/", StringComparison.Ordinal)
                    && template[2..].StartsWith("api/", StringComparison.OrdinalIgnoreCase))
                {
                    // Rooted route ("~/api/...") — absolute as written.
                    yield return template[2..];
                    continue;
                }

                if (template.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
                {
                    yield return template;
                    continue;
                }

                foreach (var baseTemplate in bases)
                {
                    yield return string.IsNullOrEmpty(template)
                        ? baseTemplate
                        : $"{baseTemplate.TrimEnd('/')}/{template.TrimStart('/')}";
                }
            }
        }
    }

    private static IEnumerable<string> CollectFrontendFiles(string root)
    {
        foreach (var searchRoot in FrontendSearchRoots)
        {
            var fullPath = Path.Combine(root, searchRoot);
            if (!Directory.Exists(fullPath))
            {
                continue;
            }

            foreach (var extension in new[] { "*.js", "*.cshtml" })
            {
                foreach (var file in Directory.EnumerateFiles(fullPath, extension, SearchOption.AllDirectories))
                {
                    if (file.Contains(".dsh-worktrees", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    yield return file;
                }
            }
        }
    }

    private static string NormalizeRoute(string route)
    {
        var path = route.Split('?', StringSplitOptions.RemoveEmptyEntries)[0];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var normalized = segments
            .Select(segment =>
            {
                if (segment.Contains('$') || segment.Contains('{') || segment.Contains('}'))
                {
                    return "{p}";
                }

                if (segment.All(char.IsDigit) && segment.Length > 0)
                {
                    return "{p}";
                }

                return segment.ToLowerInvariant();
            })
            .ToList();
        return string.Join('/', normalized);
    }

    private static bool BackendRouteCovers(HashSet<string> backendRoutes, string frontendRoute)
    {
        if (backendRoutes.Contains(frontendRoute))
        {
            return true;
        }

        // A dynamic frontend segment may resolve to a literal backend segment, but a
        // literal frontend segment must match the backend exactly (or a parameter).
        // A frontend literal may also be a prefix of a backend route (URLs built by
        // concatenation, e.g. "/api/deezer/stream/" + id).
        var frontendSegments = frontendRoute.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var backendRoute in backendRoutes)
        {
            var backendSegments = backendRoute.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (backendSegments.Length < frontendSegments.Length)
            {
                continue;
            }

            var matches = true;
            for (var index = 0; index < frontendSegments.Length; index++)
            {
                var frontendSegment = frontendSegments[index];
                var backendSegment = backendSegments[index];
                if (frontendSegment == "{p}"
                    || string.Equals(frontendSegment, backendSegment, StringComparison.OrdinalIgnoreCase)
                    || backendSegment.StartsWith("{"))
                {
                    continue;
                }

                matches = false;
                break;
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }
}
