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
        // GET /RoofHealth/HailDebug?lat=32.54&lng=-96.86
        // Returns raw NOAA SWDI response for diagnostic purposes
        // ─────────────────────────────────────────────────────────────────
        // ─────────────────────────────────────────────────────────────────
        // GET /RoofHealth/RegridDebug?address=312+Meandering+Way,+Glenn+Heights,+TX+75154
        // Returns raw Regrid API response for diagnostic purposes
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("RegridDebug")]
        public async Task<IActionResult> RegridDebug(string address = "312 Meandering Way, Glenn Heights, TX 75154")
        {
            var config = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var token  = config["Regrid:Token"] ?? "";

            if (string.IsNullOrWhiteSpace(token))
                return Json(new { error = "No Regrid token configured in appsettings.json" });

            var clean = address.Replace(", USA", "").Replace(", United States", "").Trim();
            var url   = $"https://app.regrid.com/api/v2/parcels/address" +
                        $"?query={Uri.EscapeDataString(clean)}&token={token}&limit=1&return_enhanced_ownership=true";

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            try
            {
                var resp = await client.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                return Json(new
                {
                    httpStatus  = (int)resp.StatusCode,
                    address     = clean,
                    bodyPreview = body.Length > 1000 ? body[..1000] : body,
                    totalLength = body.Length
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            }
            catch (Exception ex)
            {
                return Json(new { error = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /RoofHealth/GridDebug?lat=32.54&lng=-96.86&radius=0.5
        // Tests the Google reverse-geocode grid fallback directly
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("GridDebug")]
        public async Task<IActionResult> GridDebug(double lat = 32.54, double lng = -96.86, double radius = 0.5)
        {
            // Single-point test first
            using var singleClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var singleUrl  = $"https://maps.googleapis.com/maps/api/geocode/json?latlng={lat},{lng}&result_type=street_address&key={ApiKey}";
            string singleBody = "";
            try { singleBody = await singleClient.GetStringAsync(singleUrl); }
            catch (Exception ex) { singleBody = ex.Message; }

            // Full grid run
            var gridAddresses = await _realData.GetAddressesViaGoogleGridAsync(lat, lng, radius, ApiKey);

            return Json(new
            {
                singlePointTest = new { url = singleUrl.Replace(ApiKey, "***"), bodyPreview = singleBody.Length > 300 ? singleBody[..300] : singleBody },
                gridResult = new { count = gridAddresses.Count, sample = gridAddresses.Take(5) }
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        [HttpGet("HailDebug")]
        public async Task<IActionResult> HailDebug(double lat = 32.54, double lng = -96.86)
        {
            double hailMiles = 5.0;
            double span      = hailMiles / 69.0;
            double west      = Math.Round(lng - span, 4);
            double east      = Math.Round(lng + span, 4);
            double south     = Math.Round(lat - span, 4);
            double north     = Math.Round(lat + span, 4);

            // Test two windows: spring 2025 (peak TX hail season) + most recent valid period
            var recentEnd   = DateTime.UtcNow.AddDays(-121);
            var recentStart = recentEnd.AddDays(-30);
            var springStart = new DateTime(2025, 4, 1);
            var springEnd   = new DateTime(2025, 4, 30);
            var start = recentStart; // keep for dateRange display
            var end   = recentEnd;

            var urls = new[]
            {
                $"https://www.ncei.noaa.gov/swdiws/json/nx3hail/{springStart:yyyyMMdd}:{springEnd:yyyyMMdd}?bbox={west},{south},{east},{north}",
                $"https://www.ncei.noaa.gov/swdiws/json/nx3hail/{recentStart:yyyyMMdd}:{recentEnd:yyyyMMdd}?bbox={west},{south},{east},{north}"
            };

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "StormLeadPro/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);

            var results = new List<object>();
            foreach (var url in urls)
            {
                try
                {
                    var resp = await client.GetAsync(url);
                    var body = await resp.Content.ReadAsStringAsync();
                    results.Add(new { url, status = (int)resp.StatusCode, bodyPreview = body.Length > 500 ? body[..500] : body, totalLength = body.Length });
                }
                catch (Exception ex)
                {
                    results.Add(new { url, error = ex.Message });
                }
            }

            return Json(new { bbox = new { west, south, east, north }, dateRange = new { start, end }, results });
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
            string stateAbbr        = "";

            if (lat == 0 || lng == 0)
            {
                var center = await GeocodeAsync(address);
                if (center == null)
                    return BadRequest(new { error = "Could not geocode the provided address." });
                lat              = center.Lat;
                lng              = center.Lng;
                formattedAddress = center.FormattedAddress;
                stateAbbr        = center.StateAbbr;
            }
            var (properties, hailEventCount) = await GetPropertiesAsync(formattedAddress, lat, lng, radius, stateAbbr);

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

            string exportState = "";
            if (lat == 0 || lng == 0)
            {
                var center = await GeocodeAsync(address);
                if (center == null)
                    return BadRequest("Could not geocode the provided address.");
                lat          = center.Lat;
                lng          = center.Lng;
                exportState  = center.StateAbbr;
            }

            var (properties, _) = await GetPropertiesAsync(address, lat, lng, radius, exportState);

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
            string centerAddress, double centerLat, double centerLng, double radiusMiles,
            string stateAbbr = "")
        {
            // Run OSM and NOAA in parallel — NOAA failure must never kill OSM results
            var osmTask  = _realData.GetNearbyAddressesAsync(centerLat, centerLng, radiusMiles);
            var hailTask = _realData.GetSwdiHailEventsAsync(centerLat, centerLng, radiusMiles)
                               .ContinueWith(t =>
                               {
                                   if (t.IsFaulted)
                                       return new List<RealDataService.HailEvent>();
                                   return t.Result;
                               });
            await Task.WhenAll(osmTask, hailTask);

            var osmAddresses = osmTask.Result;
            var hailEvents   = hailTask.Result;

            // Fallback: if OSM returned nothing (common in newer suburbs), use Google reverse-geocode grid
            if (osmAddresses.Count == 0)
                osmAddresses = await _realData.GetAddressesViaGoogleGridAsync(
                    centerLat, centerLng, radiusMiles, ApiKey);

            // Fallback: if SWDI returned nothing, try NOAA Storm Events (ground-truth reports)
            if (hailEvents.Count == 0 && !string.IsNullOrEmpty(stateAbbr))
                hailEvents = await _realData.GetStormEventsHailAsync(
                    centerLat, centerLng, radiusMiles, stateAbbr);

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
        // Google Maps geocoding — also extracts state abbreviation for Storm Events fallback
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

                // Extract state abbreviation from address_components
                string stateAbbr = "";
                if (result.TryGetProperty("address_components", out var components))
                {
                    foreach (var comp in components.EnumerateArray())
                    {
                        if (!comp.TryGetProperty("types", out var types)) continue;
                        bool isState = types.EnumerateArray()
                            .Any(t => t.GetString() == "administrative_area_level_1");
                        if (isState && comp.TryGetProperty("short_name", out var sn))
                        { stateAbbr = sn.GetString() ?? ""; break; }
                    }
                }

                return new GeoResult
                {
                    FormattedAddress = formatted,
                    Lat        = loc.GetProperty("lat").GetDouble(),
                    Lng        = loc.GetProperty("lng").GetDouble(),
                    StateAbbr  = stateAbbr
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
            public double Lat        { get; set; }
            public double Lng        { get; set; }
            public string StateAbbr  { get; set; } = "";
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
