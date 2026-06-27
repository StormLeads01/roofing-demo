using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
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
        private readonly IMemoryCache                 _cache;
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

        public RoofHealthController(RealDataService realData, IMemoryCache cache,
                                    IConfiguration config,
                                    IWebHostEnvironment env, ILogger<RoofHealthController> logger)
        {
            _realData    = realData;
            _cache       = cache;
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
            var (properties, hailEventCount, hailEvents) = await GetPropertiesAsync(formattedAddress, lat, lng, radius, stateAbbr);

            return Json(new { centerAddress = formattedAddress, lat, lng, hailEventCount, hailEvents, osmCount = properties.Count, properties },
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented        = false
                });
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /RoofHealth/SingleAddress?address=...&lat=...&lng=...
        // Scores exactly one property — no OSM neighbour lookup.
        // Returns the same JSON shape as Neighborhood for frontend compatibility.
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("SingleAddress")]
        public async Task<IActionResult> SingleAddress(
            string address, double lat = 0, double lng = 0)
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

            if (string.IsNullOrWhiteSpace(stateAbbr))
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    formattedAddress, @"\b([A-Z]{2})\b\s*\d{5}");
                if (m.Success) stateAbbr = m.Groups[1].Value;
            }

            // Fetch hail data only — no OSM neighbour scan
            const double hailRadius = 2.0;
            var cacheKey = $"hail:{Math.Round(lat, 1)}:{Math.Round(lng, 1)}:{stateAbbr}";
            if (!_cache.TryGetValue(cacheKey, out List<RealDataService.HailEvent>? cachedHail) || cachedHail == null)
            {
                var swdiTask     = SafeSwdi(lat, lng, hailRadius);
                var mesonetTask  = SafeLsr(lat, lng, hailRadius, stateAbbr);
                var tomorrowTask = SafeTomorrow(lat, lng);
                await Task.WhenAll(swdiTask, mesonetTask, tomorrowTask);

                cachedHail = new List<RealDataService.HailEvent>();
                cachedHail.AddRange(swdiTask.Result);
                cachedHail.AddRange(mesonetTask.Result);
                cachedHail.AddRange(tomorrowTask.Result);
                _cache.Set(cacheKey, cachedHail, TimeSpan.FromMinutes(30));
            }

            var centerOsm = new RealDataService.OsmAddress
            {
                FullAddress = formattedAddress,
                Lat         = lat,
                Lng         = lng
            };
            var record = BuildRealRecord(centerOsm, cachedHail, stateAbbr);

            var hailDtos = cachedHail.Select(e => new HailEventDto
            {
                Lat        = Math.Round(e.Lat, 6),
                Lng        = Math.Round(e.Lng, 6),
                SizeInches = Math.Round(e.SizeInches, 2),
                Date       = e.Date.ToString("yyyy-MM-dd"),
                Source     = e.Source
            }).ToList();

            return Json(
                new { centerAddress = formattedAddress, lat, lng,
                      hailEventCount = cachedHail.Count, hailEvents = hailDtos,
                      osmCount = 1, properties = new[] { record } },
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

            var (properties, _, _) = await GetPropertiesAsync(address, lat, lng, radius, exportState);

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

        private async Task<List<RealDataService.HailEvent>> SafeStormEvents(double lat, double lng, double r, string state)
        {
            if (string.IsNullOrWhiteSpace(state)) return new();
            try   { return await _realData.GetStormEventsHailAsync(lat, lng, r, state); }
            catch (Exception ex) { _logger.LogError(ex, "StormEvents history fetch failed"); return new(); }
        }
        private async Task<List<RealDataService.WindEvent>> SafeWindHistory(double lat, double lng, double r, string state)
        {
            if (string.IsNullOrWhiteSpace(state)) return new();
            try   { return await _realData.GetMesonetLsrWindAsync(lat, lng, r, state, lookbackDays: 365); }
            catch (Exception ex) { _logger.LogError(ex, "Wind history fetch failed"); return new(); }
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /RoofHealth/StormHistory?lat=...&lng=...&state=TX
        // Returns hail events (5 years) and wind events (1 year) within 2 miles,
        // deduplicated to one event per calendar day. State is auto-detected via
        // Nominatim reverse-geocode if not supplied by the caller.
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("StormHistory")]
        public async Task<IActionResult> StormHistory(double lat, double lng, string state = "")
        {
            if (lat == 0 && lng == 0)
                return BadRequest(new { error = "lat and lng are required." });

            // Auto-detect state when caller doesn't supply one — required for LSR and StormEvents
            var stateAbbr = (state ?? "").Trim().ToUpperInvariant();
            if (stateAbbr.Length != 2)
                stateAbbr = await _realData.GetStateFromLatLngAsync(lat, lng);

            var cacheKey = $"stormhist:{Math.Round(lat, 3)}:{Math.Round(lng, 3)}:{stateAbbr}";
            if (_cache.TryGetValue(cacheKey, out StormHistoryResult? cached) && cached != null)
                return Json(cached, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            // Use a wider fetch radius so SWDI radar-pixel offsets don't exclude nearby hits,
            // then filter down to 2 miles for display.
            const double fetchRadius   = 10.0;
            const double displayRadius = 2.0;

            var swdiTask        = SafeSwdi(lat, lng, fetchRadius);
            var lsrTask         = SafeLsr(lat, lng, fetchRadius, stateAbbr);
            var stormEventsTask = SafeStormEvents(lat, lng, fetchRadius, stateAbbr);
            var windTask        = SafeWindHistory(lat, lng, fetchRadius, stateAbbr);

            await Task.WhenAll(swdiTask, lsrTask, stormEventsTask, windTask);

            var fiveYearsAgo = DateTime.UtcNow.AddYears(-5);
            var oneYearAgo   = DateTime.UtcNow.AddYears(-1);

            // ── Hail — 5 years, deduplicated per calendar day ────────────
            var allHail = new List<RealDataService.HailEvent>();
            allHail.AddRange(swdiTask.Result);
            allHail.AddRange(lsrTask.Result);
            allHail.AddRange(stormEventsTask.Result);

            var hailList = allHail
                .Where(e => e.Date >= fiveYearsAgo &&
                            RealDataService.HaversineDistanceMiles(lat, lng, e.Lat, e.Lng) <= displayRadius)
                .GroupBy(e => e.Date.Date)
                .Select(g => g.OrderByDescending(e => e.SizeInches).First())
                .OrderByDescending(e => e.Date)
                .Select(e => new StormHistoryHailDto
                {
                    Date       = e.Date.ToString("yyyy-MM-dd"),
                    SizeInches = Math.Round(e.SizeInches, 2),
                    Source     = e.Source
                })
                .ToList();

            // ── Wind — 1 year, deduplicated per calendar day ─────────────
            var windList = windTask.Result
                .Where(w => w.Date >= oneYearAgo &&
                            RealDataService.HaversineDistanceMiles(lat, lng, w.Lat, w.Lng) <= displayRadius)
                .GroupBy(w => w.Date.Date)
                .Select(g => g.OrderByDescending(w => w.SpeedMph).First())
                .OrderByDescending(w => w.Date)
                .Select(w => new StormHistoryWindDto
                {
                    Date    = w.Date.ToString("yyyy-MM-dd"),
                    WindMph = (int)Math.Round(w.SpeedMph),
                    Source  = w.Source
                })
                .ToList();

            var result = new StormHistoryResult { Hail = hailList, Wind = windList };
            _cache.Set(cacheKey, result, TimeSpan.FromHours(2));
            _logger.LogInformation(
                "StormHistory lat={Lat} lng={Lng} state={State} → {Hail} hail, {Wind} wind",
                lat, lng, stateAbbr, hailList.Count, windList.Count);

            return Json(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }

        private async Task<(List<PropertyRecord> Records, int HailEventCount, List<HailEventDto> HailEvents)> GetPropertiesAsync(
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

            // ── Start OSM immediately so it runs in parallel with everything else ──
            var osmTask = SafeOsm(centerLat, centerLng, radiusMiles);

            // ── Hail event cache ─────────────────────────────────────────────
            // Weather data changes at most once per day — cache for 30 min.
            // Key is snapped to a ~7-mile grid cell so adjacent addresses reuse
            // the same cached data rather than firing a fresh round of API calls.
            var cacheKey = $"hail:{Math.Round(centerLat, 1)}:{Math.Round(centerLng, 1)}:{stateAbbr}";

            if (!_cache.TryGetValue(cacheKey, out List<RealDataService.HailEvent>? cachedHail) || cachedHail == null)
            {
                // Fetch all hail sources in parallel — OSM is already running above
                var swdiTask     = SafeSwdi(centerLat, centerLng, radiusMiles);
                var mesonetTask  = SafeLsr(centerLat, centerLng, radiusMiles, stateAbbr);
                var tomorrowTask = SafeTomorrow(centerLat, centerLng);

                // Wait for hail AND OSM together so nothing serializes
                await Task.WhenAll(osmTask, swdiTask, mesonetTask, tomorrowTask);

                cachedHail = new List<RealDataService.HailEvent>();
                cachedHail.AddRange(swdiTask.Result);
                cachedHail.AddRange(mesonetTask.Result);
                cachedHail.AddRange(tomorrowTask.Result);

                _cache.Set(cacheKey, cachedHail, TimeSpan.FromMinutes(30));
                _logger.LogInformation("Hail cache MISS for {Key} — fetched {Count} events", cacheKey, cachedHail.Count);
            }
            else
            {
                _logger.LogInformation("Hail cache HIT for {Key} — {Count} events", cacheKey, cachedHail.Count);
            }

            var osmAddresses = await osmTask;   // already done on cache miss; instant on cache hit

            // Merge all hail sources
            var hailEvents = cachedHail;

            // Fallback: if OSM returned nothing (common in newer suburbs), use Google reverse-geocode grid
            if (osmAddresses.Count == 0)
                osmAddresses = await _realData.GetAddressesViaGoogleGridAsync(
                    centerLat, centerLng, radiusMiles, _apiKey);

            // Fallback: if still no hail data at all, try NOAA Storm Events (ground-truth reports)
            if (hailEvents.Count == 0 && !string.IsNullOrEmpty(stateAbbr))
            {
                var stormEvents = await SafeStormEvents(centerLat, centerLng, radiusMiles, stateAbbr);
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

            var hailDtos = hailEvents.Select(e => new HailEventDto
            {
                Lat        = Math.Round(e.Lat, 6),
                Lng        = Math.Round(e.Lng, 6),
                SizeInches = Math.Round(e.SizeInches, 2),
                Date       = e.Date.ToString("yyyy-MM-dd"),
                Source     = e.Source
            }).ToList();
            return (records, hailEvents.Count, hailDtos);
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
        // GET /RoofHealth/StormEvents
        //   lat, lng      — map center (required)
        //   state         — 2-letter state abbr; auto-detected via Nominatim if blank
        //   radiusMiles   — search radius (5–200, default 50)
        //   minHailInches — minimum hail size filter (0.25–3.0, default 0.75)
        //   includeWind   — include wind gust events in clustering (default true)
        //   lookbackDays  — how far back to search (7–365, default 90)
        // Returns storm clusters ranked by relevancy score.
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("StormEvents")]
        public async Task<IActionResult> StormEvents(
            double lat = 0, double lng = 0, string state = "",
            double radiusMiles   = 50,   double minHailInches = 0.75,
            bool   includeWind   = true, int    lookbackDays  = 90)
        {
            if (lat == 0 && lng == 0)
                return BadRequest(new { error = "lat and lng are required" });

            lookbackDays  = Math.Clamp(lookbackDays,  7,    365);
            radiusMiles   = Math.Clamp(radiusMiles,   5,    200);
            minHailInches = Math.Clamp(minHailInches, 0.25, 3.0);

            // Auto-detect state if caller didn't supply one
            var stateAbbr = state?.Trim().ToUpperInvariant() ?? "";
            if (stateAbbr.Length != 2)
                stateAbbr = await _realData.GetStateFromLatLngAsync(lat, lng);

            _logger.LogInformation(
                "StormEvents: lat={Lat} lng={Lng} state={State} r={R} minHail={MinHail} wind={Wind} days={Days}",
                lat, lng, stateAbbr, radiusMiles, minHailInches, includeWind, lookbackDays);

            var clusters = await _realData.GetStormClustersAsync(
                lat, lng, radiusMiles, stateAbbr, minHailInches, includeWind, lookbackDays);

            var dtos = clusters.Select(c => new StormClusterDto
            {
                Id            = c.Id,
                Date          = c.Date.ToString("yyyy-MM-dd"),
                Lat           = c.Lat,
                Lng           = c.Lng,
                MaxHailInches = c.MaxHailInches,
                MaxWindMph    = c.MaxWindMph,
                HailReports   = c.HailReports,
                WindReports   = c.WindReports,
                Score         = c.RelevancyScore,
            }).ToList();

            var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            return Json(new
            {
                storms      = dtos,
                count       = dtos.Count,
                lookbackDays,
                state       = stateAbbr
            }, opts);
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /RoofHealth/WmsLayers
        //   Fetches Iowa State Mesonet MRMS WMS GetCapabilities and returns
        //   all available layer names as JSON — use this to verify the correct
        //   MESH layer name when the tile overlay isn't rendering.
        //   Open in browser: /RoofHealth/WmsLayers
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("WmsLayers")]
        public async Task<IActionResult> WmsLayers()
        {
            // Probe multiple WMS endpoints — the precipitation mrms.cgi doesn't have MESH
            var endpoints = new[]
            {
                "https://mesonet.agron.iastate.edu/cgi-bin/wms/us/mrms.cgi",
                "https://mesonet.agron.iastate.edu/cgi-bin/wms/nexrad/n0r.cgi",
                "https://mesonet.agron.iastate.edu/cgi-bin/wms/us/hail.cgi",
                "https://opengeo.ncep.noaa.gov/geoserver/conus/ows",
                "https://mapservices.weather.noaa.gov/eventdriven/services/radar/radar_base_reflectivity/MapServer/WMSServer",
            };

            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "StormLeadPro/1.0");
            http.Timeout = TimeSpan.FromSeconds(8);

            var results = new List<object>();
            foreach (var baseUrl in endpoints)
            {
                var capUrl = baseUrl + "?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.1.1";
                try
                {
                    var xml    = await http.GetStringAsync(capUrl);
                    var layers = new List<string>();
                    var doc    = new System.Xml.XmlDocument();
                    doc.LoadXml(xml);
                    foreach (System.Xml.XmlNode layer in doc.GetElementsByTagName("Layer"))
                    {
                        var nameNode = layer.SelectSingleNode("Name");
                        if (nameNode != null && !string.IsNullOrWhiteSpace(nameNode.InnerText))
                            layers.Add(nameNode.InnerText.Trim());
                    }
                    var unique = layers.Distinct().OrderBy(x => x).ToList();
                    results.Add(new
                    {
                        url        = baseUrl,
                        status     = "ok",
                        layerCount = unique.Count,
                        meshLayers = unique.Where(l =>
                            l.Contains("mesh", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("hail", StringComparison.OrdinalIgnoreCase)).ToList(),
                        allLayers  = unique
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new { url = baseUrl, status = "error", error = ex.Message });
                }
            }
            return Json(results);
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /RoofHealth/NhpProbe
        //   Tests candidate NHP / ArcGIS endpoints to find the working one.
        //   Open in browser: /RoofHealth/NhpProbe
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("NhpProbe")]
        public async Task<IActionResult> NhpProbe()
        {
            // Bounding box over Dallas TX — used to test each endpoint
            const string envelope = "-97.1,32.5,-96.4,33.1";

            var candidates = new[]
            {
                // Candidate 1 — speculative URL used in current code
                "https://services1.arcgis.com/A6seFM3Tl8hPB0Q6/arcgis/rest/services/NHP_HailSwath_MRMS_MESH/FeatureServer/0/query",
                // Candidate 2 — alternate org ID pattern for Western University
                "https://services.arcgis.com/A6seFM3Tl8hPB0Q6/arcgis/rest/services/NHP_HailSwath_MRMS_MESH/FeatureServer/0/query",
                // Candidate 3 — public NHP data via ArcGIS Online
                "https://services1.arcgis.com/jUJYIo9tSA7EHvfZ/arcgis/rest/services/NHP_HailSwath/FeatureServer/0/query",
                // Candidate 4 — Iowa State tile cache MESH product probe
                "https://mesonet.agron.iastate.edu/cache/tile.py/1.0.0/ridge::USCOMP-MESH/0/0/0.png",
                // Candidate 5 — Iowa State MRMS archive info
                "https://mesonet.agron.iastate.edu/api/1/currents.geojson?network=MESH",
            };

            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "StormLeadPro/1.0");
            http.Timeout = TimeSpan.FromSeconds(8);

            var results = new List<object>();
            foreach (var url in candidates)
            {
                try
                {
                    // ArcGIS feature service queries need POST with params
                    string testUrl = url;
                    System.Net.Http.HttpResponseMessage resp;

                    if (url.Contains("FeatureServer"))
                    {
                        var qp = new Dictionary<string, string>
                        {
                            ["f"]             = "json",
                            ["where"]         = "1=1",
                            ["geometry"]      = envelope,
                            ["geometryType"]  = "esriGeometryEnvelope",
                            ["inSR"]          = "4326",
                            ["outFields"]     = "*",
                            ["resultRecordCount"] = "1"
                        };
                        resp = await http.PostAsync(url, new FormUrlEncodedContent(qp));
                    }
                    else
                    {
                        resp = await http.GetAsync(url);
                    }

                    var body    = await resp.Content.ReadAsStringAsync();
                    var preview = body.Length > 300 ? body[..300] + "…" : body;
                    results.Add(new
                    {
                        url,
                        status      = (int)resp.StatusCode,
                        ok          = resp.IsSuccessStatusCode,
                        bodyPreview = preview
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new { url, status = 0, ok = false, bodyPreview = ex.Message });
                }
            }
            return Json(results);
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /RoofHealth/NhpSwathDebug
        //   Runs all three ArcGIS search strategies and shows what each returns.
        //   Use this to verify that the NHP hail swath feature service can be found.
        //   Open in browser: /RoofHealth/NhpSwathDebug
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("NhpSwathDebug")]
        public async Task<IActionResult> NhpSwathDebug()
        {
            var searches = new[]
            {
                "https://hub.arcgis.com/api/v3/datasets?q=hail+swath+MRMS+MESH&filter[access]=public&limit=5",
                "https://www.arcgis.com/sharing/rest/search?q=NHP+hail+swath+MRMS+MESH&num=10&f=json",
                "https://www.arcgis.com/sharing/rest/search?q=hail+swath+MRMS+type:Feature+Service&num=5&f=json",
            };

            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "StormLeadPro/1.0");
            http.Timeout = TimeSpan.FromSeconds(12);

            var results = new List<object>();
            foreach (var url in searches)
            {
                try
                {
                    var body    = await http.GetStringAsync(url);
                    var preview = body.Length > 600 ? body[..600] + "…" : body;
                    results.Add(new { url, status = "ok", bodyPreview = preview, length = body.Length });
                }
                catch (Exception ex)
                {
                    results.Add(new { url, status = "error", error = ex.Message });
                }
            }
            return Json(results, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /RoofHealth/NhpFieldsDebug
        //   Resolves the NHP feature service URL, fetches layer metadata (geometry
        //   type + all field names), and runs a 2-record sample query over Dallas.
        //   Use after NhpSwathDebug confirms the search is working.
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("NhpFieldsDebug")]
        public async Task<IActionResult> NhpFieldsDebug()
        {
            // Reuse the same search strategy as GetMrmsHailSwathGeoJsonAsync
            var searches = new[]
            {
                "https://hub.arcgis.com/api/v3/datasets?q=hail+swath+MRMS+MESH&filter%5Baccess%5D=public&limit=5",
                "https://www.arcgis.com/sharing/rest/search?q=NHP+hail+swath+MRMS+MESH&num=10&f=json",
            };

            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "StormLeadPro/1.0");
            http.Timeout = TimeSpan.FromSeconds(12);

            // Step 1: find the FeatureServer URL
            string? svcUrl = null;
            foreach (var s in searches)
            {
                try
                {
                    var body = await http.GetStringAsync(s);
                    // Pull the url field out of each result manually
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    // ArcGIS Online format
                    if (root.TryGetProperty("results", out var res))
                    {
                        foreach (var item in res.EnumerateArray())
                        {
                            if (item.TryGetProperty("url", out var u))
                            {
                                var raw = u.GetString() ?? "";
                                if (raw.Contains("FeatureServer", StringComparison.OrdinalIgnoreCase))
                                { svcUrl = raw.TrimEnd('/'); break; }
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(svcUrl)) break;
                }
                catch { }
            }

            if (string.IsNullOrEmpty(svcUrl))
                return Json(new { error = "No FeatureServer URL found in any search. Run /RoofHealth/NhpSwathDebug first." });

            var result = new System.Collections.Generic.Dictionary<string, object>
            {
                ["resolvedServiceUrl"] = svcUrl
            };

            // Step 2: fetch layer 0 metadata
            try
            {
                var metaBody = await http.GetStringAsync(svcUrl + "/0?f=json");
                using var metaDoc = JsonDocument.Parse(metaBody);
                var metaRoot = metaDoc.RootElement;

                var geomType = metaRoot.TryGetProperty("geometryType", out var gt) ? gt.GetString() : "unknown";
                result["geometryType"] = geomType ?? "unknown";

                var fieldList = new List<string>();
                if (metaRoot.TryGetProperty("fields", out var fields))
                    foreach (var f in fields.EnumerateArray())
                        if (f.TryGetProperty("name", out var fn))
                            fieldList.Add(fn.GetString() ?? "");
                result["fields"] = fieldList;
            }
            catch (Exception ex)
            {
                result["metadataError"] = ex.Message;
            }

            // Step 3: sample query — 2 records from Dallas bbox, no date filter
            try
            {
                var qp = new Dictionary<string, string>
                {
                    ["f"]                 = "json",
                    ["outFields"]         = "*",
                    ["where"]             = "1=1",
                    ["geometry"]          = "-97.1,32.5,-96.4,33.1",
                    ["geometryType"]      = "esriGeometryEnvelope",
                    ["spatialRel"]        = "esriSpatialRelIntersects",
                    ["inSR"]              = "4326",
                    ["resultRecordCount"] = "2"
                };
                var qResp  = await http.PostAsync(svcUrl + "/0/query", new FormUrlEncodedContent(qp));
                var qBody  = await qResp.Content.ReadAsStringAsync();
                result["sampleQueryStatus"] = (int)qResp.StatusCode;
                result["sampleQueryBody"]   = qBody.Length > 1200 ? qBody[..1200] + "…" : qBody;
            }
            catch (Exception ex)
            {
                result["sampleQueryError"] = ex.Message;
            }

            return Json(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /RoofHealth/HailSwathPolygons
        //   Returns size-banded convex-hull swath polygons as GeoJSON for the
        //   given bounding box and lookback window.
        //   Used by the "Hail Swaths" overlay toggle in Storm Explorer.
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("HailSwathPolygons")]
        public async Task<IActionResult> HailSwathPolygons(
            double minLat = 0, double maxLat = 0,
            double minLng = 0, double maxLng = 0,
            int    lookbackDays = 90)
        {
            if (minLat == 0 && maxLat == 0)
                return Content("{\"type\":\"FeatureCollection\",\"features\":[]}", "application/json");

            var geojson = await _realData.GetMrmsHailSwathGeoJsonAsync(
                minLat, maxLat, minLng, maxLng, lookbackDays);

            return Content(geojson, "application/json");
        }

        // GET /RoofHealth/HailSwath
        //   Returns individual LSR + SPC hail event reports as GeoJSON Points
        //   for the given bounding box and lookback window.
        //   Used by the "Hail Reports" overlay toggle in Storm Explorer.
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("HailSwath")]
        public async Task<IActionResult> HailSwath(
            double minLat = 0, double maxLat = 0,
            double minLng = 0, double maxLng = 0,
            int    lookbackDays = 90)
        {
            if (minLat == 0 && maxLat == 0)
                return Content("{\"type\":\"FeatureCollection\",\"features\":[]}", "application/json");

            var geojson = await _realData.GetHailEventsGeoJsonAsync(
                minLat, maxLat, minLng, maxLng, lookbackDays);

            return Content(geojson, "application/json");
        }

        // DTOs

        private class GeoResult
        {
            public string FormattedAddress { get; set; } = "";
            public double Lat        { get; set; }
            public double Lng        { get; set; }
            public string StateAbbr  { get; set; } = "";
        }

        private class HailEventDto
        {
            [JsonPropertyName("lat")]        public double Lat        { get; set; }
            [JsonPropertyName("lng")]        public double Lng        { get; set; }
            [JsonPropertyName("sizeInches")] public double SizeInches { get; set; }
            [JsonPropertyName("date")]       public string Date       { get; set; } = "";
            [JsonPropertyName("source")]     public string Source     { get; set; } = "";
        }

        private class StormHistoryResult
        {
            [JsonPropertyName("hail")] public List<StormHistoryHailDto> Hail { get; set; } = new();
            [JsonPropertyName("wind")] public List<StormHistoryWindDto> Wind { get; set; } = new();
        }
        private class StormHistoryHailDto
        {
            [JsonPropertyName("date")]       public string Date       { get; set; } = "";
            [JsonPropertyName("sizeInches")] public double SizeInches { get; set; }
            [JsonPropertyName("source")]     public string Source     { get; set; } = "";
        }
        private class StormHistoryWindDto
        {
            [JsonPropertyName("date")]    public string Date    { get; set; } = "";
            [JsonPropertyName("windMph")] public int    WindMph { get; set; }
            [JsonPropertyName("source")]  public string Source  { get; set; } = "";
        }

        private class StormClusterDto
        {
            [JsonPropertyName("id")]            public string Id            { get; set; } = "";
            [JsonPropertyName("date")]          public string Date          { get; set; } = "";
            [JsonPropertyName("lat")]           public double Lat           { get; set; }
            [JsonPropertyName("lng")]           public double Lng           { get; set; }
            [JsonPropertyName("maxHailInches")] public double MaxHailInches { get; set; }
            [JsonPropertyName("maxWindMph")]    public double MaxWindMph    { get; set; }
            [JsonPropertyName("hailReports")]   public int    HailReports   { get; set; }
            [JsonPropertyName("windReports")]   public int    WindReports   { get; set; }
            [JsonPropertyName("score")]         public double Score         { get; set; }
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
