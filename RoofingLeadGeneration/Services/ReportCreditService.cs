using Microsoft.EntityFrameworkCore;
using RoofingLeadGeneration.Data;
using RoofingLeadGeneration.Data.Models;

namespace RoofingLeadGeneration.Services
{
    /// <summary>
    /// Grant/consume/forfeit logic for PDF hail-report credits, implementing
    /// the pricing policy locked in docs/pricing-refresh-punchlist.md:
    ///
    ///   - Trial: 1 report, granted once at signup, never expires.
    ///   - Starter ($9.99/mo): 10 reports/mo. Unused reports roll over, capped
    ///     at 3x the monthly allotment (30), and expire 60 days after the
    ///     cycle they were earned in. Forfeited on cancel/downgrade.
    ///   - Pro ($29.99/mo): 75 reports/mo. Same rollover policy (cap 225).
    ///   - Top-up ($5.99/3), 50-pack ($39.99), 100-pack ($59.99): one-time
    ///     purchases, never expire, never forfeited — kept separate from the
    ///     subscription rollover accounting below.
    ///
    /// Built on <see cref="ReportCreditGrant"/> (a per-batch ledger, not a
    /// single running balance) because that mix of expiring/non-expiring,
    /// capped/uncapped credit can't be represented as one scalar balance.
    /// Every grant/consume also writes an <see cref="OrgCreditTransaction"/>
    /// row (CreditType = "report") so the existing admin/reporting ledger
    /// surfaces report-credit activity the same way it already does for
    /// enrichment credits.
    ///
    /// NOTE: This service is fully wired but inert in production until
    /// FeatureFlags:ReportCreditsEnabled is turned on (see LeadsController.
    /// Report) — see docs/pricing-refresh-punchlist.md Workstream A for the
    /// remaining steps (Stripe checkout, existing-org migration) before that
    /// flag should flip.
    /// </summary>
    public class ReportCreditService
    {
        private readonly AppDbContext _db;

        public const int RolloverCapMultiplier = 3;
        public const int RolloverExpireDays    = 60;

        /// <summary>Monthly report allotment per recurring subscription plan. Trial is handled separately (one-time, not monthly).</summary>
        public static readonly IReadOnlyDictionary<string, int> PlanMonthlyAllotment = new Dictionary<string, int>
        {
            ["starter"] = 10,
            ["pro"]     = 75
        };

        public ReportCreditService(AppDbContext db)
        {
            _db = db;
        }

        /// <summary>Total usable report credits for an org right now (all non-expired grants, any source).</summary>
        public async Task<int> GetBalanceAsync(long orgId)
        {
            var now = DateTime.UtcNow;
            return await _db.ReportCreditGrants
                .Where(g => g.OrgId == orgId && g.Remaining > 0 && (g.ExpiresAt == null || g.ExpiresAt > now))
                .SumAsync(g => (int?)g.Remaining) ?? 0;
        }

        /// <summary>One-time trial grant (1 report, never expires) — called once at signup.</summary>
        public Task GrantTrialAsync(long orgId, long? userId, int amount = 1) =>
            GrantAsync(orgId, amount, source: "trial", expiresAt: null, userId,
                description: "Trial allotment");

        /// <summary>
        /// Grants a subscription's monthly report allotment, applying the
        /// rollover cap: existing unexpired "subscription"-sourced balance
        /// plus this grant can't exceed 3x the plan's monthly amount. If the
        /// org is already at (or above) the cap, this grants 0 — same
        /// "banked minutes" behavior as mobile-carrier rollover plans.
        /// Returns the amount actually granted (may be less than the full
        /// monthly allotment, or zero).
        /// </summary>
        public async Task<int> GrantSubscriptionAllotmentAsync(long orgId, string plan, long? userId)
        {
            if (!PlanMonthlyAllotment.TryGetValue(plan, out var monthlyAmount))
                throw new ArgumentException($"'{plan}' is not a recurring report-credit plan.", nameof(plan));

            var now = DateTime.UtcNow;
            var currentSubBalance = await _db.ReportCreditGrants
                .Where(g => g.OrgId == orgId && g.Source == "subscription" &&
                            g.Remaining > 0 && (g.ExpiresAt == null || g.ExpiresAt > now))
                .SumAsync(g => (int?)g.Remaining) ?? 0;

            var cap          = monthlyAmount * RolloverCapMultiplier;
            var room         = Math.Max(0, cap - currentSubBalance);
            var grantAmount  = Math.Min(monthlyAmount, room);

            if (grantAmount > 0)
            {
                await GrantAsync(orgId, grantAmount, source: "subscription",
                    expiresAt: now.AddDays(RolloverExpireDays), userId,
                    description: $"Monthly allotment ({plan}, {grantAmount}/{monthlyAmount} — rollover cap {cap})");
            }

            return grantAmount;
        }

        /// <summary>One-time purchase grant (top-up, 50-pack, 100-pack) — never expires, never forfeited.</summary>
        public Task GrantPurchaseAsync(long orgId, int amount, string source, long? userId, string description)
        {
            if (source is not ("purchase_topup" or "purchase_pack50" or "purchase_pack100"))
                throw new ArgumentException($"'{source}' is not a recognized purchase source.", nameof(source));

            return GrantAsync(orgId, amount, source, expiresAt: null, userId, description);
        }

        /// <summary>Manual admin grant — e.g. seeding an existing org's balance before enabling the entitlement gate. Never expires.</summary>
        public Task GrantAdminAsync(long orgId, int amount, long? adminUserId, string description) =>
            GrantAsync(orgId, amount, source: "admin_grant", expiresAt: null, adminUserId, description);

        private async Task GrantAsync(long orgId, int amount, string source, DateTime? expiresAt, long? userId, string description)
        {
            if (amount <= 0) return;

            _db.ReportCreditGrants.Add(new ReportCreditGrant
            {
                OrgId           = orgId,
                Source          = source,
                Amount          = amount,
                Remaining       = amount,
                ExpiresAt       = expiresAt,
                CreatedByUserId = userId,
                Description     = description,
                GrantedAt       = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

            var balanceAfter = await GetBalanceAsync(orgId);
            _db.OrgCreditTransactions.Add(new OrgCreditTransaction
            {
                OrgId         = orgId,
                UserId        = userId,
                CreditType    = "report",
                Amount        = amount,
                BalanceAfter  = balanceAfter,
                Description   = description,
                ReferenceType = source,
                CreatedAt     = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }

        /// <summary>
        /// Spends report credits, drawing down soonest-expiring grants first
        /// (subscription rollover before permanent purchased credits) so
        /// users don't lose credits to expiration while a permanent balance
        /// sits unused. All-or-nothing: returns false without consuming
        /// anything if the org doesn't have enough.
        /// </summary>
        public async Task<bool> ConsumeAsync(long orgId, int amount, long? userId, string? referenceId = null, string? referenceType = null, string description = "PDF hail report generated")
        {
            if (amount <= 0) return true;

            var now = DateTime.UtcNow;
            var grants = await _db.ReportCreditGrants
                .Where(g => g.OrgId == orgId && g.Remaining > 0 && (g.ExpiresAt == null || g.ExpiresAt > now))
                .OrderBy(g => g.ExpiresAt == null ? 1 : 0) // non-expiring grants sort last
                .ThenBy(g => g.ExpiresAt)
                .ToListAsync();

            var available = grants.Sum(g => g.Remaining);
            if (available < amount) return false;

            var remainingToConsume = amount;
            foreach (var grant in grants)
            {
                if (remainingToConsume <= 0) break;
                var take = Math.Min(grant.Remaining, remainingToConsume);
                grant.Remaining -= take;
                remainingToConsume -= take;
            }

            await _db.SaveChangesAsync();

            var balanceAfter = await GetBalanceAsync(orgId);
            _db.OrgCreditTransactions.Add(new OrgCreditTransaction
            {
                OrgId         = orgId,
                UserId        = userId,
                CreditType    = "report",
                Amount        = -amount,
                BalanceAfter  = balanceAfter,
                Description   = description,
                ReferenceId   = referenceId,
                ReferenceType = referenceType,
                CreatedAt     = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

            return true;
        }

        /// <summary>
        /// Zeroes out any banked subscription rollover (never touches
        /// purchase_* grants) — called when an org cancels or downgrades.
        /// Returns the amount forfeited.
        /// </summary>
        public async Task<int> ForfeitSubscriptionRolloverAsync(long orgId, long? userId = null, string reason = "Subscription canceled/downgraded")
        {
            var now = DateTime.UtcNow;
            var grants = await _db.ReportCreditGrants
                .Where(g => g.OrgId == orgId && g.Source == "subscription" &&
                            g.Remaining > 0 && (g.ExpiresAt == null || g.ExpiresAt > now))
                .ToListAsync();

            var forfeited = grants.Sum(g => g.Remaining);
            if (forfeited == 0) return 0;

            foreach (var grant in grants) grant.Remaining = 0;
            await _db.SaveChangesAsync();

            var balanceAfter = await GetBalanceAsync(orgId);
            _db.OrgCreditTransactions.Add(new OrgCreditTransaction
            {
                OrgId         = orgId,
                UserId        = userId,
                CreditType    = "report",
                Amount        = -forfeited,
                BalanceAfter  = balanceAfter,
                Description   = reason,
                ReferenceType = "forfeit",
                CreatedAt     = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

            return forfeited;
        }
    }
}
