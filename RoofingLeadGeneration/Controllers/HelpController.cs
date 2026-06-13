using Microsoft.AspNetCore.Mvc;
using RoofingLeadGeneration.Filters;

namespace RoofingLeadGeneration.Controllers
{
    [Route("[controller]")]
    [SkipTrialGate]
    public class HelpController : Controller
    {
        [HttpGet("")]
        public IActionResult Index() => View();
    }
}
 