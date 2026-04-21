# StormLead Pro — Feature Roadmap

## 🔴 Priority 1 — Core Product (Do These First)

- [x] **Complete BatchSkipTracing integration** — fully wired as fallback when Whitepages Pro is not configured. Whitepages is primary (multi-contact), BST is fallback (single contact). ✅
- [x] **Lead pipeline / status tracking** — add a `Status` column to leads (New → Contacted → Appointment Set → Closed Won / Closed Lost). Needs DB column, PATCH endpoint, and UI dropdown on the saved leads table. ✅
- [ ] **Storm alerts + watched areas** — roofers define their territory upfront (address or zip + radius). A background job watches those areas for new hail events via Tomorrow.io and sends an email alert within minutes of a storm, before the roofer has saved a single lead. Alert email includes hail size, area name, and a deep link that drops them straight into a pre-filtered neighborhood search. This is the onboarding hook — sign up, set your territory, get alerted. Watched areas are unlimited (revisit per-plan limits when Stripe billing is added). Components:
  - **WatchedAreas DB table** — userId, label, centerLat, centerLng, radiusMiles, minHailSizeInches (default 1.0"), createdAt
  - **Watched areas UI** — add area by typing an address/zip, pick radius (5/10/25/50 miles), set minimum hail size. Simple list management, no map drawing needed (Options B/C — map drawing and named multi-zone territories — deferred for later).
  - **Background service** — ASP.NET `BackgroundService`, polls every 30 min, calls Tomorrow.io for each watched area, deduplicates via SentAlerts table
  - **SentAlerts DB table** — userId, watchedAreaId, eventDate, hailSizeInches, sentAt
  - **Alert email** — use SMTP via MailKit (user has access to a private email server — free, no third-party account needed). Fallback option: SendGrid (100/day free) or Amazon SES ($0.10/1000 emails). Deliverability note: private SMTP server must have SPF/DKIM configured or alerts may land in spam.
  - **Email config** — SMTP host, port, username, password in appsettings.json. Same pattern as other API keys already in the file.
  - **User alert preferences** — minimum hail size threshold per watched area, email on/off toggle

## 🟡 Priority 2 — Feels Professional

- [x] **Satellite map tiles** — Esri World Imagery is the default tile layer, with a Street/Satellite toggle button. ✅
- [x] **Hail size visual indicator** — show a coin/ball comparison next to hail size (penny / quarter / golf ball / baseball). Roofers talk in those terms, not decimal inches. ✅
- [x] **Mobile field mode** — hamburger nav on all pages, mobile card view on Saved Leads with tap-to-call and action buttons. ✅
- [ ] **Bulk actions** — bulk delete, bulk export, bulk enrich on the saved leads table.
- [x] **Notes on leads** — sticky note button on every row, expandable textarea, Ctrl+Enter to save, Esc to cancel, amber highlight when a note exists, included in CSV export. ✅

## 🟢 Priority 3 — Business Model

- [ ] **Stripe billing** — tiered plans: Free (25 leads/month, no enrichment), Pro ($49/mo, unlimited + enrichment credits), Agency ($149/mo, team seats). Enrichment credit system already exists in the DB.
- [ ] **Team accounts** — let a roofing company add multiple reps, assign leads to specific team members, track who's working what territory.
- [ ] **CSV import** — let users upload their own address lists and run NOAA hail scoring against them. Opens the door to roofers with existing canvassing lists.
- [x] **Landing page** — hero, how-it-works, features, pricing tiers (Free/Pro/Agency), CTA. ✅
- [ ] **Certified hail report (NWD/HailTrace)** — "Get Certified Report" button on enriched leads. NWD/HailTrace provide property-level certified hail certificates (~$5–15 each) that homeowners hand to their adjuster. Free data finds the lead; paid report closes the deal. Sell as a per-report add-on or include in Pro/Agency plan.

## ⚫ Priority 1.5 — Data Quality

- [x] **Commercial hail data (Tomorrow.io)** — Tomorrow.io integrated as a third hail source alongside NOAA SWDI and Mesonet LSR. Fills the 120-day NOAA radar lag gap with near-real-time data. Free tier: 500 req/day, 2-year lookback. ✅

## 🏆 Priority 2.5 — Competitive Edge (Differentiators)

- [ ] **Homeowner SMS outreach** — send a templated text directly from the lead record ("We noticed your home may have storm damage — we're offering free inspections this week"). Integrates with Twilio. Text response rates are 5x higher than email. Converts StormLead from a data tool into a full outreach platform.

- [ ] **Claim window countdown by state** — each state has a different statute of limitations (TX: 2yr, FL: 3yr, CO: 2yr, etc.). Auto-detect state from lead address and show the exact deadline date ("47 days left to file") rather than just hot/fileable/expired. Turns the app into a closing tool, not just a data tool.

- [ ] **AI roof condition pre-screening** — use satellite imagery (already on the map) to let roofers visually flag roof condition before calling, and eventually auto-score visible storm damage with a lightweight ML model. Undercuts EagleView ($40+/report) dramatically.

- [ ] **HOA / property management targeting** — flag leads that are HOA-managed properties and surface the property manager contact instead of (or in addition to) the homeowner. One sale = potentially dozens of roofs.

- [ ] **Neighborhood saturation tracking** — show which streets your team has already contacted so reps don't reach out to the same households twice. Shared across team accounts.

- [ ] **Canvassing route optimizer** — GPS-optimized driving/walking order through saved leads. Lower priority — StormLead is a phone/email outreach tool, not a door-to-door tool.

## 🔵 Priority 4 — Differentiation / Longer Term

- [ ] **Permit data overlay** — pull county building permit data (many counties publish this publicly). Recent roofing permits on nearby houses = strong storm damage signal.
- [ ] **Zapier / HubSpot integration** — push new leads directly into the contractor's existing CRM.
- [ ] **White-label / agency mode** — let roofing marketing agencies run the platform for multiple contractor clients under their own branding.
- [ ] **Progressive Web App (PWA)** — installable on a phone, works offline for field use. Required foundation for push notifications and the route optimizer.
