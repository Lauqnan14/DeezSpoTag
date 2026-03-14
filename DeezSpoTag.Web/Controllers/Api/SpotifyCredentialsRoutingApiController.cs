using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[LocalApiAuthorize]
[Route("api/spotify-credentials")]
public sealed class SpotifyCredentialsApiController : SpotifyCredentialsApiControllerCore
{
    public SpotifyCredentialsApiController(
        SpotifyCredentialsCollaborators collaborators,
        ILogger<SpotifyCredentialsApiController> logger,
        IWebHostEnvironment environment)
        : base(collaborators, logger, environment)
    {
    }
}
