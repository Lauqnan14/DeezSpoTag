namespace DeezSpoTag.Web.Models;

public sealed class AutoBuildVersionState
{
    public string Fingerprint { get; set; } = string.Empty;
    public int Major { get; set; }
    public int Minor { get; set; }
    public int Patch { get; set; }
    public int Revision { get; set; }

    public string ToVersionString()
        => $"{Major}.{Minor}.{Patch}.{Revision}";
}
