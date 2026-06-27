using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using RoofingLeadGeneration.Filters;
using RoofingLeadGeneration.Models;

namespace RoofingLeadGeneration.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly IConfiguration          _config;

    public HomeController(ILogger<HomeController> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public IActionResult Index()
    {
        if (!User.Identity?.IsAuthenticated ?? true)
            return RedirectToAction("Landing");
        ViewBag.GoogleMapsApiKey       = _config["GoogleMaps:ApiKey"] ?? "";
        ViewBag.MapTilerApiKey         = _config["MapTiler:ApiKey"] ?? "";
        ViewBag.HailSwathPolygons      = _config.GetValue<bool>("FeatureFlags:HailSwathPolygons");
        return View();
    }

    [SkipTrialGate]
    public IActionResult Landing()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index");
        return View();
    }

    [SkipTrialGate]
    public IActionResult Privacy()
    {
        return View();
    }

    [SkipTrialGate]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
