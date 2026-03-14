namespace DeezSpoTag.Web.Services.AutoTag;

public interface IAutoTagPlatform
{
    AutoTagPlatformDescriptor Describe();
}

public sealed class PortedPlatformRegistry
{
    private readonly IReadOnlyList<IAutoTagPlatform> _platforms;

    public PortedPlatformRegistry(IEnumerable<IAutoTagPlatform> platforms)
    {
        _platforms = platforms.ToList();
    }

    public IReadOnlyList<AutoTagPlatformDescriptor> DescribeAll()
    {
        return _platforms.Select(p => p.Describe()).ToList();
    }
}
