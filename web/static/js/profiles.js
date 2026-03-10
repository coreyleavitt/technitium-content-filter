const pageData = JSON.parse(document.getElementById('page-data').textContent);
const profiles = pageData.profiles;
const services = pageData.services;
const scheduleAllDay = pageData.scheduleAllDay ?? true;
const globalBlockLists = pageData.blockLists || [];
const DAYS = ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'];

// Build service checkboxes
const svcContainer = document.getElementById('servicesCheckboxes');
for (const [id, svc] of Object.entries(services)) {
    const label = document.createElement('label');
    label.className = 'flex items-center space-x-2';
    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.name = 'blockedServices';
    cb.value = id;
    cb.className = 'rounded border-gray-300 text-indigo-600 focus:ring-indigo-500';
    const span = document.createElement('span');
    span.className = 'text-sm text-gray-700';
    span.textContent = svc.name;
    label.appendChild(cb);
    label.appendChild(span);
    svcContainer.appendChild(label);
}

// Build blocklist checkboxes
const blContainer = document.getElementById('blocklistCheckboxes');
if (globalBlockLists.length === 0) {
    blContainer.innerHTML = '<p class="text-xs text-gray-400">No global blocklists configured yet.</p>';
} else {
    for (const bl of globalBlockLists) {
        const label = document.createElement('label');
        label.className = 'flex items-center space-x-2';
        const cb = document.createElement('input');
        cb.type = 'checkbox';
        cb.name = 'profileBlockLists';
        cb.value = bl.url;
        cb.className = 'rounded border-gray-300 text-indigo-600 focus:ring-indigo-500';
        const nameSpan = document.createElement('span');
        nameSpan.className = 'text-sm text-gray-700';
        nameSpan.textContent = bl.name || bl.url;
        label.appendChild(cb);
        label.appendChild(nameSpan);
        if (!bl.enabled) {
            const disabledSpan = document.createElement('span');
            disabledSpan.className = 'text-xs text-gray-400';
            disabledSpan.textContent = '(disabled)';
            label.appendChild(disabledSpan);
        }
        blContainer.appendChild(label);
    }
}

// Build schedule day rows
const daysContainer = document.getElementById('scheduleDays');
for (const day of DAYS) {
    const row = document.createElement('div');
    row.className = 'flex items-center space-x-2';
    row.innerHTML = `<label class="flex items-center space-x-1 w-16">
            <input type="checkbox" class="day-toggle rounded border-gray-300 text-indigo-600" data-day="${day}">
            <span class="text-sm">${day.charAt(0).toUpperCase() + day.slice(1)}</span>
        </label>
        <span class="day-time-inputs ${scheduleAllDay ? 'hidden' : ''}" data-day="${day}">
            <input type="time" class="day-start border border-gray-300 rounded px-2 py-1 text-sm" data-day="${day}" value="08:00">
            <span class="text-sm text-gray-500">to</span>
            <input type="time" class="day-end border border-gray-300 rounded px-2 py-1 text-sm" data-day="${day}" value="20:00">
        </span>`;
    daysContainer.appendChild(row);
}

// Render profiles list
function renderProfiles() {
    const container = document.getElementById('profilesList');
    const entries = Object.entries(profiles);
    if (entries.length === 0) {
        container.innerHTML = '<div class="text-center py-12 bg-white rounded-lg shadow"><p class="text-gray-500">No profiles yet. Create one to get started.</p></div>';
        return;
    }

    let html = '<div class="grid grid-cols-1 gap-6 lg:grid-cols-2">';
    for (const [name, profile] of entries) {
        const blocked = (profile.blockedServices || []).map(id => {
            const svc = services[id];
            return '<span class="inline-flex items-center rounded-full bg-red-100 px-2.5 py-0.5 text-xs font-medium text-red-800">' + escapeHtml(svc ? svc.name : id) + '</span>';
        }).join('') || '<span class="text-sm text-gray-400">None</span>';

        // Counts
        const allowCount = (profile.allowList || []).filter(x => x.trim()).length;
        const allowBadge = allowCount > 0
            ? '<span class="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800">' + allowCount + ' allowed</span>'
            : '';

        const customCount = (profile.customRules || []).filter(x => x.trim() && !x.trim().startsWith('#')).length;
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

        let scheduleHtml = '';
        if (profile.schedule && Object.keys(profile.schedule).length > 0) {
            scheduleHtml = '<div class="mt-4"><h4 class="text-sm font-medium text-gray-700 mb-2">Blocking Schedule</h4><div class="grid grid-cols-7 gap-1 text-center text-xs">';
            for (const day of DAYS) {
                const s = profile.schedule[day];
                const cls = s ? 'bg-red-100 text-red-800' : 'bg-gray-100 text-gray-400';
                const label = day.charAt(0).toUpperCase() + day.slice(1);
                const timeRange = s ? (scheduleAllDay ? '<div>24hr</div>' : '<div>' + s.start + '-' + s.end + '</div>') : '';
                scheduleHtml += '<div class="' + cls + ' rounded px-1 py-1"><div class="font-medium">' + label + '</div>' + timeRange + '</div>';
            }
            scheduleHtml += '</div></div>';
        }

        const desc = profile.description ? '<p class="text-sm text-gray-500">' + escapeHtml(profile.description) + '</p>' : '';
        const escapedName = escapeHtml(name);
        html += '<div class="bg-white shadow rounded-lg p-6">' +
            '<div class="flex justify-between items-start mb-4"><div>' +
            '<h3 class="text-lg font-semibold text-gray-900">' + escapedName + '</h3>' + desc +
            '</div><div class="flex space-x-2">' +
            '<button data-edit-profile="' + escapeHtml(name) + '" class="text-sm text-indigo-600 hover:text-indigo-500">Edit</button>' +
            '<button data-delete-profile="' + escapeHtml(name) + '" class="text-sm text-red-600 hover:text-red-500">Delete</button>' +
            '</div></div>' +
            '<div><h4 class="text-sm font-medium text-gray-700 mb-2">Blocked Services</h4>' +
            '<div class="flex flex-wrap gap-2">' + blocked + '</div></div>' +
            (allowBadge || customBadge || blBadge || rwBadge ? '<div class="mt-3 flex flex-wrap gap-2">' + allowBadge + customBadge + blBadge + rwBadge + '</div>' : '') +
            scheduleHtml + '</div>';
    }
    html += '</div>';
    container.innerHTML = html;
}
renderProfiles();

document.getElementById('profilesList').addEventListener('click', (e) => {
    const editBtn = e.target.closest('[data-edit-profile]');
    if (editBtn) { openProfileModal(editBtn.dataset.editProfile); return; }
    const deleteBtn = e.target.closest('[data-delete-profile]');
    if (deleteBtn) { deleteProfile(deleteBtn.dataset.deleteProfile); }
});

function openProfileModal(name) {
    const profile = name ? profiles[name] : null;
    document.getElementById('modalTitle').textContent = name ? 'Edit Profile' : 'Add Profile';
    document.getElementById('originalName').value = name || '';
    document.getElementById('profileName').value = name || '';
    document.getElementById('profileDesc').value = profile?.description || '';

    document.querySelectorAll('input[name="blockedServices"]').forEach(cb => {
        cb.checked = profile?.blockedServices?.includes(cb.value) || false;
    });

    // Blocklist checkboxes
    const profileBls = profile?.blockLists || [];
    document.querySelectorAll('input[name="profileBlockLists"]').forEach(cb => {
        cb.checked = profileBls.includes(cb.value);
    });

    const hasSchedule = profile?.schedule && Object.keys(profile.schedule).length > 0;
    document.getElementById('enableSchedule').checked = hasSchedule;
    document.getElementById('scheduleGrid').classList.toggle('hidden', !hasSchedule);
    document.querySelectorAll('.day-toggle').forEach(cb => {
        const day = cb.dataset.day;
        const sched = profile?.schedule?.[day];
        cb.checked = !!sched;
        document.querySelector('.day-start[data-day="' + day + '"]').value = sched ? (sched.start || '08:00') : '08:00';
        document.querySelector('.day-end[data-day="' + day + '"]').value = sched ? (sched.end || '20:00') : '20:00';
    });

    document.getElementById('profileModal').classList.remove('hidden');
}

function closeProfileModal() {
    document.getElementById('profileModal').classList.add('hidden');
}

async function deleteProfile(name) {
    if (!confirm('Delete profile "' + name + '"? Clients using it will become unassigned.')) return;
    await apiCall('DELETE', '/api/profiles', { name });
    location.reload();
}

document.getElementById('profileForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    const originalName = document.getElementById('originalName').value;
    const name = document.getElementById('profileName').value.trim();
    if (!name) return;

    const blockedServices = Array.from(
        document.querySelectorAll('input[name="blockedServices"]:checked')
    ).map(cb => cb.value);

    const blockLists = Array.from(
        document.querySelectorAll('input[name="profileBlockLists"]:checked')
    ).map(cb => cb.value);

    // Preserve existing allowList, customRules, dnsRewrites from the profile data
    const existing = profiles[originalName] || {};

    let schedule = null;
    if (document.getElementById('enableSchedule').checked) {
        schedule = {};
        document.querySelectorAll('.day-toggle:checked').forEach(cb => {
            const day = cb.dataset.day;
            schedule[day] = {
                allDay: scheduleAllDay,
                start: scheduleAllDay ? '00:00' : document.querySelector('.day-start[data-day="' + day + '"]').value,
                end: scheduleAllDay ? '23:59:59' : document.querySelector('.day-end[data-day="' + day + '"]').value,
            };
        });
    }

    if (originalName && originalName !== name) {
        await apiCall('DELETE', '/api/profiles', { name: originalName });
    }

    await apiCall('POST', '/api/profiles', {
        name,
        description: document.getElementById('profileDesc').value,
        blockedServices,
        blockLists,
        allowList: existing.allowList || [],
        customRules: existing.customRules || [],
        dnsRewrites: existing.dnsRewrites || [],
        schedule,
    });
    location.reload();
});
