const pageData = JSON.parse(document.getElementById('page-data').textContent);
const profiles = pageData.profiles || {};
let currentProfile = null;

function loadProfile(name) {
    currentProfile = name;
    location.hash = name;
    renderRewrites();
}

function renderRewrites() {
    const body = document.getElementById('rewritesBody');
    const profile = profiles[currentProfile];
    const rewrites = profile?.dnsRewrites || [];

    if (rewrites.length === 0) {
        body.innerHTML = '<tr><td colspan="3" class="px-6 py-8 text-center text-sm text-gray-500">No rewrites configured for this profile.</td></tr>';
        return;
    }

    let html = '';
    for (const rw of rewrites) {
        html += '<tr>' +
            '<td class="px-6 py-3 text-sm font-mono text-gray-900">' + escapeHtml(rw.domain) + '</td>' +
            '<td class="px-6 py-3 text-sm font-mono text-gray-600">' + escapeHtml(rw.answer) + '</td>' +
            '<td class="px-6 py-3 text-right">' +
            '<button data-delete-rw="' + escapeHtml(rw.domain) + '" class="text-sm text-red-600 hover:text-red-500">Delete</button>' +
            '</td></tr>';
    }
    body.innerHTML = html;
}

document.getElementById('rewritesBody').addEventListener('click', (e) => {
    const deleteBtn = e.target.closest('[data-delete-rw]');
    if (deleteBtn) deleteRewrite(deleteBtn.dataset.deleteRw);
});

async function deleteRewrite(domain) {
    if (!currentProfile) return;
    const result = await apiCall('DELETE', '/api/rewrites', { profile: currentProfile, domain });
    if (result.error) { showToast(result.error, 'error'); return; }
    const profile = profiles[currentProfile];
    profile.dnsRewrites = (profile.dnsRewrites || []).filter(rw => rw.domain !== domain);
    renderRewrites();
    showToast('Rewrite deleted.', 'success');
}

document.getElementById('addRewriteForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    if (!currentProfile) return;
    const domain = document.getElementById('newDomain').value.trim().toLowerCase();
    const answer = document.getElementById('newAnswer').value.trim();
    if (!domain || !answer) return;

    const addResult = await apiCall('POST', '/api/rewrites', { profile: currentProfile, domain, answer });
    if (addResult.error) { showToast(addResult.error, 'error'); return; }
    showToast('Rewrite added.', 'success');
    const profile = profiles[currentProfile];
    profile.dnsRewrites = profile.dnsRewrites || [];
    const existing = profile.dnsRewrites.findIndex(rw => rw.domain === domain);
    if (existing >= 0) {
        profile.dnsRewrites[existing].answer = answer;
    } else {
        profile.dnsRewrites.push({ domain, answer });
    }
    document.getElementById('newDomain').value = '';
    document.getElementById('newAnswer').value = '';
    renderRewrites();
});

initProfilePicker('profilePicker', profiles, loadProfile);
