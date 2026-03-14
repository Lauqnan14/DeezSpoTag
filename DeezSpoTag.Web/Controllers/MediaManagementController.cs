using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers
{
    public class MediaManagementController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}