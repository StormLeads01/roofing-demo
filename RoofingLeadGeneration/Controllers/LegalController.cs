using Microsoft.AspNetCore.Mvc;
using RoofingLeadGeneration.Filters;

namespace RoofingLeadGeneration.Controllers
{
    [Route("[controller]")]
    [SkipTrialGate]
    public class LegalController : Controller
    {
        [HttpGet("privacy")]
        public IActionResult Privacy() => View();

        [HttpGet("terms")]
        public IActionResult Terms() => View();
    }
}
