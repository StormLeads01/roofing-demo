using Microsoft.AspNetCore.Mvc;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoofingLeadGeneration.Controllers
{
    [Route("[controller]")]
    public class RoofHealthController : Controller
    {
        private const string ApiKey = "AIzaSyB2YmUC-KAbjTUSO4p9NNIaG_3af4iTevM";
        private const string GeocodingBase = "https://maps.googleapis.com/maps/api/geocode/json";

        // ─────────────────────────────────────────────────────────────────
        // GET /RoofHealth/Neighborhood?address=...&radius=0.5
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("Neighborhood")]
        public async Task<IActionResult> Neighborhood(string address, double radius = 0.5, double lat = 0, double lng = 0)
        {
            if (string.IsNullOrWhiteSpace(address))
                return BadRequest(new { error = "Address is required." });

            string formattedAddress = address;

            // Use client-supplied coords if provided; fall back to server-side geocoding
            if (lat == 0 || lng == 0)
            {
                var center = await GeocodeAsync(address);
                if (center == null)
                    return BadRequest(new { error = "Could not geocode the provided address." });
                lat = center.Lat;
                lng = center.Lng;
                formattedAddress = center.FormattedAddress;
            }

            var properties = GenerateNearbyProperties(address, lat, lng, radius);

            var result = new
            {
                centerAddress = formattedAddress,
                lat,
                lng,
                properties
            };

            return Json(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /RoofHealth/Export?address=...&radius=0.5
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("Export")]
        public async Task<IActionResult> Export(string address, double radius = 0.5, double lat = 0, double lng = 0)
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

            var properties = GenerateNearbyProperties(address, lat, lng, radius);

            var sb = new StringBuilder();
            sb.AppendLine("Address,Latitude,Longitude,Risk Level,Last Storm Date,Hail Size,Estimated Damage,Roof Age (yrs),Property Type");

            foreach (var p in properties)
            {
                sb.AppendLine(
                    $"\"{p.Address}\",{p.Lat},{p.Lng},\"{p.RiskLevel}\",\"{p.LastStormDate}\"," +
                    $"\"{p.HailSize}\",\"{p.EstimatedDamage}\",{p.RoofAge},\"{p.PropertyType}\""
                );
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            var fileName = $"StormLead_Export_{DateTime.Now:yyyyMMdd_HHmm}.csv";
            return File(bytes, "text/csv", fileName);
        }

        // ─────────────────────────────────────────────────────────────────
        // Private helpers
        // ─────────────────────────────────────────────────────────────────

        private static async Task<GeoResult?> GeocodeAsync(string address)
        {
            using var client = new HttpClient();
            var url = $"{GeocodingBase}?address={Uri.EscapeDataString(address)}&key={ApiKey}";

            try
            {
                var json = await client.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.GetProperty("status").GetString() != "OK")
                    return null;

                var result = root.GetProperty("results")[0];
                var loc = result.GetProperty("geometry").GetProperty("location");
                var formatted = result.GetProperty("formatted_address").GetString() ?? address;

                return new GeoResult
                {
                    FormattedAddress = formatted,
                    Lat = loc.GetProperty("lat").GetDouble(),
                    Lng = loc.GetProperty("lng").GetDouble()
                };
            }
            catch
            {
                return null;
            }
        }

        private static List<PropertyRecord> GenerateNearbyProperties(
            string centerAddress, double centerLat, double centerLng, double radiusMiles)
        {
            // Parse street number and name from the center address
            var parts = centerAddress.Trim().Split(' ', 3);
            int centerNumber = 100;
            string streetName = "Main St";
            string cityStateZip = "";

            if (parts.Length >= 2 && int.TryParse(parts[0], out int parsed))
            {
                centerNumber = parsed;
                // Find the comma that separates street from city
                var commaIdx = centerAddress.IndexOf(',');
                if (commaIdx > 0)
                {
                    var streetPart = centerAddress[..commaIdx].Trim();
                    cityStateZip = centerAddress[commaIdx..].Trim(); // ", Dallas TX 75201"
                    var spaceIdx = streetPart.IndexOf(' ');
                    streetName = spaceIdx >= 0 ? streetPart[(spaceIdx + 1)..] : streetPart;
                }
                else
                {
                    var spaceIdx = centerAddress.IndexOf(' ');
                    streetName = spaceIdx >= 0 ? centerAddress[(spaceIdx + 1)..] : centerAddress;
                }
            }

            // Deterministic seed based on address so same input → same output
            int seed = Math.Abs(centerAddress.GetHashCode());
            var rng = new Random(seed);

            // ~1 mile in degrees (approximate, good enough for lead-gen demo)
            double degPerMile = 1.0 / 69.0;
            double span = radiusMiles * degPerMile;

            // How many total properties to generate (15–25 range clamped to 20 for determinism)
            int count = 20;

            var records = new List<PropertyRecord>();

            // ── Generate same-street addresses ──────────────────────────
            int sameStreetCount = (int)(count * 0.65); // ~13 on same street
            var offsets = GenerateHouseNumberOffsets(seed, sameStreetCount);

            for (int i = 0; i < sameStreetCount; i++)
            {
                int houseNum = centerNumber + offsets[i];
                if (houseNum <= 0) houseNum = Math.Abs(houseNum) + 10;

                // Snap to even/odd side to be realistic
                if (centerNumber % 2 == 0 && houseNum % 2 != 0) houseNum++;
                else if (centerNumber % 2 != 0 && houseNum % 2 == 0) houseNum++;

                var addr = string.IsNullOrEmpty(cityStateZip)
                    ? $"{houseNum} {streetName}"
                    : $"{houseNum} {streetName}{cityStateZip}";

                // Small lat/lng jitter along the street axis
                double latJitter = rng.NextDouble() * 0.0004 - 0.0002;
                double lngJitter = (offsets[i] / 100.0) * 0.0008 + (rng.NextDouble() * 0.0002 - 0.0001);

                records.Add(BuildRecord(addr, centerLat + latJitter, centerLng + lngJitter, rng));
            }

            // ── Generate cross-street addresses ─────────────────────────
            var crossStreets = InferCrossStreets(streetName);
            int crossCount = count - sameStreetCount;
            int csIdx = 0;

            for (int i = 0; i < crossCount; i++)
            {
                string cs = crossStreets[csIdx % crossStreets.Count];
                csIdx++;

                int houseNum = rng.Next(100, 999);
                if (houseNum % 2 != centerNumber % 2) houseNum++;

                var addr = string.IsNullOrEmpty(cityStateZip)
                    ? $"{houseNum} {cs}"
                    : $"{houseNum} {cs}{cityStateZip}";

                double latJitter = (rng.NextDouble() * 2 - 1) * span * 0.6;
                double lngJitter = (rng.NextDouble() * 2 - 1) * span * 0.6;

                records.Add(BuildRecord(addr, centerLat + latJitter, centerLng + lngJitter, rng));
            }

            // Sort by risk level (High first) then by address
            records.Sort((a, b) =>
            {
                int ra = RiskOrder(a.RiskLevel);
                int rb = RiskOrder(b.RiskLevel);
                return ra != rb ? ra.CompareTo(rb) : string.Compare(a.Address, b.Address, StringComparison.Ordinal);
            });

            return records;
        }

        private static int[] GenerateHouseNumberOffsets(int seed, int count)
        {
            // Spread house numbers from -200 to +200 excluding 0
            var rng = new Random(seed ^ unchecked((int)0xABCD1234));
            var used = new HashSet<int> { 0 };
            var result = new int[count];
            for (int i = 0; i < count; i++)
            {
                int v;
                do { v = rng.Next(-200, 201); } while (used.Contains(v) || Math.Abs(v) < 10);
                used.Add(v);
                result[i] = v;
            }
            return result;
        }

        private static List<string> InferCrossStreets(string streetName)
        {
            // Return plausible cross-street names based on the main street
            var lowers = streetName.ToLower();
            if (lowers.Contains("main"))
                return new List<string> { "Oak Ave", "Elm St", "Maple Dr", "Cedar Ln", "Pine Blvd", "1st St", "2nd St" };
            if (lowers.Contains("oak"))
                return new List<string> { "Elm St", "Main St", "Maple Dr", "Park Ave", "Walnut Ct" };
            if (lowers.Contains("park") || lowers.Contains("ave"))
                return new List<string> { "Oak St", "Main St", "Birch Ln", "Sycamore Dr" };

            // Generic fallback cross streets
            return new List<string> { "Oak Ave", "Elm St", "Maple Dr", "Cedar Ln", "Pine Blvd", "Walnut Ct", "Birch Ln" };
        }

        private static PropertyRecord BuildRecord(string address, double lat, double lng, Random rng)
        {
            // Risk-level weighted: ~35% High, ~40% Medium, ~25% Low
            string riskLevel = rng.Next(100) switch
            {
                < 35 => "High",
                < 75 => "Medium",
                _ => "Low"
            };

            // Storm date: within last 24 months
            int daysAgo = rng.Next(30, 730);
            var stormDate = DateTime.Today.AddDays(-daysAgo).ToString("yyyy-MM-dd");

            // Hail size correlates with risk
            string hailSize = riskLevel switch
            {
                "High" => $"{(rng.Next(150, 275) / 100.0):F2} inch",
                "Medium" => $"{(rng.Next(75, 150) / 100.0):F2} inch",
                _ => $"{(rng.Next(25, 75) / 100.0):F2} inch"
            };

            string estimatedDamage = riskLevel switch
            {
                "High" => rng.Next(2) == 0 ? "Significant" : "Severe",
                "Medium" => rng.Next(2) == 0 ? "Moderate" : "Notable",
                _ => rng.Next(2) == 0 ? "Minor" : "Minimal"
            };

            int roofAge = rng.Next(3, 28);

            string[] propertyTypes = { "Single Family", "Ranch", "Two Story", "Bungalow", "Colonial", "Cape Cod" };
            string propertyType = propertyTypes[rng.Next(propertyTypes.Length)];

            return new PropertyRecord
            {
                Address = address,
                Lat = Math.Round(lat, 6),
                Lng = Math.Round(lng, 6),
                RiskLevel = riskLevel,
                LastStormDate = stormDate,
                HailSize = hailSize,
                EstimatedDamage = estimatedDamage,
                RoofAge = roofAge,
                PropertyType = propertyType
            };
        }

        private static int RiskOrder(string risk) => risk switch
        {
            "High" => 0,
            "Medium" => 1,
            _ => 2
        };

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
            [JsonPropertyName("address")]
            public string Address { get; set; } = "";

            [JsonPropertyName("lat")]
            public double Lat { get; set; }

            [JsonPropertyName("lng")]
            public double Lng { get; set; }

            [JsonPropertyName("riskLevel")]
            public string RiskLevel { get; set; } = "";

            [JsonPropertyName("lastStormDate")]
            public string LastStormDate { get; set; } = "";

            [JsonPropertyName("hailSize")]
            public string HailSize { get; set; } = "";

            [JsonPropertyName("estimatedDamage")]
            public string EstimatedDamage { get; set; } = "";

            [JsonPropertyName("roofAge")]
            public int RoofAge { get; set; }

            [JsonPropertyName("propertyType")]
            public string PropertyType { get; set; } = "";
        }
    }
}
