using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Services.Metadata;

public interface IMetadataResolver
{
    string SourceKey { get; }

    Task ResolveTrackAsync(Track track, DeezSpoTagSettings settings, CancellationToken cancellationToken);
}

public interface IMetadataResolverRegistry
{
    IMetadataResolver? GetResolver(string? sourceKey);
}

public sealed class MetadataResolverRegistry : IMetadataResolverRegistry
{
    private readonly Dictionary<string, IMetadataResolver> _resolvers;

    public MetadataResolverRegistry(IEnumerable<IMetadataResolver> resolvers)
    {
        _resolvers = resolvers
            .Where(resolver => !string.IsNullOrWhiteSpace(resolver.SourceKey))
            .GroupBy(resolver => resolver.SourceKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    public IMetadataResolver? GetResolver(string? sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return null;
        }

        return _resolvers.TryGetValue(sourceKey.Trim(), out var resolver) ? resolver : null;
    }
}
