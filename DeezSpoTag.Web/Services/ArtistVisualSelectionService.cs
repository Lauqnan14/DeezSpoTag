using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class ArtistVisualSelectionService
{
    private const string LibraryArtistImagesPath = "library-artist-images";
    private const string SelectedSource = "selected";

    private readonly LibraryRepository _libraryRepository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<ArtistVisualSelectionService> _logger;

    public ArtistVisualSelectionService(
        LibraryRepository libraryRepository,
        IHttpClientFactory httpClientFactory,
        IWebHostEnvironment environment,
        ILogger<ArtistVisualSelectionService> logger)
    {
        _libraryRepository = libraryRepository;
        _httpClientFactory = httpClientFactory;
        _environment = environment;
        _logger = logger;
    }

    public async Task<ArtistVisualSelectionResult> SaveAsync(
        long artistId,
        ArtistVisualSelectionRequest request,
        CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return ArtistVisualSelectionResult.BadRequest("Library DB not configured.");
        }

        var cacheRoot = Path.GetFullPath(Path.Join(AppDataPaths.GetDataRoot(_environment), LibraryArtistImagesPath));
        var avatarVisual = ResolveVisualSelection(cacheRoot, request.AvatarImagePath, request.AvatarVisualUrl);
        var backgroundVisual = ResolveVisualSelection(cacheRoot, request.BackgroundImagePath, request.BackgroundVisualUrl);

        if (avatarVisual is null && backgroundVisual is null)
        {
            return ArtistVisualSelectionResult.BadRequest("Set artist avatar or background first.");
        }

        var artist = await _libraryRepository.GetArtistAsync(artistId, cancellationToken);
        if (artist is null || string.IsNullOrWhiteSpace(artist.Name))
        {
            return ArtistVisualSelectionResult.NotFound("Artist not found.");
        }

        var warnings = new List<string>();
        var avatarMaterialized = await EnsureVisualStoredAsync(artistId, "avatar", avatarVisual, cancellationToken);
        avatarVisual = avatarMaterialized.Selection;
        if (!string.IsNullOrWhiteSpace(avatarMaterialized.Warning))
        {
            warnings.Add(avatarMaterialized.Warning);
        }

        var backgroundMaterialized = await EnsureVisualStoredAsync(artistId, "background", backgroundVisual, cancellationToken);
        backgroundVisual = backgroundMaterialized.Selection;
        if (!string.IsNullOrWhiteSpace(backgroundMaterialized.Warning))
        {
            warnings.Add(backgroundMaterialized.Warning);
        }

        if (!string.IsNullOrWhiteSpace(avatarVisual?.LocalPath))
        {
            await _libraryRepository.UpdateArtistImagePathAsync(artistId, avatarVisual.LocalPath!, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(backgroundVisual?.LocalPath))
        {
            await _libraryRepository.UpdateArtistBackgroundPathAsync(artistId, backgroundVisual.LocalPath!, cancellationToken);
        }

        return ArtistVisualSelectionResult.Ok(avatarVisual?.LocalPath, backgroundVisual?.LocalPath, warnings);
    }

    private static ResolvedArtistVisualSelection? ResolveVisualSelection(
        string cacheRoot,
        string? explicitLocalPath,
        string? visualUrl)
    {
        var localPath = TryResolveLocalVisualPath(cacheRoot, explicitLocalPath, visualUrl);
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            return new ResolvedArtistVisualSelection(localPath, null);
        }

        var urlValue = (visualUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(urlValue))
        {
            return null;
        }

        if (Uri.TryCreate(urlValue, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return new ResolvedArtistVisualSelection(null, urlValue);
        }

        return null;
    }

    private static string? TryResolveLocalVisualPath(string allowedRoot, string? explicitLocalPath, string? visualUrl)
    {
        var localPath = ValidateCachePath(explicitLocalPath, allowedRoot);
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            return localPath;
        }

        var urlValue = (visualUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(urlValue))
        {
            return null;
        }

        var extractedPath = TryExtractPathFromLibraryImageUrl(urlValue);
        return ValidateCachePath(extractedPath, allowedRoot);
    }

    private static string? ValidateCachePath(string? candidatePath, string cacheRoot)
    {
        var value = (candidatePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(value);
            if (!IsPathWithinRoot(fullPath, cacheRoot))
            {
                return null;
            }

            return File.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);

        return !relative.StartsWith("..", StringComparison.Ordinal)
               && !Path.IsPathRooted(relative);
    }

    private static string? TryExtractPathFromLibraryImageUrl(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var queryIndex = trimmed.IndexOf('?');
        if (queryIndex < 0)
        {
            return null;
        }

        var endpoint = trimmed[..queryIndex];
        if (endpoint.IndexOf("/api/library/image", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return null;
        }

        var query = trimmed[(queryIndex + 1)..];
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var segments = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var equalsIndex = segment.IndexOf('=');
            var rawKey = equalsIndex >= 0 ? segment[..equalsIndex] : segment;
            var key = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
            if (!string.Equals(key, "path", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawValue = equalsIndex >= 0 ? segment[(equalsIndex + 1)..] : string.Empty;
            var decoded = Uri.UnescapeDataString(rawValue.Replace('+', ' ')).Trim();
            return string.IsNullOrWhiteSpace(decoded) ? null : decoded;
        }

        return null;
    }

    private async Task<MaterializedArtistVisual> EnsureVisualStoredAsync(
        long artistId,
        string slot,
        ResolvedArtistVisualSelection? selection,
        CancellationToken cancellationToken)
    {
        if (selection is null)
        {
            return new MaterializedArtistVisual(null, null);
        }

        var visualDir = Path.Join(
            AppDataPaths.GetDataRoot(_environment),
            LibraryArtistImagesPath,
            SelectedSource,
            "artists",
            artistId.ToString());
        Directory.CreateDirectory(visualDir);

        if (!string.IsNullOrWhiteSpace(selection.LocalPath))
        {
            var sourcePath = selection.LocalPath!;
            if (File.Exists(sourcePath))
            {
                var extension = ImageFileExtensionResolver.NormalizeStandardImageExtension(Path.GetExtension(sourcePath));
                var targetPath = Path.Join(visualDir, $"{slot}{extension}");
                DeleteVisualSlotFiles(visualDir, slot, targetPath);

                if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourcePath, targetPath, true);
                }

                return new MaterializedArtistVisual(new ResolvedArtistVisualSelection(targetPath, null), null);
            }
        }

        if (!string.IsNullOrWhiteSpace(selection.RemoteUrl))
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                using var response = await client.GetAsync(selection.RemoteUrl, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return new MaterializedArtistVisual(
                        selection,
                        $"Failed to download {slot} visual ({(int)response.StatusCode}); used remote source where possible.");
                }

                var extension = ImageFileExtensionResolver.ResolveStandardImageExtension(
                    response.Content.Headers.ContentType?.MediaType,
                    selection.RemoteUrl);
                var targetPath = Path.Join(visualDir, $"{slot}{extension}");
                DeleteVisualSlotFiles(visualDir, slot, targetPath);

                await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var targetStream = File.Create(targetPath);
                await sourceStream.CopyToAsync(targetStream, cancellationToken);

                return new MaterializedArtistVisual(new ResolvedArtistVisualSelection(targetPath, null), null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to materialize {Slot} visual for artist {ArtistId}", slot, artistId);
                return new MaterializedArtistVisual(selection, $"Failed to download {slot} visual; used remote source where possible.");
            }
        }

        return new MaterializedArtistVisual(selection, null);
    }

    private static void DeleteVisualSlotFiles(string directory, string slot, string keepPath)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var files = Directory.GetFiles(directory, $"{slot}.*", SearchOption.TopDirectoryOnly);
        foreach (var file in files)
        {
            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(keepPath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (IOException ex)
            {
                System.Diagnostics.Trace.TraceWarning("Failed to remove stale {0} visual file '{1}': {2}", slot, file, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Trace.TraceWarning("Access denied removing stale {0} visual file '{1}': {2}", slot, file, ex.Message);
            }
        }
    }
}

public sealed class ArtistVisualSelectionRequest
{
    public string? AvatarImagePath { get; set; }
    public string? AvatarVisualUrl { get; set; }
    public string? BackgroundImagePath { get; set; }
    public string? BackgroundVisualUrl { get; set; }
}

public sealed record ArtistVisualSelectionResult(
    bool Success,
    int StatusCode,
    string? Error,
    string? AvatarPath,
    string? BackgroundPath,
    IReadOnlyList<string> Warnings)
{
    public static ArtistVisualSelectionResult Ok(string? avatarPath, string? backgroundPath, IReadOnlyList<string> warnings)
        => new(true, StatusCodes.Status200OK, null, avatarPath, backgroundPath, warnings);

    public static ArtistVisualSelectionResult BadRequest(string error)
        => new(false, StatusCodes.Status400BadRequest, error, null, null, Array.Empty<string>());

    public static ArtistVisualSelectionResult NotFound(string error)
        => new(false, StatusCodes.Status404NotFound, error, null, null, Array.Empty<string>());
}

internal sealed record ResolvedArtistVisualSelection(string? LocalPath, string? RemoteUrl);
internal sealed record MaterializedArtistVisual(ResolvedArtistVisualSelection? Selection, string? Warning);
