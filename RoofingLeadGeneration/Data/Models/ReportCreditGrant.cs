namespace RoofingLeadGeneration.Data.Models
{
    /// <summary>
    /// One "batch" of PDF hail-report credits granted to an org — a trial
    /// allotment, a monthly subscription allotment (with rollover), or a
    /// one-time purchase (top-up / bulk pack).
    ///
    /// Unlike <see cref="OrgCredit"/> (a single running balance per org+type),
    /// report credits need per-batch expiration: subscription allotments roll
    /// over capped at 3x the monthly amount and expire 60 days after the
    /// cycle they were earned in, while purchased credits (top-up, 50-pack,
    /// 100-pack) never expire and are never forfeited. That mix can't be
    /// expressed as one scalar balance, so each grant is its own row with its
    /// own remaining balance and (nullable) expiration.
    ///
    /// See docs/pricing-refresh-punchlist.md for the pricing/rollover policy
    /// this implements, and <see cref="Services.ReportCreditService"/> for the
    /// grant/consume/forfeit logic built on top of this table.
    /// </summary>
    public class ReportCreditGrant
    {
        public long Id    { get; set; }
        public long OrgId { get; set; }

        /// <summary>trial | subscription | purchase_topup | purchase_pack50 | purchase_pack100 | admin_grant</summary>
        public string Source { get; set; } = "";

        /// <summary>Original size of this grant.</summary>
        public int Amount { get; set; }

        /// <summary>Unspent credits left in this specific grant. Decremented on use (FIFO, soonest-expiring first).</summary>
        public int Remaining { get; set; }

        /// <summary>When this batch expires and becomes unusable (even if Remaining > 0). Null = never expires (all purchase_* sources).</summary>
        public DateTime? ExpiresAt { get; set; }

        /// <summary>User who triggered the grant (null for system/admin-initiated grants).</summary>
        public long? CreatedByUserId { get; set; }

        /// <summary>Human-readable note, e.g. "Monthly allotment (starter)" or "Purchased: 50-report pack".</summary>
        public string Description { get; set; } = "";

        public DateTime GrantedAt { get; set; } = DateTime.UtcNow;

        public Org? Org { get; set; }
    }
}
