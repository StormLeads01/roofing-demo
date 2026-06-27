// ══════════════════════════════════════════════════════════════════════════
// Storm Explorer  –  viewport-driven storm event browser
//   • Shows a Leaflet map the user can pan/zoom freely
//   • On each map move, fetches /RoofHealth/StormEvents for the visible area
//   • Renders a ranked event list on the left, map circles on the right
//   • Filter controls: min hail size, lookback period, wind toggle
// ══════════════════════════════════════════════════════════════════════════
(function () {
    'use strict';

    // ── Internal state ──────────────────────────────────────────────────
    let seMap       = null;         // Leaflet map instance
    let seCircles   = null;         // L.layerGroup for storm circles
    let seLoading   = false;
    let seDebounce  = null;
    let seStorms    = [];           // last fetched cluster array
    let seState     = '';           // detected 2-letter state abbr
    let seInitialized = false;

    let seSelectedDates  = null;    // Set of selected date strings; null = all selected
    let seSwathCache     = null;    // cached HailSwath GeoJSON for current viewport
    let seSwathLayer     = null;    // L.layerGroup holding rendered swath polygons
    let seHoverControl   = null;    // fixed hover info panel (topright)

    // Hail Reports overlay state
    let hailReportsLayer   = null;    // Leaflet GeoJSON layer — individual LSR/SPC event dots
    let hailReportsVisible = false;
    let hailLegendControl  = null;    // L.control floating legend

    // Hail Swath Polygons overlay state
    let swathLayer   = null;          // Leaflet GeoJSON layer — size-banded convex hull polygons
    let swathVisible = false;
    let swathLegend  = null;          // L.control floating legend for swaths

    // ── Public API ──────────────────────────────────────────────────────

    /**
     * Called by switchMainTab when Storm Explorer becomes visible.
     * Safe to call multiple times — map is only built once.
     * @param {number} defaultLat - initial map center lat
     * @param {number} defaultLng - initial map center lng
     */
    window.initStormExplorer = function (defaultLat, defaultLng) {
        if (seInitialized) {
            if (seMap) setTimeout(function () { seMap.invalidateSize(); }, 100);
            return;
        }
        seInitialized = true;
        buildSeMap(defaultLat || 32.78, defaultLng || -96.80);
        scheduleSeLoad(200);
    };

    /** Called by filter controls (onchange). */
    window.applySeFilters = function () {
        scheduleSeLoad(400);
    };

    /**
     * Toggle the Hail Reports dot overlay on/off.
     * Shows individual LSR + SPC hail event reports as coloured circles,
     * sized and coloured by hail diameter.
     */
    window.toggleHailReports = function () {
        if (!seMap) return;
        hailReportsVisible = !hailReportsVisible;

        var btn = document.getElementById('mrmsToggleBtn');
        if (btn) {
            btn.classList.toggle('border-sky-500',   hailReportsVisible);
            btn.classList.toggle('text-sky-300',     hailReportsVisible);
            btn.classList.toggle('bg-sky-500/10',    hailReportsVisible);
            btn.classList.toggle('border-slate-600', !hailReportsVisible);
            btn.classList.toggle('text-slate-400',   !hailReportsVisible);
            btn.classList.toggle('bg-slate-900',     !hailReportsVisible);
        }

        if (hailReportsVisible) {
            loadHailReportsLayer();
            addHailLegend();
        } else {
            if (hailReportsLayer) { seMap.removeLayer(hailReportsLayer); hailReportsLayer = null; }
            if (hailLegendControl) { hailLegendControl.remove(); hailLegendControl = null; }
        }
    };

    /**
     * Toggle the Hail Swath Polygon overlay on/off.
     * Shows size-banded convex-hull polygons — one ring per hail size tier per storm cluster.
     * Outer ring = pea hail (≥0.75"), inner rings progressively smaller and more intense coloured.
     */
    window.toggleHailSwaths = function () {
        if (!seMap) return;
        swathVisible = !swathVisible;

        var btn = document.getElementById('swathToggleBtn');
        if (btn) {
            btn.classList.toggle('border-orange-500',   swathVisible);
            btn.classList.toggle('text-orange-300',     swathVisible);
            btn.classList.toggle('bg-orange-500/10',    swathVisible);
            btn.classList.toggle('border-slate-600',    !swathVisible);
            btn.classList.toggle('text-slate-400',      !swathVisible);
            btn.classList.toggle('bg-slate-900',        !swathVisible);
        }

        if (swathVisible) {
            loadSwathLayer();
            addSwathLegend();
        } else {
            if (swathLayer)  { seMap.removeLayer(swathLayer);  swathLayer  = null; }
            if (swathLegend) { swathLegend.remove();           swathLegend = null; }
        }
    };

    /** Map a sizeBand threshold to a stroke/fill colour (red centre → yellow outer). */
    function swathColor(sizeBand) {
        if (sizeBand >= 3.0)  return '#7c3aed'; // purple  — grapefruit
        if (sizeBand >= 2.5)  return '#dc2626'; // dark red — baseball
        if (sizeBand >= 2.0)  return '#ef4444'; // red      — golf ball
        if (sizeBand >= 1.75) return '#f97316'; // red-orange
        if (sizeBand >= 1.5)  return '#fb923c'; // orange   — ping pong
        if (sizeBand >= 1.25) return '#f59e0b'; // amber    — half dollar
        if (sizeBand >= 1.0)  return '#eab308'; // yellow   — quarter
        return '#84cc16';                        // yellow-green — penny
    }

    /** Fetch banded swath polygons for the current viewport and render as filled polygons. */
    function loadSwathLayer() {
        if (!seMap) return;
        var bounds   = seMap.getBounds();
        var lookback = parseInt(document.getElementById('seLookback')?.value || '90', 10);

        fetch('/RoofHealth/HailSwathPolygons?' + new URLSearchParams({
            minLat:       bounds.getSouth().toFixed(4),
            maxLat:       bounds.getNorth().toFixed(4),
            minLng:       bounds.getWest().toFixed(4),
            maxLng:       bounds.getEast().toFixed(4),
            lookbackDays: lookback
        }))
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (geojson) {
            if (!geojson || !seMap || !swathVisible) return;

            if (swathLayer) seMap.removeLayer(swathLayer);

            swathLayer = L.geoJSON(geojson, {
                style: function (feature) {
                    var band  = (feature.properties && feature.properties.sizeBand) || 0.75;
                    var color = swathColor(band);
                    return {
                        color:       color,
                        weight:      1.5,
                        opacity:     0.75,
                        fillColor:   color,
                        fillOpacity: band >= 2.0 ? 0.28 : 0.18
                    };
                },
                onEachFeature: function (feature, layer) {
                    var p    = feature.properties || {};
                    var band = p.sizeBand || 0;
                    var coinLabel = band >= 3.0  ? 'Grapefruit+'
                                 : band >= 2.5  ? 'Baseball'
                                 : band >= 2.0  ? 'Golf Ball'
                                 : band >= 1.75 ? 'Ping Pong'
                                 : band >= 1.5  ? 'Walnut'
                                 : band >= 1.25 ? 'Half Dollar'
                                 : band >= 1.0  ? 'Quarter'
                                 : 'Penny';
                    layer.bindTooltip(
                        '<b>' + band.toFixed(2) + '&quot;+ hail swath</b> &mdash; ' + coinLabel + '<br>' +
                        (p.date || '') + '<br>' +
                        '<span style="opacity:0.7;font-size:0.85em">' + (p.reportCount || 0) + ' ground reports · max ' + (p.maxHailIn ? p.maxHailIn.toFixed(2) + '"' : '—') + '</span>',
                        { sticky: true }
                    );
                }
            }).addTo(seMap);

            console.info('[Hail Swaths] Rendered ' + (geojson.features ? geojson.features.length : 0) + ' banded polygons.');
        })
        .catch(function (err) { console.warn('[Hail Swaths]', err); });
    }

    /** Floating legend for swath bands. */
    function addSwathLegend() {
        if (!seMap || swathLegend) return;

        swathLegend = L.control({ position: 'bottomleft' });
        swathLegend.onAdd = function () {
            var div = L.DomUtil.create('div');
            div.style.cssText = [
                'background:rgba(15,20,30,0.88)',
                'border:1px solid rgba(100,116,139,0.45)',
                'border-radius:10px',
                'padding:10px 13px',
                'font-family:inherit',
                'font-size:12px',
                'line-height:1.6',
                'color:#cbd5e1',
                'min-width:170px',
                'box-shadow:0 2px 12px rgba(0,0,0,0.5)',
                'pointer-events:none'
            ].join(';');

            var rows = [
                ['#7c3aed', '≥ 3.0"', 'Grapefruit+'],
                ['#dc2626', '≥ 2.5"', 'Baseball'],
                ['#ef4444', '≥ 2.0"', 'Golf Ball'],
                ['#fb923c', '≥ 1.5"', 'Ping Pong'],
                ['#eab308', '≥ 1.0"', 'Quarter'],
                ['#84cc16', '≥ 0.75"','Penny'],
            ];

            div.innerHTML =
                '<div style="font-weight:700;font-size:11px;letter-spacing:.06em;' +
                    'text-transform:uppercase;color:#94a3b8;margin-bottom:7px">Hail Swath Size</div>' +
                rows.map(function (r) {
                    return '<div style="display:flex;align-items:center;gap:7px;margin-bottom:3px">' +
                        '<span style="display:inline-block;width:13px;height:13px;border-radius:3px;' +
                            'background:' + r[0] + ';opacity:0.85;flex-shrink:0"></span>' +
                        '<span>' + r[1] + ' <span style="opacity:0.6">(' + r[2] + ')</span></span>' +
                        '</div>';
                }).join('');

            return div;
        };
        swathLegend.addTo(seMap);
    }

    /** Fetch individual LSR/SPC hail events for the current viewport and render as dots. */
    function loadHailReportsLayer() {
        if (!seMap) return;
        var bounds   = seMap.getBounds();
        var lookback = parseInt(document.getElementById('seLookback')?.value || '90', 10);

        fetch('/RoofHealth/HailSwath?' + new URLSearchParams({
            minLat:       bounds.getSouth().toFixed(4),
            maxLat:       bounds.getNorth().toFixed(4),
            minLng:       bounds.getWest().toFixed(4),
            maxLng:       bounds.getEast().toFixed(4),
            lookbackDays: lookback
        }))
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (geojson) {
            if (!geojson || !seMap || !hailReportsVisible) return;

            var features = geojson.features;
            if (!features || features.length === 0) {
                console.info('[Hail Reports] No events found for this area/period.');
                return;
            }

            if (hailReportsLayer) seMap.removeLayer(hailReportsLayer);

            hailReportsLayer = L.geoJSON(geojson, {
                pointToLayer: function (feature, latlng) {
                    var sizeIn = (feature.properties && feature.properties.maxHailIn) || 0;
                    var color  = sizeIn >= 2.0 ? '#ef4444'
                               : sizeIn >= 1.0 ? '#f97316'
                               : '#eab308';
                    var radius = Math.max(5, Math.min(18, sizeIn * 9));
                    return L.circleMarker(latlng, {
                        radius:      radius,
                        color:       color,
                        fillColor:   color,
                        fillOpacity: 0.55,
                        weight:      1,
                        opacity:     0.9
                    });
                },
                onEachFeature: function (feature, layer) {
                    var p = feature.properties || {};
                    var srcLabel = p.source === 'lsr'      ? 'NWS Spotter'
                                 : p.source === 'SPC/NOAA' ? 'SPC Report'
                                 : (p.source || 'NOAA');
                    layer.bindTooltip(
                        '<b>' + (p.maxHailIn ? p.maxHailIn.toFixed(2) + '" hail' : '\u2014') + '</b><br>' +
                        (p.date || '') + '<br>' +
                        '<span style="opacity:0.7;font-size:0.85em">' + srcLabel + '</span>',
                        { sticky: true }
                    );
                }
            }).addTo(seMap);

            console.info('[Hail Reports] Rendered ' + features.length + ' events.');
        })
        .catch(function (err) { console.warn('[Hail Reports]', err); });
    }

    /** Create and attach the floating hail-size legend to the map. */
    function addHailLegend() {
        if (!seMap || hailLegendControl) return;

        hailLegendControl = L.control({ position: 'bottomleft' });

        hailLegendControl.onAdd = function () {
            var div = L.DomUtil.create('div');
            div.style.cssText = [
                'background:rgba(15,20,30,0.88)',
                'border:1px solid rgba(100,116,139,0.45)',
                'border-radius:10px',
                'padding:10px 13px',
                'font-family:inherit',
                'font-size:12px',
                'line-height:1.55',
                'color:#cbd5e1',
                'min-width:155px',
                'box-shadow:0 2px 12px rgba(0,0,0,0.5)',
                'pointer-events:none'
            ].join(';');

            div.innerHTML =
                '<div style="font-weight:700;font-size:11px;letter-spacing:.06em;' +
                    'text-transform:uppercase;color:#94a3b8;margin-bottom:7px">' +
                    '<i class="fa-solid fa-circle-dot" style="color:#38bdf8;margin-right:5px"></i>' +
                    'Hail Reports' +
                '</div>' +
                '<div style="display:flex;align-items:center;gap:8px;margin-bottom:5px">' +
                    '<svg width="18" height="18" viewBox="0 0 18 18">' +
                        '<circle cx="9" cy="9" r="7" fill="#ef4444" fill-opacity="0.55" stroke="#ef4444" stroke-width="1.2"/>' +
                    '</svg>' +
                    '<span><b style="color:#f87171">\u2265 2.0"</b>' +
                    ' <span style="color:#64748b;font-size:10px">golf ball</span></span>' +
                '</div>' +
                '<div style="display:flex;align-items:center;gap:8px;margin-bottom:5px">' +
                    '<svg width="14" height="14" viewBox="0 0 14 14">' +
                        '<circle cx="7" cy="7" r="5.5" fill="#f97316" fill-opacity="0.55" stroke="#f97316" stroke-width="1.2"/>' +
                    '</svg>' +
                    '<span><b style="color:#fb923c">\u2265 1.0"</b>' +
                    ' <span style="color:#64748b;font-size:10px">quarter</span></span>' +
                '</div>' +
                '<div style="display:flex;align-items:center;gap:8px">' +
                    '<svg width="10" height="10" viewBox="0 0 10 10">' +
                        '<circle cx="5" cy="5" r="3.8" fill="#eab308" fill-opacity="0.55" stroke="#eab308" stroke-width="1.2"/>' +
                    '</svg>' +
                    '<span><b style="color:#facc15">&lt; 1.0"</b>' +
                    ' <span style="color:#64748b;font-size:10px">pea / dime</span></span>' +
                '</div>' +
                '<div style="margin-top:8px;padding-top:7px;border-top:1px solid rgba(100,116,139,0.25);' +
                    'color:#475569;font-size:10px">' +
                    'NWS Spotter &amp; SPC Reports' +
                '</div>';

            return div;
        };

        hailLegendControl.addTo(seMap);
    }

    /** Called by storm cards to fly to and highlight a storm. */
    window.seSelectCard = function (idx) {
        document.querySelectorAll('.se-card').forEach(function (el) {
            el.classList.remove('ring-2', 'ring-brand', 'brightness-110');
        });

        const card = document.getElementById('se-card-' + idx);
        if (card) {
            card.classList.add('ring-2', 'ring-brand');
            card.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
        }

        if (seMap && seStorms[idx]) {
            seMap.flyTo([seStorms[idx].lat, seStorms[idx].lng], Math.max(seMap.getZoom(), 11));
        }
    };

    // ── Map construction ────────────────────────────────────────────────
    function buildSeMap(lat, lng) {
        seMap = L.map('seMapContainer', { center: [lat, lng], zoom: 10, zoomControl: true });

        var tileSat = L.tileLayer(
            'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
            { attribution: 'Tiles &copy; Esri', maxZoom: 19 }
        );
        var mtKey = window.MAPTILER_KEY || '';
        var tileStreet = mtKey
            ? L.tileLayer(
                'https://api.maptiler.com/maps/dataviz-dark/{z}/{x}/{y}.png?key=' + mtKey,
                { attribution: '&copy; <a href="https://www.maptiler.com/">MapTiler</a> &copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>', maxZoom: 20, tileSize: 512, zoomOffset: -1 }
              )
            : L.tileLayer(
                'https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png',
                { attribution: '&copy; CARTO', maxZoom: 19 }
              );
        tileStreet.addTo(seMap);
        L.control.layers(
            { 'Dark Street': tileStreet, 'Satellite': tileSat },
            {},
            { position: 'topright' }
        ).addTo(seMap);

        // Hover info panel — sits below the layer toggle, updates on swath mouseover
        seHoverControl = L.control({ position: 'topright' });
        seHoverControl.onAdd = function () {
            var div = L.DomUtil.create('div', 'se-hover-panel');
            div.style.cssText = 'display:none';
            return div;
        };
        seHoverControl.addTo(seMap);

        seCircles = L.layerGroup().addTo(seMap);

        seMap.on('moveend', function () { scheduleSeLoad(700); });
    }

    // ── Debounced load ───────────────────────────────────────────────────
    function scheduleSeLoad(ms) {
        clearTimeout(seDebounce);
        seDebounce = setTimeout(loadSeEvents, ms);
    }

    // ── Fetch from backend ───────────────────────────────────────────────
    function loadSeEvents() {
        if (!seMap || seLoading) return;
        seLoading = true;

        var center = seMap.getCenter();
        var bounds = seMap.getBounds();
        var radiusMiles = Math.min(
            seHaversine(center.lat, center.lng, bounds.getNorth(), center.lng) * 0.85,
            200
        );

        var minHail  = parseFloat(document.getElementById('seMinHail')?.value  || '0.75');
        var lookback = parseInt(document.getElementById('seLookback')?.value   || '90', 10);
        var wind     = (document.getElementById('seIncludeWind')?.checked !== false);

        setSeLoading(true);

        var params = new URLSearchParams({
            lat:           center.lat.toFixed(6),
            lng:           center.lng.toFixed(6),
            state:         seState,
            radiusMiles:   radiusMiles.toFixed(1),
            minHailInches: minHail.toFixed(2),
            includeWind:   wind,
            lookbackDays:  lookback
        });

        fetch('/RoofHealth/StormEvents?' + params.toString())
            .then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            })
            .then(function (data) {
                if (data.state) seState = data.state;
                seStorms = data.storms || [];
                seSelectedDates = null;  // reset so new dates are all-selected
                renderSeCount(seStorms.length);
                renderSeList(seStorms);
                renderSeCircles(seStorms);
            })
            .catch(function (err) {
                console.warn('[StormExplorer] fetch failed:', err);
            })
            .finally(function () {
                seLoading = false;
                setSeLoading(false);
            });
    }

    // ── Render event list — grouped by date ──────────────────────────────
    function renderSeList(storms) {
        var list = document.getElementById('seList');
        if (!list) return;

        if (!storms || storms.length === 0) {
            list.innerHTML =
                '<div class="flex flex-col items-center justify-center py-14 text-center text-slate-500 text-sm gap-3">' +
                '<i class="fa-solid fa-magnifying-glass-location text-4xl text-slate-700"></i>' +
                '<div>No storms found in this area.<br>Try wider filters or pan the map.</div>' +
                '</div>';
            return;
        }

        // Group clusters by date
        var byDate = {};
        storms.forEach(function (s) {
            var key = s.date || 'unknown';
            if (!byDate[key]) byDate[key] = [];
            byDate[key].push(s);
        });

        // Sort dates descending
        var dates = Object.keys(byDate).sort(function (a, b) { return b.localeCompare(a); });

        // Init selection to most recent date only
        if (!seSelectedDates) {
            seSelectedDates = new Set(dates.length > 0 ? [dates[0]] : []);
        }

        list.innerHTML = dates.map(function (date) {
            return seDateCardHtml(date, byDate[date]);
        }).join('');
    }

    function seDateCardHtml(date, clusters) {
        var maxHail    = clusters.reduce(function (m, s) { return Math.max(m, s.maxHailInches || 0); }, 0);
        var maxScore   = clusters.reduce(function (m, s) { return Math.max(m, s.score || 0); }, 0);
        var totalRpts  = clusters.reduce(function (m, s) { return m + (s.hailReports || 0); }, 0);
        var maxWind    = clusters.reduce(function (m, s) { return Math.max(m, s.maxWindMph || 0); }, 0);
        var selected   = !seSelectedDates || seSelectedDates.has(date);

        var color      = swathColor(maxHail || 0.75);
        var hailStr    = maxHail ? maxHail.toFixed(2) + '"' : '—';
        var dayStr     = seFormatDate(date);
        var scoreWidth = Math.min(Math.round(maxScore), 100);

        var tier, tierBg, tierBadge;
        if (maxScore >= 70) {
            tier = 'HOT';   tierBg = 'border-red-500/30';    tierBadge = 'bg-red-500/20 text-red-300';
        } else if (maxScore >= 40) {
            tier = 'ACTIVE'; tierBg = 'border-orange-500/30'; tierBadge = 'bg-orange-500/20 text-orange-300';
        } else {
            tier = 'MINOR';  tierBg = 'border-slate-600/40';  tierBadge = 'bg-slate-600/30 text-slate-400';
        }

        var opacity = selected ? '' : 'opacity-40';

        return '<div class="se-date-card rounded-xl border px-4 py-3.5 mb-2 cursor-pointer ' +
               'transition-all hover:brightness-110 active:scale-[0.99] ' +
               (selected ? 'bg-slate-800/60 ' : 'bg-slate-900/40 ') + tierBg + ' ' + opacity + '" ' +
               'onclick="seToggleDate(\'' + date + '\')" ' +
               'id="se-date-' + date + '">' +

               '<div class="flex items-start justify-between gap-2">' +
               '<div class="min-w-0 flex-1">' +
               '<div class="flex items-center gap-2 mb-1.5">' +
               '<span class="text-xs font-bold uppercase tracking-wider px-2 py-0.5 rounded-full ' + tierBadge + '">' + tier + '</span>' +
               '<span class="text-sm font-semibold text-white">' + dayStr + '</span>' +
               '</div>' +
               '<div class="flex items-baseline gap-3 flex-wrap">' +
               '<span class="text-2xl font-extrabold" style="color:' + color + ';line-height:1">' + hailStr + '</span>' +
               '<span class="text-xs text-slate-500">max hail</span>' +
               (maxWind > 0 ? '<span class="text-xs text-blue-400"><i class="fa-solid fa-wind mr-0.5"></i>' + maxWind.toFixed(0) + ' mph</span>' : '') +
               '</div>' +
               '</div>' +
               '<div class="flex flex-col items-end gap-1.5 flex-shrink-0">' +
               '<div class="w-5 h-5 rounded-full border-2 flex items-center justify-center transition-all ' +
               (selected ? 'border-brand bg-brand/20' : 'border-slate-600 bg-transparent') + '">' +
               (selected ? '<i class="fa-solid fa-check text-brand" style="font-size:9px"></i>' : '') +
               '</div>' +
               '<span class="text-xs text-slate-600">' + clusters.length + ' cluster' + (clusters.length !== 1 ? 's' : '') + '</span>' +
               '</div>' +
               '</div>' +

               '<div class="mt-2.5 flex items-center gap-3 text-xs text-slate-600">' +
               '<span><i class="fa-solid fa-location-dot mr-0.5"></i>' + totalRpts + ' report' + (totalRpts !== 1 ? 's' : '') + '</span>' +
               '<div class="flex-1"></div>' +
               '<div class="w-20 h-1.5 rounded-full bg-slate-800 overflow-hidden">' +
               '<div class="h-full rounded-full bg-gradient-to-r from-orange-500 to-red-500 transition-all" ' +
               'style="width:' + scoreWidth + '%"></div>' +
               '</div>' +
               '</div>' +

               '</div>';
    }

    /** Toggle a date's swath on/off from the left panel. */
    window.seToggleDate = function (date) {
        if (!seSelectedDates) return;
        if (seSelectedDates.has(date)) {
            seSelectedDates.delete(date);
        } else {
            seSelectedDates.add(date);
        }
        // Update card appearance without full re-render
        var storms = seStorms;
        var byDate = {};
        storms.forEach(function (s) {
            var key = s.date || 'unknown';
            if (!byDate[key]) byDate[key] = [];
            byDate[key].push(s);
        });
        var card = document.getElementById('se-date-' + date);
        if (card) {
            var newHtml = seDateCardHtml(date, byDate[date] || []);
            var tmp = document.createElement('div');
            tmp.innerHTML = newHtml;
            card.replaceWith(tmp.firstElementChild);
        }
        applySwathFilter();
    };

    // ── Storm swath rendering ─────────────────────────────────────────────
    function renderSeCircles(storms) {
        if (!seCircles || !seMap) return;
        seCircles.clearLayers();
        if (seSwathLayer) { seMap.removeLayer(seSwathLayer); seSwathLayer = null; }
        seSwathCache = null;
        if (!storms || storms.length === 0) return;

        // Dot markers on top for every cluster (always visible)
        storms.forEach(function (s, i) {
            var dot = L.circleMarker([s.lat, s.lng], {
                radius: 5, color: '#fff',
                fillColor: swathColor(s.maxHailInches || 0.75),
                fillOpacity: 1, weight: 1.5, zIndexOffset: 1000
            });
            dot.bindTooltip(
                '<b>' + (s.maxHailInches || 0.75).toFixed(2) + '" hail</b>' +
                (s.maxWindMph > 0 ? ' &nbsp;&middot;&nbsp; ' + s.maxWindMph.toFixed(0) + ' mph wind' : '') +
                '<br>' + seFormatDate(s.date) + '<br>Score: ' + Math.round(s.score),
                { direction: 'top', sticky: false }
            );
            dot.on('click', function () { seSelectCard(i); });
            seCircles.addLayer(dot);
        });

        if (typeof turf === 'undefined') return;

        var bounds   = seMap.getBounds();
        var lookback = parseInt(document.getElementById('seLookback')?.value || '90', 10);

        fetch('/RoofHealth/HailSwath?' + new URLSearchParams({
            minLat:       bounds.getSouth().toFixed(4),
            maxLat:       bounds.getNorth().toFixed(4),
            minLng:       bounds.getWest().toFixed(4),
            maxLng:       bounds.getEast().toFixed(4),
            lookbackDays: lookback
        }))
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (geojson) {
            if (!geojson || !seMap) return;
            seSwathCache = (geojson.features || []).filter(function (f) {
                return f.geometry && f.geometry.type === 'Point';
            });
            applySwathFilter();
        })
        .catch(function (err) { console.warn('[StormExplorer] HailSwath fetch failed:', err); });
    }

    /** Re-draw swath polygons using the cached report points + current date selection. */
    function applySwathFilter() {
        if (!seMap) return;
        if (seSwathLayer) { seMap.removeLayer(seSwathLayer); seSwathLayer = null; }
        var features = seSwathCache;
        if (!features || features.length === 0 || typeof turf === 'undefined') return;

        seSwathLayer = L.layerGroup().addTo(seMap);

        // Filter to selected dates only
        var active = features.filter(function (f) {
            var d = f.properties && f.properties.date;
            return !seSelectedDates || seSelectedDates.has(d);
        });
        if (active.length === 0) return;

        // Group by date
        var byDate = {};
        active.forEach(function (f) {
            var key = (f.properties && f.properties.date) || 'unknown';
            if (!byDate[key]) byDate[key] = [];
            byDate[key].push(f);
        });

        Object.keys(byDate).forEach(function (date) {
            var pts = byDate[date];
            var fc  = turf.featureCollection(pts);

            // DBSCAN: separate report points into per-cell clusters (70 km radius)
            var clustered;
            try { clustered = turf.clustersDbscan(fc, 70, { minPoints: 2, units: 'kilometers' }); }
            catch (e) { clustered = null; }

            var clusterMap = {};
            ((clustered && clustered.features) || pts).forEach(function (f) {
                var cid = (f.properties && f.properties.cluster != null)
                    ? f.properties.cluster : 'n_' + Math.random();
                if (!clusterMap[cid]) clusterMap[cid] = [];
                clusterMap[cid].push(f);
            });

            Object.keys(clusterMap).forEach(function (cid) {
                var members = clusterMap[cid];
                var maxHail = members.reduce(function (m, f) {
                    return Math.max(m, (f.properties && f.properties.maxHailIn) || 0);
                }, 0);
                var color = swathColor(maxHail || 0.75);
                var bufKm = Math.max(4, Math.min(12, (maxHail || 0.75) * 4.5));
                var mfc   = turf.featureCollection(members);

                try {
                    var hull  = members.length >= 3 ? turf.convex(mfc) : null;
                    var base  = hull || turf.bboxPolygon(turf.bbox(mfc));
                    var swath = turf.buffer(base, bufKm,       { units: 'kilometers', steps: 24 });
                    var glow  = turf.buffer(base, bufKm * 2.0, { units: 'kilometers', steps: 24 });

                    var panelHtml =
                        '<div style="font-size:13px;min-width:200px">' +
                        '<div style="font-size:15px;font-weight:800;color:#fff;margin-bottom:10px;border-bottom:1px solid rgba(100,116,139,0.25);padding-bottom:8px">' + seFormatDate(date) + '</div>' +
                        '<div style="display:flex;align-items:center;gap:10px">' +
                        '<div style="width:36px;height:36px;border-radius:50%;background:' + color + '22;border:1.5px solid ' + color + '55;display:flex;align-items:center;justify-content:center;flex-shrink:0">' +
                        '<i class="fa-solid fa-cloud-bolt" style="color:' + color + ';font-size:14px"></i>' +
                        '</div>' +
                        '<div>' +
                        '<div style="font-size:15px;font-weight:800;color:#fff;line-height:1.2">Hail up to ' + maxHail.toFixed(2) + '"</div>' +
                        '<div style="color:#94a3b8;font-size:11px;margin-top:3px">' + members.length + ' report' + (members.length !== 1 ? 's' : '') + '</div>' +
                        '</div>' +
                        '</div>' +
                        '</div>';

                    L.geoJSON(glow, { style: { fillColor: color, fillOpacity: 0.09, color: 'transparent', weight: 0 } }).addTo(seSwathLayer);
                    L.geoJSON(swath, {
                        style: { fillColor: color, fillOpacity: 0.36, color: color, weight: 1, opacity: 0.50 },
                        onEachFeature: function (f, layer) {
                            layer.on('mouseover', function () {
                                if (!seHoverControl) return;
                                var el = seHoverControl.getContainer();
                                el.innerHTML = panelHtml;
                                el.style.display = 'block';
                            });
                            layer.on('mouseout', function () {
                                if (seHoverControl) seHoverControl.getContainer().style.display = 'none';
                            });
                        }
                    }).addTo(seSwathLayer);
                } catch (e) { console.warn('[StormExplorer] swath hull failed:', e); }
            });
        });

        // Keep dot markers on top — layerGroup has no bringToFront; lift each child
        if (seCircles) seCircles.eachLayer(function (l) { if (l.bringToFront) l.bringToFront(); });
    }

    // ── UI helpers ────────────────────────────────────────────────────────
    function setSeLoading(on) {
        var badge = document.getElementById('seLoadingBadge');
        if (!badge) return;
        if (on) badge.classList.remove('hidden');
        else    badge.classList.add('hidden');
    }

    function renderSeCount(n) {
        var el = document.getElementById('seCount');
        if (el) el.textContent = n;
    }

    function seFormatDate(ds) {
        if (!ds) return '—';
        try {
            var d = new Date(ds + 'T12:00:00Z');
            return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
        } catch (e) { return ds; }
    }

    // Haversine distance in miles (same formula as the backend)
    function seHaversine(lat1, lng1, lat2, lng2) {
        var R    = 3958.8;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLng = (lng2 - lng1) * Math.PI / 180;
        var a    = Math.sin(dLat / 2) * Math.sin(dLat / 2)
                 + Math.cos(lat1 * Math.PI / 180) * Math.cos(lat2 * Math.PI / 180)
                 * Math.sin(dLng / 2) * Math.sin(dLng / 2);
        return R * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
    }

})();
