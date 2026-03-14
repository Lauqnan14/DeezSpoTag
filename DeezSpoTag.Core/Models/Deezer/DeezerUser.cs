namespace DeezSpoTag.Core.Models.Deezer;

/// <summary>
/// Deezer user model (ported from deezer-sdk User interface)
/// </summary>
public class DeezerUser
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Picture { get; set; }
    public string? LicenseToken { get; set; }
    public bool? CanStreamHq { get; set; }
    public bool? CanStreamLossless { get; set; }
    public string? Country { get; set; }
    public string? Language { get; set; }
    public int? LovedTracks { get; set; }
    public string? LovedTracksId { get; set; }
    public string[]? ChildAccounts { get; set; }
}