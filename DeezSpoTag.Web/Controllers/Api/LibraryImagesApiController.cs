using DeezSpoTag.Web.Services;
using DeezSpoTag.Services.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using System.Security.Cryptography;
using System.Text;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/image")]
[ApiController]
[Authorize]
public class LibraryImagesApiController : ControllerBase
{
    private const string LibraryArtistImagesPath = "library-artist-images";
    private const string LibraryThumbsPath = "library-thumbs";

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp"
    };

    private readonly LibraryConfigStore _configStore;
    private readonly LibraryRepository _repository;
    private readonly IWebHostEnvironment _environment;

    public LibraryImagesApiController(
        LibraryConfigStore configStore,
        LibraryRepository repository,
        IWebHostEnvironment environment)
    {
        _configStore = configStore;
        _repository = repository;
        _environment = environment;
    }

    [HttpGet]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Any, NoStore = false)]
    public async Task<IActionResult> Get([FromQuery] string path, [FromQuery] int? size, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("Path is required.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound();
        }

        var extension = Path.GetExtension(fullPath);
        if (!AllowedExtensions.Contains(extension))
        {
            return BadRequest("Unsupported image type.");
        }

        var allowedRoots = _configStore.GetFolders()
            .Select(folder => folder.RootPath)
            .Concat(GetAllowedCacheRoots())
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var legacyCacheRoot = Path.GetFullPath(Path.Join(_environment.ContentRootPath, "Data", LibraryArtistImagesPath));
        var appDataCacheRoot = Path.GetFullPath(Path.Join(AppDataPaths.GetDataRoot(_environment), LibraryArtistImagesPath));
        allowedRoots.Add(legacyCacheRoot);
        allowedRoots.Add(appDataCacheRoot);
        if (_repository.IsConfigured)
        {
            try
            {
                var dbFolders = await _repository.GetFoldersAsync(cancellationToken);
                allowedRoots.AddRange(dbFolders.Select(folder => Path.GetFullPath(folder.RootPath)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) {
                // If the DB is unavailable, fall back to config-backed roots.
            }
        }
        allowedRoots = allowedRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var isAllowed = allowedRoots.Any(root => IsPathWithinRoot(fullPath, root));
        if (!isAllowed)
        {
            return Forbid();
        }

        if (size.HasValue)
        {
            var thumbSize = Math.Clamp(size.Value, 64, 512);
            var fileInfo = new FileInfo(fullPath);
            var thumbPath = GetThumbnailPath(fullPath, fileInfo.LastWriteTimeUtc.Ticks, thumbSize);
            if (!System.IO.File.Exists(thumbPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(thumbPath)!);
                using var image = await Image.LoadAsync(fullPath, cancellationToken);
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(thumbSize, thumbSize)
                }));
                await image.SaveAsync(thumbPath, new JpegEncoder { Quality = 82 }, cancellationToken);
            }

            Response.Headers.CacheControl = "public, max-age=86400";
            return PhysicalFile(thumbPath, "image/jpeg");
        }

        Response.Headers.CacheControl = "public, max-age=86400";
        return PhysicalFile(fullPath, $"image/{extension.TrimStart('.')}");
    }

    private string GetThumbnailPath(string fullPath, long lastWriteTicks, int size)
    {
        var hash = ComputeHash($"{fullPath}|{lastWriteTicks}|{size}");
        return Path.Join(AppDataPaths.GetDataRoot(_environment), LibraryThumbsPath, $"{hash}.jpg");
    }

    private List<string> GetAllowedCacheRoots()
    {
        var roots = new List<string>
        {
            Path.Join(_environment.ContentRootPath, "Data", LibraryArtistImagesPath),
            Path.Join(_environment.ContentRootPath, "Data", LibraryThumbsPath)
        };

        var appDataRoot = AppDataPaths.GetDataRoot(_environment);
        if (!string.IsNullOrWhiteSpace(appDataRoot))
        {
            roots.Add(Path.Join(appDataRoot, LibraryArtistImagesPath));
            roots.Add(Path.Join(appDataRoot, LibraryThumbsPath));
        }

        return roots;
    }

    private static bool IsPathWithinRoot(string fullPath, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(fullPath);
        var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);

        return !relative.StartsWith("..", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
