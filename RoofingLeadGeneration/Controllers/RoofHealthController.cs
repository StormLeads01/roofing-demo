using Microsoft.AspNetCore.Mvc;
using RoofingLeadGeneration.Services;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoofingLeadGeneration.Controllers
{
    [Route("[controller]")]
    public class RoofHealthController : Controller
    {
        private readonly RealDataService _realData;

        private const string ApiKey        = "AIzaSyB2YmUC-KAbjTUSO4p9NNIaG_3af4iTevM";
        private const string GeocodingBase = "https://maps.googleapis.com/maps/api/geocode/json";

        public RoofHealthController(RealDataService realData)
        {
            _realData = realData;
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /RoofHealth/Neighborhood?address=...&radius=0.5
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("Neighborhood")]
        public async Task<IActionResult> Neighborhood(
            string address, double radius = 0.5, double lat = 0, double lng = 0)
        {
            if (string.IsNullOrWhiteSpace(address))
                return BadRequest(new { error = "Address is required." });

            string formattedAddress = address;

            if (lat == 0 || lng == 0)
            {
                var center = await GeocodeAsync(address);
                if (center == null)
                    return BadRequest(new { error = "Could not geocode the provided address." });
                lat              = center.Lat;
                lng              = center.Lng;
                formattedAddress = center.FormattedAddress;
            }

            var (properties, hailEventCount) = await GetPropertiesAsync(formattedAddress, lat, lng, radius);

            return Json(new { centerAddress = formattedAddress, lat, lng, hailEventCount, osmCount = properties.Count, properties },
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented        = false
                });
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /RoofHealth/Export?address=...&radius=0.5
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("Export")]
        public async Task<IActionResult> Export(
            string address, double radius = 0.5, double lat = 0, double lng = 0)
        {
            if (string.IsNullOrWhiteSpace(address))
                return BadRequest("Address is required.");

            if (lat == 0 || lng == 0)
            {
                var center = await GeocodeAsync(address);
                if (center == null)
                    return BadRequest("Could not geocode the provided address.");
                lat = center.Lat;
                lng = center.Lng;
            }

            var (properties, _) = await GetPropertiesAsync(address, lat, lng, radius);

            var sb = new StringBuilder();
            sb.AppendLine("Address,Latitude,Longitude,Risk Level,Last Storm Date,Hail Size,Data Source");

            foreach (var p in properties)
            {
                sb.AppendLine(
                    $"\"{p.Address}\",{p.Lat},{p.Lng},\"{p.RiskLevel}\",\"{p.LastStormDate}\"," +
                    $"\"{p.HailSize}\",\"{p.DataSource}\"");
            }

            var bytes    = Encoding.UTF8.GetBytes(sb.ToString());
            var fileName = $"StormLead_Export_{DateTime.Now:yyyyMMdd_HHmm}.csv";
            return File(bytes, "text/csv", fileName);
        }

        // ─────────────────────────────────────────────────────────────────
        // Core data-fetch logic
        //   Real OSM addresses + real NOAA hail data only.
        //   Returns whatever OSM finds — no simulated fallback.
        // ─────────────────────────────────────────────────────────────────
        private async Task<(List<PropertyRecord> Records, int HailEventCount)> GetPropertiesAsync(
            string centerAddress, double centerLat, double centerLng, double radiusMiles)
        {
            var osmTask  = _realData.GetNearbyAddressesAsync(centerLat, centerLng, radiusMiles);
            var hailTask = _realData.GetSwdiHailEventsAsync(centerLat, centerLng, radiusMiles);
            await Task.WhenAll(osmTask, hailTask);

            var osmAddresses = osmTask.Result;
            var hailEvents   = hailTask.Result;

            var rng = new Random(centerAddress.GetHashCode());
            var records = osmAddresses
                .OrderBy(_ => rng.Next())
                .Take(50)
                .Select(addr => BuildRealRecord(addr, hailEvents))
                .ToList();

            records.Sort((a, b) =>
            {
                int ra = RiskOrder(a.RiskLevel), rb = RiskOrder(b.RiskLevel);
                return ra != rb
                    ? ra.CompareTo(rb)
                    : string.Compare(a.Address, b.Address, StringComparison.Ordinal);
            });

            return (records, hailEvents.Count);
        }

        // ─────────────────────────────────────────────────────────────────
        // Build a PropertyRecord from a real OSM address + NOAA hail data
        // ─────────────────────────────────────────────────────────────────
        private static PropertyRecord BuildRealRecord(
            RealDataService.OsmAddress addr,
            List<RealDataService.HailEvent> hailEvents)
        {
            var (risk, hailSize, stormDate, dataSource) = ComputeRiskFromHail(
                addr.Lat, addr.Lng, hailEvents);

            return new PropertyRecord
            {
                Address       = addr.FullAddress,
                Lat           = Math.Round(addr.Lat, 6),
                Lng           = Math.Round(addr.Lng, 6),
                RiskLevel     = risk,
                LastStormDate = stormDate,
                HailSize      = hailSize,
                DataSource    = dataSource
            };
        }

        // ─────────────────────────────────────────────────────────────────
        // Map real hail events → risk / hail size / last storm date
        //   - High   if any event ≥ 1.50" within 2 miles
        //   - Medium if any event ≥ 0.75" within 2 miles
        //   - Low    if events exist but below threshold
        //   - No data if NOAA has no events for this area
        // ─────────────────────────────────────────────────────────────────
        private static (string risk, string hailSize, string stormDate, string dataSource)
            ComputeRiskFromHail(
                double lat, double lng,
                List<RealDataService.HailEvent> hailEvents)
        {
            if (hailEvents.Count == 0)
                return ("Low", "No data", "No data", "none");

            var nearby = hailEvents
                .Select(h => new
                {
                    h,
                    dist = RealDataService.HaversineDistanceMiles(lat, lng, h.Lat, h.Lng)
                })
                .Where(x => x.dist <= 2.0)
                .OrderByDescending(x => x.h.SizeInches)
                .ThenBy(x => x.dist)
                .ToList();

            if (nearby.Count == 0)
                return ("Low", "No data", "No data", "none");

            var best = nearby.First();

            string risk = best.h.SizeInches >= 1.50 ? "High"
                        : best.h.SizeInches >= 0.75 ? "Medium"
                        : "Low";

            string hailSize  = $"{best.h.SizeInches:F2} inch";
            string stormDate = best.h.Date.ToString("yyyy-MM-dd");

            return (risk, hailSize, stormDate, "noaa");
        }

        // ─────────────────────────────────────────────────────────────────
        // Small helpers
        // ─────────────────────────────────────────────────────────────────

        private static int RiskOrder(string risk) => risk switch
        {
            "High"   => 0,
            "Medium" => 1,
            _        => 2
        };

        // ─────────────────────────────────────────────────────────────────
        // Google Maps geocoding (kept as-is from original)
        // ─────────────────────────────────────────────────────────────────
        private static async Task<GeoResult?> GeocodeAsync(string address)
        {
            using var client = new HttpClient();
            var url = $"{GeocodingBase}?address={Uri.EscapeDataString(address)}&key={ApiKey}";
            try
            {
                var json = await client.GetStringAsync(url);
                using var doc  = JsonDocument.Parse(json);
                var       root = doc.RootElement;

                if (root.GetProperty("status").GetString() != "OK") return null;

                var result    = root.GetProperty("results")[0];
                var loc       = result.GetProperty("geometry").GetProperty("location");
                var formatted = result.GetProperty("formatted_address").GetString() ?? address;

                return new GeoResult
                {
                    FormattedAddress = formatted,
                    Lat = loc.GetProperty("lat").GetDouble(),
                    Lng = loc.GetProperty("lng").GetDouble()
                };
            }
            catch { return null; }
        }

        // ─────────────────────────────────────────────────────────────────
        // DTOs
        // ─────────────────────────────────────────────────────────────────

        private class GeoResult
        {
            public string FormattedAddress { get; set; } = "";
            public double Lat { get; set; }
            public double Lng { get; set; }
        }

        private class PropertyRecord
        {
            [JsonPropertyName("address")]         public string Address         { get; set; } = "";
            [JsonPropertyName("lat")]             public double Lat             { get; set; }
            [JsonPropertyName("lng")]             public double Lng             { get; set; }
            [JsonPropertyName("riskLevel")]       public string RiskLevel       { get; set; } = "";
            [JsonPropertyName("lastStormDate")]   public string LastStormDate   { get; set; } = "";
            [JsonPropertyName("hailSize")]        public string HailSize        { get; set; } = "";
            [JsonPropertyName("dataSource")]      public string DataSource      { get; set; } = "none";
        }
    }
}
