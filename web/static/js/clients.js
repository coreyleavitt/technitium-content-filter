const pageData = JSON.parse(document.getElementById('page-data').textContent);
const clients = pageData.clients;
const profileNames = pageData.profileNames;

// #82: Cache DOM elements
const clientsListEl = document.getElementById('clientsList');
const clientModalEl = document.getElementById('clientModal');
const clientModalTitleEl = document.getElementById('clientModalTitle');
const editIndexEl = document.getElementById('editIndex');
const clientNameEl = document.getElementById('clientName');
const clientIdsEl = document.getElementById('clientIds');
const clientProfileEl = document.getElementById('clientProfile');
const clientFormEl = document.getElementById('clientForm');

function renderClients() {
    if (clients.length === 0) {
        clientsListEl.innerHTML = '<div class="text-center py-12 bg-white rounded-lg shadow"><p class="text-gray-500">No clients configured. Add a device to assign it a filtering profile.</p></div>';
        return;
    }

    let rows = '';
    for (let i = 0; i < clients.length; i++) {
        const client = clients[i];
        const hasProfile = client.profile && profileNames.includes(client.profile);
        let profileBadge;
        if (hasProfile) {
            profileBadge = '<span class="inline-flex items-center rounded-full bg-indigo-100 px-2.5 py-0.5 text-xs font-medium text-indigo-800">' + escapeHtml(client.profile) + '</span>';
        } else if (client.profile) {
            profileBadge = '<span class="inline-flex items-center rounded-full bg-red-100 px-2.5 py-0.5 text-xs font-medium text-red-800">' + escapeHtml(client.profile) + ' (missing)</span>';
        } else {
            profileBadge = '<span class="text-sm text-gray-400">Unassigned</span>';
        }

        const idBadges = (client.ids || []).map(function (id) {
            const isCidr = id.includes('/');
            // #86: Proper IPv6 detection using colon check
            const isIpv4 = /^\d{1,3}(\.\d{1,3}){3}$/.test(id);
            const isIpv6 = /^[0-9a-fA-F:]+$/.test(id) && id.includes(':');
            const isIp = isIpv4 || isIpv6;
            let cls;
            if (isCidr) {
                cls = 'bg-amber-50 text-amber-700';
            } else if (isIp) {
                cls = 'bg-gray-100 text-gray-700';
            } else {
                cls = 'bg-blue-50 text-blue-700'; // Domain-based ClientID
            }
            return '<span class="inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium font-mono ' + cls + '">' + escapeHtml(id) + '</span>';
        }).join(' ');

        // #73: Use client name/IDs as identifier instead of array index
        const clientIdentifier = escapeHtml(client.name || (client.ids || []).join(','));
        rows += '<tr>' +
            '<td class="px-6 py-4 whitespace-nowrap text-sm font-medium text-gray-900">' + escapeHtml(client.name || 'Unnamed') + '</td>' +
            '<td class="px-6 py-4"><div class="flex flex-wrap gap-1">' + (idBadges || '<span class="text-sm text-gray-400">None</span>') + '</div></td>' +
            '<td class="px-6 py-4 whitespace-nowrap">' + profileBadge + '</td>' +
            '<td class="px-6 py-4 whitespace-nowrap text-right text-sm">' +
            '<button onclick="openClientModal(' + i + ')" class="text-indigo-600 hover:text-indigo-500">Edit</button>' +
            '<button onclick="deleteClient(' + i + ')" class="ml-3 text-red-600 hover:text-red-500">Delete</button>' +
            '</td></tr>';
    }

    clientsListEl.innerHTML = '<div class="bg-white shadow rounded-lg overflow-hidden">' +
        '<table class="min-w-full divide-y divide-gray-200">' +
        '<thead class="bg-gray-50"><tr>' +
        '<th class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Name</th>' +
        '<th class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Identifiers</th>' +
        '<th class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Profile</th>' +
        '<th class="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">Actions</th>' +
        '</tr></thead>' +
        '<tbody class="bg-white divide-y divide-gray-200">' + rows + '</tbody></table></div>';
}
renderClients();

function populateProfileSelect(selected) {
    clientProfileEl.innerHTML = '<option value="">No profile</option>';
    profileNames.forEach(function (p) {
        const opt = document.createElement('option');
        opt.value = p;
        opt.textContent = p;
        if (p === selected) opt.selected = true;
        clientProfileEl.appendChild(opt);
    });
}

function openClientModal(index) {
    const client = (index !== undefined) ? clients[index] : null;
    clientModalTitleEl.textContent = client ? 'Edit Client' : 'Add Client';
    editIndexEl.value = (index !== undefined) ? index : '';
    clientNameEl.value = client?.name || '';
    clientIdsEl.value = (client?.ids || []).join('\n');
    populateProfileSelect(client?.profile || '');
    clientModalEl.classList.remove('hidden');
}

function closeClientModal() {
    clientModalEl.classList.add('hidden');
}

// #73: Delete using client name/IDs identifier; #81: confirmation dialog
async function deleteClient(index) {
    const client = clients[index];
    const label = client.name || (client.ids || []).join(', ') || 'this client';
    if (!confirm('Remove ' + label + '?')) return;
    try {
        await apiCall('DELETE', API_PATHS.clients, { name: client.name, ids: client.ids });
        location.reload();
    } catch (err) {
        // error shown by apiCall
    }
}

// #72: Form validation; #76: Loading states; #78: Debounce via setButtonLoading
clientFormEl.addEventListener('submit', async function handleClientSubmit(e) {
    e.preventDefault();
    const editIndex = editIndexEl.value;
    const idsText = clientIdsEl.value.trim();
    const ids = idsText.split('\n').map(function (s) { return s.trim(); }).filter(Boolean);

    // #72: Validate required fields
    if (ids.length === 0) {
        showToast('At least one identifier (IP, CIDR, or client ID) is required.', 'error');
        return;
    }

    const submitBtn = clientFormEl.querySelector('button[type="submit"]');
    setButtonLoading(submitBtn, true);

    const payload = {
        name: clientNameEl.value.trim(),
        ids: ids,
        profile: clientProfileEl.value,
    };

    if (editIndex !== '') {
        payload.index = parseInt(editIndex, 10);
    }

    try {
        await apiCall('POST', API_PATHS.clients, payload);
        location.reload();
    } catch (err) {
        // error shown by apiCall
    } finally {
        setButtonLoading(submitBtn, false, 'Save');
    }
});
