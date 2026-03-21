const pageData = JSON.parse(document.getElementById('page-data').textContent);
const cfg = pageData.config;
let blockLists = cfg.blockLists || [];
const profiles = cfg.profiles || {};
const builtinServices = pageData.builtinServices;
let customServices = pageData.customServices || {};

// --- Global Settings ---
const tzSelectEl = document.getElementById('timeZone');
const settingsFormEl = document.getElementById('settingsForm');

const effectiveTz = cfg.timeZone || 'UTC';
const commonTimezones = [
    'America/New_York', 'America/Chicago', 'America/Denver', 'America/Los_Angeles',
    'America/Phoenix', 'America/Anchorage', 'Pacific/Honolulu', 'America/Boise',
    'America/Detroit', 'America/Indiana/Indianapolis',
];
let allTz;
try { allTz = Intl.supportedValuesOf('timeZone'); } catch { allTz = commonTimezones; }
const seen = new Set();
for (const tz of [...commonTimezones, ...allTz]) {
    if (seen.has(tz)) continue;
    seen.add(tz);
    const opt = document.createElement('option');
    opt.value = tz;
    opt.textContent = tz.replace(/_/g, ' ');
    if (tz === effectiveTz) opt.selected = true;
    tzSelectEl.appendChild(opt);
}

settingsFormEl.addEventListener('submit', async function (e) {
    e.preventDefault();
    const submitBtn = settingsFormEl.querySelector('button[type="submit"]');
    setButtonLoading(submitBtn, true);
    const blockingAddresses = document.getElementById('globalBlockingAddresses').value
        .split('\n').map(function (s) { return s.trim(); }).filter(Boolean);
    try {
        await apiCall('POST', API_PATHS.settings, {
            enableBlocking: cfg.enableBlocking,
            timeZone: tzSelectEl.value,
            defaultProfile: document.getElementById('defaultProfile').value || null,
            baseProfile: document.getElementById('baseProfile').value || null,
            scheduleAllDay: document.getElementById('scheduleAllDay').checked,
            allowTxtBlockingReport: document.getElementById('allowTxtBlockingReport').checked,
            blockingAddresses: blockingAddresses.length > 0 ? blockingAddresses : [],
        });
        showToast('Settings saved.', 'success');
    } catch (err) {
        // error shown by apiCall
    } finally {
        setButtonLoading(submitBtn, false, 'Save Settings');
    }
});

// --- DNS Blocklists ---
const blocklistsListEl = document.getElementById('blocklistsList');
const blocklistModalEl = document.getElementById('blocklistModal');
const blModalTitleEl = document.getElementById('blModalTitle');
const originalUrlEl = document.getElementById('originalUrl');
const blNameEl = document.getElementById('blName');
const blUrlEl = document.getElementById('blUrl');
const blEnabledEl = document.getElementById('blEnabled');
const blRefreshHoursEl = document.getElementById('blRefreshHours');
const blTypeEl = document.getElementById('blType');
const blocklistFormEl = document.getElementById('blocklistForm');
const refreshBtnEl = document.getElementById('refreshBtn');

function renderBlocklists() {
    if (blockLists.length === 0) {
        blocklistsListEl.innerHTML = '<div class="text-center py-12"><p class="text-gray-500">No blocklists configured. Add one to get started.</p></div>';
        return;
    }

    let html = '<table class="min-w-full divide-y divide-gray-200">' +
        '<thead class="bg-gray-50"><tr>' +
        '<th class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Name</th>' +
        '<th class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Type</th>' +
        '<th class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">URL</th>' +
        '<th class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Status</th>' +
        '<th class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Profiles</th>' +
        '<th class="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Actions</th>' +
        '</tr></thead><tbody class="divide-y divide-gray-200">';

    for (const bl of blockLists) {
        const enabledClass = bl.enabled ? 'text-green-600' : 'text-gray-400';
        const enabledText = bl.enabled ? 'Enabled' : 'Disabled';
        const usingProfiles = Object.entries(profiles)
            .filter(function ([, p]) { return (p.blockLists || []).includes(bl.url); })
            .map(function ([name]) { return name; });
        const profileBadges = usingProfiles.length > 0
            ? usingProfiles.map(function (n) { return '<span class="inline-flex items-center rounded-full bg-indigo-100 px-2 py-0.5 text-xs font-medium text-indigo-800">' + escapeHtml(n) + '</span>'; }).join(' ')
            : '<span class="text-xs text-gray-400">None</span>';
        const blType = bl.type || 'domain';
        const typeBadgeClass = blType === 'regex' ? 'bg-purple-100 text-purple-800' : 'bg-green-100 text-green-800';
        const typeBadgeText = blType === 'regex' ? 'Regex' : 'Domain';
        html += '<tr>' +
            '<td class="px-6 py-4 whitespace-nowrap text-sm font-medium text-gray-900">' + escapeHtml(bl.name || '--') + '</td>' +
            '<td class="px-6 py-4 whitespace-nowrap"><span class="inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ' + typeBadgeClass + '">' + typeBadgeText + '</span></td>' +
            '<td class="px-6 py-4 text-xs font-mono text-gray-500 max-w-xs truncate">' + escapeHtml(bl.url) + '</td>' +
            '<td class="px-6 py-4 whitespace-nowrap text-xs ' + enabledClass + '"><button data-toggle-bl="' + escapeHtml(bl.url) + '" class="hover:underline">' + enabledText + '</button></td>' +
            '<td class="px-6 py-4"><div class="flex flex-wrap gap-1">' + profileBadges + '</div></td>' +
            '<td class="px-6 py-4 whitespace-nowrap text-right text-sm">' +
            '<button data-edit-bl="' + escapeHtml(bl.url) + '" class="text-indigo-600 hover:text-indigo-500 mr-2">Edit</button>' +
            '<button data-delete-bl="' + escapeHtml(bl.url) + '" class="text-red-600 hover:text-red-500">Delete</button></td></tr>';
    }
    html += '</tbody></table>';
    blocklistsListEl.innerHTML = html;
}
renderBlocklists();

blocklistsListEl.addEventListener('click', function (e) {
    const editBtn = e.target.closest('[data-edit-bl]');
    if (editBtn) { openBlocklistModal(editBtn.dataset.editBl); return; }
    const deleteBtn = e.target.closest('[data-delete-bl]');
    if (deleteBtn) { deleteBlocklist(deleteBtn.dataset.deleteBl); return; }
    const toggleBtn = e.target.closest('[data-toggle-bl]');
    if (toggleBtn) { toggleBlocklist(toggleBtn.dataset.toggleBl); }
});

function openBlocklistModal(url) {
    const bl = url ? blockLists.find(function (b) { return b.url === url; }) : null;
    blModalTitleEl.textContent = bl ? 'Edit Blocklist' : 'Add Blocklist';
    originalUrlEl.value = url || '';
    blNameEl.value = bl?.name || '';
    blUrlEl.value = bl?.url || '';
    blUrlEl.readOnly = !!bl;
    blTypeEl.value = bl?.type || 'domain';
    blTypeEl.disabled = !!bl;
    blEnabledEl.checked = bl?.enabled !== false;
    blRefreshHoursEl.value = bl?.refreshHours || 24;
    blocklistModalEl.classList.remove('hidden');
}

function closeBlocklistModal() {
    blocklistModalEl.classList.add('hidden');
}

async function deleteBlocklist(url) {
    if (!confirm('Remove this blocklist? It will also be unassigned from all profiles.')) return;
    try {
        await apiCall('DELETE', API_PATHS.blocklists, { url });
        location.reload();
    } catch (err) {}
}

async function toggleBlocklist(url) {
    const bl = blockLists.find(function (b) { return b.url === url; });
    if (!bl) return;
    const newEnabled = !bl.enabled;
    try {
        await apiCall('POST', API_PATHS.blocklists, {
            url: bl.url, name: bl.name, enabled: newEnabled, refreshHours: bl.refreshHours || 24, type: bl.type || 'domain',
        });
        bl.enabled = newEnabled;
        renderBlocklists();
        showToast('Blocklist ' + (newEnabled ? 'enabled' : 'disabled') + '.', 'success');
    } catch (err) {}
}

async function refreshBlocklists() {
    refreshBtnEl.textContent = 'Refreshing...';
    refreshBtnEl.disabled = true;
    try {
        await apiCall('POST', API_PATHS.blocklistsRefresh);
        showToast('Blocklists refreshed.', 'success');
    } catch (err) {
    } finally {
        refreshBtnEl.textContent = 'Refresh All';
        refreshBtnEl.disabled = false;
    }
}

blocklistFormEl.addEventListener('submit', async function (e) {
    e.preventDefault();
    const url = blUrlEl.value.trim();
    if (!url) { showToast('Blocklist URL is required.', 'error'); return; }
    try { new URL(url); } catch { showToast('Please enter a valid URL.', 'error'); return; }

    const submitBtn = blocklistFormEl.querySelector('button[type="submit"]');
    setButtonLoading(submitBtn, true);
    try {
        await apiCall('POST', API_PATHS.blocklists, {
            url, name: blNameEl.value.trim(), enabled: blEnabledEl.checked,
            refreshHours: parseInt(blRefreshHoursEl.value) || 24, type: blTypeEl.value,
        });
        location.reload();
    } catch (err) {
    } finally {
        setButtonLoading(submitBtn, false, 'Save');
    }
});

// --- Service Definitions ---
const customServicesListEl = document.getElementById('customServicesList');
const serviceModalEl = document.getElementById('serviceModal');
const serviceModalTitleEl = document.getElementById('serviceModalTitle');
const originalServiceIdEl = document.getElementById('originalServiceId');
const serviceIdEl = document.getElementById('serviceId');
const serviceNameEl = document.getElementById('serviceName');
const serviceDomainsEl = document.getElementById('serviceDomains');
const serviceFormEl = document.getElementById('serviceForm');

function renderCustomServices() {
    const entries = Object.entries(customServices);
    if (entries.length === 0) {
        customServicesListEl.innerHTML = '<div class="text-center py-8"><p class="text-gray-500">No custom services defined.</p></div>';
        return;
    }
    let html = '<div class="divide-y divide-gray-200">';
    for (const [id, svc] of entries.sort(function (a, b) { return a[1].name.localeCompare(b[1].name); })) {
        const domains = svc.domains || [];
        const domainPreview = domains.slice(0, 5).map(function (d) {
            return '<span class="text-xs font-mono text-gray-500 bg-gray-50 px-1 rounded">' + escapeHtml(d) + '</span>';
        }).join('');
        const more = domains.length > 5 ? '<span class="text-xs text-gray-400">+' + (domains.length - 5) + ' more</span>' : '';
        html += '<div class="py-4"><div class="flex justify-between items-start"><div>' +
            '<span class="text-sm font-medium text-gray-900">' + escapeHtml(svc.name) + '</span>' +
            '<span class="text-xs text-gray-400 ml-1 font-mono">(' + escapeHtml(id) + ')</span>' +
            '<span class="text-xs text-gray-400 ml-2">' + domains.length + ' domains</span>' +
            '</div><div class="flex space-x-2">' +
            '<button data-edit-svc="' + escapeHtml(id) + '" class="text-sm text-indigo-600 hover:text-indigo-500">Edit</button>' +
            '<button data-delete-svc="' + escapeHtml(id) + '" class="text-sm text-red-600 hover:text-red-500">Delete</button>' +
            '</div></div><div class="mt-1 flex flex-wrap gap-1">' + domainPreview + more + '</div></div>';
    }
    html += '</div>';
    customServicesListEl.innerHTML = html;
}
renderCustomServices();

customServicesListEl.addEventListener('click', function (e) {
    const editBtn = e.target.closest('[data-edit-svc]');
    if (editBtn) { openServiceModal(editBtn.dataset.editSvc); return; }
    const deleteBtn = e.target.closest('[data-delete-svc]');
    if (deleteBtn) { deleteService(deleteBtn.dataset.deleteSvc); }
});

function openServiceModal(id) {
    const svc = id ? customServices[id] : null;
    serviceModalTitleEl.textContent = id ? 'Edit Custom Service' : 'Add Custom Service';
    originalServiceIdEl.value = id || '';
    serviceIdEl.value = id || '';
    serviceIdEl.readOnly = !!id;
    serviceNameEl.value = svc?.name || '';
    serviceDomainsEl.value = (svc?.domains || []).join('\n');
    serviceModalEl.classList.remove('hidden');
}

function closeServiceModal() {
    serviceModalEl.classList.add('hidden');
}

async function deleteService(id) {
    if (!confirm('Delete custom service "' + id + '"?')) return;
    try {
        await apiCall('DELETE', API_PATHS.customServices, { id });
        location.reload();
    } catch (err) {}
}

serviceFormEl.addEventListener('submit', async function (e) {
    e.preventDefault();
    const originalId = originalServiceIdEl.value;
    const id = serviceIdEl.value.trim().toLowerCase();
    if (!id) { showToast('Service ID is required.', 'error'); return; }
    if (!originalId && builtinServices[id]) { showToast('Cannot use ID "' + id + '" -- it conflicts with a built-in service.', 'error'); return; }
    const name = serviceNameEl.value.trim();
    if (!name) { showToast('Service name is required.', 'error'); return; }
    const domains = serviceDomainsEl.value.split('\n').map(function (s) { return s.trim(); }).filter(Boolean);
    if (domains.length === 0) { showToast('At least one domain is required.', 'error'); return; }

    const submitBtn = serviceFormEl.querySelector('button[type="submit"]');
    setButtonLoading(submitBtn, true);
    try {
        if (originalId && originalId !== id) {
            await apiCall('DELETE', API_PATHS.customServices, { id: originalId });
        }
        await apiCall('POST', API_PATHS.customServices, { id, name, domains });
        location.reload();
    } catch (err) {
    } finally {
        setButtonLoading(submitBtn, false, 'Save');
    }
});
