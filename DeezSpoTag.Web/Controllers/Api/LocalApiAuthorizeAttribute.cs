using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Authorization;
using System.Reflection;

namespace DeezSpoTag.Web.Controllers.Api;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class LocalApiAuthorizeAttribute : Attribute, IAsyncAuthorizationFilter, IAllowAnonymous
{
    private static readonly PropertyInfo AuthorizationFilterResultProperty =
        typeof(AuthorizationFilterContext).GetProperty("Result")
        ?? throw new InvalidOperationException("Authorization filter result property was not found.");

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (LocalApiAccess.IsAllowed(context.HttpContext))
        {
            return Task.CompletedTask;
        }

        AuthorizationFilterResultProperty.SetValue(context, new UnauthorizedObjectResult("Authentication required."));
        return Task.CompletedTask;
    }
}
