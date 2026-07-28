using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Tests;

internal sealed class PassthroughLocalTrackAmbiguityResolver : ILocalTrackAmbiguityResolver
{
    public Task<LibraryRepository.LocalTrackIdentityResult> ResolveAsync(
        LibraryRepository.LibraryExistenceInput input,
        LibraryRepository.LocalTrackIdentityResult initial,
        CancellationToken cancellationToken)
        => Task.FromResult(initial);
}
