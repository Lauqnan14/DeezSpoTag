using System.Collections.Generic;

namespace DeezSpoTag.Services.Download.Shared.Models;

public class DeezSpoTagDownloadError
{
    public string Message { get; set; } = "";
    public string? ErrorId { get; set; }
    public Dictionary<string, object> Data { get; set; } = new();
    public string? Stack { get; set; }
    public string Type { get; set; } = "";
}