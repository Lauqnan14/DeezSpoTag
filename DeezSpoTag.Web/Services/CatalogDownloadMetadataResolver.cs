using System.Text.Json;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Tidal;
using DeezSpoTag.Services.Metadata;

namespace DeezSpoTag.Web.Services;

/// <summary>Adapts the existing Atmos catalog clients to the download metadata registry.</summary>
public sealed class CatalogDownloadMetadataResolver(string sourceKey, IServiceProvider services) : IMetadataResolver
{
    public string SourceKey { get; } = sourceKey;

    public async Task ResolveTrackAsync(Track track, DeezSpoTagSettings settings, CancellationToken cancellationToken)
    {
        // The shared download path supplies the accepted identity for the selected source.
        // Do not use a foreign ID or silently search a different provider.
        if (!string.Equals(track.Source, SourceKey, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(track.SourceId)) return;

        var id = track.SourceId;
        switch (SourceKey)
        {
            case "tidal":
                var tidal = await services.GetRequiredService<TidalDownloadService>()
                    .ResolveTrackMetadataAsync(id, cancellationToken);
                if (tidal == null || tidal.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) != id) return;
                ApplyCatalogTrack(track, tidal.Title, tidal.Artist, tidal.Album, tidal.Isrc,
                    tidal.DurationSeconds, tidal.ReleaseDate, settings);
                break;
            case "amazon":
                var amazon = await services.GetRequiredService<AmazonMusicMetadataService>()
                    .GetTracklistAsync(id, "track", null, cancellationToken);
                var song = amazon?.Tracks.FirstOrDefault(t => string.Equals(t.AmazonId, id, StringComparison.OrdinalIgnoreCase));
                if (song == null) return;
                ApplyCatalogTrack(track, song.Title, song.Artist, song.Album, song.Isrc,
                    song.DurationMs / 1000, null, settings);
                break;
            case "apple":
                using (var document = await services.GetRequiredService<AppleMusicCatalogService>()
                    .GetSongAsync(id, settings.AppleMusic.Storefront, settings.DeezerLanguage, cancellationToken))
                {
                    ApplyAppleSong(track, document.RootElement, settings);
                }
                break;
        }
    }

    private static void ApplyAppleSong(Track track, JsonElement root, DeezSpoTagSettings settings)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return;
        foreach (var song in data.EnumerateArray())
        {
            if (ReadString(song, "id") != track.SourceId || !song.TryGetProperty("attributes", out var attributes)) continue;
            ApplyCatalogTrack(track, ReadString(attributes, "name"), ReadString(attributes, "artistName"),
                ReadString(attributes, "albumName"), ReadString(attributes, "isrc"),
                attributes.TryGetProperty("durationInMillis", out var duration) && duration.TryGetInt32(out var milliseconds)
                    ? milliseconds / 1000 : 0,
                ReadString(attributes, "releaseDate"), settings);
            if (attributes.TryGetProperty("trackNumber", out var number) && number.TryGetInt32(out var trackNumber) && trackNumber > 0)
                track.TrackNumber = trackNumber;
            if (attributes.TryGetProperty("discNumber", out var disc) && disc.TryGetInt32(out var discNumber) && discNumber > 0)
                track.DiscNumber = discNumber;
            return;
        }
    }

    private static string ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    private static void ApplyCatalogTrack(Track track, string title, string artist, string album,
        string isrc, int duration, string? releaseDate, DeezSpoTagSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(title)) track.Title = title;
        if (!string.IsNullOrWhiteSpace(artist))
        {
            track.MainArtist = new Artist(artist);
            track.Artists = [artist];
            track.Artist["Main"] = [artist];
        }
        track.Album ??= new Album(album);
        if (!string.IsNullOrWhiteSpace(album)) track.Album.Title = album;
        if (!string.IsNullOrWhiteSpace(isrc)) track.ISRC = isrc;
        if (duration > 0) track.Duration = duration;
        var date = CustomDate.FromString(releaseDate ?? string.Empty);
        if (date.IsValid())
        {
            track.Date = date;
            track.DateString = date.Format(settings.DateFormat);
            track.Album.Date = date;
            track.Album.DateString = track.DateString;
            track.Album.ReleaseDate = date.ToDateTime();
        }
    }
}
