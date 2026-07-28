using System.Text.Json;
using System.Text.Json.Nodes;
using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Services.Download.Shared;

/// <summary>
/// Captures the source-routing settings at enqueue time so retries can
/// resolve fallback order against the original user preferences.
/// </summary>
public sealed class QueueSourceSettingsSnapshot
{
    public string? Service { get; set; }
    public int? MaxBitrate { get; set; }
    public string? TidalQuality { get; set; }
    public string? QobuzQuality { get; set; }
    public string? ApplePreferredAudioProfile { get; set; }
    public bool? FallbackBitrate { get; set; }
    public bool? FallbackSearch { get; set; }
    public string? DeezerCountry { get; set; }
    public string? DeezerLanguage { get; set; }
    public MultiQualityDownloadSettings? MultiQuality { get; set; }
    public DownloadEngineOrderSettings? DownloadEngineOrder { get; set; }
    public bool? SyncedLyrics { get; set; }
    public bool? SaveLyrics { get; set; }
    public string? LrcType { get; set; }
    public string? LrcFormat { get; set; }
    public bool? SynthesizeLrcFromTtml { get; set; }
    public bool? LyricsFallbackEnabled { get; set; }
    public string? LyricsFallbackOrder { get; set; }

    public bool HasValues =>
        !string.IsNullOrWhiteSpace(Service)
        || MaxBitrate.HasValue
        || !string.IsNullOrWhiteSpace(TidalQuality)
        || !string.IsNullOrWhiteSpace(QobuzQuality)
        || !string.IsNullOrWhiteSpace(ApplePreferredAudioProfile)
        || FallbackBitrate.HasValue
        || FallbackSearch.HasValue
        || !string.IsNullOrWhiteSpace(DeezerCountry)
        || !string.IsNullOrWhiteSpace(DeezerLanguage)
        || MultiQuality != null
        || DownloadEngineOrder != null
        || SyncedLyrics.HasValue
        || SaveLyrics.HasValue
        || !string.IsNullOrWhiteSpace(LrcType)
        || !string.IsNullOrWhiteSpace(LrcFormat)
        || SynthesizeLrcFromTtml.HasValue
        || LyricsFallbackEnabled.HasValue
        || !string.IsNullOrWhiteSpace(LyricsFallbackOrder);

    public static QueueSourceSettingsSnapshot Capture(DeezSpoTagSettings? settings)
    {
        settings ??= new DeezSpoTagSettings();
        return new QueueSourceSettingsSnapshot
        {
            Service = NormalizeString(settings.Service),
            MaxBitrate = settings.MaxBitrate,
            TidalQuality = NormalizeString(settings.TidalQuality),
            QobuzQuality = NormalizeString(settings.QobuzQuality),
            ApplePreferredAudioProfile = NormalizeString(settings.AppleMusic?.PreferredAudioProfile),
            FallbackBitrate = settings.FallbackBitrate,
            FallbackSearch = settings.FallbackSearch,
            DeezerCountry = NormalizeString(settings.DeezerCountry),
            DeezerLanguage = NormalizeString(settings.DeezerLanguage),
            MultiQuality = CloneMultiQuality(settings.MultiQuality),
            DownloadEngineOrder = CloneDownloadEngineOrder(settings.DownloadEngineOrder),
            SyncedLyrics = settings.SyncedLyrics,
            SaveLyrics = settings.SaveLyrics,
            LrcType = NormalizeString(settings.LrcType),
            LrcFormat = NormalizeString(settings.LrcFormat),
            SynthesizeLrcFromTtml = settings.SynthesizeLrcFromTtml,
            LyricsFallbackEnabled = settings.LyricsFallbackEnabled,
            LyricsFallbackOrder = NormalizeString(settings.LyricsFallbackOrder)
        };
    }

    public DeezSpoTagSettings ApplyTo(DeezSpoTagSettings? fallbackSettings)
    {
        var effective = fallbackSettings ?? new DeezSpoTagSettings();
        effective.AppleMusic ??= new AppleMusicSettings();
        effective.Service = Service ?? effective.Service;
        effective.MaxBitrate = MaxBitrate ?? effective.MaxBitrate;
        effective.TidalQuality = TidalQuality ?? effective.TidalQuality;
        effective.QobuzQuality = QobuzQuality ?? effective.QobuzQuality;
        effective.FallbackBitrate = FallbackBitrate ?? effective.FallbackBitrate;
        effective.FallbackSearch = FallbackSearch ?? effective.FallbackSearch;
        effective.DeezerCountry = DeezerCountry ?? effective.DeezerCountry;
        effective.DeezerLanguage = DeezerLanguage ?? effective.DeezerLanguage;
        if (!string.IsNullOrWhiteSpace(ApplePreferredAudioProfile))
        {
            effective.AppleMusic.PreferredAudioProfile = ApplePreferredAudioProfile;
        }
        if (MultiQuality != null)
        {
            effective.MultiQuality = CloneMultiQuality(MultiQuality)!;
        }
        if (DownloadEngineOrder != null)
        {
            effective.DownloadEngineOrder = CloneDownloadEngineOrder(DownloadEngineOrder);
        }
        effective.SyncedLyrics = SyncedLyrics ?? effective.SyncedLyrics;
        effective.SaveLyrics = SaveLyrics ?? effective.SaveLyrics;
        effective.LrcType = LrcType ?? effective.LrcType;
        effective.LrcFormat = LrcFormat ?? effective.LrcFormat;
        effective.SynthesizeLrcFromTtml = SynthesizeLrcFromTtml ?? effective.SynthesizeLrcFromTtml;
        effective.LyricsFallbackEnabled = LyricsFallbackEnabled ?? effective.LyricsFallbackEnabled;
        effective.LyricsFallbackOrder = LyricsFallbackOrder ?? effective.LyricsFallbackOrder;

        return effective;
    }

    public static QueueSourceSettingsSnapshot? ReadFromPayload(JsonObject payloadObj)
    {
        if (payloadObj == null)
        {
            return null;
        }

        var node = payloadObj["SourceSettingsSnapshot"] ?? payloadObj["sourceSettingsSnapshot"];
        if (node is not JsonObject snapshotObj)
        {
            return null;
        }

        var snapshot = new QueueSourceSettingsSnapshot
        {
            Service = ReadString(snapshotObj, "Service", "service"),
            MaxBitrate = ReadInt(snapshotObj, "MaxBitrate", "maxBitrate"),
            TidalQuality = ReadString(snapshotObj, "TidalQuality", "tidalQuality"),
            QobuzQuality = ReadString(snapshotObj, "QobuzQuality", "qobuzQuality"),
            ApplePreferredAudioProfile = ReadString(snapshotObj, "ApplePreferredAudioProfile", "applePreferredAudioProfile"),
            FallbackBitrate = ReadBool(snapshotObj, "FallbackBitrate", "fallbackBitrate"),
            FallbackSearch = ReadBool(snapshotObj, "FallbackSearch", "fallbackSearch"),
            DeezerCountry = ReadString(snapshotObj, "DeezerCountry", "deezerCountry"),
            DeezerLanguage = ReadString(snapshotObj, "DeezerLanguage", "deezerLanguage"),
            MultiQuality = ReadMultiQuality(snapshotObj),
            DownloadEngineOrder = ReadDownloadEngineOrder(snapshotObj),
            SyncedLyrics = ReadBool(snapshotObj, "SyncedLyrics", "syncedLyrics"),
            SaveLyrics = ReadBool(snapshotObj, "SaveLyrics", "saveLyrics"),
            LrcType = ReadString(snapshotObj, "LrcType", "lrcType"),
            LrcFormat = ReadString(snapshotObj, "LrcFormat", "lrcFormat"),
            SynthesizeLrcFromTtml = ReadBool(snapshotObj, "SynthesizeLrcFromTtml", "synthesizeLrcFromTtml"),
            LyricsFallbackEnabled = ReadBool(snapshotObj, "LyricsFallbackEnabled", "lyricsFallbackEnabled"),
            LyricsFallbackOrder = ReadString(snapshotObj, "LyricsFallbackOrder", "lyricsFallbackOrder")
        };

        return snapshot.HasValues ? snapshot : null;
    }

    private static DownloadEngineOrderSettings? ReadDownloadEngineOrder(JsonObject obj)
    {
        var node = obj[nameof(DownloadEngineOrder)] ?? obj["downloadEngineOrder"];
        if (node == null)
        {
            return null;
        }

        try
        {
            return node.Deserialize<DownloadEngineOrderSettings>();
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return null;
        }
    }

    private static MultiQualityDownloadSettings? ReadMultiQuality(JsonObject obj)
    {
        var node = obj[nameof(MultiQuality)] ?? obj["multiQuality"];
        if (node == null)
        {
            return null;
        }

        try
        {
            return node.Deserialize<MultiQualityDownloadSettings>();
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return null;
        }
    }

    private static DownloadEngineOrderSettings CloneDownloadEngineOrder(DownloadEngineOrderSettings? settings)
    {
        return DownloadSourceOrder.NormalizeDownloadEngineOrderSettings(settings);
    }

    private static MultiQualityDownloadSettings? CloneMultiQuality(MultiQualityDownloadSettings? settings)
    {
        if (settings == null)
        {
            return null;
        }

        return new MultiQualityDownloadSettings
        {
            Enabled = settings.Enabled,
            SecondaryEnabled = settings.SecondaryEnabled,
            PrimaryDestinationFolderId = settings.PrimaryDestinationFolderId,
            SecondaryDestinationFolderId = settings.SecondaryDestinationFolderId,
            AtmosEngine = settings.AtmosEngine,
            AtmosSearchFallback = settings.AtmosSearchFallback,
            AtmosDownloadFallback = settings.AtmosDownloadFallback
        };
    }

    private static string? ReadString(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj[key] is not JsonNode node)
            {
                continue;
            }

            var value = NormalizeString(node.ToString());
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static int? ReadInt(JsonObject obj, params string[] keys)
    {
        foreach (var raw in keys.Select(key => ReadString(obj, key)))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (int.TryParse(raw, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool? ReadBool(JsonObject obj, params string[] keys)
    {
        foreach (var raw in keys.Select(key => ReadString(obj, key)))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (bool.TryParse(raw, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? NormalizeString(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
