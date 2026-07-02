using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace DeezSpoTag.Web.Filters;

public sealed class ApiTokenAwareAntiforgeryFilter : IAsyncAuthorizationFilter, IAntiforgeryPolicy, IOrderedFilter
{
    private readonly IAntiforgery _antiforgery;

    public ApiTokenAwareAntiforgeryFilter(IAntiforgery antiforgery)
    {
        _antiforgery = antiforgery;
    }

    public int Order => 1000;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (!ShouldValidate(context))
        {
            return;
        }

        try
        {
            await _antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = new BadRequestObjectResult(new { error = "Invalid anti-forgery token." });
        }
    }

    private static bool ShouldValidate(AuthorizationFilterContext context)
    {
        if (string.Equals(context.HttpContext.User?.Identity?.AuthenticationType, "ApiToken", StringComparison.Ordinal))
        {
            return false;
        }

        if (context.Filters.OfType<IgnoreAntiforgeryTokenAttribute>().Any())
        {
            return false;
        }

        var method = context.HttpContext.Request.Method;
        return !HttpMethods.IsGet(method)
            && !HttpMethods.IsHead(method)
            && !HttpMethods.IsOptions(method)
            && !HttpMethods.IsTrace(method);
    }
}
