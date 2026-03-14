using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Services.Download.Queue;

public sealed class QueueUpdate
{
    public string Uuid { get; set; } = "";
    public string? Status { get; set; }
    public double? Progress { get; set; }
    public int? Downloaded { get; set; }
    public int? Failed { get; set; }
    public string? Error { get; set; }
    public string? Engine { get; set; }
    public string? Title { get; set; }
    public string? ErrId { get; set; }
    public string? Type { get; set; }
    public JsonElement? Data { get; set; }
    public bool? AlreadyDownloaded { get; set; }
    public string? DownloadPath { get; set; }
    public string? ExtrasPath { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
