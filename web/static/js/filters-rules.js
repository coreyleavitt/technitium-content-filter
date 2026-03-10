const pageData = JSON.parse(document.getElementById('page-data').textContent);
const profiles = pageData.profiles || {};
let currentProfile = null;

function loadProfile(name) {
    currentProfile = name;
    location.hash = name;
    const profile = profiles[name];
    const rules = profile?.customRules || [];
    document.getElementById('rulesText').value = rules.join('\n');
    updateCount();
}

function updateCount() {
    const lines = document.getElementById('rulesText').value
        .split('\n').map(s => s.trim()).filter(s => s && !s.startsWith('#'));
    document.getElementById('ruleCount').textContent = lines.length + ' rule' + (lines.length !== 1 ? 's' : '');
}

document.getElementById('rulesText').addEventListener('input', updateCount);

async function saveRules() {
    if (!currentProfile) return;
    const rules = document.getElementById('rulesText').value
        .split('\n').map(s => s.trim()).filter(Boolean);
    const result = await apiCall('POST', '/api/rules', { profile: currentProfile, rules });
    if (result.error) { showToast(result.error, 'error'); return; }
    profiles[currentProfile].customRules = rules;
    updateCount();
    showToast('Rules saved.', 'success');
}

initProfilePicker('profilePicker', profiles, loadProfile);
