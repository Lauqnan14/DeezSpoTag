using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AssetUrlGuardrailTests
{
    [Fact]
    public void AssetVersioning_FallsBackWithoutFileWatchers()
    {
        var source = ReadSource("DeezSpoTag.Web", "Views", "AssetUrl.cs");

        Assert.Contains("catch (IOException)", source, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _fileVersionProviderUnavailable, 1)", source, StringComparison.Ordinal);
        Assert.Contains("AddFallbackFileVersion(viewContext, contentPath, path)", source, StringComparison.Ordinal);
        Assert.Contains("File.GetLastWriteTimeUtc(filePath).Ticks", source, StringComparison.Ordinal);
        Assert.Contains("ResolveWebRootFilePath", source, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, Path.Combine(relativeParts));
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate source file.", Path.Combine(relativeParts));
    }
}
