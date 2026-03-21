const pageData = JSON.parse(document.getElementById('page-data').textContent);
const profileName = pageData.profileName;
const profile = pageData.profile;
const services = pageData.services;
const scheduleAllDay = pageData.scheduleAllDay ?? true;
const globalBlockLists = pageData.blockLists || [];
const DAYS = ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'];

// --- Tab switching ---
function switchTab(tabName) {
    document.querySelectorAll('.tab-panel').forEach(function (el) { el.classList.add('hidden'); });
    document.querySelectorAll('.tab-link').forEach(function (el) {
        el.className = 'tab-link border-transparent text-gray-500 hover:border-gray-300 hover:text-gray-700 whitespace-nowrap border-b-2 py-4 px-1 text-sm font-medium';
    });
    const panel = document.getElementById('tab-' + tabName);
    if (panel) panel.classList.remove('hidden');
    const link = document.querySelector('[data-tab="' + tabName + '"]');
    if (link) link.className = 'tab-link border-indigo-500 text-indigo-600 whitespace-nowrap border-b-2 py-4 px-1 text-sm font-medium';
}

function getTabFromHash() {
    const hash = location.hash.slice(1);
    return ['overview', 'allowlist', 'rules', 'regex', 'rewrites', 'test'].includes(hash) ? hash : 'overview';
}

switchTab(getTabFromHash());
window.addEventListener('hashchange', function () { switchTab(getTabFromHash()); });
document.getElementById('tabBar').addEventListener('click', function (e) {
    const link = e.target.closest('[data-tab]');
    if (link) {
        e.preventDefault();
        location.hash = link.dataset.tab;
    }
});

// --- Overview Tab ---
const svcContainer = document.getElementById('servicesCheckboxes');
const blContainer = document.getElementById('blocklistCheckboxes');
const daysContainer = document.getElementById('scheduleDays');
const enableScheduleEl = document.getElementById('enableSchedule');
const scheduleGridEl = document.getElementById('scheduleGrid');

// Build service checkboxes
for (const [id, svc] of Object.entries(services).sort(function (a, b) { return a[1].name.localeCompare(b[1].name); })) {
    const label = document.createElement('label');
    label.className = 'flex items-center space-x-2';
    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.name = 'blockedServices';
    cb.value = id;
    cb.className = 'rounded border-gray-300 text-indigo-600 focus:ring-indigo-500';
    cb.checked = (profile.blockedServices || []).includes(id);
    const span = document.createElement('span');
    span.className = 'text-sm text-gray-700';
    span.textContent = svc.name;
    label.appendChild(cb);
    label.appendChild(span);
    svcContainer.appendChild(label);
}

// Build blocklist checkboxes with disabled/orphan badges
const profileBls = profile.blockLists || [];
const globalUrls = new Set(globalBlockLists.map(function (bl) { return bl.url; }));
if (globalBlockLists.length === 0 && profileBls.length === 0) {
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
        cb.checked = profileBls.includes(bl.url);
        const nameSpan = document.createElement('span');
        nameSpan.className = bl.enabled ? 'text-sm text-gray-700' : 'text-sm text-gray-400';
        nameSpan.textContent = bl.name || bl.url;
        label.appendChild(cb);
        label.appendChild(nameSpan);
        if (!bl.enabled) {
            const badge = document.createElement('span');
            badge.className = 'text-xs text-amber-600 font-medium';
            badge.textContent = '(paused)';
            badge.title = 'Globally disabled -- will not block until re-enabled in Settings';
            label.appendChild(badge);
        }
        blContainer.appendChild(label);
    }
    // Show orphaned references
    for (const url of profileBls) {
        if (!globalUrls.has(url)) {
            const label = document.createElement('label');
            label.className = 'flex items-center space-x-2';
            const cb = document.createElement('input');
            cb.type = 'checkbox';
            cb.name = 'profileBlockLists';
            cb.value = url;
            cb.className = 'rounded border-gray-300 text-indigo-600 focus:ring-indigo-500';
            cb.checked = true;
            const nameSpan = document.createElement('span');
            nameSpan.className = 'text-sm text-gray-400';
            nameSpan.textContent = url;
            const badge = document.createElement('span');
            badge.className = 'text-xs text-red-600 font-medium';
            badge.textContent = '(removed)';
            label.appendChild(cb);
            label.appendChild(nameSpan);
            label.appendChild(badge);
            blContainer.appendChild(label);
        }
    }
}

// Build schedule day rows
for (const day of DAYS) {
    const row = document.createElement('div');
    row.className = 'flex items-center space-x-2';
    row.innerHTML = '<label class="flex items-center space-x-1 w-16">' +
        '<input type="checkbox" class="day-toggle rounded border-gray-300 text-indigo-600" data-day="' + day + '">' +
        '<span class="text-sm">' + day.charAt(0).toUpperCase() + day.slice(1) + '</span>' +
        '</label>' +
        '<span class="day-time-inputs ' + (scheduleAllDay ? 'hidden' : '') + '" data-day="' + day + '">' +
        '<input type="time" class="day-start border border-gray-300 rounded px-2 py-1 text-sm" data-day="' + day + '" value="08:00">' +
        '<span class="text-sm text-gray-500">to</span>' +
        '<input type="time" class="day-end border border-gray-300 rounded px-2 py-1 text-sm" data-day="' + day + '" value="20:00">' +
        '</span>';
    daysContainer.appendChild(row);
}

// Populate schedule state
const hasSchedule = profile.schedule && Object.keys(profile.schedule).length > 0;
enableScheduleEl.checked = hasSchedule;
scheduleGridEl.classList.toggle('hidden', !hasSchedule);
document.querySelectorAll('.day-toggle').forEach(function (cb) {
    const day = cb.dataset.day;
    const sched = profile.schedule?.[day];
    cb.checked = !!sched;
    document.querySelector('.day-start[data-day="' + day + '"]').value = sched ? (sched.start || '08:00') : '08:00';
    document.querySelector('.day-end[data-day="' + day + '"]').value = sched ? (sched.end || '20:00') : '20:00';
});

// Blocking addresses
const blockingAddressesEl = document.getElementById('blockingAddresses');
const ba = profile.blockingAddresses;
if (Array.isArray(ba)) {
    blockingAddressesEl.value = ba.join('\n');
}

// Overview form submit
document.getElementById('overviewForm').addEventListener('submit', async function (e) {
    e.preventDefault();
    const btn = document.getElementById('overviewSaveBtn');
    setButtonLoading(btn, true);

    const blockedServices = Array.from(document.querySelectorAll('input[name="blockedServices"]:checked')).map(function (cb) { return cb.value; });
    const blockLists = Array.from(document.querySelectorAll('input[name="profileBlockLists"]:checked')).map(function (cb) { return cb.value; });

    let schedule = null;
    if (enableScheduleEl.checked) {
        schedule = {};
        const checkedDays = document.querySelectorAll('.day-toggle:checked');
        if (checkedDays.length === 0) {
            showToast('Select at least one day for the schedule.', 'error');
            setButtonLoading(btn, false, 'Save');
            return;
        }
        for (const cb of checkedDays) {
            const day = cb.dataset.day;
            const start = scheduleAllDay ? '00:00' : document.querySelector('.day-start[data-day="' + day + '"]').value;
            const end = scheduleAllDay ? '23:59:59' : document.querySelector('.day-end[data-day="' + day + '"]').value;
            if (!scheduleAllDay && start >= end) {
                showToast('Schedule for ' + day.charAt(0).toUpperCase() + day.slice(1) + ': start time must be before end time.', 'error');
                setButtonLoading(btn, false, 'Save');
                return;
            }
            schedule[day] = { allDay: scheduleAllDay, start, end };
        }
    }

    const blockingAddresses = blockingAddressesEl.value.split('\n').map(function (s) { return s.trim(); }).filter(Boolean);

    try {
        await apiCall('POST', API_PATHS.profiles, {
            name: profileName,
            description: document.getElementById('profileDesc').value,
            blockedServices,
            blockLists,
            schedule,
            blockingAddresses: blockingAddresses.length > 0 ? blockingAddresses : undefined,
        });
        showToast('Profile saved.', 'success');
    } catch (err) {
        // error shown by apiCall
    } finally {
        setButtonLoading(btn, false, 'Save');
    }
});

// --- Allowlist Tab ---
const allowListTextEl = document.getElementById('allowListText');
const domainCountEl = document.getElementById('domainCount');
allowListTextEl.value = (profile.allowList || []).join('\n');

function updateDomainCount() {
    const count = allowListTextEl.value.split('\n').map(function (s) { return s.trim(); }).filter(Boolean).length;
    domainCountEl.textContent = count + ' domain' + (count !== 1 ? 's' : '');
}
updateDomainCount();
allowListTextEl.addEventListener('input', updateDomainCount);

document.getElementById('saveAllowlistBtn').addEventListener('click', async function () {
    const btn = this;
    setButtonLoading(btn, true);
    try {
        const domains = allowListTextEl.value.split('\n').map(function (s) { return s.trim(); }).filter(Boolean);
        await apiCall('POST', profileApiPath(profileName, 'allowlist'), { domains });
        updateDomainCount();
        showToast('Allowlist saved.', 'success');
    } catch (err) {
        // error shown by apiCall
    } finally {
        setButtonLoading(btn, false);
    }
});

// --- Custom Rules Tab ---
const rulesTextEl = document.getElementById('rulesText');
const ruleCountEl = document.getElementById('ruleCount');
rulesTextEl.value = (profile.customRules || []).join('\n');

function updateRuleCount() {
    const count = rulesTextEl.value.split('\n').map(function (s) { return s.trim(); }).filter(function (s) { return s && !s.startsWith('#'); }).length;
    ruleCountEl.textContent = count + ' rule' + (count !== 1 ? 's' : '');
}
updateRuleCount();
rulesTextEl.addEventListener('input', updateRuleCount);

document.getElementById('saveRulesBtn').addEventListener('click', async function () {
    const btn = this;
    setButtonLoading(btn, true);
    try {
        const rules = rulesTextEl.value.split('\n').map(function (s) { return s.trim(); }).filter(Boolean);
        await apiCall('POST', profileApiPath(profileName, 'rules'), { rules });
        updateRuleCount();
        showToast('Rules saved.', 'success');
    } catch (err) {
        // error shown by apiCall
    } finally {
        setButtonLoading(btn, false);
    }
});

// --- Regex Tab ---
const regexBlockTextEl = document.getElementById('regexBlockText');
const regexAllowTextEl = document.getElementById('regexAllowText');
const blockCountEl = document.getElementById('blockCount');
const allowCountEl = document.getElementById('allowCount');

regexBlockTextEl.value = (profile.regexBlockRules || []).join('\n');
regexAllowTextEl.value = (profile.regexAllowRules || []).join('\n');

function countPatterns(textareaEl, countEl) {
    const lines = textareaEl.value.split('\n').map(function (s) { return s.trim(); }).filter(function (s) { return s && !s.startsWith('#'); });
    countEl.textContent = lines.length + ' pattern' + (lines.length !== 1 ? 's' : '');
}
countPatterns(regexBlockTextEl, blockCountEl);
countPatterns(regexAllowTextEl, allowCountEl);
regexBlockTextEl.addEventListener('input', function () { countPatterns(regexBlockTextEl, blockCountEl); });
regexAllowTextEl.addEventListener('input', function () { countPatterns(regexAllowTextEl, allowCountEl); });

document.getElementById('saveRegexBtn').addEventListener('click', async function () {
    const btn = this;
    setButtonLoading(btn, true);
    try {
        const blockRules = regexBlockTextEl.value.split('\n').map(function (s) { return s.trim(); }).filter(Boolean);
        const allowRules = regexAllowTextEl.value.split('\n').map(function (s) { return s.trim(); }).filter(Boolean);
        await apiCall('POST', profileApiPath(profileName, 'regex'), { regexBlockRules: blockRules, regexAllowRules: allowRules });
        countPatterns(regexBlockTextEl, blockCountEl);
        countPatterns(regexAllowTextEl, allowCountEl);
        showToast('Regex rules saved.', 'success');
    } catch (err) {
        // error shown by apiCall
    } finally {
        setButtonLoading(btn, false);
    }
});

// --- Rewrites Tab ---
const rewritesBodyEl = document.getElementById('rewritesBody');
const newDomainEl = document.getElementById('newDomain');
const newAnswerEl = document.getElementById('newAnswer');
let rewrites = profile.dnsRewrites || [];

function renderRewrites() {
    if (rewrites.length === 0) {
        rewritesBodyEl.innerHTML = '<tr><td colspan="3" class="px-6 py-8 text-center text-sm text-gray-500">No rewrites configured for this profile.</td></tr>';
        return;
    }
    let html = '';
    for (const rw of rewrites) {
        html += '<tr>' +
            '<td class="px-6 py-3 text-sm font-mono text-gray-900">' + escapeHtml(rw.domain) + '</td>' +
            '<td class="px-6 py-3 text-sm font-mono text-gray-600">' + escapeHtml(rw.answer) + '</td>' +
            '<td class="px-6 py-3 text-right"><button data-delete-rw="' + escapeHtml(rw.domain) + '" class="text-sm text-red-600 hover:text-red-500">Delete</button></td></tr>';
    }
    rewritesBodyEl.innerHTML = html;
}
renderRewrites();

rewritesBodyEl.addEventListener('click', function (e) {
    const deleteBtn = e.target.closest('[data-delete-rw]');
    if (deleteBtn) deleteRewrite(deleteBtn.dataset.deleteRw);
});

async function deleteRewrite(domain) {
    if (!confirm('Delete rewrite for "' + domain + '"?')) return;
    try {
        await apiCall('DELETE', profileApiPath(profileName, 'rewrites'), { domain });
        rewrites = rewrites.filter(function (rw) { return rw.domain !== domain; });
        renderRewrites();
        showToast('Rewrite deleted.', 'success');
    } catch (err) {
        // error shown by apiCall
    }
}

document.getElementById('addRewriteForm').addEventListener('submit', async function (e) {
    e.preventDefault();
    const domain = newDomainEl.value.trim().toLowerCase();
    const answer = newAnswerEl.value.trim();
    if (!domain) { showToast('Domain is required.', 'error'); return; }
    if (!answer) { showToast('Answer (IP or domain) is required.', 'error'); return; }

    const submitBtn = this.querySelector('button[type="submit"]');
    setButtonLoading(submitBtn, true);
    try {
        await apiCall('POST', profileApiPath(profileName, 'rewrites'), { domain, answer });
        const existing = rewrites.findIndex(function (rw) { return rw.domain === domain; });
        if (existing >= 0) {
            rewrites[existing].answer = answer;
        } else {
            rewrites.push({ domain, answer });
        }
        newDomainEl.value = '';
        newAnswerEl.value = '';
        renderRewrites();
        showToast('Rewrite saved.', 'success');
    } catch (err) {
        // error shown by apiCall
    } finally {
        setButtonLoading(submitBtn, false, 'Add');
    }
});

// --- Test Tab ---
const testDomainFormEl = document.getElementById('testDomainForm');
const testDomainResultsEl = document.getElementById('testDomainResults');

testDomainFormEl.addEventListener('submit', async function (e) {
    e.preventDefault();
    const btn = document.getElementById('testDomainBtn');
    setButtonLoading(btn, true, 'Testing...');
    testDomainResultsEl.classList.add('hidden');

    try {
        const domain = document.getElementById('testDomainInput').value.trim();
        const clientIp = document.getElementById('testClientIp').value.trim();
        const payload = { domain };
        if (clientIp) payload.clientIp = clientIp;
        const result = await apiCall('POST', API_PATHS.testDomain, payload);
        renderTestResults(result);
    } catch (err) {
        // error shown by apiCall
    } finally {
        setButtonLoading(btn, false, 'Test');
    }
});

function renderTestResults(result) {
    const verdictColors = {
        ALLOW: { bg: 'bg-green-50', border: 'border-green-200', text: 'text-green-800', badge: 'bg-green-100 text-green-800' },
        BLOCK: { bg: 'bg-red-50', border: 'border-red-200', text: 'text-red-800', badge: 'bg-red-100 text-red-800' },
        REWRITE: { bg: 'bg-blue-50', border: 'border-blue-200', text: 'text-blue-800', badge: 'bg-blue-100 text-blue-800' },
    };
    const colors = verdictColors[result.verdict] || verdictColors.ALLOW;
    let html = '<div class="' + colors.bg + ' ' + colors.border + ' border rounded-lg p-4 mb-4">';
    html += '<span class="inline-flex items-center rounded-full px-3 py-1 text-sm font-bold ' + colors.badge + '">' + escapeHtml(result.verdict) + '</span>';
    if (result.profile) html += '<span class="ml-3 text-sm ' + colors.text + '">Profile: <strong>' + escapeHtml(result.profile) + '</strong></span>';
    if (result.verdict === 'REWRITE' && result.rewriteAnswer) html += '<span class="ml-3 text-sm font-mono ' + colors.text + '">' + escapeHtml(result.rewriteAnswer) + '</span>';
    html += '</div>';

    html += '<table class="min-w-full text-sm"><thead><tr class="border-b border-gray-200">';
    html += '<th class="text-left py-2 pr-4 font-medium text-gray-500">Step</th>';
    html += '<th class="text-left py-2 pr-4 font-medium text-gray-500">Result</th>';
    html += '<th class="text-left py-2 font-medium text-gray-500">Detail</th>';
    html += '</tr></thead><tbody>';
    const resultBadge = { PASS: 'bg-gray-100 text-gray-600', ALLOW: 'bg-green-100 text-green-700', BLOCK: 'bg-red-100 text-red-700', REWRITE: 'bg-blue-100 text-blue-700' };
    for (const step of result.steps) {
        const badge = resultBadge[step.result] || resultBadge.PASS;
        const rowClass = step.result !== 'PASS' ? ' font-medium' : '';
        html += '<tr class="border-b border-gray-100' + rowClass + '">';
        html += '<td class="py-2 pr-4 whitespace-nowrap text-gray-700">' + escapeHtml(step.step) + '</td>';
        html += '<td class="py-2 pr-4 whitespace-nowrap"><span class="inline-flex rounded-full px-2 py-0.5 text-xs font-medium ' + badge + '">' + escapeHtml(step.result) + '</span></td>';
        html += '<td class="py-2 text-gray-600">' + escapeHtml(step.detail) + '</td></tr>';
    }
    html += '</tbody></table>';
    testDomainResultsEl.innerHTML = html;
    testDomainResultsEl.classList.remove('hidden');
}

// --- Rename / Delete ---
document.getElementById('renameBtn').addEventListener('click', function () {
    document.getElementById('newProfileName').value = profileName;
    document.getElementById('renameModal').classList.remove('hidden');
});

document.getElementById('renameForm').addEventListener('submit', async function (e) {
    e.preventDefault();
    const newName = document.getElementById('newProfileName').value.trim();
    if (!newName) { showToast('Name is required.', 'error'); return; }
    if (newName === profileName) { document.getElementById('renameModal').classList.add('hidden'); return; }

    const btn = this.querySelector('button[type="submit"]');
    setButtonLoading(btn, true, 'Renaming...');
    try {
        await apiCall('POST', API_PATHS.profilesRename, { old_name: profileName, new_name: newName });
        window.location.href = BASE_PATH + '/profiles/' + encodeURIComponent(newName);
    } catch (err) {
        // error shown by apiCall
    } finally {
        setButtonLoading(btn, false, 'Rename');
    }
});

document.getElementById('deleteBtn').addEventListener('click', async function () {
    if (!confirm('Delete profile "' + profileName + '"? Clients using it will become unassigned.')) return;
    try {
        await apiCall('DELETE', API_PATHS.profiles, { name: profileName });
        window.location.href = BASE_PATH + '/profiles';
    } catch (err) {
        // error shown by apiCall
    }
});
