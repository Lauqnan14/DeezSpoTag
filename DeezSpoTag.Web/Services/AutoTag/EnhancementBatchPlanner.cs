namespace DeezSpoTag.Web.Services.AutoTag;

internal static class EnhancementBatchPlanner
{
    public static List<(int Start, int End)> BuildRanges(
        IReadOnlyList<string> orderedFiles,
        int fileCount,
        int batchSize)
    {
        var ranges = new List<(int Start, int End)>();
        if (orderedFiles.Count == 0 || fileCount <= 0)
        {
            return ranges;
        }

        var limit = Math.Min(fileCount, orderedFiles.Count);
        var resolvedBatchSize = Math.Max(1, batchSize);
        for (var start = 0; start < limit;)
        {
            var end = start + 1;
            while (end < limit
                   && (end - start < resolvedBatchSize
                       || SameAlbumDirectory(orderedFiles[end - 1], orderedFiles[end])))
            {
                end++;
            }

            ranges.Add((start, end));
            start = end;
        }

        return ranges;
    }

    private static bool SameAlbumDirectory(string first, string second)
        => string.Equals(
            Path.GetDirectoryName(Path.GetFullPath(first)),
            Path.GetDirectoryName(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);
}
