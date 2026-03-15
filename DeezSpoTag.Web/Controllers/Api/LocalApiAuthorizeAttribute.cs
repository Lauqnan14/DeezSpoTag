using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class LocalApiAuthorizeAttribute : Attribute, IAsyncAuthorizationFilter, IAllowAnonymous
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (LocalApiAccess.IsAllowed(context.HttpContext))
        {
            return Task.CompletedTask;
        }

        context.Result = new UnauthorizedObjectResult("Authentication required.");
        return Task.CompletedTask;
    }
}
