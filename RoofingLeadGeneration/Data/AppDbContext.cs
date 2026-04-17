using Microsoft.EntityFrameworkCore;
using RoofingLeadGeneration.Data.Models;

namespace RoofingLeadGeneration.Data
{
    public class AppDbContext : DbContext
    {
        public DbSet<User>        Users        => Set<User>();
        public DbSet<Lead>        Leads        => Set<Lead>();
        public DbSet<Enrichment>  Enrichments  => Set<Enrichment>();

        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder m)
        {
            // ── User ─────────────────────────────────────────────────────
            m.Entity<User>(e =>
            {
                e.ToTable("users");
                e.HasKey(u => u.Id);
                e.Property(u => u.Id).HasColumnName("id");
                e.Property(u => u.Provider).HasColumnName("provider").IsRequired();
                e.Property(u => u.ProviderId).HasColumnName("provider_id").IsRequired();
                e.Property(u => u.Email).HasColumnName("email");
                e.Property(u => u.DisplayName).HasColumnName("display_name");
                e.Property(u => u.CreatedAt).HasColumnName("created_at")
                 .HasDefaultValueSql("datetime('now')");
                e.Property(u => u.IsAdmin).HasColumnName("is_admin").HasDefaultValue(false);

                e.HasIndex(u => new { u.Provider, u.ProviderId }).IsUnique();
            });

            // ── Lead ─────────────────────────────────────────────────────
            m.Entity<Lead>(e =>
            {
                e.ToTable("leads");
                e.HasKey(l => l.Id);
                e.Property(l => l.Id).HasColumnName("id");
                e.Property(l => l.Address).HasColumnName("address").IsRequired();
                e.Property(l => l.Lat).HasColumnName("lat");
                e.Property(l => l.Lng).HasColumnName("lng");
                e.Property(l => l.RiskLevel).HasColumnName("risk_level");
                e.Property(l => l.LastStormDate).HasColumnName("last_storm_date");
                e.Property(l => l.HailSize).HasColumnName("hail_size");
                e.Property(l => l.EstimatedDamage).HasColumnName("estimated_damage");
                e.Property(l => l.RoofAge).HasColumnName("roof_age");
                e.Property(l => l.YearBuilt).HasColumnName("year_built");
                e.Property(l => l.PropertyType).HasColumnName("property_type");
                e.Property(l => l.SourceAddress).HasColumnName("source_address");
                e.Property(l => l.SavedAt).HasColumnName("saved_at")
                 .HasDefaultValueSql("datetime('now')");
                e.Property(l => l.Notes).HasColumnName("notes");
                e.Property(l => l.OwnerName).HasColumnName("owner_name");
                e.Property(l => l.OwnerPhone).HasColumnName("owner_phone");
                e.Property(l => l.OwnerEmail).HasColumnName("owner_email");
                e.Property(l => l.UserId).HasColumnName("user_id");

                e.HasIndex(l => l.Address).IsUnique();
                e.HasIndex(l => l.RiskLevel);

                e.HasOne(l => l.User)
                 .WithMany(u => u.Leads)
                 .HasForeignKey(l => l.UserId)
                 .OnDelete(DeleteBehavior.SetNull);
            });

            // ── Enrichment ───────────────────────────────────────────────
            m.Entity<Enrichment>(e =>
            {
                e.ToTable("enrichments");
                e.HasKey(en => en.Id);
                e.Property(en => en.Id).HasColumnName("id");
                e.Property(en => en.UserId).HasColumnName("user_id");
                e.Property(en => en.LeadId).HasColumnName("lead_id");
                e.Property(en => en.Address).HasColumnName("address");
                e.Property(en => en.Status).HasColumnName("status").HasDefaultValue("pending");
                e.Property(en => en.Provider).HasColumnName("provider").HasDefaultValue("batchskiptracing");
                e.Property(en => en.CreditsUsed).HasColumnName("credits_used").HasDefaultValue(1);
                e.Property(en => en.CreatedAt).HasColumnName("created_at")
                 .HasDefaultValueSql("datetime('now')");

                e.HasIndex(en => en.UserId);
                e.HasIndex(en => en.CreatedAt);

                e.HasOne(en => en.User)
                 .WithMany(u => u.Enrichments)
                 .HasForeignKey(en => en.UserId)
                 .OnDelete(DeleteBehavior.SetNull);

                e.HasOne(en => en.Lead)
                 .WithMany(l => l.Enrichments)
                 .HasForeignKey(en => en.LeadId)
                 .OnDelete(DeleteBehavior.SetNull);
            });
        }
    }
}
