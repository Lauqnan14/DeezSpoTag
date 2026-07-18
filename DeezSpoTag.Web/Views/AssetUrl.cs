using System.Globalization;
using System.Threading;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;

namespace DeezSpoTag.Web.Views;

public static class AssetUrl
{
    private static int _fileVersionProviderUnavailable;

    public static string Content(IUrlHelper url, string path) => url.Content(path);

    public static string Versioned(ViewContext viewContext, IUrlHelper url, string path)
    {
        var contentPath = url.Content(path);
        if (Volatile.Read(ref _fileVersionProviderUnavailable) == 0)
        {
            try
            {
                var fileVersionProvider = viewContext.HttpContext.RequestServices.GetRequiredService<IFileVersionProvider>();
                return fileVersionProvider.AddFileVersionToPath(viewContext.HttpContext.Request.PathBase, contentPath);
            }
            catch (IOException)
            {
                Interlocked.Exchange(ref _fileVersionProviderUnavailable, 1);
            }
        }

        return AddFallbackFileVersion(viewContext, contentPath, path);
    }

    private static string AddFallbackFileVersion(ViewContext viewContext, string contentPath, string path)
    {
        if (string.IsNullOrWhiteSpace(contentPath) || contentPath.Contains('?'))
        {
            return contentPath;
        }

        var environment = viewContext.HttpContext.RequestServices.GetService<IWebHostEnvironment>();
        var webRoot = environment?.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            return contentPath;
        }

        var filePath = ResolveWebRootFilePath(webRoot, path);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return contentPath;
        }

        var version = File.GetLastWriteTimeUtc(filePath).Ticks.ToString("x", CultureInfo.InvariantCulture);
        return $"{contentPath}?v={Uri.EscapeDataString(version)}";
    }

    private static string? ResolveWebRootFilePath(string webRoot, string path)
    {
        var relativePath = (path ?? string.Empty).Trim();
        if (relativePath.StartsWith("~/", StringComparison.Ordinal))
        {
            relativePath = relativePath[2..];
        }

        relativePath = relativePath.TrimStart('/', '\\');
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var root = Path.GetFullPath(webRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }
}
