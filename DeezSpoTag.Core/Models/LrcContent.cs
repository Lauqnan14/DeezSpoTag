using System.Text.RegularExpressions;

namespace DeezSpoTag.Core.Models;

public static partial class LrcContent
{
    [GeneratedRegex(@"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\][^\r\n]*<\d{1,3}:\d{2}(?:[.:]\d{1,3})?>", RegexOptions.CultureInvariant)]
    private static partial Regex WordTimestampPattern();

    public static bool IsWordSynchronized(string? content)
        => !string.IsNullOrWhiteSpace(content) && WordTimestampPattern().IsMatch(content);

    public static bool IsWordSynchronized(IEnumerable<string>? lines)
        => lines != null && lines.Any(static line => IsWordSynchronized(line));
}
