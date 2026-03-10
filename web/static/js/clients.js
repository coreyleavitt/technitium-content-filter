const pageData = JSON.parse(document.getElementById('page-data').textContent);
const clients = pageData.clients;
const profileNames = pageData.profileNames;

function renderClients() {
    const container = document.getElementById('clientsList');
    if (clients.length === 0) {
        container.innerHTML = '<div class="text-center py-12 bg-white rounded-lg shadow"><p class="text-gray-500">No clients configured. Add a device to assign it a filtering profile.</p></div>';
        return;
    }

    let rows = '';
    for (let i = 0; i < clients.length; i++) {
        const client = clients[i];
        const ids = (client.ids || []).join(', ');
        const hasProfile = client.profile && profileNames.includes(client.profile);
        let profileBadge;
        if (hasProfile) {
            profileBadge = '<span class="inline-flex items-center rounded-full bg-indigo-100 px-2.5 py-0.5 text-xs font-medium text-indigo-800">' + escapeHtml(client.profile) + '</span>';
        } else if (client.profile) {
            profileBadge = '<span class="inline-flex items-center rounded-full bg-red-100 px-2.5 py-0.5 text-xs font-medium text-red-800">' + escapeHtml(client.profile) + ' (missing)</span>';
        } else {
            profileBadge = '<span class="text-sm text-gray-400">Unassigned</span>';
        }

        const idBadges = (client.ids || []).map(id => {
            const isCidr = id.includes('/');
            const isIp = /^\d{1,3}(\.\d{1,3}){3}$/.test(id) || id.includes(':') && !id.includes('.');
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

        rows += '<tr>' +
            '<td class="px-6 py-4 whitespace-nowrap text-sm font-medium text-gray-900">' + escapeHtml(client.name || 'Unnamed') + '</td>' +
            '<td class="px-6 py-4"><div class="flex flex-wrap gap-1">' + (idBadges || '<span class="text-sm text-gray-400">None</span>') + '</div></td>' +
            '<td class="px-6 py-4 whitespace-nowrap">' + profileBadge + '</td>' +
            '<td class="px-6 py-4 whitespace-nowrap text-right text-sm">' +
            '<button onclick="openClientModal(' + i + ')" class="text-indigo-600 hover:text-indigo-500">Edit</button>' +
            '<button onclick="deleteClient(' + i + ')" class="ml-3 text-red-600 hover:text-red-500">Delete</button>' +
            '</td></tr>';
    }

    container.innerHTML = '<div class="bg-white shadow rounded-lg overflow-hidden">' +
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
    const sel = document.getElementById('clientProfile');
    sel.innerHTML = '<option value="">No profile</option>';
    profileNames.forEach(p => {
        const opt = document.createElement('option');
        opt.value = p;
        opt.textContent = p;
        if (p === selected) opt.selected = true;
        sel.appendChild(opt);
    });
}

function openClientModal(index) {
    const client = (index !== undefined) ? clients[index] : null;
    document.getElementById('clientModalTitle').textContent = client ? 'Edit Client' : 'Add Client';
    document.getElementById('editIndex').value = (index !== undefined) ? index : '';
    document.getElementById('clientName').value = client?.name || '';
    document.getElementById('clientIds').value = (client?.ids || []).join('\n');
    populateProfileSelect(client?.profile || '');
    document.getElementById('clientModal').classList.remove('hidden');
}

function closeClientModal() {
    document.getElementById('clientModal').classList.add('hidden');
}

async function deleteClient(index) {
    const client = clients[index];
    const label = client.name || (client.ids || []).join(', ') || 'this client';
    if (!confirm('Remove ' + label + '?')) return;
    await apiCall('DELETE', '/api/clients', { index });
    location.reload();
}

document.getElementById('clientForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    const editIndex = document.getElementById('editIndex').value;
    const idsText = document.getElementById('clientIds').value.trim();
    const ids = idsText.split('\n').map(s => s.trim()).filter(Boolean);
    if (ids.length === 0) return;

    const payload = {
        name: document.getElementById('clientName').value.trim(),
        ids: ids,
        profile: document.getElementById('clientProfile').value,
    };

    if (editIndex !== '') {
        payload.index = parseInt(editIndex, 10);
    }

    await apiCall('POST', '/api/clients', payload);
    location.reload();
});
