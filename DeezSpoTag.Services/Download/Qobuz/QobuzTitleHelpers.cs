namespace DeezSpoTag.Services.Download.Qobuz;

internal static class QobuzTitleHelpers
{
    internal static string ExtractCoreTitle(string title)
    {
        var parenIdx = title.IndexOf('(');
        var bracketIdx = title.IndexOf('[');
        var dashIdx = title.IndexOf(" - ", StringComparison.Ordinal);
        var cutIdx = title.Length;

        if (parenIdx > 0 && parenIdx < cutIdx)
        {
            cutIdx = parenIdx;
        }

        if (bracketIdx > 0 && bracketIdx < cutIdx)
        {
            cutIdx = bracketIdx;
        }

        if (dashIdx > 0 && dashIdx < cutIdx)
        {
            cutIdx = dashIdx;
        }

        return title[..cutIdx].Trim();
    }
}
