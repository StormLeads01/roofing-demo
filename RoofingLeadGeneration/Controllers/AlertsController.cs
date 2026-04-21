using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RoofingLeadGeneration.Data;
using RoofingLeadGeneration.Data.Models;
using RoofingLeadGeneration.Services;
using System.Security.Claims;

namespace RoofingLeadGeneration.Controllers
{
    [Authorize]
    [Route("[controller]")]
    public class AlertsController : Controller
    {
        private readonly AppDbContext  _db;
        private readonly RealDataService _realData;

        public AlertsController(AppDbContext db, RealDataService realData)
        {
            _db       = db;
            _realData = realData;
        }

        private long? CurrentUserId =>
            long.TryParse(User.FindFirst("user_db_id")?.Value, out var id) ? id : null;

        // ── GET /Alerts ───────────────────────────────────────────────────────
        [HttpGet("")]
        public IActionResult Index() => View();

        // ── GET /Alerts/List — returns JSON list of watched areas ─────────────
        [HttpGet("List")]
        public async Task<IActionResult> List()
        {
            var userId = CurrentUserId;
            var areas  = await _db.WatchedAreas
                .Where(w => w.UserId == userId || w.UserId == null)
                .OrderByDescending(w => w.CreatedAt)
                .Select(w => new {
                    w.Id, w.Label, w.CenterLat, w.CenterLng,
                    w.RadiusMiles, w.MinHailSizeInches, w.AlertsEnabled, w.CreatedAt
                })
                .ToListAsync();

            return Json(areas);
        }

        // ── POST /Alerts — create a new watched area ──────────────────────────
        [HttpPost("")]
        public async Task<IActionResult> Create([FromBody] CreateWatchedAreaRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Address))
                return BadRequest(new { error = "Address is required." });

            // Geocode the address to get lat/lng
            var geoKey = HttpContext.RequestServices
                .GetRequiredService<IConfiguration>()["GoogleMaps:ApiKey"] ?? "";

            var geo = await GeocodeAsync(req.Address, geoKey);
            if (geo == null)
                return BadRequest(new { error = "Could not geocode that address. Try a more specific address or zip code." });

            var area = new WatchedArea
            {
                UserId            = CurrentUserId,
                Label             = geo.FormattedAddress,
                CenterLat         = geo.Lat,
                CenterLng         = geo.Lng,
                RadiusMiles       = req.RadiusMiles,
                MinHailSizeInches = req.MinHailSizeInches,
                AlertsEnabled     = true,
                CreatedAt         = DateTime.UtcNow
            };

            _db.WatchedAreas.Add(area);
            await _db.SaveChangesAsync();

            return Json(new {
                area.Id, area.Label, area.CenterLat, area.CenterLng,
                area.RadiusMiles, area.MinHailSizeInches, area.AlertsEnabled
            });
        }

        // ── PATCH /Alerts/{id} — update radius, threshold, or toggle ─────────
        [HttpPatch("{id:long}")]
        public async Task<IActionResult> Update(long id, [FromBody] UpdateWatchedAreaRequest req)
        {
            var userId = CurrentUserId;
            var area   = await _db.WatchedAreas.FirstOrDefaultAsync(
                w => w.Id == id && (w.UserId == userId || w.UserId == null));

            if (area == null) return NotFound();

            if (req.RadiusMiles.HasValue)       area.RadiusMiles       = req.RadiusMiles.Value;
            if (req.MinHailSizeInches.HasValue)  area.MinHailSizeInches = req.MinHailSizeInches.Value;
            if (req.AlertsEnabled.HasValue)      area.AlertsEnabled     = req.AlertsEnabled.Value;

            await _db.SaveChangesAsync();
            return NoContent();
        }

        // ── DELETE /Alerts/{id} ───────────────────────────────────────────────
        [HttpDelete("{id:long}")]
        public async Task<IActionResult> Delete(long id)
        {
            var userId = CurrentUserId;
            var area   = await _db.WatchedAreas.FirstOrDefaultAsync(
                w => w.Id == id && (w.UserId == userId || w.UserId == null));

            if (area == null) return NotFound();

            _db.WatchedAreas.Remove(area);
            await _db.SaveChangesAsync();
            return NoContent();
        }

        // ── GET /Alerts/History — recent sent alerts for this user ────────────
        [HttpGet("History")]
        public async Task<IActionResult> History()
        {
            var userId = CurrentUserId;
            var alerts = await _db.SentAlerts
                .Include(s => s.WatchedArea)
                .Where(s => s.UserId == userId || s.UserId == null)
                .OrderByDescending(s => s.SentAt)
                .Take(50)
                .Select(s => new {
                    s.Id, s.EventDate, s.HailSizeInches, s.SentAt,
                    areaLabel = s.WatchedArea == null ? "" : s.WatchedArea.Label
                })
                .ToListAsync();

            return Json(alerts);
        }

        // ── Geocoding helper ──────────────────────────────────────────────────
        private static async Task<GeoResult?> GeocodeAsync(string address, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return null;
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var url  = $"https://maps.googleapis.com/maps/api/geocode/json" +
                           $"?address={Uri.EscapeDataString(address)}&key={apiKey}";
                var json = await client.GetStringAsync(url);
                using var doc  = System.Text.Json.JsonDocument.Parse(json);
                var       root = doc.RootElement;
                if (root.GetProperty("status").GetString() != "OK") return null;
                var result    = root.GetProperty("results")[0];
                var loc       = result.GetProperty("geometry").GetProperty("location");
                var formatted = result.GetProperty("formatted_address").GetString() ?? address;
                return new GeoResult(formatted, loc.GetProperty("lat").GetDouble(), loc.GetProperty("lng").GetDouble());
            }
            catch { return null; }
        }

        // ── DTOs ──────────────────────────────────────────────────────────────
        private record GeoResult(string FormattedAddress, double Lat, double Lng);

        public record CreateWatchedAreaRequest(
            string Address,
            double RadiusMiles       = 10.0,
            double MinHailSizeInches = 1.0);

        public record UpdateWatchedAreaRequest(
            double? RadiusMiles,
            double? MinHailSizeInches,
            bool?   AlertsEnabled);
    }
}
