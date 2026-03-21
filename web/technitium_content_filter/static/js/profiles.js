const pageData = JSON.parse(document.getElementById('page-data').textContent);
const profiles = pageData.profiles;
const services = pageData.services;

const profilesListEl = document.getElementById('profilesList');
const createModalEl = document.getElementById('createModal');
const createFormEl = document.getElementById('createForm');
const profileNameEl = document.getElementById('profileName');
const profileDescEl = document.getElementById('profileDesc');

function renderProfiles() {
    const entries = Object.entries(profiles);
    if (entries.length === 0) {
        profilesListEl.innerHTML = '<div class="text-center py-12 bg-white rounded-lg shadow"><p class="text-gray-500">No profiles yet. Create one to get started.</p></div>';
        return;
    }

    let html = '<div class="grid grid-cols-1 gap-4 lg:grid-cols-2">';
    for (const [name, profile] of entries) {
        const blocked = (profile.blockedServices || []).map(function (id) {
            const svc = services[id];
            return '<span class="inline-flex items-center rounded-full bg-red-100 px-2.5 py-0.5 text-xs font-medium text-red-800">' + escapeHtml(svc ? svc.name : id) + '</span>';
        }).join('') || '<span class="text-sm text-gray-400">No services blocked</span>';

        const allowCount = (profile.allowList || []).filter(function (x) { return x.trim(); }).length;
        const allowBadge = allowCount > 0
            ? '<span class="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800">' + allowCount + ' allowed</span>'
            : '';

        const customCount = (profile.customRules || []).filter(function (x) { return x.trim() && !x.trim().startsWith('#'); }).length;
        const customBadge = customCount > 0
            ? '<span class="inline-flex items-center rounded-full bg-blue-100 px-2.5 py-0.5 text-xs font-medium text-blue-800">' + customCount + ' custom rules</span>'
            : '';

        const blCount = (profile.blockLists || []).length;
        const blBadge = blCount > 0
            ? '<span class="inline-flex items-center rounded-full bg-orange-100 px-2.5 py-0.5 text-xs font-medium text-orange-800">' + blCount + ' blocklist' + (blCount > 1 ? 's' : '') + '</span>'
            : '';

        const rwCount = (profile.dnsRewrites || []).length;
        const rwBadge = rwCount > 0
            ? '<span class="inline-flex items-center rounded-full bg-purple-100 px-2.5 py-0.5 text-xs font-medium text-purple-800">' + rwCount + ' rewrite' + (rwCount > 1 ? 's' : '') + '</span>'
            : '';

        const desc = profile.description ? '<p class="text-sm text-gray-500 mt-0.5">' + escapeHtml(profile.description) + '</p>' : '';
        const profileUrl = BASE_PATH + '/profiles/' + encodeURIComponent(name);

        html += '<a href="' + escapeHtml(profileUrl) + '" class="block bg-white shadow rounded-lg p-6 hover:shadow-md transition-shadow">' +
            '<div class="mb-3"><h3 class="text-lg font-semibold text-gray-900">' + escapeHtml(name) + '</h3>' + desc + '</div>' +
            '<div class="mb-2"><div class="flex flex-wrap gap-2">' + blocked + '</div></div>' +
            (allowBadge || customBadge || blBadge || rwBadge ? '<div class="flex flex-wrap gap-2">' + allowBadge + customBadge + blBadge + rwBadge + '</div>' : '') +
            '</a>';
    }
    html += '</div>';
    profilesListEl.innerHTML = html;
}
renderProfiles();

function openCreateModal() {
    profileNameEl.value = '';
    profileDescEl.value = '';
    createModalEl.classList.remove('hidden');
}

function closeCreateModal() {
    createModalEl.classList.add('hidden');
}

createFormEl.addEventListener('submit', async function handleCreate(e) {
    e.preventDefault();
    const name = profileNameEl.value.trim();
    if (!name) {
        showToast('Profile name is required.', 'error');
        return;
    }

    const submitBtn = createFormEl.querySelector('button[type="submit"]');
    setButtonLoading(submitBtn, true, 'Creating...');
    try {
        await apiCall('POST', API_PATHS.profiles, {
            name,
            description: profileDescEl.value,
        });
        window.location.href = BASE_PATH + '/profiles/' + encodeURIComponent(name);
    } catch (err) {
        // error shown by apiCall
    } finally {
        setButtonLoading(submitBtn, false, 'Create');
    }
});
