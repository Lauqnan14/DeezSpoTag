using System.Text.Json.Serialization;

namespace DeezSpoTag.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LyricsSourceFormat
{
    Unknown = 0,
    DownloadedLrc = 1,
    DownloadedTtml = 2,
    DownloadedPlainText = 3,
    ProviderSyncedJson = 4,
    SynthesizedTtml = 5
}
