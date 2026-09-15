using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoPlaylistsControllerBehaviorTest
{
    [Fact]
    public void LibraryPlaylistIndex_DoesNotDependOnPerPlaylistItemScans()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AutoPlaylistsApiController.cs");
        var methodBody = GetMethodBody(source, "public async Task<IActionResult> GetPlaylists");

        Assert.Contains("GetPlaylistsAsync(plex.Url, plex.Token", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPlaylistItemsAsync", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveLibraryInfoForPlaylistAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryPlaylistIndex_DoesNotDropPlaylistsThroughPlexSectionFiltering()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AutoPlaylistsApiController.cs");

        Assert.DoesNotContain("GetAllowedMusicSectionIdsAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("allowedSectionIds", source, StringComparison.Ordinal);
        Assert.Contains("librarySectionId = p.LibrarySectionId", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoPlaylists_AreRenderedInNonEmptySundayFirstDaySections()
    {
        var source = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "auto-playlists.js");

        var sunday = source.IndexOf("{ id: \"sunday\", label: \"Sunday\" }", StringComparison.Ordinal);
        var monday = source.IndexOf("{ id: \"monday\", label: \"Monday\" }", StringComparison.Ordinal);
        var saturday = source.IndexOf("{ id: \"saturday\", label: \"Saturday\" }", StringComparison.Ordinal);

        Assert.True(sunday >= 0 && sunday < monday && monday < saturday);
        Assert.Contains("const playlistsForDay = playlistsByDay.get(day.id);", source, StringComparison.Ordinal);
        Assert.Contains("if (!playlistsForDay || playlistsForDay.length === 0)", source, StringComparison.Ordinal);
        Assert.Contains("dayGrid.className = \"auto-tools-grid\";", source, StringComparison.Ordinal);
        Assert.Contains("daySection.className = \"auto-playlists-day-section\";", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoPlaylists_AreOrderedFromEarlyMorningThroughLateEveningWithinEachDay()
    {
        var source = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "auto-playlists.js");

        var earlyMorning = source.IndexOf("{ id: \"early-morning\", order: 0 }", StringComparison.Ordinal);
        var morning = source.IndexOf("{ id: \"morning\", order: 1 }", StringComparison.Ordinal);
        var noon = source.IndexOf("{ id: \"noon\", order: 2 }", StringComparison.Ordinal);
        var afternoon = source.IndexOf("{ id: \"afternoon\", order: 3 }", StringComparison.Ordinal);
        var evening = source.IndexOf("{ id: \"evening\", order: 4 }", StringComparison.Ordinal);
        var lateEvening = source.IndexOf("{ id: \"late-evening\", order: 5 }", StringComparison.Ordinal);

        Assert.True(earlyMorning >= 0
            && earlyMorning < morning
            && morning < noon
            && noon < afternoon
            && afternoon < evening
            && evening < lateEvening);
        Assert.Contains("playlistsForDay.sort((left, right) => resolvePlaylistDaypartOrder(left) - resolvePlaylistDaypartOrder(right));", source, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] relativePath)
    {
        var repoRoot = FindRepoRoot();
        return File.ReadAllText(Path.Join(new[] { repoRoot }.Concat(relativePath).ToArray()));
    }

    private static string GetMethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find method signature: {signature}");
        var brace = source.IndexOf('{', start);
        Assert.True(brace >= 0, $"Could not find method body for: {signature}");

        var depth = 0;
        for (var i = brace; i < source.Length; i++)
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
                    return source.Substring(brace, i - brace + 1);
                }
            }
        }

        throw new InvalidOperationException($"Could not parse method body for: {signature}");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(dir))
        {
            if (Directory.Exists(Path.Join(dir, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(dir, "DeezSpoTag.Tests")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
