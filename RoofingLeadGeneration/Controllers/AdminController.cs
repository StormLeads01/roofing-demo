using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RoofingLeadGeneration.Data;
using RoofingLeadGeneration.Filters;
using System.Security.Claims;

namespace RoofingLeadGeneration.Controllers
{
    [Route("[controller]")]
    [SkipTrialGate]
    public class AdminController : Controller
    {
        private readonly AppDbContext _db;
        private readonly string       _adminEmail;
        private readonly bool         _enrichmentEnabled;

        public AdminController(AppDbContext db, IConfiguration config)
        {
            _db                = db;
            _adminEmail        = config["AdminEmail"] ?? "";
            _enrichmentEnabled = config.GetValue<bool>("FeatureFlags:EnrichmentEnabled");
        }

        private bool IsAdmin() =>
            (User.FindFirst(ClaimTypes.Email)?.Value ?? "") == _adminEmail;

        // ── GET /Admin ───────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (!IsAdmin()) return Redirect("/");

            var now = DateTime.UtcNow;
            var som = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

            ViewBag.EnrichmentEnabled = _enrichmentEnabled;
            ViewBag.TotalUsers  = await _db.Users.CountAsync();
            ViewBag.TotalLeads  = await _db.Leads.CountAsync();
            ViewBag.LeadsMonth  = await _db.Leads.CountAsync(l => l.SavedAt >= som);
            if (_enrichmentEnabled)
            {
                ViewBag.TotalEnrich = await _db.Enrichments.CountAsync();
                ViewBag.EnrichMonth = await _db.Enrichments.CountAsync(e => e.CreatedAt >= som);
            }

            ViewBag.Users = await _db.Users
                .OrderByDescending(u => u.CreatedAt)
                .Select(u => new UserRow
                {
                    Id          = u.Id,
                    Email       = u.Email ?? "",
                    DisplayName = u.DisplayName ?? "",
                    Provider    = u.Provider,
                    CreatedAt   = u.CreatedAt,
                    LeadCount   = u.Leads.Count(),
                    EnrichCount = u.Enrichments.Count(),
                    LastLeadAt  = u.Leads
                                   .OrderByDescending(l => l.SavedAt)
                                   .Select(l => (DateTime?)l.SavedAt)
                                   .FirstOrDefault()
                })
                .ToListAsync();

            return View();
        }

        public class UserRow
        {
            public long      Id          { get; set; }
            public string    Email       { get; set; } = "";
            public string    DisplayName { get; set; } = "";
            public string    Provider    { get; set; } = "";
            public DateTime  CreatedAt   { get; set; }
            public int       LeadCount   { get; set; }
            public int       EnrichCount { get; set; }
            public DateTime? LastLeadAt  { get; set; }
        }
    }
}
