const pageData = JSON.parse(document.getElementById('page-data').textContent);
const profiles = pageData.profiles || {};
let currentProfile = null;

// #82: Cache DOM elements
const rewritesBodyEl = document.getElementById('rewritesBody');
const newDomainEl = document.getElementById('newDomain');
const newAnswerEl = document.getElementById('newAnswer');
const addRewriteFormEl = document.getElementById('addRewriteForm');

function loadProfile(name) {
    currentProfile = name;
    location.hash = name;
    renderRewrites();
}

function renderRewrites() {
    const profile = profiles[currentProfile];
    const rewrites = profile?.dnsRewrites || [];

    if (rewrites.length === 0) {
        rewritesBodyEl.innerHTML = '<tr><td colspan="3" class="px-6 py-8 text-center text-sm text-gray-500">No rewrites configured for this profile.</td></tr>';
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
    rewritesBodyEl.innerHTML = html;
}

// #89: Named handler
function handleRewriteClick(e) {
    const deleteBtn = e.target.closest('[data-delete-rw]');
    if (deleteBtn) deleteRewrite(deleteBtn.dataset.deleteRw);
}
rewritesBodyEl.addEventListener('click', handleRewriteClick);

// #81: Confirmation before delete
async function deleteRewrite(domain) {
    if (!currentProfile) return;
    if (!confirm('Delete rewrite for "' + domain + '"?')) return;
    try {
        await apiCall('DELETE', API_PATHS.rewrites, { profile: currentProfile, domain });
        const profile = profiles[currentProfile];
        profile.dnsRewrites = (profile.dnsRewrites || []).filter(function (rw) { return rw.domain !== domain; });
        renderRewrites();
        showToast('Rewrite deleted.', 'success');
    } catch (err) {
        // error shown by apiCall
    }
}

// #72: Validation; #76: loading states
addRewriteFormEl.addEventListener('submit', async function handleRewriteSubmit(e) {
    e.preventDefault();
    if (!currentProfile) return;
    const domain = newDomainEl.value.trim().toLowerCase();
    const answer = newAnswerEl.value.trim();

    // #72: Validate required fields
    if (!domain) {
        showToast('Domain is required.', 'error');
        return;
    }
    if (!answer) {
        showToast('Answer (IP or domain) is required.', 'error');
        return;
    }

    const submitBtn = addRewriteFormEl.querySelector('button[type="submit"]');
    setButtonLoading(submitBtn, true);
    try {
        await apiCall('POST', API_PATHS.rewrites, { profile: currentProfile, domain, answer });
        // #83: Update local state only after confirmed success
        const profile = profiles[currentProfile];
        profile.dnsRewrites = profile.dnsRewrites || [];
        const existing = profile.dnsRewrites.findIndex(function (rw) { return rw.domain === domain; });
        if (existing >= 0) {
            profile.dnsRewrites[existing].answer = answer;
        } else {
            profile.dnsRewrites.push({ domain, answer });
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

initProfilePicker('profilePicker', profiles, loadProfile);
