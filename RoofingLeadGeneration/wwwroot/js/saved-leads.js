// saved-leads.js
let allLeads = [], sortCol = 'riskLevel', sortDir = 'desc', activeFilter = 'all', editingId = null;

document.addEventListener('DOMContentLoaded', loadLeads);

function switchTab(tab) {
    const isLeads = tab === 'leads';
    document.getElementById('panelLeads').classList.toggle('hidden', !isLeads);
    document.getElementById('panelSources').classList.toggle('hidden', isLeads);
    document.getElementById('tabBtnLeads').classList.toggle('active', isLeads);
    document.getElementById('tabBtnSources').classList.toggle('active', !isLeads);
}

async function loadLeads() {
    setLoading(true);
    try {
        const resp = await fetch('/Leads');
        if (!resp.ok) throw new Error('HTTP ' + resp.status);
        allLeads = await resp.json();
        updateFilterCounts();
        renderTable();
    } catch (e) {
        setLoading(false);
        document.getElementById('leadsBody').innerHTML =
            '<tr><td colspan="11" class="text-center text-red-400 py-8 text-sm px-4">' +
            '<i class="fa-solid fa-triangle-exclamation mr-2"></i>Failed to load: ' + escapeHtml(e.message) + '</td></tr>';
    }
}

function setFilter(f) {
    activeFilter = f;
    document.querySelectorAll('.filter-btn').forEach(b => b.classList.toggle('active', b.dataset.f === f));
    renderTable();
}

function updateFilterCounts() {
    document.getElementById('fAll').textContent    = allLeads.length;
    document.getElementById('fHigh').textContent   = allLeads.filter(l => l.riskLevel === 'High').length;
    document.getElementById('fMedium').textContent = allLeads.filter(l => l.riskLevel === 'Medium').length;
    document.getElementById('fLow').textContent    = allLeads.filter(l => l.riskLevel === 'Low').length;
    const cnt = allLeads.length;
    document.getElementById('heroCount').textContent = cnt ? '(' + cnt + ')' : '';
}

function sortBy(col) {
    if (sortCol === col) sortDir = sortDir === 'asc' ? 'desc' : 'asc';
    else { sortCol = col; sortDir = 'asc'; }
    renderTable();
}

function getSortValue(lead, col) {
    const riskOrder = { High:0, Medium:1, Low:2 };
    switch (col) {
        case 'address':         return (lead.address || '').toLowerCase();
        case 'riskLevel':       return riskOrder[lead.riskLevel] != null ? riskOrder[lead.riskLevel] : 3;
        case 'hailSize':        return parseFloat(lead.hailSize) || 0;
        case 'lastStormDate':   return lead.lastStormDate || '';
        case 'yearBuilt':       return lead.yearBuilt || 0;
        case 'ownerName':       return (lead.ownerName || '').toLowerCase();
        case 'ownerPhone':      return (lead.ownerPhone || '').toLowerCase();
        case 'ownerEmail':      return (lead.ownerEmail || '').toLowerCase();
        default:                return '';
    }
}

function renderTable() {
    setLoading(false);
    const query = (document.getElementById('searchInput').value || '').toLowerCase();
    let rows = allLeads
        .filter(l => activeFilter === 'all' || l.riskLevel === activeFilter)
        .filter(l => !query || [l.address, l.ownerName, l.ownerPhone, l.ownerEmail].some(v => (v || '').toLowerCase().includes(query)));
    rows = rows.sort((a, b) => {
        const av = getSortValue(a, sortCol), bv = getSortValue(b, sortCol);
        const cmp = typeof av === 'number' ? av - bv : av.localeCompare(bv, undefined, { sensitivity: 'base' });
        return sortDir === 'asc' ? cmp : -cmp;
    });
    document.querySelectorAll('#leadsTable th').forEach(th => th.classList.remove('sort-asc', 'sort-desc'));
    const headers = ['address','riskLevel','ownerName','ownerPhone','hailSize','lastStormDate','yearBuilt','ownerEmail'];
    const ci = headers.indexOf(sortCol);
    if (ci >= 0) document.querySelectorAll('#leadsTable th')[ci].classList.add(sortDir === 'asc' ? 'sort-asc' : 'sort-desc');
    const body = document.getElementById('leadsBody');
    document.getElementById('leadsEmpty').classList.add('hidden');
    document.getElementById('leadsNoMatch').classList.add('hidden');
    const cards = document.getElementById('mobileCards');
    if (allLeads.length === 0) {
        body.innerHTML = '';
        if (cards) cards.innerHTML = '';
        document.getElementById('leadsEmpty').classList.remove('hidden');
        return;
    }
    if (rows.length === 0) {
        body.innerHTML = '';
        if (cards) cards.innerHTML = '';
        document.getElementById('leadsNoMatch').classList.remove('hidden');
        return;
    }
    body.innerHTML = rows.map(l => buildRow(l)).join('');
    if (cards) cards.innerHTML = rows.map(l => buildMobileCard(l)).join('');
}

function buildRow(lead) {
    const rc = ({ High:'badge-high', Medium:'badge-medium', Low:'badge-low' }[lead.riskLevel]) || 'badge-low';
    const ed = editingId === lead.id;
    const dash = '<span class="text-slate-600 italic">\u2014</span>';
    const nm = ed ? '<input class="owner-input" id="eName_'  + lead.id + '" value="' + escapeAttr(lead.ownerName  || '') + '" placeholder="Owner name..." />'
                  : (lead.ownerName  ? '<span class="text-slate-300">' + escapeHtml(lead.ownerName)  + '</span>' : dash);
    const ph = ed ? '<input class="owner-input" id="ePhone_' + lead.id + '" value="' + escapeAttr(lead.ownerPhone || '') + '" placeholder="(555) 000-0000" />'
                  : (lead.ownerPhone ? '<a href="tel:' + escapeAttr(lead.ownerPhone) + '" class="text-slate-300 hover:text-brand">' + escapeHtml(lead.ownerPhone) + '</a>' : dash);
    const em = ed ? '<input class="owner-input" id="eEmail_' + lead.id + '" value="' + escapeAttr(lead.ownerEmail || '') + '" placeholder="owner@example.com" />'
                  : (lead.ownerEmail ? '<a href="mailto:' + escapeAttr(lead.ownerEmail) + '" class="text-slate-300 hover:text-brand truncate block">' + escapeHtml(lead.ownerEmail) + '</a>' : dash);
    const ac = ed
        ? '<div class="flex items-center justify-center gap-1">' +
          '<button onclick="saveOwner(' + lead.id + ')" class="w-7 h-7 rounded-lg flex items-center justify-center bg-green-500/20 hover:bg-green-500/40 text-green-400 border border-green-500/30 transition" title="Save"><i class="fa-solid fa-check text-xs"></i></button>' +
          '<button onclick="cancelEdit()" class="w-7 h-7 rounded-lg flex items-center justify-center bg-slate-600/40 hover:bg-slate-600 text-slate-400 border border-slate-600 transition" title="Cancel"><i class="fa-solid fa-xmark text-xs"></i></button></div>'
        : '<div class="flex items-center justify-center gap-1">' +
          '<button onclick="enrichLead(' + lead.id + ', this)" class="w-7 h-7 rounded-lg flex items-center justify-center bg-orange-500/10 hover:bg-orange-500/30 text-orange-400 border border-orange-500/20 transition" title="Get contact info"><i class="fa-solid fa-bolt text-xs"></i></button>' +
          '<button onclick="startEdit(' + lead.id + ')" class="w-7 h-7 rounded-lg flex items-center justify-center bg-slate-700 hover:bg-slate-600 text-slate-400 hover:text-brand border border-slate-600 transition" title="Edit owner"><i class="fa-solid fa-pen text-xs"></i></button>' +
          '<button onclick="deleteLead(' + lead.id + ', this)" class="w-7 h-7 rounded-lg flex items-center justify-center bg-red-500/10 hover:bg-red-500/30 text-red-400 border border-red-500/20 transition" title="Delete"><i class="fa-solid fa-trash-can text-xs"></i></button></div>';
    return '<tr data-lead-id="' + lead.id + '" class="' + (ed ? 'editing' : '') + '">' +
        '<td class="font-medium text-white" style="max-width:200px"><span class="block truncate" title="' + escapeAttr(lead.address) + '">' + escapeHtml(lead.address) + '</span>' +
        (lead.sourceAddress ? '<span class="block text-xs text-slate-500 truncate">from ' + escapeHtml(lead.sourceAddress) + '</span>' : '') + '</td>' +
        '<td><span class="' + rc + ' px-2 py-0.5 rounded-full text-xs font-bold">' + escapeHtml(lead.riskLevel) + '</span></td>' +
        '<td class="hidden sm:table-cell">' + nm + '</td>' +
        '<td class="hidden sm:table-cell whitespace-nowrap">' + ph + '</td>' +
        '<td class="hidden md:table-cell whitespace-nowrap">' + escapeHtml(lead.hailSize) + '</td>' +
        '<td class="hidden md:table-cell whitespace-nowrap">' + escapeHtml(lead.lastStormDate) + '</td>' +
        '<td class="hidden lg:table-cell whitespace-nowrap">' + (lead.yearBuilt ? lead.yearBuilt : '<span class="text-slate-600 italic text-xs">—</span>') + '</td>' +
        '<td class="hidden xl:table-cell">' + em + '</td>' +
        '<td class="sticky-actions">' + ac + '</td></tr>';
}

function startEdit(id) { editingId = id; renderTable(); var i = document.getElementById('eName_' + id); if (i) i.focus(); }
function cancelEdit()  { editingId = null; renderTable(); }

async function saveOwner(id) {
    var name  = (document.getElementById('eName_'  + id) || {}).value || null;
    var phone = (document.getElementById('ePhone_' + id) || {}).value || null;
    var email = (document.getElementById('eEmail_' + id) || {}).value || null;
    try {
        var resp = await fetch('/Leads/' + id + '/Owner', { method: 'PATCH', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ownerName: name, ownerPhone: phone, ownerEmail: email }) });
        if (!resp.ok) throw new Error('HTTP ' + resp.status);
        var lead = allLeads.find(function(l) { return l.id === id; });
        if (lead) { lead.ownerName = name; lead.ownerPhone = phone; lead.ownerEmail = email; }
        editingId = null; renderTable(); showToast('Owner info saved', true);
    } catch (e) { showToast('Save failed: ' + e.message, false); }
}

async function deleteLead(id, btn) {
    if (!confirm('Delete this lead? This cannot be undone.')) return;
    btn.disabled = true;
    try {
        var resp = await fetch('/Leads/' + id, { method: 'DELETE' });
        if (resp.status === 204) { allLeads = allLeads.filter(function(l) { return l.id !== id; }); updateFilterCounts(); renderTable(); showToast('Lead deleted', true); }
        else throw new Error('HTTP ' + resp.status);
    } catch (e) { btn.disabled = false; showToast('Delete failed: ' + e.message, false); }
}

function exportCSV() {
    var cols   = ['address','riskLevel','hailSize','lastStormDate','yearBuilt','ownerName','ownerPhone','ownerEmail','sourceAddress','savedAt'];
    var header = ['Address','Risk Level','Hail Size','Last Storm Date','Year Built','Owner Name','Owner Phone','Owner Email','Source Address','Saved At'];
    var csv = [header.join(',')].concat(allLeads.map(function(l) { return cols.map(function(c) { return '"' + (l[c] || '').toString().replace(/"/g, '""') + '"'; }).join(','); })).join('\n');
    var a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob([csv], { type: 'text/csv' }));
    a.download = 'StormLeads_' + new Date().toISOString().slice(0,10) + '.csv';
    a.click();
}

// ── Per-row contact enrichment (BatchSkipTracing) ─────────────────
async function enrichLead(id, btn) {
    btn.disabled = true;
    var origHtml = btn.innerHTML;
    btn.innerHTML = '<i class="fa-solid fa-spinner fa-spin text-xs"></i>';
    try {
        var resp = await fetch('/Leads/' + id + '/Enrich', { method: 'POST' });
        var r    = await resp.json();
        if (!resp.ok) throw new Error(r.error || 'HTTP ' + resp.status);

        var lead = allLeads.find(function(l) { return l.id === id; });
        if (r.status === 'completed' && lead) {
            if (r.ownerName)  lead.ownerName  = r.ownerName;
            if (r.ownerPhone) lead.ownerPhone = r.ownerPhone;
            if (r.ownerEmail) lead.ownerEmail = r.ownerEmail;
            if (r.yearBuilt)  lead.yearBuilt  = r.yearBuilt;
            renderTable();
            var found = [r.ownerName, r.yearBuilt ? 'built ' + r.yearBuilt : null].filter(Boolean).join(' · ');
            showToast(found ? 'Found: ' + found : 'Parcel found — no additional data returned', true);
        } else {
            showToast('No parcel data found for this address', false);
        }
    } catch (e) { showToast('Enrichment failed: ' + e.message, false); }
    finally { btn.disabled = false; btn.innerHTML = origHtml; }
}

function setLoading(on) {
    document.getElementById('leadsLoading').classList.toggle('hidden', !on);
    if (on) {
        document.getElementById('leadsBody').innerHTML = '';
        var cards = document.getElementById('mobileCards');
        if (cards) cards.innerHTML = '';
    }
}

function escapeHtml(s) {
    if (s === null || s === undefined) return '';
    return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}
function escapeAttr(s) { return escapeHtml(s); }

// ── Mobile card view ─────────────────────────────────────────────
function buildMobileCard(lead) {
    const rc = ({ High:'badge-high', Medium:'badge-medium', Low:'badge-low' }[lead.riskLevel]) || 'badge-low';
    const ed = editingId === lead.id;

    if (ed) {
        // Edit mode card
        return '<div class="bg-slate-800 border border-brand/40 rounded-2xl p-4 shadow-md" data-lead-id="' + lead.id + '">' +
            '<p class="text-white font-semibold text-sm mb-3 truncate">' + escapeHtml(lead.address) + '</p>' +
            '<div class="space-y-2 mb-3">' +
            '<input class="owner-input w-full" id="eName_' + lead.id + '" value="' + escapeAttr(lead.ownerName || '') + '" placeholder="Owner name…" />' +
            '<input class="owner-input w-full" id="ePhone_' + lead.id + '" value="' + escapeAttr(lead.ownerPhone || '') + '" placeholder="(555) 000-0000" />' +
            '<input class="owner-input w-full" id="eEmail_' + lead.id + '" value="' + escapeAttr(lead.ownerEmail || '') + '" placeholder="owner@example.com" />' +
            '</div>' +
            '<div class="flex gap-2">' +
            '<button onclick="saveOwner(' + lead.id + ')" class="flex-1 py-2.5 rounded-xl bg-green-500/20 border border-green-500/30 text-green-400 text-sm font-semibold hover:bg-green-500/30 transition">' +
            '<i class="fa-solid fa-check mr-1"></i>Save</button>' +
            '<button onclick="cancelEdit()" class="py-2.5 px-4 rounded-xl bg-slate-700 border border-slate-600 text-slate-300 text-sm font-semibold hover:bg-slate-600 transition">' +
            '<i class="fa-solid fa-xmark"></i></button>' +
            '</div></div>';
    }

    const phoneHtml = lead.ownerPhone
        ? '<a href="tel:' + escapeAttr(lead.ownerPhone) + '" class="flex-1 flex items-center justify-center gap-2 py-3 rounded-xl bg-green-500/15 border border-green-500/30 text-green-400 text-sm font-semibold active:bg-green-500/30 transition">' +
          '<i class="fa-solid fa-phone"></i>' + escapeHtml(lead.ownerPhone) + '</a>'
        : '<span class="flex-1 flex items-center justify-center gap-2 py-3 rounded-xl bg-slate-700/40 border border-slate-600/60 text-slate-500 text-sm">' +
          '<i class="fa-solid fa-phone-slash"></i>No phone — tap Enrich</span>';

    const hailBit  = (lead.hailSize && lead.hailSize !== 'No data')
        ? '<span><i class="fa-solid fa-cloud-bolt mr-1 text-orange-400"></i>' + escapeHtml(lead.hailSize) + '"</span>' : '';
    const dateBit  = (lead.lastStormDate && lead.lastStormDate !== 'No data')
        ? '<span><i class="fa-solid fa-calendar mr-1 text-slate-500"></i>' + escapeHtml(lead.lastStormDate) + '</span>' : '';
    const yearBit  = lead.yearBuilt
        ? '<span><i class="fa-solid fa-house mr-1 text-slate-500"></i>Built ' + lead.yearBuilt + '</span>' : '';

    return '<div class="bg-slate-800 border border-slate-700/60 rounded-2xl p-4 shadow-md" data-lead-id="' + lead.id + '">' +
        '<div class="flex items-start justify-between gap-2 mb-1">' +
        '<div class="flex-1 min-w-0">' +
        '<p class="font-semibold text-white text-sm leading-tight">' + escapeHtml(lead.address) + '</p>' +
        (lead.ownerName ? '<p class="text-xs text-slate-400 mt-0.5"><i class="fa-solid fa-user mr-1"></i>' + escapeHtml(lead.ownerName) + '</p>' : '') +
        '</div>' +
        '<span class="' + rc + ' px-2.5 py-0.5 rounded-full text-xs font-bold flex-shrink-0 ml-2">' + escapeHtml(lead.riskLevel) + '</span>' +
        '</div>' +
        ((hailBit || dateBit || yearBit)
            ? '<div class="flex items-center gap-3 text-xs text-slate-400 mb-3 mt-2 flex-wrap">' + hailBit + dateBit + yearBit + '</div>'
            : '<div class="mb-3"></div>') +
        '<div class="flex gap-2 mb-3">' + phoneHtml + '</div>' +
        '<div class="flex items-center gap-2">' +
        '<button onclick="enrichLead(' + lead.id + ', this)" class="flex-1 py-2 rounded-xl bg-orange-500/10 border border-orange-500/20 text-orange-400 text-xs font-semibold hover:bg-orange-500/20 active:bg-orange-500/30 transition">' +
        '<i class="fa-solid fa-bolt mr-1"></i>Enrich</button>' +
        '<button onclick="startEdit(' + lead.id + ')" class="flex-1 py-2 rounded-xl bg-slate-700 border border-slate-600 text-slate-300 text-xs font-semibold hover:bg-slate-600 transition">' +
        '<i class="fa-solid fa-pen mr-1"></i>Edit</button>' +
        '<button onclick="deleteLead(' + lead.id + ', this)" class="py-2 px-3.5 rounded-xl bg-red-500/10 border border-red-500/20 text-red-400 text-xs font-semibold hover:bg-red-500/20 active:bg-red-500/30 transition">' +
        '<i class="fa-solid fa-trash-can"></i></button>' +
        '</div></div>';
}

// ── Mobile nav hamburger ──────────────────────────────────────────
function toggleMobileMenu() {
    var menu = document.getElementById('mobileMenu');
    var icon = document.getElementById('mobileMenuIcon');
    if (!menu) return;
    var nowHidden = menu.classList.toggle('hidden');
    icon.className = nowHidden ? 'fa-solid fa-bars text-lg' : 'fa-solid fa-xmark text-lg';
}

var _toastTimer = null;
function showToast(msg, success) {
    var toast = document.getElementById('toast');
    if (_toastTimer) clearTimeout(_toastTimer);
    toast.className = success ? 'success' : 'error';
    document.getElementById('toastIcon').className = 'fa-solid ' + (success ? 'fa-circle-check' : 'fa-circle-xmark');
    document.getElementById('toastMsg').textContent = msg;
    toast.offsetHeight;
    toast.classList.add('show');
    _toastTimer = setTimeout(function() { toast.classList.remove('show'); }, 3500);
}
