namespace DeezSpoTag.Services.Download.Shared.Utils;

public static class Base64UrlDecoder
{
    public static byte[]? TryDecode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                var normalized = value.Replace('-', '+').Replace('_', '/');
                var mod = normalized.Length % 4;
                if (mod > 0)
                {
                    normalized = normalized.PadRight(normalized.Length + (4 - mod), '=');
                }

                return Convert.FromBase64String(normalized);
            }
            catch (Exception innerEx) when (innerEx is not OperationCanceledException)
            {
                return null;
            }
        }
    }
}
