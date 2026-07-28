namespace DeezSpoTag.Services.Library;

public interface ILocalTrackAmbiguityResolver
{
    Task<LibraryRepository.LocalTrackIdentityResult> ResolveAsync(
        LibraryRepository.LibraryExistenceInput input,
        LibraryRepository.LocalTrackIdentityResult initial,
        CancellationToken cancellationToken);
}
