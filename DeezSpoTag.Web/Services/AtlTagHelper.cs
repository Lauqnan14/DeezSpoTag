using ATL;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services;

internal sealed class AtlChapterSnapshot
{
    public List<ChapterInfo> Chapters { get; init; } = new();
    public string? TableDescription { get; init; }
    public string? CueSheet { get; init; }

    public bool HasData => Chapters.Count > 0
        || !string.IsNullOrWhiteSpace(TableDescription)
        || !string.IsNullOrWhiteSpace(CueSheet);
}

internal static class AtlTagHelper
{
    private static readonly string[] Mp4FamilyExtensions = { ".m4a", ".mp4", ".m4b" };
    private const string ChapterTagKey = "CHAPTERS";
    private const string ChapterDescriptionKey = "CHAPTERS_DESC";
    private const string CueSheetKey = "CUESHEET";

    public static bool IsMp4Family(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return Mp4FamilyExtensions.Any(ext => extension.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    public static AtlChapterSnapshot? CaptureChapters(string path, string extension, ILogger? logger = null)
    {
        if (!IsMp4Family(extension))
        {
            return null;
        }

        try
        {
            var track = new Track(path);
            var chapters = track.Chapters ?? new List<ChapterInfo>();
            var cueSheet = ExtractCueSheet(track.AdditionalFields);
            var description = string.IsNullOrWhiteSpace(track.ChaptersTableDescription)
                ? null
                : track.ChaptersTableDescription;

            if (chapters.Count == 0 && string.IsNullOrWhiteSpace(cueSheet) && string.IsNullOrWhiteSpace(description))
            {
                return null;
            }

            return new AtlChapterSnapshot
            {
                Chapters = CloneChapters(chapters),
                TableDescription = description,
                CueSheet = cueSheet
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogDebug(ex, "ATL failed to capture chapters for {Path}", path);
            return null;
        }
    }

    public static void RestoreChapters(string path, AtlChapterSnapshot? snapshot, ILogger? logger = null)
    {
        if (snapshot == null || !snapshot.HasData)
        {
            return;
        }

        try
        {
            var track = new Track(path);

            if (snapshot.Chapters.Count > 0)
            {
                track.Chapters = CloneChapters(snapshot.Chapters);
            }

            if (!string.IsNullOrWhiteSpace(snapshot.TableDescription))
            {
                track.ChaptersTableDescription = snapshot.TableDescription;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.CueSheet))
            {
                var fields = track.AdditionalFields ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                fields[CueSheetKey] = snapshot.CueSheet;
                track.AdditionalFields = fields;
            }

            track.Save();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogDebug(ex, "ATL failed to restore chapters for {Path}", path);
        }
    }

    public static void AppendChapterTags(
        Dictionary<string, List<string>> tags,
        string path,
        string extension,
        ILogger? logger = null)
    {
        if (!IsMp4Family(extension))
        {
            return;
        }

        var snapshot = CaptureChapters(path, extension, logger);
        if (snapshot == null)
        {
            return;
        }

        if (snapshot.Chapters.Count > 0)
        {
            var values = new List<string>(snapshot.Chapters.Count);
            for (var i = 0; i < snapshot.Chapters.Count; i++)
            {
                values.Add(FormatChapter(snapshot.Chapters[i], i));
            }

            if (values.Count > 0 && !tags.ContainsKey(ChapterTagKey))
            {
                tags[ChapterTagKey] = values;
            }
        }

        if (!string.IsNullOrWhiteSpace(snapshot.TableDescription) && !tags.ContainsKey(ChapterDescriptionKey))
        {
            tags[ChapterDescriptionKey] = new List<string> { snapshot.TableDescription };
        }

        if (!string.IsNullOrWhiteSpace(snapshot.CueSheet) && !tags.ContainsKey(CueSheetKey))
        {
            tags[CueSheetKey] = new List<string> { snapshot.CueSheet };
        }
    }

    private static List<ChapterInfo> CloneChapters(IList<ChapterInfo> chapters)
    {
        var cloned = new List<ChapterInfo>(chapters.Count);
        foreach (var chapter in chapters)
        {
            cloned.Add(new ChapterInfo(chapter));
        }

        return cloned;
    }

    private static string? ExtractCueSheet(IDictionary<string, string>? fields)
    {
        if (fields == null || fields.Count == 0)
        {
            return null;
        }

        return fields
            .FirstOrDefault(pair => pair.Key.Equals(CueSheetKey, StringComparison.OrdinalIgnoreCase))
            .Value;
    }

    private static string FormatChapter(ChapterInfo chapter, int index)
    {
        var start = FormatTime(chapter.StartTime);
        var end = chapter.EndTime > 0 ? FormatTime(chapter.EndTime) : "";
        var title = string.IsNullOrWhiteSpace(chapter.Title)
            ? $"Chapter {index + 1}"
            : chapter.Title.Trim();

        if (string.IsNullOrWhiteSpace(end))
        {
            return $"{start} | {title}";
        }

        return $"{start} - {end} | {title}";
    }

    private static string FormatTime(uint milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(milliseconds);
        return span.ToString(@"hh\:mm\:ss\.fff");
    }
}
