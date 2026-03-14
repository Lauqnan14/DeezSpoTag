using System;
using System.Collections.Generic;
using System.IO;

namespace DeezSpoTag.Web.Services;

internal static class PreferredExtensionComparer
{
    public static bool ShouldSkipForPreferredExtension(
        string sourcePath,
        string destinationPath,
        IReadOnlyList<string> preferredExtensions)
    {
        var existingExt = Path.GetExtension(destinationPath).Trim('.').ToLowerInvariant();
        var sourceExt = Path.GetExtension(sourcePath).Trim('.').ToLowerInvariant();
        var existingRank = GetExtensionRank(existingExt, preferredExtensions);
        var sourceRank = GetExtensionRank(sourceExt, preferredExtensions);
        return existingRank < sourceRank;
    }

    private static int GetExtensionRank(string extension, IReadOnlyList<string> preferredExtensions)
    {
        for (var i = 0; i < preferredExtensions.Count; i++)
        {
            if (string.Equals(preferredExtensions[i], extension, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}
