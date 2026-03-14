namespace DeezSpoTag.Web.Services.AutoTag;

internal static class AutoTagTagValueReader
{
    public static string? ReadFirstTagValue(AutoTagAudioInfo info, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!info.Tags.TryGetValue(key, out var values) || values.Count == 0)
            {
                continue;
            }

            var value = values
                .Select(v => v?.Replace("\0", string.Empty, StringComparison.Ordinal).Trim())
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
