using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
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
        ViewBag.GoogleMapsApiKey = _config["GoogleMaps:ApiKey"] ?? "";
        return View();
    }

    public IActionResult Landing()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index");
        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
