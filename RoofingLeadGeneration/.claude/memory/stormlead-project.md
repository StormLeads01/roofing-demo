# StormLead Pro — Project State (as of 2026-04-16)

## What it is
ASP.NET Core 8 MVC app for roofing contractors. Scans a neighborhood for addresses, scores them by real NOAA hail event data, lets contractors save leads, enrich with owner contact info, and export. Primary use case: field canvassing on phones (~390px wide).

## Tech Stack
- **Backend:** ASP.NET Core 8 MVC, EF Core + SQLite (`EnsureCreated`, no migrations — manual DDL patches in Program.cs startup)
- **Auth:** Cookie auth + Google/Microsoft OAuth; dev backdoor at `/Auth/DevLogin`
- **Frontend:** Tailwind CSS via CDN, Font Awesome, Leaflet.js (CDNJS), vanilla JS
- **Data sources:**
  - OpenStreetMap Overpass API — real residential addresses (no key)
  - NOAA SWDI `nx3hail` dataset — real hail event history (no key)
  - Regrid Parcel API v2 — owner name + year built (free tier: 25 lookups/day; trial token restricted to 7 counties)
  - BatchSkipTracing — phone + email enrichment (stubbed — `// TODO` in LeadsController.Enrich; needs paid API key)
  - Google Maps Geocoding API — address → lat/lng (key hardcoded in RoofHealthController)

## Project Location
`C:\Users\James\source\repos\roofing-demo\RoofingLeadGeneration`

## Key Files
- `Controllers/RoofHealthController.cs` — neighborhood scan, OSM + NOAA data pipeline
- `Controllers/LeadsController.cs` — save/delete/enrich/export leads
- `Controllers/DashboardController.cs` — stats dashboard
- `Services/RealDataService.cs` — Overpass, NOAA SWDI, Regrid API wrappers
- `Data/Models/Lead.cs` — Lead entity (has legacy `RoofAge` + `EstimatedDamage` columns kept for DB compat)
- `Program.cs` — startup, DB init, manual schema patches
- `wwwroot/js/site.js` — scan UI, card rendering, map, toggleMobileMenu(), showToast()
- `wwwroot/js/saved-leads.js` — saved leads table + mobile cards, sort/filter/enrich/export, toggleMobileMenu()

## What's Real vs Simulated
**All real:**
- Addresses — from OpenStreetMap (sparse in rural areas; shows "No address data found" message if empty)
- Risk level, hail size, storm date — from NOAA SWDI (shows "No data" if no events within 2 miles)
- Owner name, year built — from Regrid (only after user clicks ⚡ enrich on a saved lead)

**Stubbed (needs paid API key):**
- Owner phone + email — BatchSkipTracing endpoint exists, `TODO` comment inside

**Removed (was fake, now gone):**
- Property Type — was random from hardcoded list
- Estimated Damage — was random label within risk tier
- Roof Age — was `rng.Next(3, 28)`
- Simulated address fallback — entire `GenerateSimulatedProperties` method deleted

## Database Schema (SQLite)
Tables: `users`, `leads`, `enrichments`
- `leads` has columns: `id, address, lat, lng, risk_level, last_storm_date, hail_size, estimated_damage (legacy), roof_age (legacy), year_built, property_type (legacy), source_address, saved_at, notes, owner_name, owner_phone, owner_email, user_id`
- `enrichments` tracks per-lead enrichment history with provider + credits_used
- Schema patches run at startup via `ExecuteSqlRaw` try-catch blocks

## Known Behaviour Notes
- OSM Overpass query only requires `addr:housenumber` (street optional) — covers more US properties
- NOAA data has ~120-day processing lag; queries 2-year window split into two 1-year requests
- Regrid free trial restricted to 7 specific counties — enrichment returns null outside those
- `RealDataService` registered as Singleton (fine; IHttpClientFactory is singleton-safe)
- Leaflet loaded from CDNJS (`cdnjs.cloudflare.com/ajax/libs/leaflet/1.9.4/`) — NOT unpkg (unpkg URL contains `@` which conflicts with Razor `@@` escaping)

## Mobile Work Completed (April 2026)
All three main views now have consistent hamburger nav + mobile drawer:
- `Views/Home/Index.cshtml` — hamburger, mobile drawer, `toggleMobileMenu()` in site.js
- `Views/Leads/Saved.cshtml` — hamburger, mobile drawer, `toggleMobileMenu()` in saved-leads.js
- `Views/Dashboard/Index.cshtml` — hamburger, mobile drawer, inline `toggleMobileMenu()` script

Mobile card view on Saved Leads (`id="mobileCards"`):
- Shown on `< md` breakpoint (`md:hidden`), desktop table hidden on `< md` (`hidden md:block`)
- Cards show: address, risk badge, tap-to-call phone link, storm info, Save/Delete/Enrich action buttons
- Built by `buildMobileCard(lead)` in saved-leads.js; edit mode supported
- Filter pills scroll horizontally on mobile (`overflow-x-auto`, `flex-shrink-0`)

## Roadmap / TODO
See `TODO.md` in project root for prioritized feature list.
