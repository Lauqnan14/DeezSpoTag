using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagDownloadMoveServicePayloadPathTests
{
    [Fact]
    public void TryGetPropertyIgnoreCase_ReturnsFalse_WhenPropertyIsMissing()
    {
        using var document = JsonDocument.Parse("""{"FilePath":"/tmp/demo.flac"}""");
        var method = GetPrivateStaticMethod("TryGetPropertyIgnoreCase");
        var args = new object?[] { document.RootElement, "albumPath", null };

        var result = (bool)method.Invoke(null, args)!;

        Assert.False(result);
    }

    [Fact]
    public void CollectPayloadPaths_DoesNotThrow_WhenOptionalRootPropertiesAreMissing()
    {
        const string rootPath = "/home/user/Music/Test/Downs";
        const string sourcePath = "/home/user/Music/Test/Downs/Atmos/Artist/Album/01 - Demo.m4a";
        using var document = JsonDocument.Parse(
            """
            {
              "FilePath": "/home/user/Music/Test/Downs/Atmos/Artist/Album/01 - Demo.m4a",
              "Files": [
                {
                  "path": "/home/user/Music/Test/Downs/Atmos/Artist/Album/01 - Demo.m4a",
                  "albumPath": "/home/user/Music/Test/Downs/Atmos/Artist/Album",
                  "artistPath": "/home/user/Music/Test/Downs/Atmos/Artist"
                }
              ],
              "FinalDestinations": {
                "/home/user/Music/Test/Downs/Atmos/Artist/Album/01 - Demo.m4a": "/home/user/Music/Test/Downs/Atmos/Artist/Album/01 - Demo.m4a"
              }
            }
            """);

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var method = GetPrivateStaticMethod("CollectPayloadPaths");
        var ex = Record.Exception(() =>
            method.Invoke(null, new object[] { rootPath, document.RootElement, files, roots }));

        Assert.Null(ex);
        Assert.Contains(sourcePath, files);
        Assert.Contains("/home/user/Music/Test/Downs/Atmos/Artist/Album", roots);
    }

    [Fact]
    public void ResolveRoutingFolderId_MatchesYearRule_BeforeDefault()
    {
        var metadata = CreateRoutingMetadata(
            artist: "Artist",
            title: "Title",
            album: "Album",
            genres: new List<string> { "Pop" },
            explicitValue: false,
            releaseDate: "2024-07-01");
        var rules = new List<PlaylistTrackRoutingRule>
        {
            new("year", "gte", "2020", 42, 0)
        };

        var result = InvokeResolveRoutingFolderId(metadata, rules, defaultFolderId: 10);

        Assert.Equal(42, result);
    }

    [Fact]
    public void ResolveRoutingFolderId_MatchesGenreRule_CaseInsensitive()
    {
        var metadata = CreateRoutingMetadata(
            artist: "Artist",
            title: "Title",
            album: "Album",
            genres: new List<string> { "Melodic Progressive House" },
            explicitValue: null,
            releaseDate: "2019-05-11");
        var rules = new List<PlaylistTrackRoutingRule>
        {
            new("genre", "contains", "progressive", 77, 0)
        };

        var result = InvokeResolveRoutingFolderId(metadata, rules, defaultFolderId: 10);

        Assert.Equal(77, result);
    }

    [Fact]
    public void TryRewritePayloadDestinationFolderId_RewritesDestinationFolderId()
    {
        const string payload = """{"DestinationFolderId":12,"Title":"Demo"}""";
        var method = GetPrivateStaticMethod("TryRewritePayloadDestinationFolderId");
        var args = new object?[] { payload, 35L, null };

        var rewritten = (bool)method.Invoke(null, args)!;

        Assert.True(rewritten);
        var updatedJson = Assert.IsType<string>(args[2]);
        using var document = JsonDocument.Parse(updatedJson);
        Assert.Equal(35, document.RootElement.GetProperty("DestinationFolderId").GetInt64());
    }

    private static MethodInfo GetPrivateStaticMethod(string methodName)
    {
        return typeof(AutoTagDownloadMoveService).GetMethod(
                   methodName,
                   BindingFlags.NonPublic | BindingFlags.Static)
               ?? throw new InvalidOperationException($"{methodName} was not found.");
    }

    private static object CreateRoutingMetadata(
        string artist,
        string title,
        string album,
        IReadOnlyList<string> genres,
        bool? explicitValue,
        string? releaseDate)
    {
        var metadataType = typeof(AutoTagDownloadMoveService).GetNestedType(
                               "RoutingMatchMetadata",
                               BindingFlags.NonPublic)
                           ?? throw new InvalidOperationException("RoutingMatchMetadata was not found.");
        return Activator.CreateInstance(metadataType, artist, title, album, genres, explicitValue, releaseDate)
               ?? throw new InvalidOperationException("RoutingMatchMetadata could not be created.");
    }

    private static long? InvokeResolveRoutingFolderId(
        object metadata,
        IReadOnlyList<PlaylistTrackRoutingRule> rules,
        long? defaultFolderId)
    {
        var method = GetPrivateStaticMethod("ResolveRoutingFolderId");
        return (long?)method.Invoke(null, new object?[] { metadata, rules, defaultFolderId });
    }
}
