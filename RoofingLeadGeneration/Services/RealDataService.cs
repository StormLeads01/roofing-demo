using System.Text.Json;

namespace RoofingLeadGeneration.Services
{
    /// <summary>
    /// Wraps the three free external data sources:
    ///   1. OpenStreetMap Overpass API  — real nearby addresses   (no key needed)
    ///   2. NOAA SWDI API               — real hail event history  (no key needed)
    ///   3. Regrid Parcel API           — property owner names     (free 25/day token)
    ///
    /// Sign-ups:
    ///   Regrid  → https://app.regrid.com  (free Starter account, 25 lookups/day)
    ///   Then add your token to appsettings.json: "Regrid": { "Token": "YOUR_TOKEN" }
    /// </summary>
    public class RealDataService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration     _config;
        private readonly ILogger<RealDataService> _logger;

        public RealDataService(IHttpClientFactory factory, IConfiguration config, ILogger<RealDataService> logger)
        {
            _httpFactory = factory;
            _config      = config;
            _logger      = logger;
        }

        // ─────────────────────────────────────────────────────────────────
        // 1. OpenStreetMap Overpass  –  real residential addresses
        //    No key needed. Rate limit: 1 req/sec recommended.
        //    Docs: https://wiki.openstreetmap.org/wiki/Overpass_API
        // ─────────────────────────────────────────────────────────────────
        public async Task<List<OsmAddress>> GetNearbyAddressesAsync(
            double lat, double lng, double radiusMiles)
        {
            double radiusMeters = radiusMiles * 1609.34;

            // Query for any node/way with a house number — street tag is optional
            // (many US addresses in OSM omit addr:street on the building itself)
            var query = $@"[out:json][timeout:40];
(
  node[""addr:housenumber""](around:{radiusMeters:F0},{lat},{lng});
  way[""addr:housenumber""](around:{radiusMeters:F0},{lat},{lng});
  relation[""addr:housenumber""](around:{radiusMeters:F0},{lat},{lng});
);
out center;";

            try
            {
                using var client  = _httpFactory.CreateClient("overpass");
                var       content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("data", query)
                });

                var resp = await client.PostAsync(
                    "https://overpass-api.de/api/interpreter", content);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Overpass API returned {Status}", resp.StatusCode);
                    return new List<OsmAddress>();
                }

                var json = await resp.Content.ReadAsStringAsync();
                return ParseOverpassAddresses(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Overpass API call failed");
                return new List<OsmAddress>();
            }
        }

        private static List<OsmAddress> ParseOverpassAddresses(string json)
        {
            using var doc  = JsonDocument.Parse(json);
            var       list = new List<OsmAddress>();

            if (!doc.RootElement.TryGetProperty("elements", out var elements))
                return list;

            foreach (var el in elements.EnumerateArray())
            {
                if (!el.TryGetProperty("tags", out var tags))             continue;
                if (!tags.TryGetProperty("addr:housenumber", out var hn)) continue;

                double elLat, elLng;
                var type = el.GetProperty("type").GetString();
                if (type == "node")
                {
                    elLat = el.GetProperty("lat").GetDouble();
                    elLng = el.GetProperty("lon").GetDouble();
                }
                else if (el.TryGetProperty("center", out var center))
                {
                    elLat = center.GetProperty("lat").GetDouble();
                    elLng = center.GetProperty("lon").GetDouble();
                }
                else continue;

                var street = tags.TryGetProperty("addr:street",   out var st) ? st.GetString() ?? "" : "";
                var city   = tags.TryGetProperty("addr:city",     out var c)  ? c.GetString()  ?? "" : "";
                var state  = tags.TryGetProperty("addr:state",    out var s)  ? s.GetString()  ?? "" : "";
                var zip    = tags.TryGetProperty("addr:postcode", out var z)  ? z.GetString()  ?? "" : "";

                // Skip if we can't form a meaningful address
                if (string.IsNullOrEmpty(street) && string.IsNullOrEmpty(city)) continue;

                var addr = string.IsNullOrEmpty(street)
                    ? hn.GetString()!
                    : $"{hn.GetString()} {street}";
                if (!string.IsNullOrEmpty(city))  addr += $", {city}";
                if (!string.IsNullOrEmpty(state)) addr += $", {state}";
                if (!string.IsNullOrEmpty(zip))   addr += $" {zip}";

                list.Add(new OsmAddress
                {
                    FullAddress = addr,
                    HouseNumber = hn.GetString() ?? "",
                    Street      = street,
                    City        = city,
                    State       = state,
                    Lat         = elLat,
                    Lng         = elLng
                });
            }

            return list;
        }

        // ─────────────────────────────────────────────────────────────────
        // 2. NOAA Severe Weather Data Inventory (SWDI)  –  real hail events
        //    No API key needed. Data typically available 120+ days back.
        //    Dataset: nx3hail (NEXRAD Level-3 Hail Signatures)
        //    Docs: https://www.ncei.noaa.gov/products/severe-weather-data-inventory
        //
        //    NOTE: The SWDI API has a 1-year limit per query. We make two calls
        //    to cover the last 2 years of storm history.
        // ─────────────────────────────────────────────────────────────────
        public async Task<List<HailEvent>> GetSwdiHailEventsAsync(
            double lat, double lng, double radiusMiles)
        {
            // Hail events are scored against properties within 2 miles, so the bbox
            // must extend at least (radiusMiles + 2) from center. Use a 5-mile minimum.
            double hailMiles = Math.Max(radiusMiles + 2.0, 5.0);
            double span  = hailMiles / 69.0;
            double west  = Math.Round(lng - span, 4);
            double east  = Math.Round(lng + span, 4);
            double south = Math.Round(lat - span, 4);
            double north = Math.Round(lat + span, 4);

            // SWDI max range = 744 hours (~31 days). Split the 2-year window into
            // 30-day chunks and fetch all in parallel.
            var end   = DateTime.UtcNow.AddDays(-121);
            var start = end.AddYears(-2);

            var windows = new List<(DateTime from, DateTime to)>();
            var cursor = start;
            while (cursor < end)
            {
                var next = cursor.AddDays(30);
                if (next > end) next = end;
                windows.Add((cursor, next));
                cursor = next;
            }

            // Throttle to 4 concurrent requests — NOAA rejects large parallel bursts
            var allEvents = new List<HailEvent>();
            try
            {
                using var semaphore = new System.Threading.SemaphoreSlim(4);
                var tasks = windows.Select(async w =>
                {
                    await semaphore.WaitAsync();
                    try   { return await FetchSwdiBatch(west, south, east, north, w.from, w.to); }
                    finally { semaphore.Release(); }
                });
                var batches = await Task.WhenAll(tasks);
                allEvents.AddRange(batches.SelectMany(b => b));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SWDI parallel fetch failed");
            }

            _logger.LogInformation("SWDI returned {Count} hail events near {Lat},{Lng} ({Windows} windows)",
                allEvents.Count, lat, lng, windows.Count);

            return allEvents;
        }

        private async Task<List<HailEvent>> FetchSwdiBatch(
            double west, double south, double east, double north,
            DateTime from, DateTime to)
        {
            var results = new List<HailEvent>();
            // URL format: /swdiws/json/{dataset}/{startYYYYMMDD}:{endYYYYMMDD}?bbox=W,S,E,N
            var url = $"https://www.ncei.noaa.gov/swdiws/json/nx3hail" +
                      $"/{from:yyyyMMdd}:{to:yyyyMMdd}" +
                      $"?bbox={west},{south},{east},{north}";
            try
            {
                using var client = _httpFactory.CreateClient("noaa");
                var resp = await client.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("SWDI returned {Status} for {Url}", resp.StatusCode, url);
                    return results;
                }

                var json = await resp.Content.ReadAsStringAsync();
                ParseSwdiJson(json, results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SWDI batch fetch failed for {From:yyyyMMdd}:{To:yyyyMMdd}", from, to);
            }
            return results;
        }

        private static void ParseSwdiJson(string json, List<HailEvent> events)
        {
            using var doc = JsonDocument.Parse(json);

            // SWDI response format: { "swdiJsonResponse": {}, "result": [...], "summary": {...} }
            // Also handle legacy: { "data": [...] } or bare [...]
            JsonElement arr;
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("result", out arr))
            { /* current format */ }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                     doc.RootElement.TryGetProperty("data", out arr))
            { /* legacy format */ }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            { arr = doc.RootElement; }
            else return;

            foreach (var item in arr.EnumerateArray())
            {
                // Coordinates come from SHAPE: "POINT (lon lat)"
                double? hLat = null, hLng = null;
                if (item.TryGetProperty("SHAPE", out var shape))
                {
                    var s = shape.GetString() ?? "";
                    // Format: "POINT (lon lat)"
                    var inner = s.Replace("POINT", "").Trim().Trim('(', ')').Trim();
                    var parts = inner.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 &&
                        double.TryParse(parts[0], System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var pLng) &&
                        double.TryParse(parts[1], System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var pLat))
                    {
                        hLng = pLng;
                        hLat = pLat;
                    }
                }

                if (hLat is null || hLng is null) continue;

                // Hail size: MAXSIZE field (inches)
                double? size = GetDoubleField(item, "MAXSIZE");

                // Timestamp: ZTIME is ISO 8601 e.g. "2025-04-05T08:29:42Z"
                DateTime date = DateTime.UtcNow.AddYears(-1);
                if (item.TryGetProperty("ZTIME", out var zt))
                    DateTime.TryParse(zt.GetString(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out date);

                events.Add(new HailEvent
                {
                    Lat        = hLat.Value,
                    Lng        = hLng.Value,
                    SizeInches = size ?? 0.75,
                    Date       = date
                });
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // 1b. Google Maps Reverse-Geocode Grid  –  fallback when OSM returns 0
        //     Generates a grid of points within the radius, reverse-geocodes each
        //     via Google Maps, deduplicates, and returns residential addresses.
        //     Cost: ~$5 / 1000 calls (~$0.25 for a 50-point grid search).
        // ─────────────────────────────────────────────────────────────────
        public async Task<List<OsmAddress>> GetAddressesViaGoogleGridAsync(
            double centerLat, double centerLng, double radiusMiles, string googleApiKey)
        {
            var addresses = new List<OsmAddress>();
            var seen      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Adaptive grid spacing — target ~50 points regardless of radius
            double radiusMeters  = radiusMiles * 1609.34;
            double spacingMeters = Math.Max(radiusMeters / 5.0, 150.0);
            double latStep = spacingMeters / 111111.0;
            double lngStep = spacingMeters / (111111.0 * Math.Cos(centerLat * Math.PI / 180.0));

            var points = new List<(double lat, double lng)>();
            for (double dLat = -radiusMeters / 111111.0; dLat <= radiusMeters / 111111.0; dLat += latStep)
            for (double dLng = -lngStep * 6;              dLng <= lngStep * 6;              dLng += lngStep)
            {
                var pLat = centerLat + dLat;
                var pLng = centerLng + dLng;
                if (HaversineDistanceMiles(centerLat, centerLng, pLat, pLng) <= radiusMiles)
                    points.Add((pLat, pLng));
            }

            // Throttle to 5 concurrent reverse-geocode calls
            using var sem = new System.Threading.SemaphoreSlim(5);
            var tasks = points.Take(50).Select(async pt =>
            {
                await sem.WaitAsync();
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var url  = $"https://maps.googleapis.com/maps/api/geocode/json" +
                               $"?latlng={pt.lat},{pt.lng}&result_type=street_address&key={googleApiKey}";
                    var json = await client.GetStringAsync(url);
                    return ParseGoogleReverseGeocode(json, pt.lat, pt.lng);
                }
                catch { return null; }
                finally { sem.Release(); }
            });

            var results = await Task.WhenAll(tasks);
            foreach (var addr in results)
            {
                if (addr == null) continue;
                if (!seen.Add(addr.FullAddress)) continue;
                addresses.Add(addr);
            }

            _logger.LogInformation(
                "Google grid fallback: {Count} unique addresses from {Points} points",
                addresses.Count, points.Count);

            return addresses;
        }

        private static OsmAddress? ParseGoogleReverseGeocode(string json, double fallbackLat, double fallbackLng)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("status").GetString() != "OK") return null;

            var results = root.GetProperty("results");
            if (results.GetArrayLength() == 0) return null;

            var first     = results[0];
            var formatted = first.GetProperty("formatted_address").GetString() ?? "";

            // Only residential — must start with a house number
            if (!System.Text.RegularExpressions.Regex.IsMatch(formatted, @"^\d+")) return null;

            var loc    = first.GetProperty("geometry").GetProperty("location");
            var addrLat = loc.GetProperty("lat").GetDouble();
            var addrLng = loc.GetProperty("lng").GetDouble();

            string num = "", street = "", city = "", state = "", zip = "";
            foreach (var comp in first.GetProperty("address_components").EnumerateArray())
            {
                var types = comp.GetProperty("types").EnumerateArray()
                                .Select(t => t.GetString()).ToHashSet();
                var longName  = comp.GetProperty("long_name").GetString()  ?? "";
                var shortName = comp.GetProperty("short_name").GetString() ?? "";

                if (types.Contains("street_number"))              num    = longName;
                else if (types.Contains("route"))                 street = longName;
                else if (types.Contains("locality"))              city   = longName;
                else if (types.Contains("administrative_area_level_1")) state = shortName;
                else if (types.Contains("postal_code"))           zip    = longName;
            }

            return new OsmAddress
            {
                FullAddress = formatted,
                HouseNumber = num,
                Street      = street,
                City        = city,
                State       = state,
                Lat         = addrLat,
                Lng         = addrLng
            };
        }

        // ─────────────────────────────────────────────────────────────────
        // 2b. NOAA Storm Events Database  –  fallback hail data
        //     Ground-truth spotter/insurance reports. No API key needed.
        //     Covers areas where NEXRAD radar has gaps or sparse coverage.
        //     Queries by state, then filters by proximity to search coordinates.
        //     Docs: https://www.ncdc.noaa.gov/stormeventsapi/
        // ─────────────────────────────────────────────────────────────────
        public async Task<List<HailEvent>> GetStormEventsHailAsync(
            double lat, double lng, double radiusMiles, string stateAbbr)
        {
            if (string.IsNullOrEmpty(stateAbbr)) return new List<HailEvent>();

            // Go back 5 years — Storm Events has no radar lag, data is ~60 days current
            var end   = DateTime.UtcNow;
            var start = end.AddYears(-5);

            var url = "https://www.ncdc.noaa.gov/stormeventsapi/rest/data" +
                      $"?format=json&eventType=Hail" +
                      $"&beginDate_mm={start:MM}&beginDate_dd={start:dd}&beginDate_yyyy={start:yyyy}" +
                      $"&endDate_mm={end:MM}&endDate_dd={end:dd}&endDate_yyyy={end:yyyy}" +
                      $"&state={Uri.EscapeDataString(stateAbbr)}" +
                      "&hailSize=0.50&range=50&rangetype=county&limit=500";

            try
            {
                using var client = _httpFactory.CreateClient("noaa");
                var resp = await client.GetAsync(url);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Storm Events API returned {Status} for state {State}",
                        resp.StatusCode, stateAbbr);
                    return new List<HailEvent>();
                }

                var json = await resp.Content.ReadAsStringAsync();
                _logger.LogInformation("Storm Events fallback for {State} — {Len} bytes",
                    stateAbbr, json.Length);

                return ParseStormEventsJson(json, lat, lng, radiusMiles);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Storm Events API call failed for state {State}", stateAbbr);
                return new List<HailEvent>();
            }
        }

        private List<HailEvent> ParseStormEventsJson(
            string json, double centerLat, double centerLng, double radiusMiles)
        {
            var results = new List<HailEvent>();
            // Filter radius: use 3x the search radius to ensure we catch nearby events
            double filterMiles = Math.Max(radiusMiles * 3, 10);

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Response can be { "data": [...] } or just [...]
                JsonElement arr;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("data", out arr)) { }
                else if (root.ValueKind == JsonValueKind.Array)
                    arr = root;
                else return results;

                foreach (var item in arr.EnumerateArray())
                {
                    // Latitude — try multiple field names
                    double? evLat = GetDoubleField(item, "BEGIN_LAT")
                                 ?? GetDoubleField(item, "begin_lat")
                                 ?? GetDoubleField(item, "LAT");
                    double? evLng = GetDoubleField(item, "BEGIN_LON")
                                 ?? GetDoubleField(item, "begin_lon")
                                 ?? GetDoubleField(item, "LON");

                    if (evLat is null || evLng is null) continue;

                    // Distance filter
                    if (HaversineDistanceMiles(centerLat, centerLng,
                            evLat.Value, evLng.Value) > filterMiles) continue;

                    // Hail size — MAGNITUDE field, inches
                    double? size = GetDoubleField(item, "MAGNITUDE")
                                ?? GetDoubleField(item, "magnitude")
                                ?? GetDoubleField(item, "HAIL_SIZE");

                    // Date — BEGIN_DATE_TIME or BEGIN_YEARMONTH
                    DateTime date = DateTime.UtcNow.AddYears(-1);
                    foreach (var dateField in new[] { "BEGIN_DATE_TIME", "begin_date_time", "BEGIN_DATE" })
                    {
                        if (!item.TryGetProperty(dateField, out var dtEl)) continue;
                        var s = dtEl.GetString() ?? "";
                        if (s.Length >= 8 &&
                            DateTime.TryParse(s, out var parsed))
                        { date = parsed; break; }
                    }

                    results.Add(new HailEvent
                    {
                        Lat        = evLat.Value,
                        Lng        = evLng.Value,
                        SizeInches = size ?? 0.75,
                        Date       = date
                    });
                }

                _logger.LogInformation(
                    "Storm Events parsed {Count} hail events within {Miles} miles",
                    results.Count, filterMiles);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Storm Events parse failed");
            }

            return results;
        }

        // ─────────────────────────────────────────────────────────────────
        // 3. Regrid Parcel API  –  property owner names
        //    Requires a free Regrid token (25 lookups/day on free Starter plan).
        //    Sign up at: https://app.regrid.com
        //    Add token to appsettings.json: "Regrid": { "Token": "..." }
        //    Docs: https://support.regrid.com/api/using-the-parcel-api-v1
        // ─────────────────────────────────────────────────────────────────
        // Parcel data returned by Regrid — owner name + year the home was built
        public record RegridParcelData(string? OwnerName, int? YearBuilt);

        public async Task<RegridParcelData?> GetRegridParcelDataAsync(double lat, double lng, string? address = null)
        {
            var token = _config["Regrid:Token"];
            if (string.IsNullOrWhiteSpace(token)) return null;

            // Regrid v2 API — address search uses "query" param
            // NOTE: trial sandbox tokens are restricted to 7 counties only
            string url;
            if (!string.IsNullOrWhiteSpace(address))
            {
                var clean = address
                    .Replace(", USA", "")
                    .Replace(", United States", "")
                    .Trim();

                url = $"https://app.regrid.com/api/v2/parcels/address" +
                      $"?query={Uri.EscapeDataString(clean)}" +
                      $"&token={token}&limit=1&return_enhanced_ownership=true";
            }
            else
            {
                url = $"https://app.regrid.com/api/v2/parcels/point" +
                      $"?lat={lat}&lon={lng}&token={token}&limit=1&radius=200&return_enhanced_ownership=true";
            }
            try
            {
                using var client = _httpFactory.CreateClient("regrid");
                var resp = await client.GetAsync(url);
                var json = await resp.Content.ReadAsStringAsync();

                // Log full response for debugging (will trim in production once working)
                _logger.LogInformation("Regrid {Status} for {Lat},{Lng} — body: {Body}",
                    resp.StatusCode, lat, lng, json.Length > 2000 ? json[..2000] : json);

                if (!resp.IsSuccessStatusCode) return null;
                try { return ParseRegridParcelData(json); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Regrid parse failed — raw JSON: {Json}", json);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Regrid API call failed");
                return null;
            }
        }

        private static RegridParcelData? ParseRegridParcelData(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Navigate to features array
            JsonElement features;
            if (root.TryGetProperty("parcels", out var parcels) &&
                parcels.TryGetProperty("features", out features))
            { /* v2 */ }
            else if (root.TryGetProperty("features", out features))
            { /* root FeatureCollection */ }
            else return null;

            if (features.GetArrayLength() == 0) return null;

            var first = features[0];
            if (!first.TryGetProperty("properties", out var props)) return null;

            string?  ownerName = null;
            int?     yearBuilt = null;

            if (props.TryGetProperty("fields", out var fields) &&
                fields.ValueKind == JsonValueKind.Object)
            {
                // Owner name
                foreach (var n in new[] { "owner", "owner1", "owner_name", "ownerName", "OWNER_NAME" })
                {
                    if (fields.TryGetProperty(n, out var v) && v.GetString() is { Length: > 0 } raw)
                    { ownerName = TitleCase(raw); break; }
                }

                // Year built — county assessors use many field names
                foreach (var n in new[] { "yearbuilt", "year_built", "yrbuilt", "yr_built",
                                          "YearBuilt", "YEARBUILT", "YR_BUILT", "effyearbuilt" })
                {
                    if (!fields.TryGetProperty(n, out var v)) continue;
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var yr) && yr > 1800)
                    { yearBuilt = yr; break; }
                    if (v.ValueKind == JsonValueKind.String &&
                        int.TryParse(v.GetString(), out yr) && yr > 1800)
                    { yearBuilt = yr; break; }
                }
            }

            // Fallback: enhanced_ownership for owner name
            if (ownerName == null &&
                props.TryGetProperty("enhanced_ownership", out var eoArr) &&
                eoArr.ValueKind == JsonValueKind.Array &&
                eoArr.GetArrayLength() > 0)
            {
                var eo = eoArr[0];
                foreach (var n in new[] { "eo_owner", "eo_ownerlast", "owner_name", "owner" })
                {
                    if (eo.TryGetProperty(n, out var v) && v.GetString() is { Length: > 0 } raw)
                    { ownerName = TitleCase(raw); break; }
                }
            }

            return ownerName != null || yearBuilt != null
                ? new RegridParcelData(ownerName, yearBuilt)
                : null;
        }

        private static string TitleCase(string s) =>
            System.Globalization.CultureInfo.CurrentCulture
                  .TextInfo.ToTitleCase(s.ToLower().Trim());

        // ─────────────────────────────────────────────────────────────────
        // Haversine distance helper (used by RoofHealthController)
        // ─────────────────────────────────────────────────────────────────
        public static double HaversineDistanceMiles(
            double lat1, double lng1, double lat2, double lng2)
        {
            const double R    = 3958.8;
            var          dLat = (lat2 - lat1) * Math.PI / 180;
            var          dLng = (lng2 - lng1) * Math.PI / 180;
            var          a    = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                              + Math.Cos(lat1 * Math.PI / 180)
                              * Math.Cos(lat2 * Math.PI / 180)
                              * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        // ─────────────────────────────────────────────────────────────────
        // Shared helper
        // ─────────────────────────────────────────────────────────────────
        private static double? GetDoubleField(JsonElement el, string prop)
        {
            if (!el.TryGetProperty(prop, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var d))
                return d;
            return null;
        }

        // ─────────────────────────────────────────────────────────────────
        // DTOs
        // ─────────────────────────────────────────────────────────────────
        public record OsmAddress
        {
            public string FullAddress { get; init; } = "";
            public string HouseNumber { get; init; } = "";
            public string Street      { get; init; } = "";
            public string City        { get; init; } = "";
            public string State       { get; init; } = "";
            public double Lat         { get; init; }
            public double Lng         { get; init; }
        }

        public record HailEvent
        {
            public double   Lat        { get; init; }
            public double   Lng        { get; init; }
            public double   SizeInches { get; init; }
            public DateTime Date       { get; init; }
        }
    }
}
