using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RoofingLeadGeneration.Services
{
    /// <summary>
    /// Phase 2 hail visualization: true radar-derived MRMS MESH (Maximum Estimated
    /// Size of Hail) swaths, as size-banded polygons — the same shape contract as
    /// <see cref="RealDataService.GetMrmsHailSwathGeoJsonAsync"/> (the LSR-based
    /// Phase 1 swath) so the frontend can style either source with the same
    /// <c>swathColor()</c>/legend in storm-explorer.js.
    ///
    /// Pipeline: locate the daily-max MESH_Max_1440min GRIB2 for the requested
    /// UTC date on NOAA AWS OpenData (Nov 2020 → present) or the IEM MTArchive
    /// (Oct 2014 → present), download + decompress it, clip to the requested
    /// bbox with <c>gdal_translate</c>, then run <c>gdal_contour -p</c> to
    /// produce size-banded polygons. Both the raw grib (per date) and the
    /// final GeoJSON (per date+bbox) are cached under the Fly.io data volume
    /// so repeat map/report views don't reprocess.
    ///
    /// ── NOT VERIFIED AGAINST LIVE DATA ──
    /// This was written in a sandbox with no GDAL, no wgrib2, and no path to
    /// actually hit NOAA AWS / IEM and confirm a real download+contour run.
    /// Before flipping FeatureFlags:MeshSwaths on, run /RoofHealth/MeshDebug
    /// against a known hail date/location on a machine with GDAL installed and
    /// read the step-by-step output — see docs/mesh-phase2-handoff.md for the
    /// verification checklist. Also note: IEM's own MRMS curation effort hit
    /// real gdal_contour performance problems on this exact product
    /// (github.com/akrherz/iem issue #253, "Curate MRMS MESH Hail Contours") —
    /// budget time to tune timeouts/levels, don't assume first-run correctness.
    /// </summary>
    public class MeshSwathService
    {
        private readonly IHttpClientFactory        _httpFactory;
        private readonly ILogger<MeshSwathService> _logger;
        private readonly string                    _cacheDir;
        private readonly string                    _awsBaseUrl;
        private readonly string                    _iemBaseUrl;
        private readonly string                    _gdalTranslatePath;
        private readonly string                    _gdalContourPath;
        private readonly TimeSpan                  _processTimeout;

        private const string EmptyFeatureCollection = "{\"type\":\"FeatureCollection\",\"features\":[]}";
        private const string OutputSchemaVersion     = "v2"; // bump when ReshapeToAppSchema's output shape changes

        // Storm Explorer now defaults to *every* date in the selected period
        // being "on", which can fire dozens of simultaneous MeshSwath requests
        // from one page load. Each cache-miss request downloads a grib and
        // runs two GDAL subprocesses — cheap one at a time, but the Fly.io box
        // (1 shared CPU / 1GB) OOMs and 502s if too many run concurrently.
        // This caps how many grib-download+GDAL pipelines run at once
        // app-wide; extra requests queue here instead of piling onto the box.
        // Cache hits (the common case after the first view of a date) skip
        // this gate entirely — see GetMeshSwathGeoJsonAsync.
        private static readonly SemaphoreSlim PipelineGate = new(2, 2);

        // MESH switches from IEM MTArchive to NOAA AWS OpenData at this date —
        // NOAA did not backfill Sep 2019–Oct 2020 to AWS (pre a major MRMS
        // upgrade), so that window only exists via IEM. IEM's MTArchive covers
        // back to Oct 2014 (NCEP implementation).
        // Source: mesonet.agron.iastate.edu/archive/mrms.php
        private static readonly DateTime AwsArchiveStart = new(2020, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime IemArchiveStart  = new(2014, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        // Same size bands as the Phase 1 LSR swath (RealDataService), in inches.
        private static readonly double[] SizeBandsInches = { 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 };

        public MeshSwathService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<MeshSwathService> logger, IWebHostEnvironment env)
        {
            _httpFactory = httpFactory;
            _logger      = logger;

            // App_Data (project root, survives bin/obj cleans), not BaseDirectory
            // (bin/Debug/net8.0) — see Program.cs's App_Data comment. This cache
            // is regenerable either way, but keeping it alongside the other
            // App_Data folders avoids yet another data/ vs Data/ collision.
            var defaultCacheDir = Path.Combine(env.ContentRootPath, "App_Data", "mesh-cache");
            _cacheDir = string.IsNullOrWhiteSpace(config["Mesh:CacheDir"]) ? defaultCacheDir : config["Mesh:CacheDir"]!;
            Directory.CreateDirectory(_cacheDir);

            _awsBaseUrl        = config["Mesh:AwsBaseUrl"]        ?? "https://noaa-mrms-pds.s3.amazonaws.com";
            _iemBaseUrl        = config["Mesh:IemArchiveBaseUrl"] ?? "https://mtarchive.geol.iastate.edu";
            _gdalTranslatePath = config["Mesh:GdalTranslatePath"] ?? "gdal_translate";
            _gdalContourPath   = config["Mesh:GdalContourPath"]   ?? "gdal_contour";
            _processTimeout    = TimeSpan.FromSeconds(config.GetValue<int?>("Mesh:ProcessTimeoutSeconds") ?? 90);
        }

        /// <summary>
        /// Returns size-banded MESH swath polygons as a GeoJSON FeatureCollection
        /// string for the given bbox + UTC date. Returns an empty FeatureCollection
        /// (never throws) on any failure — caller/frontend treats that identically
        /// to "no swath for this view."
        /// </summary>
        public async Task<string> GetMeshSwathGeoJsonAsync(
            double minLat, double maxLat, double minLng, double maxLng,
            DateTime dateUtc, MeshDebugSink? debug = null)
        {
            dateUtc = dateUtc.Date;
            if (dateUtc < IemArchiveStart || dateUtc > DateTime.UtcNow.Date)
            {
                debug?.Note("date out of supported range (2014-10-01 .. today)");
                return EmptyFeatureCollection;
            }

            var bboxKey  = $"{minLat:F2}_{maxLat:F2}_{minLng:F2}_{maxLng:F2}";
            var cacheKey = $"{dateUtc:yyyyMMdd}_{bboxKey}";
            // OutputSchemaVersion bumps whenever ReshapeToAppSchema/ContourAsync's
            // *output* format changes (e.g. the below-minimum-band filter added
            // 2026-07-13) — old cached files under the previous name simply won't
            // match and get regenerated, instead of silently serving stale output
            // from before the fix on an already-deployed data volume.
            var geoJsonCachePath = Path.Combine(_cacheDir, $"mesh_{OutputSchemaVersion}_{cacheKey}.geojson");

            if (!(debug?.ForceRefresh ?? false) && File.Exists(geoJsonCachePath))
            {
                debug?.Note($"geojson cache hit: {geoJsonCachePath}");
                return await File.ReadAllTextAsync(geoJsonCachePath);
            }

            await PipelineGate.WaitAsync();
            debug?.Note($"pipeline gate acquired (slots={PipelineGate.CurrentCount} free after acquire)");
            try
            {
                // Re-check the cache now that we hold a slot — another request
                // for the same date+bbox may have finished processing while we
                // were queued, in which case we can skip straight to its output.
                if (!(debug?.ForceRefresh ?? false) && File.Exists(geoJsonCachePath))
                {
                    debug?.Note($"geojson cache hit after gate wait: {geoJsonCachePath}");
                    return await File.ReadAllTextAsync(geoJsonCachePath);
                }

                string? gribPath;
                try
                {
                    gribPath = await EnsureGribDownloadedAsync(dateUtc, debug);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MESH grib download failed for {Date}", dateUtc);
                    debug?.Note("download failed: " + ex.Message);
                    return EmptyFeatureCollection;
                }

                if (gribPath == null)
                {
                    debug?.Note("no grib resolved for this date — treating as no-data");
                    return EmptyFeatureCollection;
                }

                string geojson;
                try
                {
                    geojson = await ContourAsync(gribPath, minLat, maxLat, minLng, maxLng, dateUtc, debug);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MESH gdal_contour pipeline failed for {Date}", dateUtc);
                    debug?.Note("contour pipeline failed: " + ex.Message);
                    return EmptyFeatureCollection;
                }

                try { await File.WriteAllTextAsync(geoJsonCachePath, geojson); }
                catch (Exception ex) { debug?.Note("cache write failed (non-fatal): " + ex.Message); }

                return geojson;
            }
            finally
            {
                PipelineGate.Release();
            }
        }

        // ── Locate + download the daily-max MESH grib for a UTC date ──────────

        private async Task<string?> EnsureGribDownloadedAsync(DateTime dateUtc, MeshDebugSink? debug)
        {
            var rawCachePath = Path.Combine(_cacheDir, $"raw_{dateUtc:yyyyMMdd}.grib2");
            if (!(debug?.ForceRefresh ?? false) && File.Exists(rawCachePath))
            {
                debug?.Note($"grib cache hit: {rawCachePath}");
                return rawCachePath;
            }

            var client = _httpFactory.CreateClient("mesh");

            string? gzUrl = dateUtc >= AwsArchiveStart
                ? await ResolveAwsKeyAsync(client, dateUtc, debug)
                : ResolveIemUrl(dateUtc, debug);

            if (gzUrl == null) return null;

            debug?.Note($"downloading {gzUrl}");
            var gzBytes = await client.GetByteArrayAsync(gzUrl);
            debug?.Note($"downloaded {gzBytes.Length} bytes");

            var gzPath = rawCachePath + ".gz";
            await File.WriteAllBytesAsync(gzPath, gzBytes);

            using (var inStream  = File.OpenRead(gzPath))
            using (var gzip      = new System.IO.Compression.GZipStream(inStream, System.IO.Compression.CompressionMode.Decompress))
            using (var outStream = File.Create(rawCachePath))
            {
                await gzip.CopyToAsync(outStream);
            }
            try { File.Delete(gzPath); } catch { /* best effort */ }

            return rawCachePath;
        }

        /// <summary>
        /// NOAA AWS OpenData: MESH object filenames carry an ad-hoc timestamp
        /// (not a fixed HHMMSS), so this lists the date's S3 "folder" via the
        /// public ListObjectsV2 REST API and picks the file, rather than
        /// guessing the exact key. This exact timestamping problem is documented
        /// by IEM's own MRMS curation work: github.com/akrherz/iem issue #253.
        /// </summary>
        private async Task<string?> ResolveAwsKeyAsync(HttpClient client, DateTime dateUtc, MeshDebugSink? debug)
        {
            var prefix  = $"CONUS/MESH_Max_1440min_00.50/{dateUtc:yyyyMMdd}/";
            var listUrl = $"{_awsBaseUrl}/?list-type=2&prefix={Uri.EscapeDataString(prefix)}";

            string xml;
            try
            {
                xml = await client.GetStringAsync(listUrl);
            }
            catch (Exception ex)
            {
                debug?.Note("AWS ListObjectsV2 failed: " + ex.Message);
                return null;
            }

            var keys = System.Text.RegularExpressions.Regex.Matches(xml, "<Key>([^<]+)</Key>")
                .Select(m => m.Groups[1].Value)
                .Where(k => k.EndsWith(".grib2.gz", StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.Ordinal) // filenames are UTC-timestamped -> lexical order == chronological
                .ToList();

            debug?.Note($"AWS prefix '{prefix}' -> {keys.Count} candidate object(s)");
            if (keys.Count == 0) return null;

            // Daily-max product is produced ~once/day; the latest-timestamped
            // object for the date is the authoritative one.
            return $"{_awsBaseUrl}/{keys[^1]}";
        }

        /// <summary>
        /// IEM MTArchive fallback for dates before the AWS archive (back to
        /// Oct 2014). UNVERIFIED — MTArchive is an HTML directory index, not an
        /// S3 API, so unlike ResolveAwsKeyAsync this can't list-and-pick; it
        /// guesses a filename pattern that needs confirming against a real
        /// directory listing at
        /// https://mtarchive.geol.iastate.edu/{yyyy}/{MM}/{dd}/mrms/ncep/MESH_Max_1440min/
        /// before this path is trusted. Flag pre-Nov-2020 report/map requests
        /// for manual QA until that's done.
        /// </summary>
        private string ResolveIemUrl(DateTime dateUtc, MeshDebugSink? debug)
        {
            var url = $"{_iemBaseUrl}/{dateUtc:yyyy}/{dateUtc:MM}/{dateUtc:dd}/mrms/ncep/MESH_Max_1440min/" +
                      $"MRMS_MESH_Max_1440min_00.50_{dateUtc:yyyyMMdd}-120000.grib2.gz";
            debug?.Note("IEM path is a guessed pattern, not list-verified: " + url);
            return url;
        }

        // ── Clip to bbox + contour into size-banded polygons ───────────────────

        private async Task<string> ContourAsync(
            string gribPath, double minLat, double maxLat, double minLng, double maxLng,
            DateTime dateUtc, MeshDebugSink? debug)
        {
            var tmpTif      = Path.Combine(Path.GetTempPath(), $"mesh_{Guid.NewGuid():N}.tif");
            var tmpGeoJson  = Path.Combine(Path.GetTempPath(), $"mesh_{Guid.NewGuid():N}.geojson");
            var ci          = CultureInfo.InvariantCulture;

            try
            {
                // 1. Clip to bbox with a small margin so contour rings aren't
                //    truncated right at the viewport edge.
                const double margin = 0.1;
                var translateArgs =
                    $"-projwin {(minLng - margin).ToString(ci)} {(maxLat + margin).ToString(ci)} " +
                    $"{(maxLng + margin).ToString(ci)} {(minLat - margin).ToString(ci)} " +
                    $"-of GTiff \"{gribPath}\" \"{tmpTif}\"";

                var (transOk, transOut) = await RunProcessAsync(_gdalTranslatePath, translateArgs);
                debug?.Note($"gdal_translate exit_ok={transOk}: {Truncate(transOut, 500)}");
                if (!transOk || !File.Exists(tmpTif))
                    return EmptyFeatureCollection;

                // 2. Contour in polygon mode (-p) at each size band. MESH grib
                //    units are millimeters, so convert the inch bands -> mm for
                //    the -fl level list; -amin names the output field carrying
                //    each band's lower bound.
                var levelsMm = string.Join(" ", SizeBandsInches.Select(b => (b * 25.4).ToString("F1", ci)));
                var contourArgs = $"-p -amin sizeBandMm -f GeoJSON -fl {levelsMm} \"{tmpTif}\" \"{tmpGeoJson}\"";

                var (contOk, contOut) = await RunProcessAsync(_gdalContourPath, contourArgs);
                debug?.Note($"gdal_contour exit_ok={contOk}: {Truncate(contOut, 500)}");
                if (!contOk || !File.Exists(tmpGeoJson))
                    return EmptyFeatureCollection;

                var raw = await File.ReadAllTextAsync(tmpGeoJson);
                return ReshapeToAppSchema(raw, dateUtc);
            }
            finally
            {
                try { if (File.Exists(tmpTif))     File.Delete(tmpTif); }     catch { /* best effort */ }
                try { if (File.Exists(tmpGeoJson)) File.Delete(tmpGeoJson); } catch { /* best effort */ }
            }
        }

        /// <summary>
        /// Maps gdal_contour's <c>sizeBandMm</c> field back to the
        /// { sizeBand (inches), date, source } property shape used by the
        /// Phase 1 LSR swath (RealDataService.GetMrmsHailSwathGeoJsonAsync),
        /// so storm-explorer.js can style both sources with the same
        /// swathColor()/legend.
        /// </summary>
        private static string ReshapeToAppSchema(string gdalGeoJson, DateTime dateUtc)
        {
            using var doc    = JsonDocument.Parse(gdalGeoJson);
            using var ms     = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);

            writer.WriteStartObject();
            writer.WriteString("type", "FeatureCollection");
            writer.WriteStartArray("features");

            // gdal_contour -p emits one extra ring below the lowest requested
            // -fl level (everything from the raster's actual minimum up to
            // that level) — not a real hail-size band. At MESH=0 (no hail)
            // that ring typically covers most of the requested bbox, so left
            // unfiltered it painted a large flat wash over the whole map and
            // buried the real bands. Anything below our smallest configured
            // band (0.75") gets dropped here.
            double minBandMm = SizeBandsInches[0] * 25.4;

            if (doc.RootElement.TryGetProperty("features", out var features))
            {
                foreach (var f in features.EnumerateArray())
                {
                    double sizeBandMm = 0;
                    if (f.TryGetProperty("properties", out var props) &&
                        props.TryGetProperty("sizeBandMm", out var mm) &&
                        mm.ValueKind == JsonValueKind.Number)
                    {
                        sizeBandMm = mm.GetDouble();
                    }

                    if (sizeBandMm < minBandMm - 0.01) // small epsilon for float rounding
                        continue;

                    writer.WriteStartObject();
                    writer.WriteString("type", "Feature");

                    if (f.TryGetProperty("geometry", out var geom))
                    {
                        writer.WritePropertyName("geometry");
                        geom.WriteTo(writer);
                    }

                    writer.WriteStartObject("properties");
                    writer.WriteNumber("sizeBand", Math.Round(sizeBandMm / 25.4, 2));
                    writer.WriteString("date", dateUtc.ToString("yyyy-MM-dd"));
                    writer.WriteString("source", "mesh");
                    writer.WriteEndObject(); // properties

                    writer.WriteEndObject(); // feature
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private async Task<(bool ok, string output)> RunProcessAsync(string exe, string args)
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = exe,
                    Arguments              = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                }
            };

            var sb = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(_processTimeout);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { /* best effort */ }
                return (false, sb.ToString() + $"\n[TIMEOUT after {_processTimeout.TotalSeconds}s]");
            }

            return (proc.ExitCode == 0, sb.ToString());
        }

        private static string Truncate(string s, int len) => s.Length <= len ? s : s[..len] + "…";
    }

    /// <summary>
    /// Optional diagnostic sink threaded through the MESH pipeline so
    /// /RoofHealth/MeshDebug can surface each step (resolved URL, download
    /// size, gdal exit codes/output) instead of just the final GeoJSON.
    /// Mirrors the existing HailDebug/LsrDebug/RegridDebug dev-diagnostic
    /// pattern already used elsewhere in RoofHealthController.
    /// </summary>
    public class MeshDebugSink
    {
        public bool         ForceRefresh { get; set; }
        public List<string> Notes { get; } = new();
        public void Note(string s) => Notes.Add(s);
    }
}
