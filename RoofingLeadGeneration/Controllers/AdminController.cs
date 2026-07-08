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
            string.Equals(User.FindFirst(ClaimTypes.Email)?.Value ?? "", _adminEmail, StringComparison.OrdinalIgnoreCase);

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
                                   .FirstOrDefault(),
                    OrgId       = u.OrgId,
                    Plan        = u.Org != null ? u.Org.Plan : null,
                    TrialEndsAt = u.Org != null ? u.Org.TrialEndsAt : null
                })
                .ToListAsync();

            return View();
        }

        // ── POST /Admin/Users/{id}/Plan ──────────────────────────────
        // Sets the plan tier on the user's org. Affects the whole org.
        [HttpPost("Users/{id:long}/Plan")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetPlan(long id, string plan)
        {
            if (!IsAdmin()) return Redirect("/");

            var valid = new[] { "free", "pro", "agency" };
            if (!valid.Contains(plan))
            {
                TempData["AdminError"] = "Invalid plan value.";
                return RedirectToAction(nameof(Index));
            }

            var org = await ResolveOrgForUserAsync(id);
            if (org == null)
            {
                TempData["AdminError"] = "That user has no organization.";
                return RedirectToAction(nameof(Index));
            }

            org.Plan = plan;
            await _db.SaveChangesAsync();
            TempData["AdminOk"] = $"Plan set to \"{plan}\".";
            return RedirectToAction(nameof(Index));
        }

        // ── POST /Admin/Users/{id}/Trial ─────────────────────────────
        // op = set | extend | unlimited | expire.  Affects the whole org.
        //   set       → TrialEndsAt = now + days
        //   extend    → add days to the current end (or from now if past/none)
        //   unlimited → TrialEndsAt = null (never gated)
        //   expire    → end the trial immediately
        [HttpPost("Users/{id:long}/Trial")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetTrial(long id, string op, int days = 0)
        {
            if (!IsAdmin()) return Redirect("/");

            var org = await ResolveOrgForUserAsync(id);
            if (org == null)
            {
                TempData["AdminError"] = "That user has no organization.";
                return RedirectToAction(nameof(Index));
            }

            var now = DateTime.UtcNow;
            days = Math.Max(0, days);

            switch (op)
            {
                case "unlimited":
                    org.TrialEndsAt = null;
                    TempData["AdminOk"] = "Trial set to unlimited (never gated).";
                    break;
                case "expire":
                    org.TrialEndsAt = now.AddMinutes(-1);
                    TempData["AdminOk"] = "Trial expired immediately.";
                    break;
                case "extend":
                    var basis = (org.TrialEndsAt.HasValue && org.TrialEndsAt.Value > now)
                        ? org.TrialEndsAt.Value : now;
                    org.TrialEndsAt = basis.AddDays(days);
                    TempData["AdminOk"] = $"Trial extended by {days} day(s).";
                    break;
                default: // "set"
                    org.TrialEndsAt = now.AddDays(days);
                    TempData["AdminOk"] = $"Trial set to {days} day(s) from now.";
                    break;
            }

            await _db.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        // Resolve the org that owns a given user id (trial/plan live on the org).
        private async Task<Data.Models.Org?> ResolveOrgForUserAsync(long userId)
        {
            var user = await _db.Users.FindAsync(userId);
            if (user?.OrgId == null) return null;
            return await _db.Orgs.FindAsync(user.OrgId.Value);
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
            public long?     OrgId       { get; set; }
            public string?   Plan        { get; set; }
            public DateTime? TrialEndsAt { get; set; }
        }
    }
}
