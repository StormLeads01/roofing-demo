using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoofingLeadGeneration.Services;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoofingLeadGeneration.Controllers
{
    [Authorize]
    [Route("[controller]")]
    public class RoofHealthController : Controller
    {
        private readonly RealDataService              _realData;
        private readonly string                       _apiKey;
        private readonly string                       _tomorrowKey;
        private readonly IWebHostEnvironment          _env;
        private readonly ILogger<RoofHealthController> _logger;

        private const string GeocodingBase = "https://maps.googleapis.com/maps/api/geocode/json";

        // ── Claim window lookup — mirrors claim-window.js CLAIM_WINDOW_YEARS ──────────────
        // Years a homeowner has to file after a storm event, by state.
        // Sources: state insurance codes, NAIC, United Policyholders.  Last reviewed Apr 2026.
        // Keep in sync with wwwroot/js/claim-window.js.
        private static readonly IReadOnlyDictionary<string, int> ClaimWindowYearsByState =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["AL"] = 2, ["AK"] = 3, ["AZ"] = 2, ["AR"] = 5, ["CA"] = 2,
                ["CO"] = 2, ["CT"] = 2, ["DE"] = 2, ["FL"] = 1, ["GA"] = 2,
                ["HI"] = 2, ["ID"] = 2, ["IL"] = 2, ["IN"] = 2, ["IA"] = 5,
                ["KS"] = 5, ["KY"] = 2, ["LA"] = 1, ["ME"] = 6, ["MD"] = 3,
                ["MA"] = 2, ["MI"] = 1, ["MN"] = 1, ["MS"] = 3, ["MO"] = 5,
                ["MT"] = 2, ["NE"] = 4, ["NV"] = 3, ["NH"] = 3, ["NJ"] = 2,
                ["NM"] = 6, ["NY"] = 2, ["NC"] = 3, ["ND"] = 6, ["OH"] = 2,
                ["OK"] = 5, ["OR"] = 2, ["PA"] = 2, ["RI"] = 3, ["SC"] = 3,
                ["SD"] = 6, ["TN"] = 2, ["TX"] = 1, ["UT"] = 3, ["VT"] = 3,
                ["VA"] = 5, ["WA"] = 1, ["WV"] = 2, ["WI"] = 1, ["WY"] = 4,
                ["DC"] = 3,
            };

        private static int GetClaimWindowDays(string stateAbbr)
        {
            var years = ClaimWindowYearsByState.TryGetValue(stateAbbr ?? "", out var y) ? y : 2;
            return (int)Math.Round(years * 365.25);
        }

        public RoofHealthController(RealDataService realData, IConfiguration config,
                                    IWebHostEnvironment env, ILogger<RoofHealthController> logger)
        {
            _realData    = realData;
            _env         = env;
            _logger      = logger;
            _apiKey      = config["GoogleMaps:ApiKey"] ?? throw new InvalidOperationException(
                "GoogleMaps:ApiKey is not configured in appsettings.json");
            _tomorrowKey = config["TomorrowIo:ApiKey"] ?? "";
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
            if (!_env.IsDevelopment())
                return NotFound();

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
            if (!_env.IsDevelopment())
                return NotFound();

            // Single-point test first
            using var singleClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var singleUrl  = $"https://maps.googleapis.com/maps/api/geocode/json?latlng={lat},{lng}&result_type=street_address&key={_apiKey}";
            string singleBody = "";
            try { singleBody = await singleClient.GetStringAsync(singleUrl); }
            catch (Exception ex) { singleBody = ex.Message; }

            // Full grid run
            var gridAddresses = await _realData.GetAddressesViaGoogleGridAsync(lat, lng, radius, _apiKey);

            return Json(new
            {
                singlePointTest = new { url = singleUrl.Replace(_apiKey, "***"), bodyPreview = singleBody.Length > 300 ? singleBody[..300] : singleBody },
                gridResult = new { count = gridAddresses.Count, sample = gridAddresses.Take(5) }
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /RoofHealth/LsrDebug?lat=32.54&lng=-96.86
        // Tests the Iowa State Mesonet LSR hail data directly
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("LsrDebug")]
        public async Task<IActionResult> LsrDebug(double lat = 32.54, double lng = -96.86, string state = "TX")
        {
            if (!_env.IsDevelopment())
                return NotFound();

            var events = await _realData.GetMesonetLsrHailAsync(lat, lng, 5.0, state);
            return Json(new
            {
                count       = events.Count,
                sample      = events.Take(10).Select(e => new
                {
                    e.Lat, e.Lng, e.SizeInches,
                    date   = e.Date.ToString("yyyy-MM-dd"),
                    source = e.Source
                })
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        // ── Dev-only diagnostics ─────────────────────────────────────────────
        // These endpoints are blocked in production (non-Development environments).
        // To use locally: ASPNETCORE_ENVIRONMENT=Development (the default for `dotnet run`).

        [HttpGet("HailDebug")]
        public async Task<IActionResult> HailDebug(double lat = 32.54, double lng = -96.86, string state = "TX")
        {
            if (!_env.IsDevelopment())
                return NotFound();

            var swdiTask     = SafeSwdi(lat, lng, 5.0);
            var lsrTask      = SafeLsr(lat, lng, 5.0, state);
            var tomorrowTask = SafeTomorrow(lat, lng);
            await Task.WhenAll(swdiTask, lsrTask, tomorrowTask);

            var swdiEvents     = swdiTask.Result;
            var lsrEvents      = lsrTask.Result;
            var tomorrowEvents = tomorrowTask.Result;

            // Also probe a single raw SWDI URL so we can see what the API actually returns
            double span  = 5.0 / 69.0;
            var probeUrl = $"https://www.ncei.noaa.gov/swdiws/json/nx3hail" +
                           $"/{DateTime.UtcNow.AddDays(-121).AddDays(-30):yyyyMMdd}" +
                           $":{DateTime.UtcNow.AddDays(-121):yyyyMMdd}" +
                           $"?bbox={Math.Round(lng-span,4)},{Math.Round(lat-span,4)}" +
                           $",{Math.Round(lng+span,4)},{Math.Round(lat+span,4)}";

            string probeBody = "";
            try
            {
                using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                c.DefaultRequestHeaders.Add("User-Agent", "StormLeadPro/1.0");
                var r = await c.GetAsync(probeUrl);
                probeBody = await r.Content.ReadAsStringAsync();
                probeBody = probeBody.Length > 400 ? probeBody[..400] : probeBody;
            }
            catch (Exception ex) { probeBody = ex.Message; }

            return Json(new
            {
                swdi     = new { count = swdiEvents.Count,     sample = swdiEvents.Take(3).Select(e    => new { e.Lat, e.Lng, e.SizeInches, date = e.Date.ToString("yyyy-MM-dd") }) },
                lsr      = new { count = lsrEvents.Count,      sample = lsrEvents.Take(3).Select(e     => new { e.Lat, e.Lng, e.SizeInches, date = e.Date.ToString("yyyy-MM-dd") }) },
                tomorrow = new { count = tomorrowEvents.Count, sample = tomorrowEvents.Take(3).Select(e => new { e.Lat, e.Lng, e.SizeInches, date = e.Date.ToString("yyyy-MM-dd"), e.Source }) },
                rawSwdiProbe = new { url = probeUrl, bodyPreview = probeBody }
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
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
            sb.AppendLine("Address,Latitude,Longitude,Risk Level,Last Storm Date,Hail Size,Data Source,Claim Window,Days Since Storm");

            foreach (var p in properties)
            {
                var claimLabel = p.ClaimWindowTier switch
                {
                    "hot"      => "Hot — File Now",
                    "fileable" => "Still Fileable",
                    "expired"  => "Expired",
                    _          => ""
                };
                sb.AppendLine(
                    $"\"{p.Address}\",{p.Lat},{p.Lng},\"{p.RiskLevel}\",\"{p.LastStormDate}\"," +
                    $"\"{p.HailSize}\",\"{p.DataSource}\",\"{claimLabel}\",\"{p.ClaimWindowDays}\"");
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
        // Safe wrappers — a failure in one data source must never kill the whole request
        private async Task<List<RealDataService.HailEvent>> SafeSwdi(double lat, double lng, double r)
        {
            try   { return await _realData.GetSwdiHailEventsAsync(lat, lng, r); }
            catch (Exception ex) { _logger.LogError(ex, "SWDI fetch failed"); return new(); }
        }
        private async Task<List<RealDataService.HailEvent>> SafeLsr(double lat, double lng, double r, string state)
        {
            try   { return await _realData.GetMesonetLsrHailAsync(lat, lng, r, state); }
            catch (Exception ex) { _logger.LogError(ex, "Mesonet LSR fetch failed"); return new(); }
        }
        private async Task<List<RealDataService.HailEvent>> SafeTomorrow(double lat, double lng)
        {
            if (string.IsNullOrWhiteSpace(_tomorrowKey)) return new();
            try   { return await _realData.GetTomorrowIoHailAsync(lat, lng, _tomorrowKey); }
            catch (Exception ex) { _logger.LogError(ex, "Tomorrow.io fetch failed"); return new(); }
        }
        private async Task<List<RealDataService.OsmAddress>> SafeOsm(double lat, double lng, double r)
        {
            try   { return await _realData.GetNearbyAddressesAsync(lat, lng, r); }
            catch (Exception ex) { _logger.LogError(ex, "OSM fetch failed"); return new(); }
        }

        private async Task<(List<PropertyRecord> Records, int HailEventCount)> GetPropertiesAsync(
            string centerAddress, double centerLat, double centerLng, double radiusMiles,
            string stateAbbr = "")
        {
            // If stateAbbr wasn't set by geocoding (lat/lng came from autocomplete),
            // try to extract it from the address string — e.g. "Dallas, TX 75201"
            if (string.IsNullOrWhiteSpace(stateAbbr))
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    centerAddress, @"\b([A-Z]{2})\b\s*\d{5}");
                if (m.Success) stateAbbr = m.Groups[1].Value;
            }

            // Run OSM, SWDI, Mesonet LSR, and Tomorrow.io fully in parallel —
            // each wrapped so a failure in one source never blocks the others.
            var osmTask      = SafeOsm(centerLat, centerLng, radiusMiles);
            var swdiTask     = SafeSwdi(centerLat, centerLng, radiusMiles);
            var mesonetTask  = SafeLsr(centerLat, centerLng, radiusMiles, stateAbbr);
            var tomorrowTask = SafeTomorrow(centerLat, centerLng);

            await Task.WhenAll(osmTask, swdiTask, mesonetTask, tomorrowTask);

            var osmAddresses = osmTask.Result;

            // Merge all hail sources
            var hailEvents = new List<RealDataService.HailEvent>();
            hailEvents.AddRange(swdiTask.Result);
            hailEvents.AddRange(mesonetTask.Result);
            hailEvents.AddRange(tomorrowTask.Result);   // fills NOAA 120-day lag

            // Fallback: if OSM returned nothing (common in newer suburbs), use Google reverse-geocode grid
            if (osmAddresses.Count == 0)
                osmAddresses = await _realData.GetAddressesViaGoogleGridAsync(
                    centerLat, centerLng, radiusMiles, _apiKey);

            // Fallback: if still no hail data at all, try NOAA Storm Events (ground-truth reports)
            if (hailEvents.Count == 0 && !string.IsNullOrEmpty(stateAbbr))
            {
                var stormEvents = await _realData.GetStormEventsHailAsync(
                    centerLat, centerLng, radiusMiles, stateAbbr);
                hailEvents.AddRange(stormEvents);
            }

            // Always include the searched address itself — OSM often misses the exact parcel,
            // especially in newer subdivisions like Glenn Heights.  Pin it first, then fill
            // up to 149 neighbours sorted by proximity so the whole street shows up before
            // distant blocks do.  Deduplicate anything within ~150 ft of the center.
            const double dedupeThresholdMiles = 0.03; // ~150 ft
            var centerOsm = new RealDataService.OsmAddress
            {
                FullAddress = centerAddress,
                Lat         = centerLat,
                Lng         = centerLng
            };
            var centerRecord = BuildRealRecord(centerOsm, hailEvents, stateAbbr);

            var neighbourRecords = osmAddresses
                .Where(a => RealDataService.HaversineDistanceMiles(a.Lat, a.Lng, centerLat, centerLng) > dedupeThresholdMiles)
                .OrderBy(a => RealDataService.HaversineDistanceMiles(a.Lat, a.Lng, centerLat, centerLng))
                .Take(149)
                .Select(addr => BuildRealRecord(addr, hailEvents, stateAbbr))
                .ToList();

            neighbourRecords.Sort((a, b) =>
            {
                int ra = RiskOrder(a.RiskLevel), rb = RiskOrder(b.RiskLevel);
                return ra != rb
                    ? ra.CompareTo(rb)
                    : string.Compare(a.Address, b.Address, StringComparison.Ordinal);
            });

            // Searched address always leads the list
            var records = new List<PropertyRecord> { centerRecord };
            records.AddRange(neighbourRecords);

            return (records, hailEvents.Count);
        }

        // ─────────────────────────────────────────────────────────────────
        // Build a PropertyRecord from a real OSM address + NOAA hail data
        // ─────────────────────────────────────────────────────────────────
        private static PropertyRecord BuildRealRecord(
            RealDataService.OsmAddress addr,
            List<RealDataService.HailEvent> hailEvents,
            string stateAbbr = "TX")
        {
            var (risk, hailSize, stormDate, dataSource, claimDays, claimTier) = ComputeRiskFromHail(
                addr.Lat, addr.Lng, hailEvents, stateAbbr);

            return new PropertyRecord
            {
                Address         = addr.FullAddress,
                Lat             = Math.Round(addr.Lat, 6),
                Lng             = Math.Round(addr.Lng, 6),
                RiskLevel       = risk,
                LastStormDate   = stormDate,
                HailSize        = hailSize,
                DataSource      = dataSource,
                ClaimWindowDays = claimDays,
                ClaimWindowTier = claimTier
            };
        }

        // ─────────────────────────────────────────────────────────────────
        // Map real hail events → risk / hail size / last storm date / claim window
        //   - High   if any event ≥ 1.50" within 2 miles
        //   - Medium if any event ≥ 0.75" within 2 miles
        //   - Low    if events exist but below threshold
        //   - No data if no events for this area
        //
        //   Claim window tiers — derived from ClaimWindowYearsByState lookup:
        //   - "hot"      : first half of window  (prime time, file now!)
        //   - "fileable" : second half of window  (still within deadline, getting urgent)
        //   - "expired"  : past deadline
        // ─────────────────────────────────────────────────────────────────
        private static (string risk, string hailSize, string stormDate, string dataSource, int? claimDays, string claimTier)
            ComputeRiskFromHail(
                double lat, double lng,
                List<RealDataService.HailEvent> hailEvents,
                string stateAbbr = "TX")
        {
            if (hailEvents.Count == 0)
                return ("Low", "No data", "No data", "none", null, "");

            var nearby = hailEvents
                .Select(h => new
                {
                    h,
                    dist = RealDataService.HaversineDistanceMiles(lat, lng, h.Lat, h.Lng)
                })
                .Where(x => x.dist <= 2.0)
                .ToList();

            if (nearby.Count == 0)
                return ("Low", "No data", "No data", "none", null, "");

            // For risk/hail size: pick the largest nearby event
            var best = nearby.OrderByDescending(x => x.h.SizeInches).ThenBy(x => x.dist).First();

            string risk = best.h.SizeInches >= 1.50 ? "High"
                        : best.h.SizeInches >= 0.75 ? "Medium"
                        : "Low";

            string hailSize  = $"{best.h.SizeInches:F2} inch";

            // For claim window: use the MOST RECENT event within 2 miles
            var mostRecent = nearby.OrderByDescending(x => x.h.Date).First();
            string stormDate = mostRecent.h.Date.ToString("yyyy-MM-dd");

            // Source label — prefer LSR (ground truth) > tomorrow > noaa
            bool hasLsr      = nearby.Any(x => x.h.Source == "lsr");
            bool hasTomorrow = nearby.Any(x => x.h.Source == "tomorrow");
            string source    = hasLsr      ? "lsr+noaa"
                             : hasTomorrow ? "tomorrow+noaa"
                             :               "noaa";

            // Claim window calculation — window length driven by state lookup table
            int windowDays   = GetClaimWindowDays(stateAbbr);
            int hotThreshold = windowDays / 2;
            int daysSince    = (int)(DateTime.UtcNow - mostRecent.h.Date).TotalDays;
            string claimTier = daysSince <= hotThreshold ? "hot"
                             : daysSince <= windowDays   ? "fileable"
                             : "expired";

            return (risk, hailSize, stormDate, source, daysSince, claimTier);
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
        private async Task<GeoResult?> GeocodeAsync(string address)
        {
            using var client = new HttpClient();
            var url = $"{GeocodingBase}?address={Uri.EscapeDataString(address)}&key={_apiKey}";
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
            [JsonPropertyName("address")]          public string Address          { get; set; } = "";
            [JsonPropertyName("lat")]              public double Lat              { get; set; }
            [JsonPropertyName("lng")]              public double Lng              { get; set; }
            [JsonPropertyName("riskLevel")]        public string RiskLevel        { get; set; } = "";
            [JsonPropertyName("lastStormDate")]    public string LastStormDate    { get; set; } = "";
            [JsonPropertyName("hailSize")]         public string HailSize         { get; set; } = "";
            [JsonPropertyName("dataSource")]       public string DataSource       { get; set; } = "none";
            /// <summary>Days since most recent nearby hail event. Null = no data.</summary>
            [JsonPropertyName("claimWindowDays")]  public int?   ClaimWindowDays  { get; set; }
            /// <summary>"hot" (0-365), "fileable" (366-730), "expired" (>730), or "" (no data)</summary>
            [JsonPropertyName("claimWindowTier")]  public string ClaimWindowTier  { get; set; } = "";
        }
    }
}
