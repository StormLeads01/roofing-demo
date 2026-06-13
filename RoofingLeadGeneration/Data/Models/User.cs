namespace RoofingLeadGeneration.Data.Models
{
    public class User
    {
        public long    Id          { get; set; }
        public string  Provider    { get; set; } = "";
        public string  ProviderId  { get; set; } = "";
        public string? Email       { get; set; }
        public string? DisplayName { get; set; }
        /// <summary>ASP.NET Core Identity password hash — only set for Provider == "password" accounts created via signup.</summary>
        public string? PasswordHash { get; set; }
        public DateTime CreatedAt  { get; set; } = DateTime.UtcNow;
        public bool    IsAdmin     { get; set; }
        public long?   OrgId      { get; set; }
        /// <summary>owner | manager | rep</summary>
        public string  OrgRole    { get; set; } = "owner";

        // ── My Profile ───────────────────────────────────────────
        /// <summary>Personal contact phone number, shown on My Profile</summary>
        public string? Phone             { get; set; }
        /// <summary>Email used for alert/notification delivery (may differ from login email)</summary>
        public string? NotificationEmail { get; set; }

        public Org?                      Org          { get; set; }
        public ICollection<Lead>         Leads        { get; set; } = new List<Lead>();
        public ICollection<Enrichment>   Enrichments  { get; set; } = new List<Enrichment>();
        public ICollection<WatchedArea>  WatchedAreas { get; set; } = new List<WatchedArea>();
        public ICollection<SentAlert>    SentAlerts   { get; set; } = new List<SentAlert>();
    }
}
