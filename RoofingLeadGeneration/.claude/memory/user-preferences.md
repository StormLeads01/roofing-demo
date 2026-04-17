# User Preferences — James

## Coding Style
- No fake/simulated data — James explicitly removed all mocked fields once he understood what was fabricated
- Prefers real data sources even if coverage is imperfect over plausible-looking fake data
- CSS in dedicated files (site.css, admin.css, login.css), not inline `<style>` blocks in .cshtml
- Clean separation: JS logic in wwwroot/js/*.js, not inline scripts in views (exception: Dashboard has inline toggleMobileMenu since it has no dedicated JS file)
- Use CDNJS for CDN dependencies — avoids Razor `@@` escaping issues (e.g. unpkg URLs with `@` in version strings break Razor parsing)

## Mobile Patterns (field canvassing is primary use case, ~390px wide)
- Hamburger nav: `lg:hidden` button + `absolute top-full` drawer panel with `relative` on `<header>`
- Drawer toggle function `toggleMobileMenu()` — icon swaps bars ↔ xmark, `hidden` class toggled
- Mobile card view: `md:hidden` cards + `hidden md:block` desktop table, both populated from same data
- Filter pills: `overflow-x-auto` container + `flex-shrink-0` on each pill for horizontal scroll

## Communication Style
- Direct and brief — doesn't need explanation of every change, just what and why
- Will push back if something feels wrong ("I do not want made up shit")
- Asks broad strategic questions ("what else can we do to improve this") and wants honest product opinions

## Project Priorities (stated April 2026)
1. Real data only — no simulated fallbacks ✅ done
2. Mobile field mode ✅ done (hamburger nav all pages, mobile cards on Saved Leads)
3. Functional lead enrichment (BatchSkipTracing phone/email)
4. Lead pipeline/status tracking
5. Storm alert emails
6. Stripe billing / monetization

## Dev Environment
- Visual Studio / dotnet on Windows
- App repo: `C:\Users\James\source\repos\roofing-demo\RoofingLeadGeneration`
- Database: SQLite at `{AppContext.BaseDirectory}/data/leads.db`
- No migrations — uses `EnsureCreated()` + manual `ALTER TABLE` patches at startup
