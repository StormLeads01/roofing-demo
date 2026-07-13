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
        // Note: MESH visibility is no longer gated by a ViewBag/button — Storm
        // Explorer always tries to render it per selected date; the server-side
        // FeatureFlags:MeshSwaths check in RoofHealthController controls whether
        // that returns real data. See docs/mesh-phase2-handoff.md.
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
