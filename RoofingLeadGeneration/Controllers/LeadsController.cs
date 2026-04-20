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

        // ── GET /Leads?tab=unenriched|pipeline|closed|archived ───────
        [HttpGet]
        public async Task<IActionResult> Index(string tab = "unenriched")
        {
            var userId = CurrentUserId;

            var activeStatuses = new[] { "new", "contacted", "appointment_set" };
            var closedStatuses = new[] { "closed_won", "closed_lost" };

            IQueryable<Lead> query;
            if (tab == "archived")
            {
                query = _db.Leads
                    .Where(l => (l.UserId == userId || l.UserId == null) && l.DeletedAt != null);
            }
            else
            {
                query = _db.Leads
                    .Where(l => (l.UserId == userId || l.UserId == null) && l.DeletedAt == null);
                query = tab switch
                {
                    "pipeline" => query.Where(l => l.IsEnriched && activeStatuses.Contains(l.Status)),
                    "closed"   => query.Where(l => l.IsEnriched && closedStatuses.Contains(l.Status)),
                    _          => query.Where(l => !l.IsEnriched)   // unenriched (default)
                };
            }

            var leads = await query
                .OrderBy(l => l.RiskLevel == "High" ? 0 : l.RiskLevel == "Medium" ? 1 : 2)
                .ThenByDescending(l => l.SavedAt)
                .Select(l => new
                {
                    l.Id, l.Address, l.Lat, l.Lng,
                    l.RiskLevel, l.LastStormDate, l.HailSize,
                    l.EstimatedDamage, l.PropertyType,
                    l.SourceAddress, l.SavedAt, l.Notes,
                    l.OwnerName, l.OwnerPhone, l.OwnerEmail,
                    l.YearBuilt, l.IsEnriched, l.Status,
                    Contacts = l.Contacts.Select(c => new {
                        c.Id, c.Name, c.Phone, c.Email,
                        c.ContactType, c.IsPrimary, c.Source
                    }).ToList()
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
                    // Restore if previously archived
                    existing.DeletedAt       = null;
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

            return Json(await EnrichLeadAsync(lead));
        }

        // ── POST /Leads/BulkEnrich ───────────────────────────────────
        [HttpPost("BulkEnrich")]
        public async Task<IActionResult> BulkEnrich([FromBody] BulkRequest req)
        {
            if (req?.Ids == null || req.Ids.Length == 0)
                return BadRequest(new { error = "No lead IDs provided." });

            var userId = CurrentUserId;
            var leads  = await _db.Leads
                .Where(l => req.Ids.Contains(l.Id) &&
                            (l.UserId == userId || l.UserId == null) &&
                            !l.IsEnriched && l.DeletedAt == null)
                .ToListAsync();

            var results = new List<object>();
            foreach (var lead in leads)
            {
                var r = await EnrichLeadAsync(lead);
                results.Add(new { id = lead.Id, result = r });
            }

            return Json(new { processed = results.Count, results });
        }

        // ── PATCH /Leads/{id}/Notes ─────────────────────────────────
        [HttpPatch("{id:long}/Notes")]
        public async Task<IActionResult> PatchNotes(long id, [FromBody] PatchNotesRequest req)
        {
            var userId = CurrentUserId;
            var lead   = await _db.Leads.FindAsync(id);
            if (lead == null || (lead.UserId != userId && lead.UserId != null))
                return NotFound(new { error = "Lead not found." });

            lead.Notes = req.Notes?.Trim();
            await _db.SaveChangesAsync();
            return Json(new { saved = true });
        }

        // ── PATCH /Leads/{id}/Status ─────────────────────────────────
        [HttpPatch("{id:long}/Status")]
        public async Task<IActionResult> PatchStatus(long id, [FromBody] PatchStatusRequest req)
        {
            var valid = new[] { "new", "contacted", "appointment_set", "closed_won", "closed_lost" };
            if (!valid.Contains(req.Status))
                return BadRequest(new { error = "Invalid status value." });

            var userId = CurrentUserId;
            var lead   = await _db.Leads.FindAsync(id);
            if (lead == null || (lead.UserId != userId && lead.UserId != null))
                return NotFound(new { error = "Lead not found." });

            lead.Status = req.Status;
            await _db.SaveChangesAsync();
            return Json(new { saved = true, status = req.Status });
        }

        // ── POST /Leads/{id}/Restore ─────────────────────────────────
        [HttpPost("{id:long}/Restore")]
        public async Task<IActionResult> Restore(long id)
        {
            var userId = CurrentUserId;
            var lead   = await _db.Leads.FindAsync(id);
            if (lead == null || (lead.UserId != userId && lead.UserId != null))
                return NotFound(new { error = "Lead not found." });

            lead.DeletedAt = null;
            await _db.SaveChangesAsync();
            return Json(new { restored = true });
        }

        // ── POST /Leads/BulkArchive ──────────────────────────────────
        // Soft-deletes leads — enriched leads are protected and skipped.
        [HttpPost("BulkArchive")]
        public async Task<IActionResult> BulkArchive([FromBody] BulkRequest req)
        {
            if (req?.Ids == null || req.Ids.Length == 0)
                return BadRequest(new { error = "No lead IDs provided." });

            var userId = CurrentUserId;
            var leads  = await _db.Leads
                .Where(l => req.Ids.Contains(l.Id) &&
                            (l.UserId == userId || l.UserId == null) &&
                            !l.IsEnriched && l.DeletedAt == null)
                .ToListAsync();

            var now = DateTime.UtcNow;
            foreach (var lead in leads)
                lead.DeletedAt = now;

            await _db.SaveChangesAsync();
            return Json(new { archived = leads.Count });
        }

        // ── GET /Leads/Stats ─────────────────────────────────────────
        [HttpGet("Stats")]
        public async Task<IActionResult> Stats()
        {
            var userId = CurrentUserId;
            var now    = DateTime.UtcNow;
            var som    = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

            var allLeadsQ    = _db.Leads.Where(l => l.UserId == userId || l.UserId == null);
            var activeLeadsQ = allLeadsQ.Where(l => l.DeletedAt == null);
            var enrichmentsQ = _db.Enrichments.Where(e => e.UserId == userId);

            return Json(new
            {
                totalLeads           = await activeLeadsQ.CountAsync(),
                leadsThisMonth       = await activeLeadsQ.CountAsync(l => l.SavedAt >= som),
                unenrichedCount      = await activeLeadsQ.CountAsync(l => !l.IsEnriched),
                pipelineCount        = await activeLeadsQ.CountAsync(l => l.IsEnriched && new[] { "new", "contacted", "appointment_set" }.Contains(l.Status)),
                closedCount          = await activeLeadsQ.CountAsync(l => l.IsEnriched && new[] { "closed_won", "closed_lost" }.Contains(l.Status)),
                archivedCount        = await allLeadsQ.CountAsync(l => l.DeletedAt != null),
                totalEnrichments     = await enrichmentsQ.CountAsync(),
                enrichmentsThisMonth = await enrichmentsQ.CountAsync(e => e.CreatedAt >= som)
            });
        }

        // ── DELETE /Leads/{id} — soft delete ─────────────────────────
        [HttpDelete("{id:long}")]
        public async Task<IActionResult> Delete(long id)
        {
            var lead = await _db.Leads.FindAsync(id);
            if (lead == null) return NotFound();
            if (lead.IsEnriched)
                return BadRequest(new { error = "Enriched leads cannot be deleted." });

            lead.DeletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return NoContent();
        }

        // ─────────────────────────────────────────────────────────────
        // Shared enrichment logic
        // ─────────────────────────────────────────────────────────────
        private async Task<object> EnrichLeadAsync(Lead lead)
        {
            var services  = HttpContext.RequestServices;
            var config    = services.GetService<IConfiguration>();
            var realData  = services.GetRequiredService<RealDataService>();
            var bstApiKey = config?["BatchSkipTracing:ApiKey"];
            var wpApiKey  = config?["WhitepagesPro:ApiKey"];

            string? ownerName = null;
            int?    yearBuilt = null;
            string  provider  = "regrid";
            string  status    = "not_found";

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

            // ── Step 2: Whitepages Pro — phone + email ────────────────
            if (!string.IsNullOrWhiteSpace(wpApiKey))
            {
                provider = "whitepages";
                var contacts = await realData.GetWhitepagesContactAsync(
                    wpApiKey, lead.OwnerName, lead.Address);

                if (contacts.Count > 0)
                {
                    // Remove stale contacts from a previous enrich
                    var old = _db.LeadContacts.Where(c => c.LeadId == lead.Id);
                    _db.LeadContacts.RemoveRange(old);

                    for (int i = 0; i < contacts.Count; i++)
                    {
                        var c       = contacts[i];
                        var primary = i == 0;

                        _db.LeadContacts.Add(new Data.Models.LeadContact
                        {
                            LeadId      = lead.Id,
                            Name        = c.OwnerName,
                            Phone       = c.Phone,
                            Email       = c.Email,
                            ContactType = c.ContactType,
                            IsPrimary   = primary,
                            Source      = "whitepages",
                            CreatedAt   = DateTime.UtcNow
                        });

                        // Populate legacy fields from the primary contact
                        if (primary)
                        {
                            if (c.OwnerName != null && lead.OwnerName == null)
                                lead.OwnerName = c.OwnerName;
                            if (c.Phone != null && lead.OwnerPhone == null)
                                lead.OwnerPhone = c.Phone;
                            if (c.Email != null && lead.OwnerEmail == null)
                                lead.OwnerEmail = c.Email;
                        }
                    }

                    status = "completed";
                }
            }
            // ── Step 2b: BatchSkipTracing fallback (if WP not configured) ─
            else if (!string.IsNullOrWhiteSpace(bstApiKey))
            {
                provider = "batchskiptracing";
                var contact = await realData.GetBstContactAsync(
                    bstApiKey, lead.OwnerName, lead.Address);

                if (contact != null)
                {
                    if (contact.Phone != null && lead.OwnerPhone == null)
                        lead.OwnerPhone = contact.Phone;
                    if (contact.Email != null && lead.OwnerEmail == null)
                        lead.OwnerEmail = contact.Email;

                    status = "completed";
                }
            }

            // Mark as enriched if we got anything useful
            if (status == "completed")
                lead.IsEnriched = true;

            _db.Enrichments.Add(new Enrichment
            {
                UserId      = CurrentUserId,
                LeadId      = lead.Id,
                Address     = lead.Address,
                Status      = status,
                Provider    = provider,
                CreditsUsed = 1,
                CreatedAt   = DateTime.UtcNow
            });

            await _db.SaveChangesAsync();

            return new { status, ownerName, yearBuilt, ownerPhone = lead.OwnerPhone, ownerEmail = lead.OwnerEmail };
        }

        // ── GET /Leads/WpDebug?name=John+Smith&address=123+Main+St,Dallas,TX+75201 ──
        [HttpGet("WpDebug")]
        public async Task<IActionResult> WpDebug(string? name, string? address, bool mock = false)
        {
            var config   = HttpContext.RequestServices.GetService<IConfiguration>();
            var realData = HttpContext.RequestServices.GetRequiredService<RealDataService>();
            var apiKey   = config?["WhitepagesPro:ApiKey"];

            // ?mock=true — parse the sample response without hitting the API
            if (mock)
            {
                const string sampleJson = """
                {
                  "result": {
                    "ownership_info": {
                      "owner_type": "Business",
                      "business_owners": [{ "name": "United States Of America" }],
                      "person_owners": [{
                        "id": "PX3vr2aM2E3",
                        "name": "Donald Duck",
                        "phones": [{ "number": "12015215520", "type": "Landline" }],
                        "emails": [{ "email": "sample.email@gmail.com" }]
                      }]
                    },
                    "residents": [{
                      "id": "PX3vr2aM2E3",
                      "name": "Donald Duck",
                      "phones": [{ "number": "12015215520", "type": "Landline" }],
                      "emails": [{ "email": "sample.email@gmail.com" }]
                    }]
                  }
                }
                """;
                var parsed = realData.ParseWpResponsePublic(sampleJson);
                return Json(new { mock = true, contacts = parsed });
            }

            if (string.IsNullOrWhiteSpace(apiKey))
                return Content("WhitepagesPro:ApiKey not configured", "text/plain");
            if (string.IsNullOrWhiteSpace(address))
                return Content("Pass ?address=123+Main+St,City,ST+Zip  or  ?mock=true", "text/plain");

            // Parse name + address the same way the service does
            var cleaned  = address.Replace(", USA","").Replace(", United States","");
            var parts    = cleaned.Split(',');
            var street   = parts.Length > 0 ? parts[0].Trim() : cleaned;
            var city     = parts.Length > 1 ? parts[1].Trim() : "";
            var stateZip = parts.Length > 2 ? parts[2].Trim().Split(' ') : Array.Empty<string>();
            var state    = stateZip.Length > 0 ? stateZip[0] : "";

            var qs  = $"street={Uri.EscapeDataString(street)}&city={Uri.EscapeDataString(city)}&state_code={Uri.EscapeDataString(state)}";
            var url = $"https://api.whitepages.com/v2/property/?{qs}";

            using var http = new System.Net.Http.HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
            var resp = await http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();

            return Content($"Status: {(int)resp.StatusCode} {resp.StatusCode}\nURL: {url}\n\n{body}", "text/plain");
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

        public class BulkRequest
        {
            [JsonPropertyName("ids")] public long[]? Ids { get; set; }
        }

        public class PatchNotesRequest
        {
            [JsonPropertyName("notes")] public string? Notes { get; set; }
        }

        public class PatchStatusRequest
        {
            [JsonPropertyName("status")] public string Status { get; set; } = "new";
        }
    }
}
