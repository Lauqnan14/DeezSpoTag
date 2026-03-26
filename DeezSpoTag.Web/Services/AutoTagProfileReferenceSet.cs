namespace DeezSpoTag.Web.Services;

internal static class AutoTagProfileReferenceSet
{
    public static HashSet<string> Build(string? profileId, string? profileName)
    {
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(references, profileId);
        Add(references, profileName);
        return references;
    }

    private static void Add(HashSet<string> references, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            references.Add(value.Trim());
        }
    }
}
