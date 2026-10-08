using DeezSpoTag.Services.Utils;

namespace DeezSpoTag.Web.Services.Audiomack;

/// <summary>
/// Resolves the destination for the opt-in Audiomack page-payload dump
/// (<c>AUDIOMACK_DEBUG_PAYLOAD=1</c>). The dump contains scraped markup, so it
/// is written under the application's own data root rather than a shared
/// world-writable temporary directory, and the directory is created if needed.
/// </summary>
internal static class AudiomackDebugPayload
{
    private const string DebugDirectoryName = "debug";
    private const string DebugFileName = "audiomack-nextdata.json";

    /// <summary>
    /// Returns the dump path when <c>AUDIOMACK_DEBUG_PAYLOAD=1</c>, otherwise null.
    /// </summary>
    public static string? ResolveOutputPath()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("AUDIOMACK_DEBUG_PAYLOAD"),
                "1",
                StringComparison.Ordinal))
        {
            return null;
        }

        var dataRoot = AppDataPathResolver.ResolveDataRootOrDefault(AppContext.BaseDirectory);
        var debugDirectory = Path.Join(dataRoot, DebugDirectoryName);
        Directory.CreateDirectory(debugDirectory);
        return Path.Join(debugDirectory, DebugFileName);
    }
}
