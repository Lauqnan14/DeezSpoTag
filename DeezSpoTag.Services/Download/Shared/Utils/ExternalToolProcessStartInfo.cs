using System.Diagnostics;

namespace DeezSpoTag.Services.Download.Shared.Utils;

public static class ExternalToolProcessStartInfo
{
    public static ProcessStartInfo CreateRedirected(string fileName)
    {
        return new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }
}
