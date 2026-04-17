using System.IO;

namespace DeezSpoTag.Services.Download.Utils;

public static class FileMoveFallbackHelper
{
    public static void MoveWithFallback(string sourcePath, string destinationPath)
    {
        var allowCopyFallback = ShouldUseCopyFallback(sourcePath, destinationPath);
        const int maxMoveAttempts = 6;
        for (var attempt = 1; attempt <= maxMoveAttempts; attempt++)
        {
            try
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < maxMoveAttempts)
            {
                System.Threading.Thread.Sleep(50 * attempt);
            }
            catch (IOException) when (allowCopyFallback)
            {
                MoveByCopyWithDeleteGuard(sourcePath, destinationPath);
                return;
            }
        }

        File.Move(sourcePath, destinationPath, overwrite: true);
    }

    public static bool ShouldUseCopyFallback(string sourcePath, string destinationPath)
    {
        var sourceIo = DownloadPathResolver.ResolveIoPath(sourcePath);
        var destinationIo = DownloadPathResolver.ResolveIoPath(destinationPath);
        if (string.IsNullOrWhiteSpace(sourceIo) || string.IsNullOrWhiteSpace(destinationIo))
        {
            return false;
        }

        if (DownloadPathResolver.IsSmbPath(sourceIo) || DownloadPathResolver.IsSmbPath(destinationIo))
        {
            return true;
        }

        try
        {
            var sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourceIo));
            var destinationRoot = Path.GetPathRoot(Path.GetFullPath(destinationIo));
            if (string.IsNullOrWhiteSpace(sourceRoot) || string.IsNullOrWhiteSpace(destinationRoot))
            {
                return false;
            }

            return !string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void MoveByCopyWithDeleteGuard(string sourcePath, string destinationPath)
    {
        File.Copy(sourcePath, destinationPath, overwrite: true);
        if (TryDeleteWithRetries(sourcePath))
        {
            return;
        }

        TryDeleteSilently(destinationPath);
        throw new IOException($"Move fallback copied file but could not delete source: {sourcePath}");
    }

    private static bool TryDeleteWithRetries(string path)
    {
        const int maxDeleteAttempts = 12;
        for (var attempt = 1; attempt <= maxDeleteAttempts; attempt++)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (IOException) when (attempt < maxDeleteAttempts)
            {
                System.Threading.Thread.Sleep(50 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < maxDeleteAttempts)
            {
                System.Threading.Thread.Sleep(50 * attempt);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return !File.Exists(path);
    }

    private static void TryDeleteSilently(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup only.
        }
    }
}
