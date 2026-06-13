using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RoofingLeadGeneration.Data;
using RoofingLeadGeneration.Filters;

namespace RoofingLeadGeneration.Controllers
{
    [Authorize]
    [Route("Billing")]
    [SkipTrialGate]
    public class BillingController : Controller
    {
        private readonly AppDbContext _db;

        public BillingController(AppDbContext db)
        {
            _db = db;
        }

        private long? CurrentOrgId =>
            long.TryParse(User.FindFirst("user_org_id")?.Value, out var id) ? id : null;

        // ── GET /Billing/Upgrade ───────────────────────────────────────
        // Landing spot for the post-trial hard paywall (see TrialGateFilter).
        [HttpGet("Upgrade")]
        public async Task<IActionResult> Upgrade()
        {
            var orgId = CurrentOrgId;
            var org = orgId.HasValue
                ? await _db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId.Value)
                : null;

            ViewBag.TrialEndsAt   = org?.TrialEndsAt;
            ViewBag.TrialExpired  = org?.TrialEndsAt.HasValue == true && org!.TrialEndsAt!.Value < DateTime.UtcNow;
            ViewBag.CompanyName   = org?.CompanyName ?? org?.Name;
            return View();
        }

        // ── POST /Billing/ContinueFree ──────────────────────────────────
        // No payment processor is wired up yet, so "Free" is the only plan a
        // user can self-select once their trial ends. This clears the gate.
        [HttpPost("ContinueFree")]
        public async Task<IActionResult> ContinueFree()
        {
            var orgId = CurrentOrgId;
            if (orgId.HasValue)
            {
                var org = await _db.Orgs.FindAsync(orgId.Value);
                if (org != null)
                {
                    org.Plan        = "free";
                    org.TrialEndsAt = null;
                    await _db.SaveChangesAsync();
                }
            }
            return Redirect("/");
        }
    }
}
