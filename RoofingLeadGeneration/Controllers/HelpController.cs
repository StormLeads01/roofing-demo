using Microsoft.AspNetCore.Mvc;

namespace RoofingLeadGeneration.Controllers
{
    [Route("[controller]")]
    public class HelpController : Controller
    {
        [HttpGet("")]
        public IActionResult Index() => View();
    }
}
