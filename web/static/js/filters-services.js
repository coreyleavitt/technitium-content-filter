const pageData = JSON.parse(document.getElementById('page-data').textContent);
const builtinServices = pageData.builtinServices;
let customServices = pageData.customServices || {};

function renderCustomServices() {
    const container = document.getElementById('customServicesList');
    const entries = Object.entries(customServices);

    if (entries.length === 0) {
        container.innerHTML = '<div class="text-center py-8 bg-white rounded-lg shadow"><p class="text-gray-500">No custom services defined. Use the button above to add one.</p></div>';
        return;
    }

    let html = '<div class="bg-white shadow rounded-lg overflow-hidden"><div class="px-6 py-4 border-b border-gray-200"><h3 class="text-sm font-semibold text-gray-900">Custom Services (' + entries.length + ')</h3></div>';
    html += '<div class="divide-y divide-gray-200">';

    for (const [id, svc] of entries.sort((a, b) => a[1].name.localeCompare(b[1].name))) {
        const domains = svc.domains || [];
        const domainPreview = domains.slice(0, 5).map(d =>
            '<span class="text-xs font-mono text-gray-500 bg-gray-50 px-1 rounded">' + escapeHtml(d) + '</span>'
        ).join('');
        const more = domains.length > 5 ? '<span class="text-xs text-gray-400">+' + (domains.length - 5) + ' more</span>' : '';

        html += '<div class="px-6 py-4">' +
            '<div class="flex justify-between items-start">' +
            '<div>' +
            '<span class="text-sm font-medium text-gray-900">' + escapeHtml(svc.name) + '</span>' +
            '<span class="text-xs text-gray-400 ml-1 font-mono">(' + escapeHtml(id) + ')</span>' +
            '<span class="text-xs text-gray-400 ml-2">' + domains.length + ' domains</span>' +
            '</div>' +
            '<div class="flex space-x-2">' +
            '<button data-edit-svc="' + escapeHtml(id) + '" class="text-sm text-indigo-600 hover:text-indigo-500">Edit</button>' +
            '<button data-delete-svc="' + escapeHtml(id) + '" class="text-sm text-red-600 hover:text-red-500">Delete</button>' +
            '</div></div>' +
            '<div class="mt-1 flex flex-wrap gap-1">' + domainPreview + more + '</div>' +
            '</div>';
    }

    html += '</div></div>';
    container.innerHTML = html;
}
renderCustomServices();

document.getElementById('customServicesList').addEventListener('click', (e) => {
    const editBtn = e.target.closest('[data-edit-svc]');
    if (editBtn) { openServiceModal(editBtn.dataset.editSvc); return; }
    const deleteBtn = e.target.closest('[data-delete-svc]');
    if (deleteBtn) { deleteService(deleteBtn.dataset.deleteSvc); }
});

function openServiceModal(id) {
    const svc = id ? customServices[id] : null;
    document.getElementById('serviceModalTitle').textContent = id ? 'Edit Custom Service' : 'Add Custom Service';
    document.getElementById('originalServiceId').value = id || '';
    document.getElementById('serviceId').value = id || '';
    document.getElementById('serviceId').readOnly = !!id;
    document.getElementById('serviceName').value = svc?.name || '';
    document.getElementById('serviceDomains').value = (svc?.domains || []).join('\n');
    document.getElementById('serviceModal').classList.remove('hidden');
}

function closeServiceModal() {
    document.getElementById('serviceModal').classList.add('hidden');
}

async function deleteService(id) {
    if (!confirm('Delete custom service "' + id + '"?')) return;
    await apiCall('DELETE', '/api/custom-services', { id });
    location.reload();
}

document.getElementById('serviceForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    const originalId = document.getElementById('originalServiceId').value;
    const id = document.getElementById('serviceId').value.trim().toLowerCase();
    if (!id) return;

    if (!originalId && builtinServices[id]) {
        alert('Cannot use ID "' + id + '" -- it conflicts with a built-in service.');
        return;
    }

    const name = document.getElementById('serviceName').value.trim();
    const domains = document.getElementById('serviceDomains').value
        .split('\n').map(s => s.trim()).filter(Boolean);

    if (originalId && originalId !== id) {
        await apiCall('DELETE', '/api/custom-services', { id: originalId });
    }

    await apiCall('POST', '/api/custom-services', { id, name, domains });
    location.reload();
});
