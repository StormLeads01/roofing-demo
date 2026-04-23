using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RoofingLeadGeneration.Data;
using System.Security.Claims;

namespace RoofingLeadGeneration.Controllers
{
    [Authorize]
    [Route("[controller]")]
    public class DashboardController : Controller
    {
        private readonly AppDbContext _db;

        public DashboardController(AppDbContext db) => _db = db;

        private long? CurrentUserId =>
            long.TryParse(User.FindFirst("user_db_id")?.Value, out var id) ? id : null;

        private long? CurrentOrgId =>
            long.TryParse(User.FindFirst("user_org_id")?.Value, out var id) ? id : null;

        private bool IsAdmin() =>
            (User.FindFirst(ClaimTypes.Email)?.Value ?? "") == "jaholder78@gmail.com";

        // ── GET /Dashboard ───────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var orgId  = CurrentOrgId;
            var userId = CurrentUserId;
            var now    = DateTime.UtcNow;
            var som    = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

            var leadsQ       = _db.Leads.Where(l => l.OrgId == orgId || l.OrgId == null);
            var enrichmentsQ = _db.Enrichments.Where(e => e.UserId == userId);

            ViewBag.UserName           = User.Identity?.Name ?? "Guest";
            ViewBag.IsAdmin            = IsAdmin();
            ViewBag.TotalLeads         = await leadsQ.CountAsync();
            ViewBag.LeadsThisMonth     = await leadsQ.CountAsync(l => l.SavedAt >= som);
            ViewBag.TotalEnrich        = await enrichmentsQ.CountAsync();
            ViewBag.EnrichThisMonth    = await enrichmentsQ.CountAsync(e => e.CreatedAt >= som);

            var rawLeads = await leadsQ
                .OrderByDescending(l => l.SavedAt)
                .Take(8)
                .Select(l => new { l.Address, l.RiskLevel, l.HailSize, l.SavedAt })
                .ToListAsync();

            ViewBag.RecentLeads = rawLeads
                .Select(l => (
                    Address: l.Address ?? "",
                    Risk:    l.RiskLevel ?? "",
                    Hail:    l.HailSize  ?? "",
                    SavedAt: l.SavedAt.ToString("o")
                ))
                .ToList();

            var rawEnrich = await enrichmentsQ
                .OrderByDescending(e => e.CreatedAt)
                .Take(5)
                .Select(e => new { e.Address, e.Status, e.CreatedAt })
                .ToListAsync();

            ViewBag.RecentEnrich = rawEnrich
                .Select(e => (
                    Address:   e.Address   ?? "",
                    Status:    e.Status    ?? "",
                    CreatedAt: e.CreatedAt.ToString("o")
                ))
                .ToList();

            return View();
        }
    }
}
