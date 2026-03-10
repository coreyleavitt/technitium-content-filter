const pageData = JSON.parse(document.getElementById('page-data').textContent);
let blockLists = pageData.blockLists || [];
const profiles = pageData.profiles || {};

function renderBlocklists() {
    const container = document.getElementById('blocklistsList');
    if (blockLists.length === 0) {
        container.innerHTML = '<div class="text-center py-12 bg-white rounded-lg shadow"><p class="text-gray-500">No blocklists configured. Add one to get started.</p></div>';
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
        const escapedUrl = bl.url.replace(/'/g, "\\'").replace(/"/g, '&quot;');

        // Count which profiles use this blocklist
        const usingProfiles = Object.entries(profiles)
            .filter(([, p]) => (p.blockLists || []).includes(bl.url))
            .map(([name]) => name);
        const profileBadges = usingProfiles.length > 0
            ? usingProfiles.map(n => '<span class="inline-flex items-center rounded-full bg-indigo-100 px-2 py-0.5 text-xs font-medium text-indigo-800">' + n + '</span>').join(' ')
            : '<span class="text-xs text-gray-400">None</span>';

        html += '<tr>' +
            '<td class="px-6 py-4 whitespace-nowrap text-sm font-medium text-gray-900">' + (bl.name || '--') + '</td>' +
            '<td class="px-6 py-4 text-xs font-mono text-gray-500 max-w-xs truncate">' + bl.url + '</td>' +
            '<td class="px-6 py-4 whitespace-nowrap text-xs ' + enabledClass + '">' + enabledText + '</td>' +
            '<td class="px-6 py-4"><div class="flex flex-wrap gap-1">' + profileBadges + '</div></td>' +
            '<td class="px-6 py-4 whitespace-nowrap text-right text-sm">' +
            '<button onclick="openBlocklistModal(\'' + escapedUrl + '\')" class="text-indigo-600 hover:text-indigo-500 mr-2">Edit</button>' +
            '<button onclick="deleteBlocklist(\'' + escapedUrl + '\')" class="text-red-600 hover:text-red-500">Delete</button>' +
            '</td></tr>';
    }

    html += '</tbody></table></div>';
    container.innerHTML = html;
}
renderBlocklists();

function openBlocklistModal(url) {
    const bl = url ? blockLists.find(b => b.url === url) : null;
    document.getElementById('blModalTitle').textContent = bl ? 'Edit Blocklist' : 'Add Blocklist';
    document.getElementById('originalUrl').value = url || '';
    document.getElementById('blName').value = bl?.name || '';
    document.getElementById('blUrl').value = bl?.url || '';
    document.getElementById('blUrl').readOnly = !!bl;
    document.getElementById('blEnabled').checked = bl?.enabled !== false;
    document.getElementById('blRefreshHours').value = bl?.refreshHours || 24;
    document.getElementById('blocklistModal').classList.remove('hidden');
}

function closeBlocklistModal() {
    document.getElementById('blocklistModal').classList.add('hidden');
}

async function deleteBlocklist(url) {
    if (!confirm('Remove this blocklist? It will also be unassigned from all profiles.')) return;
    await apiCall('DELETE', '/api/blocklists', { url });
    location.reload();
}

async function refreshBlocklists() {
    const btn = document.getElementById('refreshBtn');
    btn.textContent = 'Refreshing...';
    btn.disabled = true;
    await apiCall('POST', '/api/blocklists/refresh');
    btn.textContent = 'Refresh All';
    btn.disabled = false;
}

document.getElementById('blocklistForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    const url = document.getElementById('blUrl').value.trim();
    if (!url) return;

    await apiCall('POST', '/api/blocklists', {
        url,
        name: document.getElementById('blName').value.trim(),
        enabled: document.getElementById('blEnabled').checked,
        refreshHours: parseInt(document.getElementById('blRefreshHours').value) || 24,
    });
    location.reload();
});
