using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RoofingLeadGeneration.Data;
using RoofingLeadGeneration.Data.Models;
using System.Security.Claims;

namespace RoofingLeadGeneration.Controllers
{
    [Route("[controller]")]
    public class AuthController : Controller
    {
        private readonly AppDbContext        _db;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<AuthController> _logger;

        public AuthController(AppDbContext db, IWebHostEnvironment env, ILogger<AuthController> logger)
        {
            _db     = db;
            _env    = env;
            _logger = logger;
        }

        // ── GET /Auth/Login ─────────────────────────────────────────
        [HttpGet("Login")]
        public IActionResult Login(string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true) return Redirect(returnUrl ?? "/");

            var cfg = HttpContext.RequestServices.GetService<IConfiguration>();
            var demoEnabled = string.Equals(cfg?["Auth:DemoEnabled"], "true", StringComparison.OrdinalIgnoreCase);
            ViewData["ReturnUrl"]        = returnUrl ?? "/";
            ViewData["GoogleEnabled"]    = !string.IsNullOrWhiteSpace(cfg?["Auth:Google:ClientId"]);
            ViewData["MicrosoftEnabled"] = !string.IsNullOrWhiteSpace(cfg?["Auth:Microsoft:ClientId"]);
            ViewData["PasswordEnabled"]  = demoEnabled || !string.IsNullOrWhiteSpace(cfg?["Auth:AdminEmail"]);
            ViewData["DemoEmail"]        = demoEnabled ? cfg?["Auth:DemoEmail"] : null;
            ViewData["DemoPassword"]     = demoEnabled ? cfg?["Auth:DemoPassword"] : null;
            return View();
        }

        // ── POST /Auth/Login — password login ───────────────────────
        [HttpPost("Login")]
        public async Task<IActionResult> LoginPost(string email, string password, string? returnUrl = "/")
        {
            var cfg           = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var demoEnabled   = string.Equals(cfg["Auth:DemoEnabled"], "true", StringComparison.OrdinalIgnoreCase);
            var demoEmail     = cfg["Auth:DemoEmail"]     ?? "";
            var demoPassword  = cfg["Auth:DemoPassword"]  ?? "";
            var adminEmail    = cfg["Auth:AdminEmail"]    ?? "";
            var adminPassword = cfg["Auth:AdminPassword"] ?? "";

            // Check demo credentials
            if (demoEnabled &&
                string.Equals(email, demoEmail, StringComparison.OrdinalIgnoreCase) &&
                password == demoPassword)
            {
                var userId = await FindOrCreateUserAsync("demo", "demo-user-1", demoEmail, "Demo User");
                await SignInUserAsync(userId, "demo", "demo-user-1", demoEmail, "Demo User");
                return LocalRedirect(returnUrl ?? "/");
            }

            // Check admin credentials
            if (!string.IsNullOrWhiteSpace(adminEmail) &&
                string.Equals(email, adminEmail, StringComparison.OrdinalIgnoreCase) &&
                password == adminPassword)
            {
                var userId = await FindOrCreateUserAsync("password", adminEmail, adminEmail, adminEmail.Split('@')[0]);
                await SignInUserAsync(userId, "password", adminEmail, adminEmail, adminEmail.Split('@')[0]);
                return LocalRedirect(returnUrl ?? "/");
            }

            ViewData["ReturnUrl"]        = returnUrl ?? "/";
            ViewData["GoogleEnabled"]    = !string.IsNullOrWhiteSpace(cfg["Auth:Google:ClientId"]);
            ViewData["MicrosoftEnabled"] = !string.IsNullOrWhiteSpace(cfg["Auth:Microsoft:ClientId"]);
            ViewData["PasswordEnabled"]  = true;
            ViewData["DemoEmail"]        = demoEnabled ? demoEmail : null;
            ViewData["DemoPassword"]     = demoEnabled ? demoPassword : null;
            ViewData["LoginError"]       = "Invalid email or password.";
            return View("Login");
        }

        // ── GET /Auth/SignIn/{provider} ─────────────────────────────
        [HttpGet("SignIn/{provider}")]
        public IActionResult SignIn(string provider, string? returnUrl = "/")
        {
            var props = new AuthenticationProperties
            {
                RedirectUri = Url.Action("Callback", "Auth", new { returnUrl }),
                Items       = { ["provider"] = provider }
            };
            return Challenge(props, provider);
        }

        // ── GET /Auth/Callback ──────────────────────────────────────
        [HttpGet("Callback")]
        public async Task<IActionResult> Callback(string? returnUrl = "/")
        {
            var result = await HttpContext.AuthenticateAsync("External");
            if (!result.Succeeded)
            {
                _logger.LogWarning("External auth failed: {Error}", result.Failure?.Message);
                return Redirect("/Auth/Login");
            }

            await HttpContext.SignOutAsync("External");

            var provider   = result.Properties?.Items["provider"] ?? "unknown";
            var providerId = result.Principal!.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
            var email      = result.Principal.FindFirst(ClaimTypes.Email)?.Value ?? "";
            var name       = result.Principal.FindFirst(ClaimTypes.Name)?.Value  ?? email;

            var userId = await FindOrCreateUserAsync(provider, providerId, email, name);
            await SignInUserAsync(userId, provider, providerId, email, name);

            return LocalRedirect(returnUrl ?? "/");
        }

        // ── GET /Auth/Logout ────────────────────────────────────────
        [HttpGet("Logout")]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Redirect("/Auth/Login");
        }

        // ── GET /Auth/DemoLogin ─────────────────────────────────────
        // One-click demo login — enabled when Auth:DemoEnabled = true in config
        [HttpGet("DemoLogin")]
        public async Task<IActionResult> DemoLogin(string? returnUrl = "/")
        {
            var cfg         = HttpContext.RequestServices.GetService<IConfiguration>();
            var demoEnabled = string.Equals(cfg?["Auth:DemoEnabled"], "true", StringComparison.OrdinalIgnoreCase);
            if (!demoEnabled && !_env.IsDevelopment()) return NotFound();

            var demoEmail = cfg?["Auth:DemoEmail"] ?? "demo@stormlead.pro";
            var userId = await FindOrCreateUserAsync("demo", "demo-user-1", demoEmail, "Demo User");
            await SignInUserAsync(userId, "demo", "demo-user-1", demoEmail, "Demo User");
            _logger.LogInformation("Demo account login (id={Id})", userId);
            return LocalRedirect(returnUrl ?? "/");
        }

        // ── GET /Auth/DevLogin ──────────────────────────────────────
        // ⚠️  DEV BACKDOOR — returns 404 in Production
        [HttpGet("DevLogin")]
        public async Task<IActionResult> DevLogin(string? returnUrl = "/")
        {
            if (!_env.IsDevelopment()) return NotFound();

            var userId = await FindOrCreateUserAsync("dev", "dev-user-1", "dev@localhost", "Dev User");
            await SignInUserAsync(userId, "dev", "dev-user-1", "dev@localhost", "Dev User");
            _logger.LogWarning("DEV BACKDOOR used — signed in as Dev User (id={Id})", userId);
            return LocalRedirect(returnUrl ?? "/");
        }

        // ── Helpers ─────────────────────────────────────────────────

        private async Task<long> FindOrCreateUserAsync(
            string provider, string providerId, string email, string name)
        {
            var user = await _db.Users.FirstOrDefaultAsync(
                u => u.Provider == provider && u.ProviderId == providerId);

            if (user != null) return user.Id;

            user = new User
            {
                Provider    = provider,
                ProviderId  = providerId,
                Email       = email,
                DisplayName = name,
                CreatedAt   = DateTime.UtcNow
            };
            _db.Users.Add(user);
            await _db.SaveChangesAsync();
            return user.Id;
        }

        private async Task SignInUserAsync(
            long userId, string provider, string providerId, string email, string name)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, providerId),
                new(ClaimTypes.Name,           name),
                new(ClaimTypes.Email,          email),
                new("provider",                provider),
                new("user_db_id",              userId.ToString())
            };

            var identity  = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var props     = new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc   = DateTimeOffset.UtcNow.AddDays(30)
            };

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity), props);
        }
    }
}
