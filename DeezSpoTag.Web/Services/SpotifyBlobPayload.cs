namespace DeezSpoTag.Web.Services;

public sealed class SpotifyBlobPayload
{
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string UserAgent { get; set; } = string.Empty;
    public List<SpotifyBlobCookie> Cookies { get; set; } = new();
    public Dictionary<string, string> LocalStorage { get; set; } = new();
}

public sealed class SpotifyBlobCookie
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string? Path { get; set; } = "/";
    public double? Expires { get; set; }
    public bool Secure { get; set; }
    public bool HttpOnly { get; set; }
    public string? SameSite { get; set; }
}

public sealed class SpotifyWebPlayerTokenCheck
{
    public bool Ok { get; set; }
    public int? StatusCode { get; set; }
    public string? Message { get; set; }
    public long? ExpiresAtUnixMs { get; set; }
    public bool? IsAnonymous { get; set; }
    public string? Country { get; set; }
    public string? ClientId { get; set; }
}
