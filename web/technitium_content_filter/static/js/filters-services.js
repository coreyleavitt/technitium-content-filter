const pageData = JSON.parse(document.getElementById('page-data').textContent);
const builtinServices = pageData.builtinServices;
let customServices = pageData.customServices || {};

// #82: Cache DOM elements
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
        customServicesListEl.innerHTML = '<div class="text-center py-8 bg-white rounded-lg shadow"><p class="text-gray-500">No custom services defined. Use the button above to add one.</p></div>';
        return;
    }

    let html = '<div class="bg-white shadow rounded-lg overflow-hidden"><div class="px-6 py-4 border-b border-gray-200"><h3 class="text-sm font-semibold text-gray-900">Custom Services (' + entries.length + ')</h3></div>';
    html += '<div class="divide-y divide-gray-200">';

    for (const [id, svc] of entries.sort(function (a, b) { return a[1].name.localeCompare(b[1].name); })) {
        const domains = svc.domains || [];
        const domainPreview = domains.slice(0, 5).map(function (d) {
            return '<span class="text-xs font-mono text-gray-500 bg-gray-50 px-1 rounded">' + escapeHtml(d) + '</span>';
        }).join('');
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
    customServicesListEl.innerHTML = html;
}
renderCustomServices();

// #89: Named handler
function handleServiceClick(e) {
    const editBtn = e.target.closest('[data-edit-svc]');
    if (editBtn) { openServiceModal(editBtn.dataset.editSvc); return; }
    const deleteBtn = e.target.closest('[data-delete-svc]');
    if (deleteBtn) { deleteService(deleteBtn.dataset.deleteSvc); }
}
customServicesListEl.addEventListener('click', handleServiceClick);

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

// #81: Confirmation already existed here
async function deleteService(id) {
    if (!confirm('Delete custom service "' + id + '"?')) return;
    try {
        await apiCall('DELETE', API_PATHS.customServices, { id });
        location.reload();
    } catch (err) {
        // error shown by apiCall
    }
}

// #72: Validation; #76: loading states
serviceFormEl.addEventListener('submit', async function handleServiceSubmit(e) {
    e.preventDefault();
    const originalId = originalServiceIdEl.value;
    const id = serviceIdEl.value.trim().toLowerCase();

    // #72: Validate required fields
    if (!id) {
        showToast('Service ID is required.', 'error');
        return;
    }

    if (!originalId && builtinServices[id]) {
        showToast('Cannot use ID "' + id + '" -- it conflicts with a built-in service.', 'error');
        return;
    }

    const name = serviceNameEl.value.trim();
    if (!name) {
        showToast('Service name is required.', 'error');
        return;
    }

    const domains = serviceDomainsEl.value
        .split('\n').map(function (s) { return s.trim(); }).filter(Boolean);
    if (domains.length === 0) {
        showToast('At least one domain is required.', 'error');
        return;
    }

    const submitBtn = serviceFormEl.querySelector('button[type="submit"]');
    setButtonLoading(submitBtn, true);
    try {
        if (originalId && originalId !== id) {
            await apiCall('DELETE', API_PATHS.customServices, { id: originalId });
        }
        await apiCall('POST', API_PATHS.customServices, { id, name, domains });
        location.reload();
    } catch (err) {
        // error shown by apiCall
    } finally {
        setButtonLoading(submitBtn, false, 'Save');
    }
});
