# StormLead Pro — Project State (as of 2026-04-27)

## What it is
ASP.NET Core 8 MVC app for roofing contractors. Scans a neighborhood for addresses, scores them by real NOAA hail event data, lets contractors save leads, enrich with owner contact info, and export. Primary use case: field canvassing on phones (~390px wide).

## Tech Stack
- **Backend:** ASP.NET Core 8 MVC, EF Core + SQLite (`EnsureCreated`, no migrations — manual DDL patches in Program.cs startup)
- **Auth:** Cookie auth + Google/Microsoft OAuth; dev backdoor at `/Auth/DevLogin`
- **Frontend:** Tailwind CSS via CDN, Font Awesome, Leaflet.js (CDNJS), vanilla JS
- **Data sources:**
  - OpenStreetMap Overpass API — real residential addresses (no key)
  - NOAA SWDI `nx3hail` dataset — real hail event history (no key); ~90–120 day processing lag
  - Iowa State Mesonet LSR API — NWS storm-spotter hail reports, 5-year history (no key); used for Hail Reports overlay in Storm Explorer
  - NOAA SPC hail CSV feed — last ~3 days of verified hail reports (no key); merged with LSR for Hail Reports overlay
  - Tomorrow.io — near-real-time hail data for Storm Alerts and NOAA gap-fill (free tier: 500 req/day)
  - Regrid Parcel API v2 — owner name + year built (free tier: 25 lookups/day; trial token restricted to 7 counties)
  - Whitepages Pro — phone + email enrichment (~$0.22/record; needs paid API key)
  - BatchSkipTracing — phone + email enrichment fallback (stubbed — `// TODO` in LeadsController.Enrich)
  - Google Maps Geocoding API — address → lat/lng (key in appsettings)
  - Esri World Imagery / CARTO dark tiles — Storm Explorer map base layers (no key)

## Project Location
`C:\Users\James\source\repos\roofing-demo\RoofingLeadGeneration`

## Key Files
- `Controllers/RoofHealthController.cs` — neighborhood scan, OSM + NOAA data pipeline; `/RoofHealth/HailSwath` endpoint for Storm Explorer Hail Reports; `/RoofHealth/StormEvents` for Storm Explorer clusters
- `Controllers/LeadsController.cs` — save/delete/enrich/export leads
- `Controllers/DashboardController.cs` — stats dashboard
- `Controllers/HelpController.cs` — help page
- `Services/RealDataService.cs` — Overpass, NOAA SWDI, Regrid, Tomorrow.io, Iowa State LSR, SPC hail CSV wrappers; `GetHailEventsGeoJsonAsync` merges LSR + SPC and returns GeoJSON FeatureCollection
- `Data/Models/Lead.cs` — Lead entity (has legacy `RoofAge` + `EstimatedDamage` columns kept for DB compat)
- `Program.cs` — startup, DB init, manual schema patches
- `wwwroot/js/site.js` — Neighborhood Scan UI, card rendering, map, toggleMobileMenu(), showToast()
- `wwwroot/js/saved-leads.js` — saved leads table + mobile cards, sort/filter/enrich/export, toggleMobileMenu()
- `wwwroot/js/storm-explorer.js` — Storm Explorer IIFE: Leaflet map init, debounced viewport fetch, event list rendering, Hail Reports overlay (LSR/SPC dots + legend), cluster circle rendering
- `Views/Home/Index.cshtml` — main app shell; two tabs: Neighborhood Scan + Storm Explorer
- `Views/Help/Index.cshtml` — full help/docs page including Storm Explorer and Hail Reports sections

## What's Real vs Simulated
**All real:**
- Addresses — from OpenStreetMap (sparse in rural areas; shows "No address data found" message if empty)
- Risk level, hail size, storm date — from NOAA SWDI (shows "No data" if no events within 2 miles)
- Storm Explorer clusters — from NOAA SWDI + Tomorrow.io, scored 0–100
- Hail Reports overlay — from Iowa State LSR + SPC CSV, individual verified spotter reports
- Owner name, year built — from Regrid (only after user clicks enrich on a saved lead)

**Stubbed (needs paid API key):**
- Owner phone + email — Whitepages Pro endpoint exists, BatchSkipTracing as fallback; both need keys

**Removed (was fake, now gone):**
- Property Type — was random from hardcoded list
- Estimated Damage — was random label within risk tier
- Roof Age — was rng.Next(3, 28)
- Simulated address fallback — entire GenerateSimulatedProperties method deleted

## Database Schema (SQLite)
Tables: users, leads, enrichments, watched_areas, sent_alerts, orgs, org_credits, org_credit_transactions, org_invites, lead_contacts
- leads: id, address, lat, lng, risk_level, last_storm_date, hail_size, estimated_damage (legacy), roof_age (legacy), year_built, property_type (legacy), source_address, saved_at, notes, owner_name, owner_phone, owner_email, user_id, status
- enrichments: tracks per-lead enrichment history with provider + credits_used
- watched_areas: userId, label, centerLat, centerLng, radiusMiles, minHailSizeInches, isActive, createdAt
- sent_alerts: deduplication table for storm alert emails
- Schema patches run at startup via ExecuteSqlRaw try-catch blocks

## Storm Explorer — Implementation Notes
- Lives in wwwroot/js/storm-explorer.js as a self-contained IIFE
- Map initialised by window.initStormExplorer(lat, lng) — called from switchMainTab('storm') in site.js
- Viewport fetch: scheduleSeLoad(ms) -> loadSeEvents() -> /RoofHealth/StormEvents
- Hail Reports toggle: window.toggleHailReports() -> loadHailReportsLayer() -> /RoofHealth/HailSwath
- GetHailEventsGeoJsonAsync in RealDataService: radius-based LSR fetch + SPC CSV parse, deduplicated within 5-mile/same-day proximity, returns GeoJSON FeatureCollection (max 500 features)
- Legend: addHailLegend() creates an L.control({ position: 'bottomleft' }) with dark-themed SVG color swatches; shown when Hail Reports is on, removed when off
- Empty result guard: JS checks features.length === 0 before calling L.geoJSON() to avoid "Invalid GeoJSON object" Leaflet error
- All no-data backend paths return EmptyFeatureCollection constant (never bare {})

## Known Behaviour Notes
- OSM Overpass query only requires addr:housenumber (street optional) — covers more US properties
- NOAA SWDI has ~90–120 day lag; queries 2-year window split into two 1-year requests
- Regrid free trial restricted to 7 specific counties — enrichment returns null outside those
- RealDataService registered as Singleton (fine; IHttpClientFactory is singleton-safe)
- Leaflet loaded from CDNJS (cdnjs.cloudflare.com/ajax/libs/leaflet/1.9.4/) — NOT unpkg (unpkg URL contains @ which conflicts with Razor @@ escaping)
- dotnet CLI not available in the Cowork Linux sandbox — use static analysis (see .claude/dotnet-build-verify-SKILL.md)

## Reference Docs
All in `docs/` folder:
- `technical-docs.md` — full technical documentation with Mermaid diagrams (architecture, data flows, DB schema, config reference)
- `market-research.md` — competitor analysis, pricing benchmarks, pain points, GTM strategy (combined from two prior files)
- `clue-research.md` — CLUE insurance claims data research
- `resources.md` — API docs, third-party service links
- `certified-hail-report-research.md` — NWD/HailTrace certified report research
- `competitive-research-and-redesign-plan.md` — UI/UX competitive analysis

## Mobile Work Completed (April 2026)
All three main views now have consistent hamburger nav + mobile drawer:
- Views/Home/Index.cshtml — hamburger, mobile drawer, toggleMobileMenu() in site.js
- Views/Leads/Saved.cshtml — hamburger, mobile drawer, toggleMobileMenu() in saved-leads.js
- Views/Dashboard/Index.cshtml — hamburger, mobile drawer, inline toggleMobileMenu() script

Mobile card view on Saved Leads (id="mobileCards"):
- Shown on < md breakpoint (md:hidden), desktop table hidden on < md (hidden md:block)
- Cards show: address, risk badge, tap-to-call phone link, storm info, Save/Delete/Enrich action buttons
- Built by buildMobileCard(lead) in saved-leads.js; edit mode supported
- Filter pills scroll horizontally on mobile (overflow-x-auto, flex-shrink-0)

## Roadmap / TODO
See TODO.md in project root for prioritized feature list.
