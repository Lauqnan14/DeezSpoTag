namespace DeezSpoTag.Web.Models;

public sealed class PlatformLoginStatusCardModel
{
    public string ContainerId { get; init; } = string.Empty;
    public string ImageId { get; init; } = string.Empty;
    public string ImageAlt { get; init; } = string.Empty;
    public string? ImageSrc { get; init; }
    public string ImageCssClass { get; init; } = "h-32 w-32 rounded-full";
    public string ButtonId { get; init; } = string.Empty;
    public string ButtonLabel { get; init; } = "Logout";
    public string TextPrefix { get; init; } = "Logged in as";
    public string NameId { get; init; } = string.Empty;
    public string NameFallback { get; init; } = string.Empty;
    public string? DetailId { get; init; }
    public string? DetailText { get; init; }
    public string WrapperCssClass { get; init; } = "settings-group mt-6 hidden";
}
