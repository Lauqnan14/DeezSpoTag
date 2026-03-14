namespace DeezSpoTag.Core.Security;

public static class LogSanitizer
{
    public static string OneLine(string? value, int maxLength = 256)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim();

        if (sanitized.Length <= maxLength)
        {
            return sanitized;
        }

        return sanitized[..maxLength] + "...";
    }

    public static string MaskEmail(string? email)
    {
        var normalized = OneLine(email, 254);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "n/a";
        }

        var atIndex = normalized.IndexOf('@');
        if (atIndex <= 0 || atIndex == normalized.Length - 1)
        {
            return "***";
        }

        var local = normalized[..atIndex];
        var domain = normalized[(atIndex + 1)..];

        if (local.Length == 1)
        {
            return $"*@{domain}";
        }

        if (local.Length == 2)
        {
            return $"{local[0]}*@{domain}";
        }

        var middleMaskLength = Math.Min(local.Length - 2, 8);
        var maskedLocal = $"{local[0]}{new string('*', middleMaskLength)}{local[^1]}";
        return $"{maskedLocal}@{domain}";
    }
}
