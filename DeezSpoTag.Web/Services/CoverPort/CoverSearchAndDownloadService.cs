using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace DeezSpoTag.Web.Services.CoverPort;

public sealed class CoverSearchAndDownloadService
{
    private readonly record struct SaveImageRequest(
        string TempPath,
        string SourceFormat,
        string TargetOutputFormat,
        string EffectiveOutputFormat,
        bool NeedResize,
        bool NeedFormatChange,
        CoverSearchOptions Options);

    private readonly IReadOnlyList<ICoverSource> _sources;
    private readonly CoverSourceHttpService _httpService;
    private readonly CoverPerceptualHashService _hashService;
    private readonly ILogger<CoverSearchAndDownloadService> _logger;

    public CoverSearchAndDownloadService(
        IEnumerable<ICoverSource> sources,
        CoverSourceHttpService httpService,
        CoverPerceptualHashService hashService,
        ILogger<CoverSearchAndDownloadService> logger)
    {
        _sources = sources.ToList();
        _httpService = httpService;
        _hashService = hashService;
        _logger = logger;
    }

    public async Task<CoverDownloadResult?> SearchAndDownloadAsync(
        CoverSearchQuery query,
        string outputPath,
        CoverSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new CoverSearchOptions();

        var selectedSources = ResolveSources(options.EnabledSources);
        if (selectedSources.Count == 0)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("No cover sources configured for query {Artist} - {Album}", query.Artist, query.Album);
            }
            return null;
        }

        var searchTasks = selectedSources
            .Select(source => SearchSourceSafeAsync(source, query, cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(searchTasks);
        var mergedCandidates = results
            .SelectMany(static items => items)
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate.Url))
            .GroupBy(static candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();

        if (mergedCandidates.Count == 0)
        {
            return null;
        }

        var minFiltered = mergedCandidates
            .Where(candidate => MatchesMinSize(candidate, options))
            .ToList();
        if (minFiltered.Count == 0)
        {
            return null;
        }

        var enrichedCandidates = await ApplySimilarityScoresAsync(minFiltered, options, outputPath, cancellationToken);
        var ranked = CoverRankingService.Rank(enrichedCandidates, options);
        var maxAttempts = Math.Clamp(options.MaxCandidatesToTry, 1, 200);
        foreach (var candidate in ranked.Take(maxAttempts))
        {
            var output = await TryDownloadAndSaveAsync(candidate, outputPath, options, cancellationToken);
            if (output != null)
            {
                return output;
            }
        }

        return null;
    }

    private List<ICoverSource> ResolveSources(IReadOnlyCollection<CoverSourceName>? configured)
    {
        if (configured == null || configured.Count == 0)
        {
            return _sources.ToList();
        }

        var selected = configured.ToHashSet();
        return _sources.Where(source => selected.Contains(source.Name)).ToList();
    }

    private async Task<IReadOnlyList<CoverCandidate>> SearchSourceSafeAsync(
        ICoverSource source,
        CoverSearchQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            return await source.SearchAsync(query, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Cover source {Source} failed for query {Artist} - {Album}", source.Name, query.Artist, query.Album);
            }
            return Array.Empty<CoverCandidate>();
        }
    }

    private async Task<IReadOnlyList<CoverCandidate>> ApplySimilarityScoresAsync(
        List<CoverCandidate> candidates,
        CoverSearchOptions options,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (!options.UsePerceptualHashScoring || candidates.Count == 0)
        {
            return candidates;
        }

        var referenceHash = await ResolveReferenceHashAsync(options, outputPath, cancellationToken);
        var effectiveReferenceHash = referenceHash;
        if (!effectiveReferenceHash.HasValue && options.ScoringMode == CoverScoringMode.SacadCompatibility)
        {
            effectiveReferenceHash = await ResolveReferenceHashFromCandidatesAsync(candidates, cancellationToken);
        }
        if (!effectiveReferenceHash.HasValue)
        {
            return candidates;
        }

        var enrichCandidates = options.ScoringMode == CoverScoringMode.SacadCompatibility
            ? candidates.ToList()
            : CoverRankingService
                .Rank(candidates, options)
                .Take(Math.Clamp(Math.Max(options.MaxCandidatesToTry * 3, 8), 8, 60))
                .Select(item => item.Candidate)
                .ToList();
        var similarityByUrl = new Dictionary<string, (double similarity, bool isSimilar)>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in enrichCandidates)
        {
            var bytes = await _httpService.GetImageBytesAsync(candidate.Source, candidate.Url, cancellationToken);
            if (bytes == null || bytes.Length == 0)
            {
                continue;
            }

            var candidateHash = _hashService.TryComputeHash(bytes);
            if (!candidateHash.HasValue)
            {
                continue;
            }

            var similarity = _hashService.Similarity(effectiveReferenceHash.Value, candidateHash.Value);
            similarityByUrl[candidate.Url] = (
                similarity,
                _hashService.IsSacadSimilar(effectiveReferenceHash.Value, candidateHash.Value));
        }

        if (similarityByUrl.Count == 0)
        {
            return candidates;
        }

        return candidates
            .Select(candidate => similarityByUrl.TryGetValue(candidate.Url, out var similarity)
                ? candidate with { SimilarityScore = similarity.similarity, IsSimilarToReference = similarity.isSimilar }
                : candidate)
            .ToList();
    }

    private async Task<ulong?> ResolveReferenceHashAsync(
        CoverSearchOptions options,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (options.ReferenceImageBytes != null && options.ReferenceImageBytes.Length > 0)
        {
            var hashFromBytes = _hashService.TryComputeHash(options.ReferenceImageBytes);
            if (hashFromBytes.HasValue)
            {
                return hashFromBytes;
            }
        }

        var referencePath = options.ReferenceImagePath;
        if (string.IsNullOrWhiteSpace(referencePath))
        {
            referencePath = outputPath;
        }

        if (string.IsNullOrWhiteSpace(referencePath) || !File.Exists(referencePath))
        {
            return null;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(referencePath, cancellationToken);
            return _hashService.TryComputeHash(bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<ulong?> ResolveReferenceHashFromCandidatesAsync(
        IReadOnlyList<CoverCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var referenceCandidates = CoverRankingService.SortReferenceCandidates(
            candidates.Where(candidate => (candidate.Relevance ?? new CoverRelevance(false, true, false)).IsReference));
        foreach (var candidate in referenceCandidates)
        {
            var bytes = await _httpService.GetImageBytesAsync(candidate.Source, candidate.Url, cancellationToken);
            if (bytes == null || bytes.Length == 0)
            {
                continue;
            }

            var hash = _hashService.TryComputeHash(bytes);
            if (hash.HasValue)
            {
                return hash;
            }
        }

        return null;
    }

    private async Task<CoverDownloadResult?> TryDownloadAndSaveAsync(
        RankedCoverCandidate rankedCandidate,
        string outputPath,
        CoverSearchOptions options,
        CancellationToken cancellationToken)
    {
        var candidate = rankedCandidate.Candidate;

        try
        {
            var sourceBytes = await _httpService.GetImageBytesAsync(candidate.Source, candidate.Url, cancellationToken);
            if (sourceBytes == null || sourceBytes.Length == 0)
            {
                return null;
            }

            await using var sourceStream = new MemoryStream(sourceBytes);
            using var memory = new MemoryStream();
            await sourceStream.CopyToAsync(memory, cancellationToken);
            memory.Position = 0;

            using var image = await Image.LoadAsync(memory, cancellationToken);
            var sourceFormat = NormalizeFormat(candidate.Format);
            var targetOutputFormat = ResolvePathOutputFormat(outputPath, options);
            var maxEdge = Math.Max(image.Width, image.Height);
            var needResize = !MatchesMaxSize(maxEdge, options);
            var needFormatChange = !string.Equals(sourceFormat, targetOutputFormat, StringComparison.OrdinalIgnoreCase) &&
                                   !options.PreserveSourceFormat;
            var (effectiveOutputPath, effectiveOutputFormat) = ResolveOutputTarget(
                outputPath,
                sourceFormat,
                targetOutputFormat,
                needResize,
                options.PreserveSourceFormat);
            var tempPath = effectiveOutputPath + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(effectiveOutputPath)!);

            if (needResize)
            {
                image.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(options.TargetSize, options.TargetSize)
                }));
            }

            await SaveImageAsync(
                image,
                sourceBytes,
                new SaveImageRequest(
                    TempPath: tempPath,
                    SourceFormat: sourceFormat,
                    TargetOutputFormat: targetOutputFormat,
                    EffectiveOutputFormat: effectiveOutputFormat,
                    NeedResize: needResize,
                    NeedFormatChange: needFormatChange,
                    Options: options),
                cancellationToken);

            File.Move(tempPath, effectiveOutputPath, overwrite: true);
            return new CoverDownloadResult(
                OutputPath: effectiveOutputPath,
                Candidate: candidate,
                Width: image.Width,
                Height: image.Height,
                Format: effectiveOutputFormat,
                Score: rankedCandidate.Score);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to download/convert cover from {Url}", candidate.Url);
            }
            return null;
        }
    }

    private static (string OutputPath, string OutputFormat) ResolveOutputTarget(
        string outputPath,
        string sourceFormat,
        string targetOutputFormat,
        bool needResize,
        bool preserveSourceFormat)
    {
        var shouldKeepSourceFormat = !needResize
                                     && !string.Equals(sourceFormat, targetOutputFormat, StringComparison.OrdinalIgnoreCase)
                                     && preserveSourceFormat;
        var effectiveOutputFormat = shouldKeepSourceFormat ? sourceFormat : targetOutputFormat;
        var effectiveOutputPath = NormalizeOutputPath(outputPath, effectiveOutputFormat);
        return (effectiveOutputPath, effectiveOutputFormat);
    }

    private static async Task SaveImageAsync(
        Image image,
        byte[] sourceBytes,
        SaveImageRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.NeedResize
            && !request.NeedFormatChange
            && string.Equals(request.SourceFormat, request.TargetOutputFormat, StringComparison.OrdinalIgnoreCase))
        {
            await File.WriteAllBytesAsync(request.TempPath, sourceBytes, cancellationToken);
            return;
        }

        if (string.Equals(request.EffectiveOutputFormat, "png", StringComparison.OrdinalIgnoreCase))
        {
            var pngEncoder = new PngEncoder
            {
                CompressionLevel = request.Options.CrunchPng ? PngCompressionLevel.BestCompression : PngCompressionLevel.DefaultCompression
            };
            await image.SaveAsPngAsync(request.TempPath, pngEncoder, cancellationToken);
            return;
        }

        await image.SaveAsJpegAsync(request.TempPath, new JpegEncoder { Quality = 92 }, cancellationToken);
    }

    private static bool MatchesMinSize(CoverCandidate candidate, CoverSearchOptions options)
    {
        var minEdge = Math.Min(candidate.Width, candidate.Height);
        if (minEdge <= 0)
        {
            return true;
        }

        var tolerance = Math.Max(0, options.SizeTolerancePercent);
        var minSize = options.TargetSize - (options.TargetSize * tolerance / 100);
        return minEdge >= minSize;
    }

    private static bool MatchesMaxSize(int size, CoverSearchOptions options)
    {
        if (size <= 0)
        {
            return true;
        }

        var tolerance = Math.Max(0, options.SizeTolerancePercent);
        var maxSize = options.TargetSize + (options.TargetSize * tolerance / 100);
        return size <= maxSize;
    }

    private static string ResolvePathOutputFormat(string outputPath, CoverSearchOptions options)
    {
        var extension = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "png" => "png",
            "jpeg" => "jpg",
            "jpg" => "jpg",
            _ => options.PreferPng ? "png" : "jpg"
        };
    }

    private static string NormalizeFormat(string? format)
    {
        var normalized = format?.Trim().TrimStart('.').ToLowerInvariant();
        return normalized switch
        {
            "png" => "png",
            "jpeg" => "jpg",
            "jpg" => "jpg",
            _ => "jpg"
        };
    }

    private static string NormalizeOutputPath(string outputPath, string outputFormat)
    {
        var currentExtension = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
        var targetExtension = outputFormat.ToLowerInvariant();
        if (string.Equals(currentExtension, targetExtension, StringComparison.OrdinalIgnoreCase))
        {
            return outputPath;
        }

        var directory = Path.GetDirectoryName(outputPath);
        var filename = Path.GetFileNameWithoutExtension(outputPath);
        var safeFilename = string.IsNullOrWhiteSpace(filename) ? "cover" : filename;
        var effectiveDirectory = string.IsNullOrWhiteSpace(directory) ? "." : directory;
        return Path.Join(effectiveDirectory, $"{safeFilename}.{targetExtension}");
    }
}
