using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Services.Download.Tidal;

internal readonly record struct TidalPublicManifestRequest(
    long TrackId,
    string Quality,
    bool IsAtmos);

internal sealed record TidalPublicTrackMetadata(
    long TrackId,
    string Title,
    string Artist,
    string Album,
    string Isrc,
    int DurationSeconds,
    string CoverId,
    string AudioQuality,
    IReadOnlyList<string> AudioModes,
    IReadOnlyList<string> MediaTags);

internal interface ITidalPublicDownloadProviderAdapter
{
    string Kind { get; }
    TidalPublicProviderCapabilities Capabilities { get; }
    Task<bool> IsReadyAsync(TidalPublicProvider provider, CancellationToken cancellationToken);
    Task<TidalPublicTrackMetadata?> ResolveTrackMetadataAsync(
        TidalPublicProvider provider,
        long trackId,
        CancellationToken cancellationToken);
    Task<string?> AcquireManifestAsync(
        TidalPublicProvider provider,
        TidalPublicManifestRequest request,
        CancellationToken cancellationToken);
}

internal sealed class TidalPublicDownloadProviderAdapterRegistry
{
    private readonly IReadOnlyDictionary<string, ITidalPublicDownloadProviderAdapter> _adapters;

    public TidalPublicDownloadProviderAdapterRegistry(IEnumerable<ITidalPublicDownloadProviderAdapter> adapters)
    {
        _adapters = adapters.ToDictionary(static adapter => adapter.Kind, StringComparer.OrdinalIgnoreCase);
    }

    public ITidalPublicDownloadProviderAdapter Resolve(TidalPublicProvider provider)
        => _adapters.TryGetValue(provider.Kind, out var adapter)
            ? adapter
            : throw new InvalidOperationException($"Unsupported Tidal public provider kind: {provider.Kind}.");
}

internal sealed class ZarzTidalPublicDownloadProviderAdapter : ITidalPublicDownloadProviderAdapter
{
    private readonly Func<object, long, CancellationToken, Task<string>> _downloadRequest;
    private readonly Func<string, string, CancellationToken, Task<string>> _textRequest;
    private readonly Func<CancellationToken, Task<bool>> _verifiedSessionProbe;

    public ZarzTidalPublicDownloadProviderAdapter(
        Func<object, long, CancellationToken, Task<string>> downloadRequest,
        Func<string, string, CancellationToken, Task<string>> textRequest,
        Func<CancellationToken, Task<bool>> verifiedSessionProbe)
    {
        _downloadRequest = downloadRequest;
        _textRequest = textRequest;
        _verifiedSessionProbe = verifiedSessionProbe;
    }

    public string Kind => TidalPublicProviderDefaults.ZarzProviderKind;

    public TidalPublicProviderCapabilities Capabilities { get; } = new(
        SupportsMetadata: false,
        SupportsStereo: true,
        SupportsAtmos: true,
        SupportsDirectAssets: true,
        SupportsManifests: true);

    public Task<bool> IsReadyAsync(TidalPublicProvider provider, CancellationToken cancellationToken)
        => provider.RequiresVerification
            ? _verifiedSessionProbe(cancellationToken)
            : Task.FromResult(true);

    public Task<TidalPublicTrackMetadata?> ResolveTrackMetadataAsync(
        TidalPublicProvider provider,
        long trackId,
        CancellationToken cancellationToken)
    {
        _ = provider;
        _ = trackId;
        _ = cancellationToken;
        return Task.FromResult<TidalPublicTrackMetadata?>(null);
    }

    public async Task<string?> AcquireManifestAsync(
        TidalPublicProvider provider,
        TidalPublicManifestRequest request,
        CancellationToken cancellationToken)
    {
        _ = provider;
        if (request.IsAtmos && !Capabilities.SupportsAtmos)
        {
            throw new InvalidOperationException("The selected Tidal public provider does not support Atmos.");
        }

        if (!request.IsAtmos && !Capabilities.SupportsStereo)
        {
            throw new InvalidOperationException("The selected Tidal public provider does not support stereo audio.");
        }

        var normalizedQuality = TidalStereoQuality.ToTidalRequestQuality(request.Quality);
        if (request.IsAtmos)
        {
            var atmosBody = await _downloadRequest(
                new
                {
                    id = request.TrackId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    endpoint = "manifests",
                    formats = new[] { "EAC3_JOC" }
                },
                request.TrackId,
                cancellationToken);
            if (!TidalDownloadService.TryExtractZarzAtmosManifestUri(atmosBody, out var manifestUri))
            {
                throw new InvalidOperationException("Tidal Zarz Atmos provider did not return a manifest URI.");
            }

            var manifestText = await _textRequest(
                manifestUri,
                "Tidal Zarz Atmos manifest",
                cancellationToken);
            return string.IsNullOrWhiteSpace(manifestText)
                ? null
                : TidalDownloadService.ManifestPrefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(manifestText));
        }

        var body = await _downloadRequest(
            new
            {
                id = request.TrackId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                quality = normalizedQuality
            },
            request.TrackId,
            cancellationToken);
        if (TidalDownloadService.BodyContainsPreviewAsset(body))
        {
            throw new InvalidOperationException("Tidal Zarz provider returned a preview asset.");
        }

        return TidalDownloadService.TryParseManifest(body, out var manifest) ? manifest : null;
    }
}
