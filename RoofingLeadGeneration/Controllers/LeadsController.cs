using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RoofingLeadGeneration.Data;
using RoofingLeadGeneration.Data.Models;
using RoofingLeadGeneration.Services;
using System.Security.Claims;
using System.Text.Json.Serialization;

namespace RoofingLeadGeneration.Controllers
{
    [Route("[controller]")]
    public class LeadsController : Controller
    {
        private readonly AppDbContext _db;

        public LeadsController(AppDbContext db) => _db = db;

        private long? CurrentUserId =>
            long.TryParse(User.FindFirst("user_db_id")?.Value, out var id) ? id : null;

        // ── GET /Leads/Saved → HTML page ────────────────────────────
        [HttpGet("Saved")]
        public IActionResult Saved() => View();

        // ── GET /Leads → JSON list ───────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var userId = CurrentUserId;

            var leads = await _db.Leads
                .Where(l => l.UserId == userId || l.UserId == null)
                .OrderBy(l => l.RiskLevel == "High" ? 0 : l.RiskLevel == "Medium" ? 1 : 2)
                .ThenByDescending(l => l.SavedAt)
                .Select(l => new
                {
                    l.Id, l.Address, l.Lat, l.Lng,
                    l.RiskLevel, l.LastStormDate, l.HailSize,
                    l.EstimatedDamage, l.PropertyType,
                    l.SourceAddress, l.SavedAt, l.Notes,
                    l.OwnerName, l.OwnerPhone, l.OwnerEmail,
                    l.YearBuilt
                })
                .ToListAsync();

            return Json(leads);
        }

        // ── POST /Leads/Save ─────────────────────────────────────────
        [HttpPost("Save")]
        public async Task<IActionResult> Save([FromBody] SaveRequest req)
        {
            if (req?.Properties == null || req.Properties.Length == 0)
                return BadRequest(new { error = "No properties provided." });

            var userId = CurrentUserId;
            int saved = 0, updated = 0;

            foreach (var p in req.Properties)
            {
                if (string.IsNullOrWhiteSpace(p.Address)) continue;

                var existing = await _db.Leads.FirstOrDefaultAsync(l => l.Address == p.Address);
                if (existing != null)
                {
                    existing.Lat             = p.Lat;
                    existing.Lng             = p.Lng;
                    existing.RiskLevel       = p.RiskLevel;
                    existing.LastStormDate   = p.LastStormDate;
                    existing.HailSize        = p.HailSize;
                    existing.EstimatedDamage = p.EstimatedDamage;
                    existing.RoofAge         = p.RoofAge;
                    existing.PropertyType    = p.PropertyType;
                    existing.SourceAddress   = req.SourceAddress;
                    existing.SavedAt         = DateTime.UtcNow;
                    existing.UserId          = userId;
                    updated++;
                }
                else
                {
                    _db.Leads.Add(new Lead
                    {
                        Address         = p.Address,
                        Lat             = p.Lat,
                        Lng             = p.Lng,
                        RiskLevel       = p.RiskLevel,
                        LastStormDate   = p.LastStormDate,
                        HailSize        = p.HailSize,
                        EstimatedDamage = p.EstimatedDamage,
                        RoofAge         = p.RoofAge,
                        PropertyType    = p.PropertyType,
                        SourceAddress   = req.SourceAddress,
                        SavedAt         = DateTime.UtcNow,
                        UserId          = userId
                    });
                    saved++;
                }
            }

            await _db.SaveChangesAsync();
            return Json(new { saved, updated });
        }

        // ── PATCH /Leads/{id}/Owner ──────────────────────────────────
        [HttpPatch("{id:long}/Owner")]
        public async Task<IActionResult> UpdateOwner(long id, [FromBody] OwnerDto dto)
        {
            var lead = await _db.Leads.FindAsync(id);
            if (lead == null) return NotFound();

            lead.OwnerName  = dto.OwnerName;
            lead.OwnerPhone = dto.OwnerPhone;
            lead.OwnerEmail = dto.OwnerEmail;

            await _db.SaveChangesAsync();
            return NoContent();
        }

        // ── POST /Leads/{id}/Enrich ──────────────────────────────────
        [HttpPost("{id:long}/Enrich")]
        public async Task<IActionResult> Enrich(long id)
        {
            var lead = await _db.Leads.FindAsync(id);
            if (lead == null) return NotFound(new { error = "Lead not found." });

            var services   = HttpContext.RequestServices;
            var config     = services.GetService<IConfiguration>();
            var realData   = services.GetRequiredService<RealDataService>();
            var bstApiKey  = config?["BatchSkipTracing:ApiKey"];

            string?  ownerName = null;
            int?     yearBuilt = null;
            string   provider  = "regrid";
            string   status    = "not_found";

            // ── Step 1: Regrid — owner name + year built ─────────────
            var parcel = await realData.GetRegridParcelDataAsync(
                lead.Lat ?? 0, lead.Lng ?? 0, lead.Address);

            if (parcel != null)
            {
                ownerName = parcel.OwnerName;
                yearBuilt = parcel.YearBuilt;
                status    = "completed";

                if (ownerName != null && lead.OwnerName == null)
                    lead.OwnerName = ownerName;
                if (yearBuilt != null)
                    lead.YearBuilt = yearBuilt;
            }

            // ── Step 2: BatchSkipTracing — phone + email (when configured) ──
            if (!string.IsNullOrWhiteSpace(bstApiKey))
            {
                provider = "batchskiptracing";
                // TODO: call BatchSkipTracing API
            }

            _db.Enrichments.Add(new Enrichment
            {
                UserId      = CurrentUserId,
                LeadId      = id,
                Address     = lead.Address,
                Status      = status,
                Provider    = provider,
                CreditsUsed = 1,
                CreatedAt   = DateTime.UtcNow
            });

            await _db.SaveChangesAsync();

            return Json(new { status, ownerName, yearBuilt, ownerPhone = lead.OwnerPhone, ownerEmail = lead.OwnerEmail });
        }

        // ── GET /Leads/Stats ─────────────────────────────────────────
        [HttpGet("Stats")]
        public async Task<IActionResult> Stats()
        {
            var userId = CurrentUserId;
            var now    = DateTime.UtcNow;
            var som    = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

            var leadsQ      = _db.Leads.Where(l => l.UserId == userId || l.UserId == null);
            var enrichmentsQ = _db.Enrichments.Where(e => e.UserId == userId);

            return Json(new
            {
                totalLeads           = await leadsQ.CountAsync(),
                leadsThisMonth       = await leadsQ.CountAsync(l => l.SavedAt >= som),
                totalEnrichments     = await enrichmentsQ.CountAsync(),
                enrichmentsThisMonth = await enrichmentsQ.CountAsync(e => e.CreatedAt >= som)
            });
        }

        // ── DELETE /Leads/{id} ───────────────────────────────────────
        [HttpDelete("{id:long}")]
        public async Task<IActionResult> Delete(long id)
        {
            var lead = await _db.Leads.FindAsync(id);
            if (lead == null) return NotFound();
            _db.Leads.Remove(lead);
            await _db.SaveChangesAsync();
            return NoContent();
        }

        // ── DTOs ─────────────────────────────────────────────────────

        public class SaveRequest
        {
            [JsonPropertyName("sourceAddress")] public string?       SourceAddress { get; set; }
            [JsonPropertyName("properties")]    public PropertyDto[]? Properties   { get; set; }
        }

        public class PropertyDto
        {
            [JsonPropertyName("address")]         public string? Address         { get; set; }
            [JsonPropertyName("lat")]             public double  Lat             { get; set; }
            [JsonPropertyName("lng")]             public double  Lng             { get; set; }
            [JsonPropertyName("riskLevel")]       public string? RiskLevel       { get; set; }
            [JsonPropertyName("lastStormDate")]   public string? LastStormDate   { get; set; }
            [JsonPropertyName("hailSize")]        public string? HailSize        { get; set; }
            [JsonPropertyName("estimatedDamage")] public string? EstimatedDamage { get; set; }
            [JsonPropertyName("roofAge")]         public int     RoofAge         { get; set; }
            [JsonPropertyName("propertyType")]    public string? PropertyType    { get; set; }
        }

        public class OwnerDto
        {
            [JsonPropertyName("ownerName")]  public string? OwnerName  { get; set; }
            [JsonPropertyName("ownerPhone")] public string? OwnerPhone { get; set; }
            [JsonPropertyName("ownerEmail")] public string? OwnerEmail { get; set; }
        }
    }
}
