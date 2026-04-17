# StormLead Pro — Feature Roadmap

## 🔴 Priority 1 — Core Product (Do These First)

- [ ] **Complete BatchSkipTracing integration** — wire up the `TODO` stub in `LeadsController.Enrich` to actually call the BST API and return phone + email. This is the biggest gap between demo and paid product.
- [ ] **Lead pipeline / status tracking** — add a `Status` column to leads (New → Contacted → Appointment Set → Closed Won / Closed Lost). Needs DB column, PATCH endpoint, and UI dropdown on the saved leads table.
- [ ] **Storm alert emails** — when NOAA records a new hail event over an area where a user has saved leads, send them an email notification. Requires a background job that polls NOAA periodically and a transactional email provider (SendGrid / Mailgun).

## 🟡 Priority 2 — Feels Professional

- [ ] **Satellite map tiles** — swap the Leaflet tile layer to Esri satellite imagery so users can visually assess roof condition before knocking. Free via Esri's public tile service.
- [ ] **Hail size visual indicator** — show a coin/ball comparison next to hail size (penny / quarter / golf ball / baseball). Roofers talk in those terms, not decimal inches.
- [x] **Mobile field mode** — hamburger nav on all pages, mobile card view on Saved Leads with tap-to-call and action buttons. ✅
- [ ] **Bulk actions** — bulk delete, bulk export, bulk enrich on the saved leads table.
- [ ] **Notes on leads** — the `Notes` column exists in the DB and model but is never surfaced in the UI. Add an expandable notes field per lead.

## 🟢 Priority 3 — Business Model

- [ ] **Stripe billing** — tiered plans: Free (25 leads/month, no enrichment), Pro ($49/mo, unlimited + enrichment credits), Agency ($149/mo, team seats). Enrichment credit system already exists in the DB.
- [ ] **Team accounts** — let a roofing company add multiple reps, assign leads to specific team members, track who's working what territory.
- [ ] **CSV import** — let users upload their own address lists and run NOAA hail scoring against them. Opens the door to roofers with existing canvassing lists.
- [x] **Landing page** — hero, how-it-works, features, pricing tiers (Free/Pro/Agency), CTA. ✅

## ⚫ Priority 1.5 — Data Quality

- [ ] **Commercial hail data (HailTrace or Tomorrow.io)** — replace/supplement NOAA SWDI with a purpose-built hail data provider. HailTrace is what most roofing CRMs use — event history by address, accurate hail size, date, going back years. Tomorrow.io has a generous free tier with historical weather. Either would dramatically improve risk scoring coverage nationwide. Current NOAA SWDI is radar-only and has coverage gaps + 120-day lag.

## 🔵 Priority 4 — Differentiation / Longer Term

- [ ] **Permit data overlay** — pull county building permit data (many counties publish this publicly). Recent roofing permits on nearby houses = strong storm damage signal.
- [ ] **Zapier / HubSpot integration** — push new leads directly into the contractor's existing CRM.
- [ ] **White-label / agency mode** — let roofing marketing agencies run the platform for multiple contractor clients under their own branding.
- [ ] **Progressive Web App (PWA)** — installable on a phone, works offline for field use.
