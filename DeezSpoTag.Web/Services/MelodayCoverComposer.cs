using System.Security.Cryptography;
using System.Text;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DeezSpoTag.Web.Services;

public sealed record MelodayCoverResult(string FilePath, string? Url);

/// <summary>
/// Renders the generated cover for one playlist instance: the assigned source image plus
/// the MELODAY / daypart / tagline overlay. The user's source image is never modified;
/// derivatives land in images/meloday/generated and are content-addressed so repeated
/// generations with unchanged inputs reuse the same file.
/// </summary>
public sealed class MelodayCoverComposer
{
    private const int CoverSize = 1000;
    private const int MaxTextWidth = 900;
    private const int HeaderBaseline = 640;
    private const int SlotBaseline = 736;
    private const int TaglineBaseline = 852;

    private readonly string _fontPath;
    private readonly string _generatedDirectory;
    private readonly ILogger<MelodayCoverComposer> _logger;

    public MelodayCoverComposer(IWebHostEnvironment env, ILogger<MelodayCoverComposer> logger)
    {
        _logger = logger;
        _generatedDirectory = Path.Join(env.WebRootPath, "images", "meloday", "generated");
        _fontPath = Path.Join(env.WebRootPath, "fonts", "onetagger", "Dosis-Bold.ttf");
    }

    public string? TryResolveExistingCoverWebPath(string? mixId)
    {
        var prefix = Path.GetFileName((mixId ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(prefix)
            || !prefix.StartsWith("meloday-", StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(_generatedDirectory))
        {
            return null;
        }

        try
        {
            var match = Directory.EnumerateFiles(_generatedDirectory, $"{prefix}-*.jpg")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(match))
            {
                return null;
            }

            return $"/images/meloday/generated/{Uri.EscapeDataString(Path.GetFileName(match))}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to resolve existing Meloday cover for {MixId}.", mixId);
            return null;
        }
    }

    public MelodayCoverResult? Compose(
        string sourcePath,
        string slotName,
        string tagline,
        long libraryId,
        string slotId,
        string mode,
        string weekday,
        string? baseUrl)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                _logger.LogWarning("Meloday artwork source image missing: {Path}.", sourcePath);
                return null;
            }

            if (!File.Exists(_fontPath))
            {
                _logger.LogWarning("Meloday cover font missing at {Path}.", _fontPath);
                return null;
            }

            Directory.CreateDirectory(_generatedDirectory);
            var heading = CoverHeading(slotName, weekday);
            var (fileName, _) = ResolveOutputFileName(sourcePath, heading, tagline, libraryId, slotId, mode, weekday);
            var outputPath = Path.Join(_generatedDirectory, fileName);
            var url = BuildUrl(baseUrl, fileName);
            if (File.Exists(outputPath))
            {
                return new MelodayCoverResult(outputPath, url);
            }

            using (var image = Image.Load<Rgba32>(sourcePath))
            {
                image.Mutate(context => context.Resize(new ResizeOptions
                {
                    Size = new Size(CoverSize, CoverSize),
                    Mode = ResizeMode.Crop
                }));
                using (var overlay = new Image<Rgba32>(CoverSize, CoverSize, Color.Transparent))
                {
                    overlay.Mutate(context => context.Fill(BuildScrimBrush()));
                    image.Mutate(context => context.DrawImage(
                        overlay,
                        new GraphicsOptions
                        {
                            AlphaCompositionMode = PixelAlphaCompositionMode.SrcOver,
                            ColorBlendingMode = PixelColorBlendingMode.Normal
                        }));
                }

                var fontCollection = new FontCollection();
                var family = fontCollection.Add(_fontPath);
                var headerFont = family.CreateFont(38, FontStyle.Bold);
                var slotFont = FitFont(family, heading.ToUpperInvariant(), 96);
                var taglineFont = FitFont(family, tagline, 48);

                DrawCentered(image, "M E L O D A Y", headerFont, HeaderBaseline);
                DrawCentered(image, heading.ToUpperInvariant(), slotFont, SlotBaseline);
                if (!string.IsNullOrWhiteSpace(tagline))
                {
                    DrawCentered(image, tagline, taglineFont, TaglineBaseline);
                }

                image.SaveAsJpeg(outputPath, new JpegEncoder { Quality = 90 });
            }

            PruneStaleGenerations(libraryId, slotId, mode, weekday, outputPath);
            return new MelodayCoverResult(outputPath, url);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to compose Meloday cover for library {LibraryId} slot {SlotId}.", libraryId, slotId);
            return null;
        }
    }

    private static string CoverHeading(string slotName, string weekday)
    {
        var day = MelodayScheduleSlots.WeekdayDisplayName(weekday);
        var slot = string.IsNullOrWhiteSpace(slotName) ? "Meloday" : slotName.Trim();
        return day.Length == 0 ? slot : $"{day} {slot}";
    }

    private static (string FileName, string ContentHash) ResolveOutputFileName(
        string sourcePath,
        string heading,
        string tagline,
        long libraryId,
        string slotId,
        string mode,
        string weekday)
    {
        var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath)));
        var content = $"{sourceHash}|MELODAY|{heading}|{tagline}|scrim-v2";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var weekdayId = MelodayScheduleSlots.NormalizeWeekdayId(weekday);
        var fileName = $"meloday-{libraryId}-{MelodayScheduleSlots.NormalizeSlotId(slotId)}-{MelodayModes.Normalize(mode)}-{weekdayId}-{hash[..8].ToLowerInvariant()}.jpg";
        return (fileName, hash);
    }

    private static LinearGradientBrush BuildScrimBrush()
        => new(
            new Point(0, (int)(CoverSize * 0.5)),
            new Point(0, CoverSize),
            GradientRepetitionMode.None,
            new ColorStop(0f, Color.Black.WithAlpha(0f)),
            new ColorStop(0.72f, Color.Black.WithAlpha(0.62f)),
            new ColorStop(1f, Color.Black.WithAlpha(0.86f)));

    private static Font FitFont(FontFamily family, string text, int baseSize)
    {
        var size = baseSize;
        while (size > 24)
        {
            var font = family.CreateFont(size, FontStyle.Bold);
            var measured = TextMeasurer.MeasureSize(text, new TextOptions(font));
            if (measured.Width <= MaxTextWidth)
            {
                return font;
            }

            size -= 4;
        }

        return family.CreateFont(24, FontStyle.Bold);
    }

    private static void DrawCentered(Image image, string text, Font font, int baseline)
    {
        var measured = TextMeasurer.MeasureSize(text, new TextOptions(font));
        var x = (CoverSize - measured.Width) / 2f;
        var location = new PointF(x, baseline);
        image.Mutate(context =>
        {
            context.DrawText(text, font, Color.Black.WithAlpha(0.55f), new PointF(x + 3, baseline + 3));
            context.DrawText(text, font, Color.White, location);
        });
    }

    private static string? BuildUrl(string? baseUrl, string fileName)
    {
        var trimmed = baseUrl?.TrimEnd('/');
        return string.IsNullOrWhiteSpace(trimmed)
            ? null
            : $"{trimmed}/images/meloday/generated/{Uri.EscapeDataString(fileName)}";
    }

    private void PruneStaleGenerations(long libraryId, string slotId, string mode, string weekday, string keepPath)
    {
        var prefix = $"meloday-{libraryId}-{MelodayScheduleSlots.NormalizeSlotId(slotId)}-{MelodayModes.Normalize(mode)}-{MelodayScheduleSlots.NormalizeWeekdayId(weekday)}-";
        try
        {
            foreach (var stale in Directory.EnumerateFiles(_generatedDirectory, $"{prefix}*.jpg"))
            {
                if (!string.Equals(Path.GetFullPath(stale), Path.GetFullPath(keepPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(stale);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to prune stale Meloday generated covers.");
        }
    }
}
