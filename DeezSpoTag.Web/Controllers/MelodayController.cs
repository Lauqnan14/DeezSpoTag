using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers;

[Authorize]
[AutoValidateAntiforgeryToken]
public class MelodayController : Controller
{
    public IActionResult Index()
    {
        return View();
    }
}
