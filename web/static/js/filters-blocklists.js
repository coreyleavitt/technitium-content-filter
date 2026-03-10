const pageData = JSON.parse(document.getElementById('page-data').textContent);
let blockLists = pageData.blockLists || [];
const profiles = pageData.profiles || {};

// #82: Cache DOM elements
const blocklistsListEl = document.getElementById('blocklistsList');
const blocklistModalEl = document.getElementById('blocklistModal');
const blModalTitleEl = document.getElementById('blModalTitle');
const originalUrlEl = document.getElementById('originalUrl');
const blNameEl = document.getElementById('blName');
const blUrlEl = document.getElementById('blUrl');
const blEnabledEl = document.getElementById('blEnabled');
const blRefreshHoursEl = document.getElementById('blRefreshHours');
const blocklistFormEl = document.getElementById('blocklistForm');
const refreshBtnEl = document.getElementById('refreshBtn');

function renderBlocklists() {
    if (blockLists.length === 0) {
        blocklistsListEl.innerHTML = '<div class="text-center py-12 bg-white rounded-lg shadow"><p class="text-gray-500">No blocklists configured. Add one to get started.</p></div>';
        return;
    }

    let html = '<div class="bg-white shadow rounded-lg overflow-hidden"><table class="min-w-full divide-y divide-gray-200">' +
        '<thead class="bg-gray-50"><tr>' +
        '<th class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Name</th>' +
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

        // #85: Toggle button for enabled/disabled
        const toggleLabel = bl.enabled ? 'Disable' : 'Enable';
        html += '<tr>' +
            '<td class="px-6 py-4 whitespace-nowrap text-sm font-medium text-gray-900">' + escapeHtml(bl.name || '--') + '</td>' +
            '<td class="px-6 py-4 text-xs font-mono text-gray-500 max-w-xs truncate">' + escapeHtml(bl.url) + '</td>' +
            '<td class="px-6 py-4 whitespace-nowrap text-xs ' + enabledClass + '">' +
            '<button data-toggle-bl="' + escapeHtml(bl.url) + '" class="hover:underline">' + enabledText + '</button>' +
            '</td>' +
            '<td class="px-6 py-4"><div class="flex flex-wrap gap-1">' + profileBadges + '</div></td>' +
            '<td class="px-6 py-4 whitespace-nowrap text-right text-sm">' +
            '<button data-edit-bl="' + escapeHtml(bl.url) + '" class="text-indigo-600 hover:text-indigo-500 mr-2">Edit</button>' +
            '<button data-delete-bl="' + escapeHtml(bl.url) + '" class="text-red-600 hover:text-red-500">Delete</button>' +
            '</td></tr>';
    }

    html += '</tbody></table></div>';
    blocklistsListEl.innerHTML = html;
}
renderBlocklists();

// #89: Named event handler
function handleBlocklistClick(e) {
    const editBtn = e.target.closest('[data-edit-bl]');
    if (editBtn) { openBlocklistModal(editBtn.dataset.editBl); return; }
    const deleteBtn = e.target.closest('[data-delete-bl]');
    if (deleteBtn) { deleteBlocklist(deleteBtn.dataset.deleteBl); return; }
    // #85: Toggle blocklist enabled/disabled
    const toggleBtn = e.target.closest('[data-toggle-bl]');
    if (toggleBtn) { toggleBlocklist(toggleBtn.dataset.toggleBl); }
}
blocklistsListEl.addEventListener('click', handleBlocklistClick);

function openBlocklistModal(url) {
    const bl = url ? blockLists.find(function (b) { return b.url === url; }) : null;
    blModalTitleEl.textContent = bl ? 'Edit Blocklist' : 'Add Blocklist';
    originalUrlEl.value = url || '';
    blNameEl.value = bl?.name || '';
    blUrlEl.value = bl?.url || '';
    blUrlEl.readOnly = !!bl;
    blEnabledEl.checked = bl?.enabled !== false;
    blRefreshHoursEl.value = bl?.refreshHours || 24;
    blocklistModalEl.classList.remove('hidden');
}

function closeBlocklistModal() {
    blocklistModalEl.classList.add('hidden');
}

// #81: Confirmation before delete
async function deleteBlocklist(url) {
    if (!confirm('Remove this blocklist? It will also be unassigned from all profiles.')) return;
    try {
        await apiCall('DELETE', API_PATHS.blocklists, { url });
        location.reload();
    } catch (err) {
        // error shown by apiCall
    }
}

// #85: Toggle blocklist with toast feedback
async function toggleBlocklist(url) {
    const bl = blockLists.find(function (b) { return b.url === url; });
    if (!bl) return;
    const newEnabled = !bl.enabled;
    try {
        await apiCall('POST', API_PATHS.blocklists, {
            url: bl.url,
            name: bl.name,
            enabled: newEnabled,
            refreshHours: bl.refreshHours || 24,
        });
        bl.enabled = newEnabled;
        renderBlocklists();
        showToast('Blocklist ' + (newEnabled ? 'enabled' : 'disabled') + '.', 'success');
    } catch (err) {
        // error shown by apiCall
    }
}

// #84: Refresh button with finally block to always reset state
async function refreshBlocklists() {
    refreshBtnEl.textContent = 'Refreshing...';
    refreshBtnEl.disabled = true;
    try {
        await apiCall('POST', API_PATHS.blocklistsRefresh);
        showToast('Blocklists refreshed.', 'success');
    } catch (err) {
        // error shown by apiCall
    } finally {
        refreshBtnEl.textContent = 'Refresh All';
        refreshBtnEl.disabled = false;
    }
}

// #72: URL validation; #76: loading state
blocklistFormEl.addEventListener('submit', async function handleBlocklistSubmit(e) {
    e.preventDefault();
    const url = blUrlEl.value.trim();
    if (!url) {
        showToast('Blocklist URL is required.', 'error');
        return;
    }

    // #72: Basic URL validation
    try {
        new URL(url);
    } catch {
        showToast('Please enter a valid URL.', 'error');
        return;
    }

    const submitBtn = blocklistFormEl.querySelector('button[type="submit"]');
    setButtonLoading(submitBtn, true);
    try {
        await apiCall('POST', API_PATHS.blocklists, {
            url,
            name: blNameEl.value.trim(),
            enabled: blEnabledEl.checked,
            refreshHours: parseInt(blRefreshHoursEl.value) || 24,
        });
        location.reload();
    } catch (err) {
        // error shown by apiCall
    } finally {
        setButtonLoading(submitBtn, false, 'Save');
    }
});
