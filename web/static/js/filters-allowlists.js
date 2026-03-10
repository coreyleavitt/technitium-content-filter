const pageData = JSON.parse(document.getElementById('page-data').textContent);
const profiles = pageData.profiles || {};
let currentProfile = null;

function loadProfile(name) {
    currentProfile = name;
    location.hash = name;
    const profile = profiles[name];
    const domains = profile?.allowList || [];
    document.getElementById('allowListText').value = domains.join('\n');
    updateCount();
}

function updateCount() {
    const lines = document.getElementById('allowListText').value
        .split('\n').map(s => s.trim()).filter(Boolean);
    document.getElementById('domainCount').textContent = lines.length + ' domain' + (lines.length !== 1 ? 's' : '');
}

document.getElementById('allowListText').addEventListener('input', updateCount);

async function saveAllowList() {
    if (!currentProfile) return;
    const domains = document.getElementById('allowListText').value
        .split('\n').map(s => s.trim()).filter(Boolean);
    await apiCall('POST', '/api/allowlists', { profile: currentProfile, domains });
    profiles[currentProfile].allowList = domains;
    updateCount();
}

initProfilePicker('profilePicker', profiles, loadProfile);
