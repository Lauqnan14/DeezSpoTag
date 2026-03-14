namespace DeezSpoTag.Web.Services.CoverPort;

public interface ICoverSource
{
    CoverSourceName Name { get; }

    Task<IReadOnlyList<CoverCandidate>> SearchAsync(CoverSearchQuery query, CancellationToken cancellationToken);
}
