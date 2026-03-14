using System;
using System.IO;
using System.Linq;

namespace DeezSpoTag.Services.Download.Utils;

public static class DownloadPathResolver
{
    private const string SmbScheme = "smb://";
    private const string GvfsSegment = "/gvfs/smb-share:server=";

    public static string ResolveIoPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsSmbPath(path))
        {
            return path;
        }

        if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, "smb", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var share = uri.Segments.Length > 1 ? uri.Segments[1].Trim('/') : string.Empty;
        if (string.IsNullOrWhiteSpace(share))
        {
            return path;
        }

        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtimeDir))
        {
            return path;
        }

        var gvfsRoot = Path.Join(runtimeDir, "gvfs");
        if (!Directory.Exists(gvfsRoot))
        {
            return path;
        }

        var basePath = Path.Join(gvfsRoot, $"smb-share:server={uri.Host},share={share}");
        if (!Directory.Exists(basePath))
        {
            return path;
        }

        var subPath = uri.AbsolutePath.Length > share.Length + 1
            ? uri.AbsolutePath[(share.Length + 1)..].TrimStart('/')
            : string.Empty;

        return string.IsNullOrWhiteSpace(subPath)
            ? basePath
            : Path.Join(basePath, Uri.UnescapeDataString(subPath));
    }

    public static string Combine(string basePath, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            if (segments == null || segments.Length == 0)
            {
                return string.Empty;
            }

            return Path.Join(segments);
        }

        if (!IsSmbPath(basePath))
        {
            if (segments == null || segments.Length == 0)
            {
                return basePath;
            }

            return Path.Join(new[] { basePath }.Concat(segments).ToArray());
        }

        var result = basePath.TrimEnd('/');
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            result = $"{result}/{segment.TrimStart('/')}";
        }

        return result;
    }

    public static string NormalizeDisplayPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var normalized = path.Replace('\\', '/');
        var idx = normalized.IndexOf(GvfsSegment, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return path;
        }

        var rest = normalized[(idx + GvfsSegment.Length)..];
        var parts = rest.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return path;
        }

        var serverShare = parts[0];
        var serverSplit = serverShare.Split(",share=", StringSplitOptions.RemoveEmptyEntries);
        if (serverSplit.Length != 2)
        {
            return path;
        }

        var server = serverSplit[0];
        var share = serverSplit[1];
        var suffix = parts.Length > 1 ? "/" + parts[1] : string.Empty;
        return $"{SmbScheme}{server}/{share}{suffix}";
    }

    public static bool IsSmbPath(string path)
    {
        return path.StartsWith(SmbScheme, StringComparison.OrdinalIgnoreCase);
    }
}
