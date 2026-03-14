using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Web.Services;

public sealed class DownloadVerificationService
{
    private readonly ShazamRecognitionService _shazamRecognitionService;
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private DateTime _lastShazamCall = DateTime.MinValue;

    public DownloadVerificationService(ILogger<DownloadVerificationService> logger, ShazamRecognitionService shazamRecognitionService)
    {
        _ = logger;
        _shazamRecognitionService = shazamRecognitionService;
    }

    public async Task<VerificationResult> VerifyUpgradeAsync(
        string originalFilePath,
        string newFilePath,
        VerificationSettings settings,
        CancellationToken cancellationToken = default)
    {
        var result = new VerificationResult
        {
            OriginalPath = originalFilePath,
            NewPath = newFilePath,
            Timestamp = DateTimeOffset.UtcNow
        };

        var originalDuration = await GetDurationAsync(originalFilePath);
        var newDuration = await GetDurationAsync(newFilePath);

        if (Math.Abs(originalDuration - newDuration) > settings.MaxDurationDifferenceSeconds)
        {
            result.Status = VerificationStatus.DurationMismatch;
            result.Message = $"Duration differs by {Math.Abs(originalDuration - newDuration)}s";
            return result;
        }

        if (settings.IsrcFirstStrategy)
        {
            var originalIsrc = await GetIsrcFromTagsAsync(originalFilePath);
            var newIsrc = await GetIsrcFromTagsAsync(newFilePath);

            if (!string.IsNullOrEmpty(originalIsrc) && !string.IsNullOrEmpty(newIsrc))
            {
                if (originalIsrc.Equals(newIsrc, StringComparison.OrdinalIgnoreCase))
                {
                    result.Status = VerificationStatus.Verified;
                    result.Method = VerificationMethod.IsrcMatch;
                    return result;
                }
                if (settings.Strictness == VerificationStrictness.Strict)
                {
                    result.Status = VerificationStatus.IsrcMismatch;
                    result.Message = $"ISRC mismatch: {originalIsrc} vs {newIsrc}";
                    return result;
                }
            }
        }

        if (settings.EnableShazamVerification)
        {
            return await ShazamVerifyAsync(originalFilePath, newFilePath, settings, result, cancellationToken);
        }

        result.Status = VerificationStatus.Skipped;
        result.Message = "No verification method available";
        return result;
    }

    private async Task<VerificationResult> ShazamVerifyAsync(
        string originalPath,
        string newPath,
        VerificationSettings settings,
        VerificationResult result,
        CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitAsync(cancellationToken);
        try
        {
            var elapsed = DateTime.UtcNow - _lastShazamCall;
            if (elapsed.TotalSeconds < settings.RateLimitSeconds)
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.RateLimitSeconds) - elapsed, cancellationToken);
            }

            var originalResult = await ShazamRecognizeAsync(originalPath, cancellationToken);
            _lastShazamCall = DateTime.UtcNow;

            if (originalResult is null)
            {
                result.Status = VerificationStatus.ShazamFailed;
                result.Message = "Could not identify original file via Shazam";
                return result;
            }

            await Task.Delay(TimeSpan.FromSeconds(settings.RateLimitSeconds), cancellationToken);

            var newResult = await ShazamRecognizeAsync(newPath, cancellationToken);
            _lastShazamCall = DateTime.UtcNow;

            if (newResult is null)
            {
                result.Status = VerificationStatus.ShazamFailed;
                result.Message = "Could not identify new file via Shazam";
                return result;
            }

            result.Method = VerificationMethod.ShazamFingerprint;
            result.OriginalShazamTitle = originalResult.Title;
            result.OriginalShazamArtist = originalResult.Artist;
            result.NewShazamTitle = newResult.Title;
            result.NewShazamArtist = newResult.Artist;

            if (!string.IsNullOrEmpty(originalResult.Isrc)
                && !string.IsNullOrEmpty(newResult.Isrc)
                && originalResult.Isrc.Equals(newResult.Isrc, StringComparison.OrdinalIgnoreCase))
            {
                result.Status = VerificationStatus.Verified;
                result.Confidence = 1.0;
                return result;
            }

            var titleSimilarity = CalculateSimilarity(originalResult.Title, newResult.Title);
            var artistSimilarity = CalculateSimilarity(originalResult.Artist, newResult.Artist);
            var combinedSimilarity = (titleSimilarity + artistSimilarity) / 2;

            result.Confidence = combinedSimilarity;

            var threshold = settings.Strictness switch
            {
                VerificationStrictness.Relaxed => 0.80,
                VerificationStrictness.Normal => 0.90,
                VerificationStrictness.Strict => 0.95,
                _ => 0.90
            };

            if (combinedSimilarity >= threshold)
            {
                result.Status = VerificationStatus.Verified;
            }
            else
            {
                result.Status = VerificationStatus.ContentMismatch;
                result.Message = $"Similarity {combinedSimilarity:P0} below threshold {threshold:P0}";
            }

            return result;
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    private static async Task<int> GetDurationAsync(string filePath)
    {
        return await Task.Run(() =>
        {
            using var file = TagLib.File.Create(filePath);
            return (int)Math.Round(file.Properties.Duration.TotalSeconds);
        });
    }

    private static async Task<string?> GetIsrcFromTagsAsync(string filePath)
    {
        return await Task.Run(() =>
        {
            using var file = TagLib.File.Create(filePath);
            return file.Tag.ISRC;
        });
    }

    private async Task<ShazamTrack?> ShazamRecognizeAsync(string filePath, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            var result = _shazamRecognitionService.Recognize(filePath, cancellationToken);
            if (result == null)
            {
                return null;
            }

            var artist = result.Artists.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))
                         ?? result.Artist
                         ?? string.Empty;
            if (string.IsNullOrWhiteSpace(result.Title) || string.IsNullOrWhiteSpace(artist))
            {
                return null;
            }

            return new ShazamTrack(result.Title, artist, result.Isrc);
        }, cancellationToken);
    }

    private static double CalculateSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        a = NormalizeForComparison(a);
        b = NormalizeForComparison(b);
        if (a == b) return 1.0;
        var distance = ShazamSharedParsing.LevenshteinDistance(a, b);
        var maxLen = Math.Max(a.Length, b.Length);
        return 1.0 - ((double)distance / maxLen);
    }

    private static string NormalizeForComparison(string s)
    {
        return s.ToLowerInvariant()
            .Replace(" feat. ", " ")
            .Replace(" feat ", " ")
            .Replace(" ft. ", " ")
            .Replace(" ft ", " ")
            .Replace(" & ", " ")
            .Replace(" and ", " ")
            .Trim();
    }

    private sealed record ShazamTrack(string Title, string Artist, string? Isrc);
}

public sealed class VerificationResult
{
    public string OriginalPath { get; set; } = "";
    public string NewPath { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public VerificationStatus Status { get; set; }
    public VerificationMethod Method { get; set; }
    public string? Message { get; set; }
    public double? Confidence { get; set; }

    public string? OriginalShazamTitle { get; set; }
    public string? OriginalShazamArtist { get; set; }
    public string? NewShazamTitle { get; set; }
    public string? NewShazamArtist { get; set; }
}

public enum VerificationStatus
{
    Verified,
    DurationMismatch,
    IsrcMismatch,
    ContentMismatch,
    ShazamFailed,
    Skipped
}

public enum VerificationMethod
{
    None,
    IsrcMatch,
    ShazamFingerprint,
    DurationOnly
}
