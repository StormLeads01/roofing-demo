using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using RoofingLeadGeneration.Services;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoofingLeadGeneration.Controllers
{
    [Route("[controller]")]
    public class LeadsController : Controller
    {
        private static readonly string DbPath =
            Path.Combine(AppContext.BaseDirectory, "data", "leads.db");

        private static readonly string ConnStr =
            new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString();

        private static bool   _initialized = false;
        private static object _initLock    = new();

        private static void EnsureDb()
        {
            if (_initialized) return;
            lock (_initLock)
            {
                if (_initialized) return;

                Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);

                using var conn = new SqliteConnection(ConnStr);
                conn.Open();

                // Create users table first (leads.user_id references it)
                using var usersCmd = conn.CreateCommand();
                usersCmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS users (
                        id           INTEGER PRIMARY KEY AUTOINCREMENT,
                        provider     TEXT NOT NULL,
                        provider_id  TEXT NOT NULL,
                        email        TEXT,
                        display_name TEXT,
                        created_at   TEXT NOT NULL DEFAULT (datetime('now')),
                        UNIQUE(provider, provider_id)
                    );";
                usersCmd.ExecuteNonQuery();

                // Create leads table
                using var createCmd = conn.CreateCommand();
                createCmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS leads (
                        id               INTEGER PRIMARY KEY AUTOINCREMENT,
                        address          TEXT    NOT NULL UNIQUE,
                        lat              REAL,
                        lng              REAL,
                        risk_level       TEXT,
                        last_storm_date  TEXT,
                        hail_size        TEXT,
                        estimated_damage TEXT,
                        roof_age         INTEGER,
                        property_type    TEXT,
                        source_address   TEXT,
                        saved_at         TEXT    NOT NULL DEFAULT (datetime('now')),
                        notes            TEXT
                    );
                    CREATE INDEX IF NOT EXISTS idx_leads_risk ON leads(risk_level);
                ";
                createCmd.ExecuteNonQuery();

                // Migrate: add columns that may not exist in older DBs
                var migrations = new[]
                {
                    ("owner_name",  "ALTER TABLE leads ADD COLUMN owner_name  TEXT"),
                    ("owner_phone", "ALTER TABLE leads ADD COLUMN owner_phone TEXT"),
                    ("owner_email", "ALTER TABLE leads ADD COLUMN owner_email TEXT"),
                    ("user_id",     "ALTER TABLE leads ADD COLUMN user_id     INTEGER"),  // no FK — SQLite doesn't enforce it anyway
                };

                foreach (var (col, sql) in migrations)
                {
                    using var checkCmd = conn.CreateCommand();
                    checkCmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('leads') WHERE name='{col}'";
                    long exists = (long)(checkCmd.ExecuteScalar() ?? 0L);
                    if (exists == 0)
                    {
                        using var alterCmd = conn.CreateCommand();
                        alterCmd.CommandText = sql;
                        alterCmd.ExecuteNonQuery();
                    }
                }

                _initialized = true;
            }
        }

        // ── GET /Leads/Saved → HTML page ─────────────────────────────
        [HttpGet("Saved")]
        public IActionResult Saved() => View();

        // Helper: get the logged-in user's DB id (null if not authenticated)
        private long? CurrentUserId =>
            long.TryParse(User.FindFirst("user_db_id")?.Value, out var id) ? id : null;

        // ── POST /Leads/Save ─────────────────────────────────────────
        [HttpPost("Save")]
        public IActionResult Save([FromBody] SaveRequest req)
        {
            if (req?.Properties == null || req.Properties.Length == 0)
                return BadRequest(new { error = "No properties provided." });

            EnsureDb();

            long? userId = CurrentUserId;
            int saved = 0, updated = 0;

            using var conn = new SqliteConnection(ConnStr);
            conn.Open();
            using var tx = conn.BeginTransaction();

            const string upsertSql = @"
                INSERT INTO leads
                    (address, lat, lng, risk_level, last_storm_date, hail_size,
                     estimated_damage, roof_age, property_type, source_address, saved_at, user_id)
                VALUES
                    ($address, $lat, $lng, $risk_level, $last_storm_date, $hail_size,
                     $estimated_damage, $roof_age, $property_type, $source_address, datetime('now'), $user_id)
                ON CONFLICT(address) DO UPDATE SET
                    lat              = excluded.lat,
                    lng              = excluded.lng,
                    risk_level       = excluded.risk_level,
                    last_storm_date  = excluded.last_storm_date,
                    hail_size        = excluded.hail_size,
                    estimated_damage = excluded.estimated_damage,
                    roof_age         = excluded.roof_age,
                    property_type    = excluded.property_type,
                    source_address   = excluded.source_address,
                    saved_at         = datetime('now'),
                    user_id          = excluded.user_id;
            ";

            using var checkCmd  = conn.CreateCommand();
            using var upsertCmd = conn.CreateCommand();
            checkCmd.Transaction  = tx;
            upsertCmd.Transaction = tx;
            checkCmd.CommandText  = "SELECT COUNT(1) FROM leads WHERE address = $a";
            upsertCmd.CommandText = upsertSql;

            var pA = checkCmd.Parameters.Add("$a", SqliteType.Text);

            var pAddr   = upsertCmd.Parameters.Add("$address",          SqliteType.Text);
            var pLat    = upsertCmd.Parameters.Add("$lat",              SqliteType.Real);
            var pLng    = upsertCmd.Parameters.Add("$lng",              SqliteType.Real);
            var pRisk   = upsertCmd.Parameters.Add("$risk_level",       SqliteType.Text);
            var pDate   = upsertCmd.Parameters.Add("$last_storm_date",  SqliteType.Text);
            var pHail   = upsertCmd.Parameters.Add("$hail_size",        SqliteType.Text);
            var pDmg    = upsertCmd.Parameters.Add("$estimated_damage", SqliteType.Text);
            var pAge    = upsertCmd.Parameters.Add("$roof_age",         SqliteType.Integer);
            var pType   = upsertCmd.Parameters.Add("$property_type",    SqliteType.Text);
            var pSrc    = upsertCmd.Parameters.Add("$source_address",   SqliteType.Text);
            var pUserId = upsertCmd.Parameters.Add("$user_id",          SqliteType.Integer);

            foreach (var p in req.Properties)
            {
                pA.Value = p.Address ?? "";
                long exists = (long)(checkCmd.ExecuteScalar() ?? 0L);

                pAddr.Value   = p.Address          ?? "";
                pLat.Value    = p.Lat;
                pLng.Value    = p.Lng;
                pRisk.Value   = p.RiskLevel        ?? "";
                pDate.Value   = p.LastStormDate    ?? "";
                pHail.Value   = p.HailSize         ?? "";
                pDmg.Value    = p.EstimatedDamage  ?? "";
                pAge.Value    = p.RoofAge;
                pType.Value   = p.PropertyType     ?? "";
                pSrc.Value    = req.SourceAddress  ?? "";
                pUserId.Value = (object?)userId    ?? DBNull.Value;
                upsertCmd.ExecuteNonQuery();

                if (exists > 0) updated++; else saved++;
            }

            tx.Commit();
            return Json(new { saved, updated });
        }

        // ── GET /Leads → JSON list ───────────────────────────────────
        [HttpGet]
        public IActionResult Index()
        {
            EnsureDb();

            var leads = new List<LeadRow>();

            using var conn = new SqliteConnection(ConnStr);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, address, lat, lng, risk_level, last_storm_date,
                       hail_size, estimated_damage, roof_age, property_type,
                       source_address, saved_at, notes,
                       owner_name, owner_phone, owner_email
                FROM leads
                WHERE user_id = $uid OR user_id IS NULL
                ORDER BY
                    CASE risk_level WHEN 'High' THEN 0 WHEN 'Medium' THEN 1 ELSE 2 END,
                    saved_at DESC;
            ";
            // Scope to logged-in user (or show all unclaimed legacy leads)
            cmd.Parameters.AddWithValue("$uid", (object?)CurrentUserId ?? DBNull.Value);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                leads.Add(new LeadRow
                {
                    Id              = reader.GetInt64(0),
                    Address         = reader.GetString(1),
                    Lat             = reader.IsDBNull(2)  ? 0 : reader.GetDouble(2),
                    Lng             = reader.IsDBNull(3)  ? 0 : reader.GetDouble(3),
                    RiskLevel       = reader.IsDBNull(4)  ? "" : reader.GetString(4),
                    LastStormDate   = reader.IsDBNull(5)  ? "" : reader.GetString(5),
                    HailSize        = reader.IsDBNull(6)  ? "" : reader.GetString(6),
                    EstimatedDamage = reader.IsDBNull(7)  ? "" : reader.GetString(7),
                    RoofAge         = reader.IsDBNull(8)  ? 0  : reader.GetInt32(8),
                    PropertyType    = reader.IsDBNull(9)  ? "" : reader.GetString(9),
                    SourceAddress   = reader.IsDBNull(10) ? "" : reader.GetString(10),
                    SavedAt         = reader.IsDBNull(11) ? "" : reader.GetString(11),
                    Notes           = reader.IsDBNull(12) ? null : reader.GetString(12),
                    OwnerName       = reader.IsDBNull(13) ? null : reader.GetString(13),
                    OwnerPhone      = reader.IsDBNull(14) ? null : reader.GetString(14),
                    OwnerEmail      = reader.IsDBNull(15) ? null : reader.GetString(15)
                });
            }

            return Json(leads, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });
        }

        // ── PATCH /Leads/{id}/Owner ──────────────────────────────────
        [HttpPatch("{id:long}/Owner")]
        public IActionResult UpdateOwner(long id, [FromBody] OwnerDto dto)
        {
            EnsureDb();

            using var conn = new SqliteConnection(ConnStr);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE leads
                SET owner_name  = $name,
                    owner_phone = $phone,
                    owner_email = $email
                WHERE id = $id
            ";
            cmd.Parameters.AddWithValue("$name",  (object?)dto.OwnerName  ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$phone", (object?)dto.OwnerPhone ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$email", (object?)dto.OwnerEmail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);

            int rows = cmd.ExecuteNonQuery();
            return rows > 0 ? NoContent() : NotFound();
        }

        // ── POST /Leads/EnrichAll  →  fill owner names via Regrid ───
        // Processes up to 25 leads per call (Regrid free-tier daily limit).
        // Requires "Regrid:Token" in appsettings.json.
        [HttpPost("EnrichAll")]
        public async Task<IActionResult> EnrichAll()
        {
            EnsureDb();

            var svc = HttpContext.RequestServices.GetService<RealDataService>();
            if (svc is null)
                return StatusCode(500, new { error = "RealDataService not registered." });

            var token = HttpContext.RequestServices
                .GetService<IConfiguration>()?["Regrid:Token"];

            if (string.IsNullOrWhiteSpace(token))
                return BadRequest(new { error = "Regrid token not configured. Add \"Regrid\": { \"Token\": \"...\" } to appsettings.json." });

            // Get leads that are missing an owner name and have valid coordinates
            var todo = new List<(long id, double lat, double lng)>();
            using var conn = new SqliteConnection(ConnStr);
            conn.Open();

            using var selCmd = conn.CreateCommand();
            selCmd.CommandText = @"
                SELECT id, lat, lng FROM leads
                WHERE (owner_name IS NULL OR owner_name = '')
                  AND lat  IS NOT NULL AND lat  != 0
                  AND lng  IS NOT NULL AND lng  != 0
                LIMIT 25";   // cap at Regrid free-tier daily limit

            using var rdr = selCmd.ExecuteReader();
            while (rdr.Read())
                todo.Add((rdr.GetInt64(0), rdr.GetDouble(1), rdr.GetDouble(2)));

            int enriched = 0;

            foreach (var (id, lat, lng) in todo)
            {
                var name = await svc.GetRegridOwnerAsync(lat, lng);
                if (string.IsNullOrWhiteSpace(name)) continue;

                using var updCmd = conn.CreateCommand();
                updCmd.CommandText = "UPDATE leads SET owner_name = $n WHERE id = $id";
                updCmd.Parameters.AddWithValue("$n",   name);
                updCmd.Parameters.AddWithValue("$id",  id);
                updCmd.ExecuteNonQuery();
                enriched++;

                // Small delay to be polite to the Regrid API
                await Task.Delay(200);
            }

            return Json(new { enriched, queued = todo.Count });
        }

        // ── DELETE /Leads/{id} ───────────────────────────────────────
        [HttpDelete("{id:long}")]
        public IActionResult Delete(long id)
        {
            EnsureDb();

            using var conn = new SqliteConnection(ConnStr);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM leads WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            int rows = cmd.ExecuteNonQuery();

            return rows > 0 ? NoContent() : NotFound();
        }

        // ── DTOs ─────────────────────────────────────────────────────

        public class SaveRequest
        {
            [JsonPropertyName("sourceAddress")]
            public string? SourceAddress { get; set; }

            [JsonPropertyName("properties")]
            public PropertyDto[]? Properties { get; set; }
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

        public class LeadRow
        {
            public long    Id              { get; set; }
            public string  Address         { get; set; } = "";
            public double  Lat             { get; set; }
            public double  Lng             { get; set; }
            public string  RiskLevel       { get; set; } = "";
            public string  LastStormDate   { get; set; } = "";
            public string  HailSize        { get; set; } = "";
            public string  EstimatedDamage { get; set; } = "";
            public int     RoofAge         { get; set; }
            public string  PropertyType    { get; set; } = "";
            public string  SourceAddress   { get; set; } = "";
            public string  SavedAt         { get; set; } = "";
            public string? Notes           { get; set; }
            public string? OwnerName       { get; set; }
            public string? OwnerPhone      { get; set; }
            public string? OwnerEmail      { get; set; }
        }
    }
}
