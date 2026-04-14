using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
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
        const string rootPath = "/home/edzoh/Music/Test/Downs";
        const string sourcePath = "/home/edzoh/Music/Test/Downs/Atmos/Artist/Album/01 - Demo.m4a";
        using var document = JsonDocument.Parse(
            """
            {
              "FilePath": "/home/edzoh/Music/Test/Downs/Atmos/Artist/Album/01 - Demo.m4a",
              "Files": [
                {
                  "path": "/home/edzoh/Music/Test/Downs/Atmos/Artist/Album/01 - Demo.m4a",
                  "albumPath": "/home/edzoh/Music/Test/Downs/Atmos/Artist/Album",
                  "artistPath": "/home/edzoh/Music/Test/Downs/Atmos/Artist"
                }
              ],
              "FinalDestinations": {
                "/home/edzoh/Music/Test/Downs/Atmos/Artist/Album/01 - Demo.m4a": "/home/edzoh/Music/Test/Downs/Atmos/Artist/Album/01 - Demo.m4a"
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
        Assert.Contains("/home/edzoh/Music/Test/Downs/Atmos/Artist/Album", roots);
    }

    private static MethodInfo GetPrivateStaticMethod(string methodName)
    {
        return typeof(AutoTagDownloadMoveService).GetMethod(
                   methodName,
                   BindingFlags.NonPublic | BindingFlags.Static)
               ?? throw new InvalidOperationException($"{methodName} was not found.");
    }
}
