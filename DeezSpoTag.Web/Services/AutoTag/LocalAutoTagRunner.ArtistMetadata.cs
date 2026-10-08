using System.Text.Json;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Web.Services.ArtistLocation;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed partial class LocalAutoTagRunner
{
    /// <summary>Cache payload version reused by both Enrichment and Enhancement.</summary>
    private const string ArtistEnrichmentCacheSource = "artist-enrichment";
    private const string ArtistEnrichmentCacheVersion = "v1";

    /// <summary>
    /// Collects provider-independent artist metadata for the matched track and retains it
    /// in the artist page cache. Runs inside an already-running enrichment/metadata update,
    /// independent of which fields are selected for file writing.
    /// </summary>
    public async Task PopulateArtistMetadataAsync(AutoTagTrack track, CancellationToken cancellationToken = default, string? providerId = null, string? filePath = null, bool preferSingleArtist = false)
    {
        if (track is null)
        {
            return;
        }

        track.ArtistMetadataUsesSingleArtistPreference = preferSingleArtist;
        var names = track.Artists.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var languages = new List<LanguageMetadataEvidence>();
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            try
            {
                using var file = TagLib.File.Create(filePath);
                var extension = Path.GetExtension(filePath);
                var knownAt = DateTimeOffset.UtcNow;
                var artistValues = ReadRawTagValues(file, extension, ArtistEnrichmentFields.RawNames["artistLanguage"])
                    .SelectMany(SplitCompositeRawValues).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (names.Length == 1 && artistValues.Length > 0)
                    languages.Add(new(artistValues, LanguageMetadataScope.Artist, "audio-file", filePath + "#ARTISTLANGUAGE", knownAt));
                var trackValues = ReadRawTagValues(file, extension, ArtistEnrichmentFields.RawNames["language"])
                    .SelectMany(SplitCompositeRawValues).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (trackValues.Length > 0)
                    track.TrackLanguageEvidence = (track.TrackLanguageEvidence ?? []).Concat(
                        [new LanguageMetadataEvidence(trackValues, LanguageMetadataScope.Track, "audio-file", filePath + "#LANGUAGE", knownAt)]).ToArray();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogDebug(ex, "Explicit language observations unavailable for {File}.", DeezSpoTag.Core.Security.LogSanitizer.OneLine(filePath)); }
        }
        if (names.Length == 0) return;
        var artistName = names[0];
        long? artistId = null;
        if (_artistLibraryRepository is not null && !string.IsNullOrWhiteSpace(providerId)
            && !string.IsNullOrWhiteSpace(track.ArtistId))
        {
            var ids = await _artistLibraryRepository.GetArtistIdsBySourceIdAsync(providerId, track.ArtistId, cancellationToken).ConfigureAwait(false);
            if (ids.Count == 1)
            {
                var matchesPrimary = names.Length == 1 || string.Equals(
                    (await _artistLibraryRepository.GetArtistIdentityNameAsync(ids[0], cancellationToken).ConfigureAwait(false))?.Trim(),
                    artistName, StringComparison.OrdinalIgnoreCase);
                if (matchesPrimary)
                    artistId = ids[0];
            }
        }

        if (_artistPageCache is not null && artistId is { } retainedId)
        {
            var entry = await _artistPageCache.TryGetAsync(ArtistEnrichmentCacheSource, retainedId.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            if (entry is not null && _artistPageCache.IsFresh(entry.FetchedUtc))
            {
                try
                {
                    using var payload = JsonDocument.Parse(entry.PayloadJson);
                    if (payload.RootElement.GetProperty("version").GetString() == ArtistEnrichmentCacheVersion)
                    {
                        var retained = payload.RootElement.GetProperty("metadata").Deserialize<ArtistEnrichmentMetadata>();
                        if (retained?.ArtistId == artistId && !string.IsNullOrWhiteSpace(retained.BindingReference))
                            languages.AddRange(retained.Languages.Where(e => e.Scope == LanguageMetadataScope.Artist));
                    }
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                { _logger.LogDebug(ex, "Retained artist enrichment payload is unavailable for {ArtistId}.", retainedId); }
            }
        }
        ArtistLocationResult? location = null;
        try
        {
            location = _artistLocationResolver is not null && artistId is { } id
                ? await _artistLocationResolver.ResolveAsync(id, artistName, cancellationToken).ConfigureAwait(false)
                : null;
            if (location is null && _artistLocationResolver is not null && providerId is not null && !string.IsNullOrWhiteSpace(track.ArtistId))
                location = await _artistLocationResolver.ResolveMatchedArtistAsync(providerId, track.ArtistId, artistName, track.Url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Artist location resolution failed for {Artist}.", artistName);
        }

        if (location is null && artistId is null)
        {
            track.ArtistMetadataRecords = names.Select(name => new ArtistEnrichmentMetadata(
                null, name, ArtistMetadataRole.Unknown, providerId ?? string.Empty, string.Empty, track.ArtistId, null, languages)).ToArray();
            track.ArtistMetadata = names.Length == 1 ? track.ArtistMetadataRecords[0] : null;
            return;
        }

        var metadata = new ArtistEnrichmentMetadata(
            artistId,
            location?.ArtistName ?? artistName,
            location?.ArtistRole ?? ArtistMetadataRole.Unknown,
            providerId ?? string.Empty,
            artistId is null ? $"matched-artist:{providerId}:{track.ArtistId}:{location?.SourceReference}"
                : $"artist-source:{providerId}:{track.ArtistId}:local:{artistId}",
            track.ArtistId,
            location is null
                ? null
                : new ArtistEnrichmentLocation(
                    location.Country, location.City, location.Region, location.CountryCode,
                    location.Source, location.SourceReference, location.RetrievedAt,
                    location.KnownAt, location.ResolutionMethod, location.LocationMeaning) { Hometown = location.Hometown },
            languages);

        track.ArtistMetadata = metadata;
        track.ArtistMetadataRecords = new[] { metadata }.Concat(names.Skip(1).Select(name => new ArtistEnrichmentMetadata(
            null, name, ArtistMetadataRole.Unknown, providerId ?? string.Empty, string.Empty, null, null, []))).ToArray();

        if (_artistPageCache is not null && artistId is { } cacheArtistId && (location is not null || languages.Count > 0))
        {
            try
            {
                var payload = new
                {
                    version = ArtistEnrichmentCacheVersion,
                    metadata,
                    retainedAt = DateTimeOffset.UtcNow
                };
                await _artistPageCache.UpsertAsync(
                    ArtistEnrichmentCacheSource,
                    cacheArtistId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    JsonSerializer.Serialize(payload),
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not retain artist enrichment metadata for artist {ArtistId}.", cacheArtistId);
            }
        }
    }
    private static List<string> GetArtistFieldValues(AutoTagTrack track, string key)
    {
        var metadata = track.ArtistMetadata;
        if (metadata is null)
            return [];
        var hasUnusableArtistBinding = string.IsNullOrWhiteSpace(metadata.BindingSource)
            || string.IsNullOrWhiteSpace(metadata.BindingReference)
            || (metadata.ArtistId is not > 0 &&
                (!metadata.BindingReference.StartsWith("matched-artist:", StringComparison.Ordinal)
                 || string.IsNullOrWhiteSpace(metadata.ProviderArtistId) || metadata.Location is null));
        if (hasUnusableArtistBinding
            || !string.Equals(metadata.ArtistName?.Trim(), track.Artists.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim(), StringComparison.OrdinalIgnoreCase)
            || (track.Artists.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1
                && !track.ArtistMetadataUsesSingleArtistPreference))
            return [];
        IEnumerable<string?> values = key switch
        {
            "artistCountry" => new string?[] { metadata.Location?.Country },
            "artistCity" => new string?[] { metadata.Location?.City },
            "artistRegion" => new string?[] { metadata.Location?.Region },
            "artistLanguage" => metadata.Languages.Where(e => e.Scope == LanguageMetadataScope.Artist).SelectMany(e => e.Values).Cast<string?>(),
            _ => Array.Empty<string?>()
        };
        return values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> GetTrackLanguageValues(AutoTagTrack track)
    {
        if (track.TrackLanguageEvidence is null)
            return ResolveOtherValues(track, LanguageTag, LanguageRawTag);
        var evidence = track.TrackLanguageEvidence.Where(e => e.Scope == LanguageMetadataScope.Track).ToArray();
        var providerEvidence = evidence.Where(e => e.Source != "audio-file").ToArray();
        return (providerEvidence.Length > 0 ? providerEvidence : evidence).SelectMany(e => e.Values)
            .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

}
