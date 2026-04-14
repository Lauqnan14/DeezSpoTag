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
    public bool? StrictEngineQuality { get; set; }

    public bool HasValues =>
        !string.IsNullOrWhiteSpace(Service)
        || MaxBitrate.HasValue
        || !string.IsNullOrWhiteSpace(TidalQuality)
        || !string.IsNullOrWhiteSpace(QobuzQuality)
        || !string.IsNullOrWhiteSpace(ApplePreferredAudioProfile)
        || FallbackBitrate.HasValue
        || StrictEngineQuality.HasValue;

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
            StrictEngineQuality = settings.StrictEngineQuality
        };
    }

    public DeezSpoTagSettings ApplyTo(DeezSpoTagSettings? fallbackSettings)
    {
        var fallback = fallbackSettings ?? new DeezSpoTagSettings();
        var fallbackApple = fallback.AppleMusic ?? new AppleMusicSettings();

        return new DeezSpoTagSettings
        {
            Service = Service ?? fallback.Service,
            MaxBitrate = MaxBitrate ?? fallback.MaxBitrate,
            TidalQuality = TidalQuality ?? fallback.TidalQuality,
            QobuzQuality = QobuzQuality ?? fallback.QobuzQuality,
            FallbackBitrate = FallbackBitrate ?? fallback.FallbackBitrate,
            StrictEngineQuality = StrictEngineQuality ?? fallback.StrictEngineQuality,
            AppleMusic = new AppleMusicSettings
            {
                PreferredAudioProfile = ApplePreferredAudioProfile ?? fallbackApple.PreferredAudioProfile
            }
        };
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
            StrictEngineQuality = ReadBool(snapshotObj, "StrictEngineQuality", "strictEngineQuality")
        };

        return snapshot.HasValues ? snapshot : null;
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
        foreach (var key in keys)
        {
            var raw = ReadString(obj, key);
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
        foreach (var key in keys)
        {
            var raw = ReadString(obj, key);
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
