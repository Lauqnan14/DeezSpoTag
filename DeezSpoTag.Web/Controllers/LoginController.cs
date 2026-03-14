using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers
{
    /// <summary>
    /// Login MVC controller for rendering login views
    /// Note: API functionality is handled by DeezSpoTag.API.Controllers.LoginController
    /// </summary>
    [Route("Login")]
    public class LoginController : Controller
    {
        private readonly ILogger<LoginController> _logger;

        public LoginController(ILogger<LoginController> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// GET: /Login
        /// </summary>
        [HttpGet("")]
        public IActionResult Index()
        {
            _logger.LogDebug("Login page requested");
            return View();
        }

        /// <summary>
        /// GET: /Login/Login (alternative route)
        /// </summary>
        [HttpGet("Login")]
        public IActionResult Login()
        {
            return RedirectToAction("Index");
        }

        /// <summary>
        /// GET: /InfoArl
        /// </summary>
        [HttpGet("/InfoArl")]
        public IActionResult InfoArl()
        {
            return View();
        }

    }
}
