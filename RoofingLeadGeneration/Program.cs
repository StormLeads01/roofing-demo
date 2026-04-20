using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using RoofingLeadGeneration.Data;
using RoofingLeadGeneration.Services;

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

// ── Services ──────────────────────────────────────────────────────────────
builder.Services.AddSingleton<RealDataService>();

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
    AddColumnIfMissing("users", "is_admin",      "INTEGER NOT NULL DEFAULT 0");
    AddColumnIfMissing("leads", "year_built",    "INTEGER");
    AddColumnIfMissing("leads", "owner_name",    "TEXT");
    AddColumnIfMissing("leads", "owner_phone",   "TEXT");
    AddColumnIfMissing("leads", "owner_email",   "TEXT");
    AddColumnIfMissing("leads", "property_type", "TEXT");
    AddColumnIfMissing("leads", "source_address","TEXT");
    AddColumnIfMissing("leads", "notes",         "TEXT");
    AddColumnIfMissing("leads", "is_enriched",   "INTEGER NOT NULL DEFAULT 0");
    AddColumnIfMissing("leads", "deleted_at",    "TEXT");
    AddColumnIfMissing("leads", "status",        "TEXT NOT NULL DEFAULT 'new'");

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
