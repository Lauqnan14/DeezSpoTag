namespace DeezSpoTag.Web.Services.Updates;

/// <summary>
/// Compares a running build version against a published release tag.
/// </summary>
/// <remarks>
/// Build versions are four-part (<c>major.minor.patch.revision</c>, from
/// <c>Directory.Build.props</c>) and release tags carry the same core with an optional
/// <c>-pre</c> suffix. The suffix is ignored: because a branch maps to exactly one channel, a
/// running build is only ever compared against releases from its own branch, so a
/// <c>-pre</c> marker on either side cannot describe a different channel.
/// </remarks>
public static class AppVersionComparison
{
    /// <summary>
    /// Returns the four-part core of <paramref name="value"/>, or <see langword="null"/> when it
    /// is not a recognisable version.
    /// </summary>
    public static (int Major, int Minor, int Patch, int Revision)? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim();
        if (candidate.Length > 0 && (candidate[0] == 'v' || candidate[0] == 'V'))
        {
            candidate = candidate[1..];
        }

        // Drop a pre-release / build-metadata suffix, but keep a dotted four-part core intact.
        var suffixIndex = candidate.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            candidate = candidate[..suffixIndex];
        }

        var parts = candidate.Split('.');
        if (parts.Length != 4)
        {
            return null;
        }

        return TryParseComponent(parts[0], out var major)
            && TryParseComponent(parts[1], out var minor)
            && TryParseComponent(parts[2], out var patch)
            && TryParseComponent(parts[3], out var revision)
                ? (major, minor, patch, revision)
                : null;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="candidate"/> is strictly newer than
    /// <paramref name="current"/>. Returns <see langword="false"/> when either side cannot be
    /// parsed, so an unrecognisable version never produces a false "update available".
    /// </summary>
    public static bool IsNewer(string? current, string? candidate)
    {
        var currentParts = TryParse(current);
        var candidateParts = TryParse(candidate);
        if (currentParts is null || candidateParts is null)
        {
            return false;
        }

        return Compare(candidateParts.Value, currentParts.Value) > 0;
    }

    private static int Compare(
        (int Major, int Minor, int Patch, int Revision) left,
        (int Major, int Minor, int Patch, int Revision) right)
    {
        var result = left.Major.CompareTo(right.Major);
        if (result != 0)
        {
            return result;
        }

        result = left.Minor.CompareTo(right.Minor);
        if (result != 0)
        {
            return result;
        }

        result = left.Patch.CompareTo(right.Patch);
        if (result != 0)
        {
            return result;
        }

        return left.Revision.CompareTo(right.Revision);
    }

    private static bool TryParseComponent(string part, out int value)
        => int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value)
           && value >= 0;
}
