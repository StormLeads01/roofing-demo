using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RoofingLeadGeneration.Data;
using System.Security.Claims;

namespace RoofingLeadGeneration.Controllers
{
    [Authorize]
    [Route("[controller]")]
    public class CompanyController : Controller
    {
        private readonly AppDbContext        _db;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<CompanyController> _logger;
        private readonly string              _adminEmail;

        private long? CurrentOrgId =>
            long.TryParse(User.FindFirst("user_org_id")?.Value, out var id) ? id : null;

        private bool IsAdmin() =>
            (User.FindFirst(ClaimTypes.Email)?.Value ?? "") == _adminEmail;

        public CompanyController(AppDbContext db, IWebHostEnvironment env,
                                 ILogger<CompanyController> logger, IConfiguration config)
        {
            _db         = db;
            _env        = env;
            _logger     = logger;
            _adminEmail = config["AdminEmail"] ?? "";
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /Company/Settings
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("Settings")]
        public async Task<IActionResult> Settings()
        {
            var orgId = CurrentOrgId;
            if (orgId == null) return RedirectToAction("Index", "Home");

            var org = await _db.Orgs.FirstOrDefaultAsync(o => o.Id == orgId);
            if (org == null) return RedirectToAction("Index", "Home");

            ViewBag.IsAdmin = IsAdmin();
            return View(org);
        }

        // ─────────────────────────────────────────────────────────────────
        // POST /Company/Settings
        // ─────────────────────────────────────────────────────────────────
        [HttpPost("Settings")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Settings(
            string? companyName, string? companyEmail, string? phone,
            string? website, string? accentColor, string? tagline,
            string? licenseNumber, IFormFile? logoFile, string? removeLogo,
            string? address, string? facebookUrl, string? instagramUrl, string? googleBusinessUrl)
        {
            var orgId = CurrentOrgId;
            if (orgId == null) return RedirectToAction("Index", "Home");

            var org = await _db.Orgs.FirstOrDefaultAsync(o => o.Id == orgId);
            if (org == null) return RedirectToAction("Index", "Home");

            org.CompanyName   = companyName?.Trim();
            org.CompanyEmail  = companyEmail?.Trim();
            org.Phone         = phone?.Trim();
            org.Website       = website?.Trim();
            org.Tagline       = tagline?.Trim();
            org.LicenseNumber = licenseNumber?.Trim();

            // ── Additional company info ──────────────────────────────────
            org.Address           = string.IsNullOrWhiteSpace(address)           ? null : address.Trim();
            org.FacebookUrl       = string.IsNullOrWhiteSpace(facebookUrl)       ? null : facebookUrl.Trim();
            org.InstagramUrl      = string.IsNullOrWhiteSpace(instagramUrl)      ? null : instagramUrl.Trim();
            org.GoogleBusinessUrl = string.IsNullOrWhiteSpace(googleBusinessUrl) ? null : googleBusinessUrl.Trim();

            // Validate and set accent color
            if (!string.IsNullOrWhiteSpace(accentColor) &&
                System.Text.RegularExpressions.Regex.IsMatch(accentColor.Trim(), @"^#[0-9a-fA-F]{6}$"))
                org.AccentColor = accentColor.Trim();

            // Handle logo removal
            if (removeLogo == "true" && !string.IsNullOrWhiteSpace(org.LogoPath))
            {
                var oldPath = Path.Combine(_env.WebRootPath, org.LogoPath.TrimStart('/'));
                if (System.IO.File.Exists(oldPath))
                    System.IO.File.Delete(oldPath);
                org.LogoPath = null;
            }

            // Handle logo upload
            if (logoFile != null && logoFile.Length > 0)
            {
                var allowed = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg" };
                var ext     = Path.GetExtension(logoFile.FileName).ToLowerInvariant();
                if (!allowed.Contains(ext))
                {
                    TempData["Error"] = "Logo must be a PNG, JPG, GIF, WebP, or SVG file.";
                    return View(org);
                }
                if (logoFile.Length > 2 * 1024 * 1024)
                {
                    TempData["Error"] = "Logo must be under 2 MB.";
                    return View(org);
                }

                // Delete old logo if any
                if (!string.IsNullOrWhiteSpace(org.LogoPath))
                {
                    var oldPath = Path.Combine(_env.WebRootPath, org.LogoPath.TrimStart('/'));
                    if (System.IO.File.Exists(oldPath))
                        System.IO.File.Delete(oldPath);
                }

                var uploadsDir = Path.Combine(_env.WebRootPath, "uploads", "logos");
                Directory.CreateDirectory(uploadsDir);
                var fileName = $"{orgId}{ext}";
                var filePath = Path.Combine(uploadsDir, fileName);
                using var stream = new FileStream(filePath, FileMode.Create);
                await logoFile.CopyToAsync(stream);
                org.LogoPath = $"/uploads/logos/{fileName}";
            }

            await _db.SaveChangesAsync();
            TempData["Success"] = "Company profile saved.";
            return RedirectToAction(nameof(Settings));
        }
    }
}
