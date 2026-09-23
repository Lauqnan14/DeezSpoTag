using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace DeezSpoTag.Services.Download.Shared;

public sealed record ArtworkAssetRequest(
    byte[] SourceBytes,
    string? SidecarFormats,
    int LocalArtworkSize,
    int EmbeddedArtworkSize,
    bool EmbedMaxQualityCover,
    int JpegImageQuality,
    string? AudioExtension);

public sealed record ArtworkVariant(byte[] Bytes, string Extension, string MimeType, int Width, int Height);

public sealed record ArtworkAssetResult(
    int SourceWidth,
    int SourceHeight,
    IReadOnlyDictionary<string, ArtworkVariant> Sidecars,
    ArtworkVariant Embedded);

public static class ArtworkAssetProcessor
{
    public static async Task<ArtworkAssetResult> PrepareAsync(
        ArtworkAssetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourceBytes is not { Length: > 0 })
        {
            throw new InvalidDataException("Artwork source is empty.");
        }

        using var source = Image.Load(request.SourceBytes);
        source.Mutate(static operation => operation.AutoOrient());
        var formats = ArtworkFormatPolicy.Parse(request.SidecarFormats);
        var sidecars = new Dictionary<string, ArtworkVariant>(StringComparer.OrdinalIgnoreCase);
        foreach (var format in formats)
        {
            sidecars[format] = await EncodeAsync(
                source,
                format,
                request.LocalArtworkSize,
                request.JpegImageQuality,
                cancellationToken);
        }

        var embeddedFormat = ArtworkFormatPolicy.ResolveEmbeddedFormat(request.SidecarFormats, request.AudioExtension);
        var embeddedSize = request.EmbedMaxQualityCover ? 0 : request.EmbeddedArtworkSize;
        var embedded = await EncodeAsync(source, embeddedFormat, embeddedSize, request.JpegImageQuality, cancellationToken);
        return new ArtworkAssetResult(source.Width, source.Height, sidecars, embedded);
    }

    private static async Task<ArtworkVariant> EncodeAsync(
        Image source,
        string format,
        int maximumDimension,
        int jpegQuality,
        CancellationToken cancellationToken)
    {
        using var output = source.Clone(operation =>
        {
            if (maximumDimension > 0 && Math.Max(source.Width, source.Height) > maximumDimension)
            {
                operation.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(maximumDimension, maximumDimension)
                });
            }
        });
        await using var stream = new MemoryStream();
        switch (format)
        {
            case "png":
                await output.SaveAsPngAsync(stream, new PngEncoder(), cancellationToken);
                break;
            case "webp":
                await output.SaveAsWebpAsync(stream, new WebpEncoder { Quality = Math.Clamp(jpegQuality, 1, 100) }, cancellationToken);
                break;
            default:
                format = "jpg";
                await output.SaveAsJpegAsync(stream, new JpegEncoder { Quality = Math.Clamp(jpegQuality, 1, 100) }, cancellationToken);
                break;
        }
        return new ArtworkVariant(
            stream.ToArray(),
            format,
            format switch { "png" => "image/png", "webp" => "image/webp", _ => "image/jpeg" },
            output.Width,
            output.Height);
    }
}
