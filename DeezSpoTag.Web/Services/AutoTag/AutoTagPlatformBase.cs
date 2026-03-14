namespace DeezSpoTag.Web.Services.AutoTag;

public abstract class AutoTagPlatformBase : IAutoTagPlatform
{
    private readonly IWebHostEnvironment _environment;

    protected AutoTagPlatformBase(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    public abstract AutoTagPlatformDescriptor Describe();

    protected AutoTagPlatformDescriptor CreateDescriptor(PlatformInfo info, string iconFileName)
    {
        return new AutoTagPlatformDescriptor
        {
            Id = info.Id,
            BuiltIn = true,
            Platform = info,
            Icon = LoadIcon(iconFileName),
            RequiresAuth = info.RequiresAuth,
            SupportedTags = info.SupportedTags,
            DownloadTags = info.DownloadTags
        };
    }

    protected static List<SupportedTag> CreateSupportedTags(params SupportedTag[] tags)
        => new(tags);

    protected static List<string> CreateDownloadTags(params string[] tags)
        => new(tags);

    protected static PlatformCustomOptions CreateOptions(params PlatformCustomOption[] options)
        => new() { Options = new(options) };

    protected readonly record struct NumberOptionValues(int Min, int Max, int Step, int Value, bool Slider = false);

    protected static PlatformCustomOption NumberOption(
        string id,
        string label,
        NumberOptionValues values,
        string? tooltip = null)
    {
        return new PlatformCustomOption
        {
            Id = id,
            Label = label,
            Tooltip = tooltip,
            Value = new PlatformCustomOptionNumber
            {
                Min = values.Min,
                Max = values.Max,
                Step = values.Step,
                Value = values.Value,
                Slider = values.Slider
            }
        };
    }

    protected static PlatformCustomOption BooleanOption(
        string id,
        string label,
        bool value,
        string? tooltip = null)
    {
        return new PlatformCustomOption
        {
            Id = id,
            Label = label,
            Tooltip = tooltip,
            Value = new PlatformCustomOptionBoolean { Value = value }
        };
    }

    protected static PlatformCustomOption StringOption(
        string id,
        string label,
        string value,
        string? tooltip = null)
    {
        return new PlatformCustomOption
        {
            Id = id,
            Label = label,
            Tooltip = tooltip,
            Value = new PlatformCustomOptionString { Value = value }
        };
    }

    protected static PlatformCustomOption SelectOption(
        string id,
        string label,
        string value,
        IEnumerable<string> values,
        string? tooltip = null)
    {
        return new PlatformCustomOption
        {
            Id = id,
            Label = label,
            Tooltip = tooltip,
            Value = new PlatformCustomOptionSelect
            {
                Value = value,
                Values = new(values)
            }
        };
    }

    private string LoadIcon(string iconFileName)
    {
        var iconPath = Path.Join(_environment.WebRootPath, "images", "icons", iconFileName);
        return File.Exists(iconPath)
            ? "data:image/png;charset=utf-8;base64," + Convert.ToBase64String(File.ReadAllBytes(iconPath))
            : string.Empty;
    }
}
