using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class BoomplayUrlFlowGuardrailTests
{
    [Fact]
    public void HomeAndSearchRouteBoomplayLinksThroughCanonicalServerResolver()
    {
        var home = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "home-index.js");
        var search = ReadSource("DeezSpoTag.Web", "Views", "Search", "Index.cshtml");

        Assert.Contains("/api/boomplay/parse-link?url=", home, StringComparison.Ordinal);
        Assert.Contains("/api/boomplay/parse-link?url=", search, StringComparison.Ordinal);
        Assert.DoesNotContain("BOOMPLAY_PLAYLIST_REGEX", home, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var pathParts = new string[segments.Length + 1];
            pathParts[0] = directory.FullName;
            Array.Copy(segments, 0, pathParts, 1, segments.Length);
            var candidate = Path.Combine(pathParts);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate repository root.");
    }
}
