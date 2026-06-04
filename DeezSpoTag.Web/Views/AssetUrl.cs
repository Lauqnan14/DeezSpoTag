using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;

namespace DeezSpoTag.Web.Views;

public static class AssetUrl
{
    public static string Content(IUrlHelper url, string path) => url.Content(path);

    public static string Versioned(ViewContext viewContext, IUrlHelper url, string path)
    {
        var fileVersionProvider = viewContext.HttpContext.RequestServices.GetRequiredService<IFileVersionProvider>();
        return fileVersionProvider.AddFileVersionToPath(viewContext.HttpContext.Request.PathBase, url.Content(path));
    }
}
