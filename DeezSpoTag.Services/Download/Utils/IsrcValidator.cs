using System.Text.RegularExpressions;

namespace DeezSpoTag.Services.Download.Utils;

public static class IsrcValidator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex IsrcRegex = new(
        "^[A-Z]{2}[A-Z0-9]{3}\\d{2}\\d{5}$",
        RegexOptions.Compiled,
        RegexTimeout);

    public static bool IsValid(string? isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return false;
        }

        return IsrcRegex.IsMatch(isrc.Trim().ToUpperInvariant());
    }
}
