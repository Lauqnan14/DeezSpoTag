namespace DeezSpoTag.Web.Services.AutoTag;

/// <summary>
/// Every provider-owned identity field. A provider and one of these fields form the
/// smallest writable/overwritable unit: no provider pass may touch another provider or
/// another field of the same provider.
/// </summary>
public enum ProviderIdentityField
{
    TrackId,
    AlbumId,
    ReleaseId,
    ArtistId,
    AlbumArtistId,
    Url
}

/// <summary>
/// Immutable snapshot of the identity values one provider actually returned. It is
/// captured immediately after the matcher returns and before any later pipeline step
/// (title/edition preservation, folder consensus, lyrics, artwork) can mutate the
/// mutable <see cref="AutoTagTrack"/>. <see cref="IsNativeProviderResult"/> is the
/// provenance flag: only a native result may write identity tags.
/// </summary>
internal sealed record ProviderIdentityPayload(
    string ProviderId,
    string? TrackId,
    string? AlbumId,
    string? ReleaseId,
    string? ArtistId,
    string? AlbumArtistId,
    string? Url,
    bool IsNativeProviderResult)
{
    public string? ValueFor(ProviderIdentityField field) => field switch
    {
        ProviderIdentityField.TrackId => TrackId,
        ProviderIdentityField.AlbumId => AlbumId,
        ProviderIdentityField.ReleaseId => ReleaseId,
        ProviderIdentityField.ArtistId => ArtistId,
        ProviderIdentityField.AlbumArtistId => AlbumArtistId,
        ProviderIdentityField.Url => Url,
        _ => null
    };

    /// <summary>True when this payload carries at least one authoritative value.</summary>
    public bool HasAnyValue => !string.IsNullOrWhiteSpace(TrackId)
        || !string.IsNullOrWhiteSpace(AlbumId)
        || !string.IsNullOrWhiteSpace(ReleaseId)
        || !string.IsNullOrWhiteSpace(ArtistId)
        || !string.IsNullOrWhiteSpace(AlbumArtistId)
        || !string.IsNullOrWhiteSpace(Url);
}

/// <summary>
/// One provider+field alias family: the canonical names written for the field, the
/// supported tag the overwrite configuration maps to, and every alias that may be
/// cleaned when that exact family is overwritten.
/// </summary>
internal sealed record ProviderIdentityTagFamily(
    SupportedTag SupportedTag,
    IReadOnlyList<string> WriteNames,
    IReadOnlyList<string> CleanupNames);