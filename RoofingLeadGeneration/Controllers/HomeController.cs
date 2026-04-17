using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using RoofingLeadGeneration.Models;

namespace RoofingLeadGeneration.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;

    public HomeController(ILogger<HomeController> logger)
    {
        _logger = logger;
    }

    public IActionResult Index()
    {
        if (!User.Identity?.IsAuthenticated ?? true)
            return RedirectToAction("Landing");
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
