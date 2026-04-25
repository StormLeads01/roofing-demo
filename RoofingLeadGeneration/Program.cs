using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Infrastructure;
using RoofingLeadGeneration.Data;
using RoofingLeadGeneration.Services;

QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);
var config  = builder.Configuration;

// ── MVC ───────────────────────────────────────────────────────────────────
builder.Services.AddControllersWithViews();

// ── Database (EF Core + SQLite) ───────────────────────────────────────────
var dbPath = Path.Combine(AppContext.BaseDirectory, "data", "leads.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// ── Authentication ────────────────────────────────────────────────────────
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, opt =>
    {
        opt.LoginPath         = "/Auth/Login";
        opt.LogoutPath        = "/Auth/Logout";
        opt.AccessDeniedPath  = "/Auth/Login";
        opt.ExpireTimeSpan    = TimeSpan.FromDays(30);
        opt.SlidingExpiration = true;
        opt.Cookie.Name       = ".StormLead.Session";
        opt.Cookie.HttpOnly   = true;
        opt.Cookie.SameSite   = SameSiteMode.Lax;
    })
    .AddCookie("External", opt =>
    {
        opt.Cookie.Name    = ".StormLead.External";
        opt.ExpireTimeSpan = TimeSpan.FromMinutes(10);
    });

if (!string.IsNullOrWhiteSpace(config["Auth:Google:ClientId"]))
{
    builder.Services.AddAuthentication()
        .AddGoogle("Google", opt =>
        {
            opt.SignInScheme = "External";
            opt.ClientId     = config["Auth:Google:ClientId"]!;
            opt.ClientSecret = config["Auth:Google:ClientSecret"]!;
        });
}

if (!string.IsNullOrWhiteSpace(config["Auth:Microsoft:ClientId"]))
{
    builder.Services.AddAuthentication()
        .AddMicrosoftAccount("Microsoft", opt =>
        {
            opt.SignInScheme = "External";
            opt.ClientId     = config["Auth:Microsoft:ClientId"]!;
            opt.ClientSecret = config["Auth:Microsoft:ClientSecret"]!;
        });
}

// ── HttpClient factory (RealDataService) ─────────────────────────────────
builder.Services.AddHttpClient("overpass", c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
    c.DefaultRequestHeaders.Add("User-Agent", "StormLeadPro/1.0");
});
builder.Services.AddHttpClient("noaa", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.Add("User-Agent", "StormLeadPro/1.0");
});
builder.Services.AddHttpClient("regrid", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient("mesonet", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.Add("User-Agent", "StormLeadPro/1.0");
});
builder.Services.AddHttpClient("bst", c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.DefaultRequestHeaders.Add("User-Agent", "StormLeadPro/1.0");
});
builder.Services.AddHttpClient("whitepages", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.Add("User-Agent", "StormLeadPro/1.0");
});
builder.Services.AddHttpClient("tomorrow", c =>
{
    c.BaseAddress = new Uri("https://api.tomorrow.io/");
    c.Timeout     = TimeSpan.FromSeconds(20);
    c.DefaultRequestHeaders.Add("User-Agent", "StormLeadPro/1.0");
});

// ── Services ──────────────────────────────────────────────────────────────
builder.Services.AddSingleton<RealDataService>();
builder.Services.AddSingleton<EmailService>();
builder.Services.AddSingleton<HailReportService>();
builder.Services.AddHostedService<StormAlertService>();

// ── Pipeline ──────────────────────────────────────────────────────────────
var app = builder.Build();

// Initialise / migrate the database at startup
using (var scope = app.Services.CreateScope())
{
    var db  = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var conn = db.Database.GetDbConnection();
    conn.Open();

    db.Database.EnsureCreated();

    // ── Helper: add a column only if it doesn't already exist ────────────
    void AddColumnIfMissing(string table, string column, string definition)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                return; // column already present
        reader.Close();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        cmd.ExecuteNonQuery();
    }

    // ── Helper: create a table only if it doesn't already exist ──────────
    bool TableExists(string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@t";
        var p = cmd.CreateParameter(); p.ParameterName = "@t"; p.Value = table;
        cmd.Parameters.Add(p);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    // ── Schema patches ────────────────────────────────────────────────────
    AddColumnIfMissing("users", "is_admin",            "INTEGER NOT NULL DEFAULT 0");
    AddColumnIfMissing("users", "org_id",              "INTEGER REFERENCES orgs(id) ON DELETE SET NULL");
    AddColumnIfMissing("users", "org_role",            "TEXT NOT NULL DEFAULT 'owner'");
    AddColumnIfMissing("leads", "year_built",          "INTEGER");
    AddColumnIfMissing("leads", "owner_name",          "TEXT");
    AddColumnIfMissing("leads", "owner_phone",         "TEXT");
    AddColumnIfMissing("leads", "owner_email",         "TEXT");
    AddColumnIfMissing("leads", "property_type",       "TEXT");
    AddColumnIfMissing("leads", "source_address",      "TEXT");
    AddColumnIfMissing("leads", "notes",               "TEXT");
    AddColumnIfMissing("leads", "is_enriched",         "INTEGER NOT NULL DEFAULT 0");
    AddColumnIfMissing("leads", "deleted_at",          "TEXT");
    AddColumnIfMissing("leads", "status",              "TEXT NOT NULL DEFAULT 'new'");
    AddColumnIfMissing("leads", "org_id",              "INTEGER REFERENCES orgs(id) ON DELETE SET NULL");
    AddColumnIfMissing("leads", "assigned_to_user_id", "INTEGER REFERENCES users(id) ON DELETE SET NULL");

    // ── Migrate leads.address unique index → (org_id, address) ──────────
    // The old global unique index prevents two orgs from saving the same address.
    // We drop it and replace with a composite (org_id, address) unique index.
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_leads_Address'";
        var hasOldIndex = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        if (hasOldIndex)
        {
            cmd.CommandText = "DROP INDEX IF EXISTS IX_leads_Address";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_leads_org_address ON leads(org_id, address)";
            cmd.ExecuteNonQuery();
        }
        // Also ensure the new composite index exists even if old one was already gone
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_leads_org_address'";
        var hasNewIndex = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        if (!hasNewIndex)
        {
            cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_leads_org_address ON leads(org_id, address)";
            cmd.ExecuteNonQuery();
        }
    }
    AddColumnIfMissing("watched_areas", "org_id",      "INTEGER REFERENCES orgs(id) ON DELETE SET NULL");
    AddColumnIfMissing("sent_alerts",   "org_id",      "INTEGER REFERENCES orgs(id) ON DELETE SET NULL");

    if (!TableExists("orgs"))
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE orgs (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                name       TEXT NOT NULL,
                owner_id   INTEGER REFERENCES users(id) ON DELETE SET NULL,
                plan       TEXT NOT NULL DEFAULT 'free',
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX ix_orgs_owner_id ON orgs(owner_id)";
        cmd.ExecuteNonQuery();
    }

    if (!TableExists("org_invites"))
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE org_invites (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                org_id      INTEGER NOT NULL REFERENCES orgs(id) ON DELETE CASCADE,
                email       TEXT NOT NULL,
                token       TEXT NOT NULL UNIQUE,
                role        TEXT NOT NULL DEFAULT 'rep',
                expires_at  TEXT NOT NULL,
                accepted_at TEXT,
                created_at  TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE UNIQUE INDEX ix_org_invites_token  ON org_invites(token)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX        ix_org_invites_org_id ON org_invites(org_id)";
        cmd.ExecuteNonQuery();
    }

    if (!TableExists("enrichments"))
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE enrichments (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id       INTEGER REFERENCES users(id) ON DELETE SET NULL,
                lead_id       INTEGER REFERENCES leads(id) ON DELETE SET NULL,
                address       TEXT,
                status        TEXT NOT NULL DEFAULT 'pending',
                provider      TEXT NOT NULL DEFAULT 'batchskiptracing',
                credits_used  INTEGER NOT NULL DEFAULT 1,
                created_at    TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = "CREATE INDEX ix_enrichments_user_id    ON enrichments(user_id)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX ix_enrichments_created_at ON enrichments(created_at)";
        cmd.ExecuteNonQuery();
    }

    if (!TableExists("lead_contacts"))
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE lead_contacts (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                lead_id      INTEGER NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
                name         TEXT,
                phone        TEXT,
                email        TEXT,
                contact_type TEXT NOT NULL DEFAULT 'owner',
                is_primary   INTEGER NOT NULL DEFAULT 0,
                source       TEXT NOT NULL DEFAULT 'whitepages',
                created_at   TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX ix_lead_contacts_lead_id ON lead_contacts(lead_id)";
        cmd.ExecuteNonQuery();
    }

    if (!TableExists("watched_areas"))
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE watched_areas (
                id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id               INTEGER REFERENCES users(id) ON DELETE CASCADE,
                label                 TEXT NOT NULL,
                center_lat            REAL NOT NULL,
                center_lng            REAL NOT NULL,
                radius_miles          REAL NOT NULL DEFAULT 10.0,
                min_hail_size_inches  REAL NOT NULL DEFAULT 1.0,
                alerts_enabled        INTEGER NOT NULL DEFAULT 1,
                created_at            TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX ix_watched_areas_user_id ON watched_areas(user_id)";
        cmd.ExecuteNonQuery();
    }

    if (!TableExists("sent_alerts"))
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE sent_alerts (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id           INTEGER REFERENCES users(id) ON DELETE CASCADE,
                watched_area_id   INTEGER NOT NULL REFERENCES watched_areas(id) ON DELETE CASCADE,
                event_date        TEXT NOT NULL,
                hail_size_inches  REAL NOT NULL,
                sent_at           TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE UNIQUE INDEX ix_sent_alerts_area_date ON sent_alerts(watched_area_id, event_date)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX ix_sent_alerts_user_id ON sent_alerts(user_id)";
        cmd.ExecuteNonQuery();
    }

    if (!TableExists("org_credits"))
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE org_credits (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                org_id           INTEGER NOT NULL REFERENCES orgs(id) ON DELETE CASCADE,
                credit_type      TEXT NOT NULL,
                balance          INTEGER NOT NULL DEFAULT 0,
                used_this_period INTEGER NOT NULL DEFAULT 0,
                period_start     TEXT NOT NULL DEFAULT (datetime('now')),
                period_end       TEXT,
                updated_at       TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE UNIQUE INDEX ix_org_credits_org_type ON org_credits(org_id, credit_type)";
        cmd.ExecuteNonQuery();
    }

    if (!TableExists("org_credit_transactions"))
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE org_credit_transactions (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                org_id         INTEGER NOT NULL REFERENCES orgs(id) ON DELETE CASCADE,
                user_id        INTEGER REFERENCES users(id) ON DELETE SET NULL,
                credit_type    TEXT NOT NULL,
                amount         INTEGER NOT NULL,
                balance_after  INTEGER NOT NULL,
                description    TEXT NOT NULL DEFAULT '',
                reference_id   TEXT,
                reference_type TEXT,
                created_at     TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX ix_org_credit_tx_org_id     ON org_credit_transactions(org_id)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX ix_org_credit_tx_user_id    ON org_credit_transactions(user_id)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX ix_org_credit_tx_created_at ON org_credit_transactions(created_at)";
        cmd.ExecuteNonQuery();
    }

    // ── Seed demo user + leads ────────────────────────────────────────
    var demoEmail = config["Auth:DemoEmail"] ?? "james@repwing.com";
    long demoUserId = 0;
    {
        using var cmd = conn.CreateCommand();
        // Find or create demo user
        cmd.CommandText = "SELECT id FROM users WHERE provider='demo' AND provider_id='demo-user-1'";
        var existing = cmd.ExecuteScalar();
        if (existing == null)
        {
            cmd.CommandText = @"INSERT INTO users (provider, provider_id, email, display_name, is_admin, created_at)
                                VALUES ('demo','demo-user-1',@e,'Demo User',0,datetime('now'))";
            var p = cmd.CreateParameter(); p.ParameterName = "@e"; p.Value = demoEmail;
            cmd.Parameters.Add(p);
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT last_insert_rowid()";
            cmd.Parameters.Clear();
            demoUserId = Convert.ToInt64(cmd.ExecuteScalar());
        }
        else
        {
            demoUserId = Convert.ToInt64(existing);
        }

        // ── Ensure demo user has an org ───────────────────────────────────
        cmd.Parameters.Clear();
        cmd.CommandText = "SELECT org_id FROM users WHERE id=@uid";
        var pOrgCheck = cmd.CreateParameter(); pOrgCheck.ParameterName = "@uid"; pOrgCheck.Value = demoUserId;
        cmd.Parameters.Add(pOrgCheck);
        var existingOrgId = cmd.ExecuteScalar();

        long demoOrgId = 0;
        if (existingOrgId == null || existingOrgId == DBNull.Value)
        {
            // Create demo org
            cmd.Parameters.Clear();
            cmd.CommandText = @"INSERT INTO orgs (name, owner_id, plan, created_at)
                                VALUES ('Demo Company', @uid, 'agency', datetime('now'))";
            var pOwner = cmd.CreateParameter(); pOwner.ParameterName = "@uid"; pOwner.Value = demoUserId;
            cmd.Parameters.Add(pOwner);
            cmd.ExecuteNonQuery();

            cmd.Parameters.Clear();
            cmd.CommandText = "SELECT last_insert_rowid()";
            demoOrgId = Convert.ToInt64(cmd.ExecuteScalar());

            // Assign demo user to org as owner
            cmd.CommandText = "UPDATE users SET org_id=@oid, org_role='owner' WHERE id=@uid";
            var pOid = cmd.CreateParameter(); pOid.ParameterName = "@oid"; pOid.Value = demoOrgId;
            var pUid2 = cmd.CreateParameter(); pUid2.ParameterName = "@uid"; pUid2.Value = demoUserId;
            cmd.Parameters.Add(pOid);
            cmd.Parameters.Add(pUid2);
            cmd.ExecuteNonQuery();

            // Migrate existing leads / watched_areas / sent_alerts to the demo org
            cmd.Parameters.Clear();
            cmd.CommandText = "UPDATE leads SET org_id=@oid WHERE user_id=@uid AND org_id IS NULL";
            pOid = cmd.CreateParameter(); pOid.ParameterName = "@oid"; pOid.Value = demoOrgId;
            pUid2 = cmd.CreateParameter(); pUid2.ParameterName = "@uid"; pUid2.Value = demoUserId;
            cmd.Parameters.Add(pOid); cmd.Parameters.Add(pUid2);
            cmd.ExecuteNonQuery();

            cmd.Parameters.Clear();
            cmd.CommandText = "UPDATE watched_areas SET org_id=@oid WHERE user_id=@uid AND org_id IS NULL";
            pOid = cmd.CreateParameter(); pOid.ParameterName = "@oid"; pOid.Value = demoOrgId;
            pUid2 = cmd.CreateParameter(); pUid2.ParameterName = "@uid"; pUid2.Value = demoUserId;
            cmd.Parameters.Add(pOid); cmd.Parameters.Add(pUid2);
            cmd.ExecuteNonQuery();

            cmd.Parameters.Clear();
            cmd.CommandText = "UPDATE sent_alerts SET org_id=@oid WHERE user_id=@uid AND org_id IS NULL";
            pOid = cmd.CreateParameter(); pOid.ParameterName = "@oid"; pOid.Value = demoOrgId;
            pUid2 = cmd.CreateParameter(); pUid2.ParameterName = "@uid"; pUid2.Value = demoUserId;
            cmd.Parameters.Add(pOid); cmd.Parameters.Add(pUid2);
            cmd.ExecuteNonQuery();
        }
        else
        {
            demoOrgId = Convert.ToInt64(existingOrgId);
        }

        // ── Ensure demo org has credit rows ──────────────────────────────
        cmd.Parameters.Clear();
        foreach (var creditType in new[] { "enrichment", "sms", "search" })
        {
            cmd.Parameters.Clear();
            cmd.CommandText = @"INSERT OR IGNORE INTO org_credits
                (org_id, credit_type, balance, used_this_period, period_start, updated_at)
                VALUES (@oid, @ct, @bal, 0, datetime('now'), datetime('now'))";
            var pOidC  = cmd.CreateParameter(); pOidC.ParameterName  = "@oid"; pOidC.Value = demoOrgId;
            var pCtC   = cmd.CreateParameter(); pCtC.ParameterName   = "@ct";  pCtC.Value  = creditType;
            // Demo org gets a generous starting balance
            var pBalC  = cmd.CreateParameter(); pBalC.ParameterName  = "@bal"; pBalC.Value =
                creditType == "enrichment" ? 500 :
                creditType == "sms"        ? 1000 : 250;
            cmd.Parameters.Add(pOidC); cmd.Parameters.Add(pCtC); cmd.Parameters.Add(pBalC);
            cmd.ExecuteNonQuery();
        }

        // Seed demo leads only if none exist yet for this user
        cmd.Parameters.Clear();
        cmd.CommandText = "SELECT COUNT(*) FROM leads WHERE user_id=@uid";
        var pUid = cmd.CreateParameter(); pUid.ParameterName = "@uid"; pUid.Value = demoUserId;
        cmd.Parameters.Add(pUid);
        var leadCount = Convert.ToInt32(cmd.ExecuteScalar());

        if (leadCount == 0)
        {
            var seedLeads = new[]
            {
                ("4521 Meadowbrook Dr, Fort Worth, TX 76103",  "High",   "2.00 inch",  "Michael Patterson", "(817) 555-0142", "mpatterson@email.com", 1885, "contacted"),
                ("935 Magnolia Ave, Fort Worth, TX 76104",     "High",   "2.50 inch",  "Robert Dunham",     "(817) 555-0198", "",                     1972, "new"),
                ("2817 Wayside Ave, Fort Worth, TX 76111",     "High",   "1.75 inch",  "Sarah Chen",        "(817) 555-0167", "s.chen@webmail.com",   2001, "quoted"),
                ("7304 Brentwood Stair Rd, Fort Worth, TX 76112","Medium","1.25 inch", "David Okafor",      "",               "",                     1998, "new"),
                ("6128 Malvey Ave, Fort Worth, TX 76116",      "Medium", "1.00 inch",  "",                  "",               "",                     2014, "new"),
                ("3429 Hemphill St, Fort Worth, TX 76110",     "Low",    "0.75 inch",  "Linda Nguyen",      "(817) 555-0123", "",                     2008, "new"),
            };

            foreach (var (addr, risk, hail, owner, phone, email2, yearBuilt, status) in seedLeads)
            {
                cmd.Parameters.Clear();
                cmd.CommandText = @"INSERT INTO leads
                    (user_id, org_id, address, risk_level, hail_size, owner_name, owner_phone, owner_email,
                     year_built, is_enriched, status, saved_at)
                    VALUES (@uid,@oid,@addr,@risk,@hail,@owner,@phone,@email,@yr,@enriched,@status,datetime('now',@offset))";
                var ps = new (string, object)[] {
                    ("@uid",      demoUserId),
                    ("@oid",      demoOrgId),
                    ("@addr",     addr),
                    ("@risk",     risk),
                    ("@hail",     hail),
                    ("@owner",    (object)(owner.Length > 0 ? owner : DBNull.Value)),
                    ("@phone",    (object)(phone.Length  > 0 ? phone : DBNull.Value)),
                    ("@email",    (object)(email2.Length > 0 ? email2: DBNull.Value)),
                    ("@yr",       (object)yearBuilt),
                    ("@enriched", (object)(owner.Length  > 0 ? 1 : 0)),
                    ("@status",   status),
                    ("@offset",   $"-{new Random().Next(1,30)} days"),
                };
                foreach (var (n, v) in ps)
                {
                    var pp = cmd.CreateParameter(); pp.ParameterName = n; pp.Value = v;
                    cmd.Parameters.Add(pp);
                }
                cmd.ExecuteNonQuery();
            }
        }
    }

    conn.Close();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

if (app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name:    "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
