using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers;

public class StatisticsController : Controller
{
    private readonly ILogger<StatisticsController> _logger;

    public StatisticsController(ILogger<StatisticsController> logger)
    {
        _logger = logger;
    }

    public IActionResult Index()
    {
        _logger.LogDebug("Statistics page requested");
        return View();
    }
}
