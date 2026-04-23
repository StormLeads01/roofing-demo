namespace RoofingLeadGeneration.Data.Models
{
    public class User
    {
        public long    Id          { get; set; }
        public string  Provider    { get; set; } = "";
        public string  ProviderId  { get; set; } = "";
        public string? Email       { get; set; }
        public string? DisplayName { get; set; }
        public DateTime CreatedAt  { get; set; } = DateTime.UtcNow;
        public bool    IsAdmin     { get; set; }
        public long?   OrgId      { get; set; }
        /// <summary>owner | manager | rep</summary>
        public string  OrgRole    { get; set; } = "owner";

        public Org?                      Org          { get; set; }
        public ICollection<Lead>         Leads        { get; set; } = new List<Lead>();
        public ICollection<Enrichment>   Enrichments  { get; set; } = new List<Enrichment>();
        public ICollection<WatchedArea>  WatchedAreas { get; set; } = new List<WatchedArea>();
        public ICollection<SentAlert>    SentAlerts   { get; set; } = new List<SentAlert>();
    }
}
