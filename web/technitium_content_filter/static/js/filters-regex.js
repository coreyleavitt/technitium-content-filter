const pageData = JSON.parse(document.getElementById('page-data').textContent);
const profiles = pageData.profiles || {};
let currentProfile = null;

function countPatterns(textareaId, countId) {
    const lines = document.getElementById(textareaId).value
        .split('\n').map(function (s) { return s.trim(); })
        .filter(function (s) { return s && !s.startsWith('#'); });
    const count = lines.length;
    document.getElementById(countId).textContent = count + ' pattern' + (count !== 1 ? 's' : '');
}

function loadProfile(name) {
    currentProfile = name;
    location.hash = name;
    const profile = profiles[name] || {};
    document.getElementById('regexBlockText').value = (profile.regexBlockRules || []).join('\n');
    document.getElementById('regexAllowText').value = (profile.regexAllowRules || []).join('\n');
    countPatterns('regexBlockText', 'blockCount');
    countPatterns('regexAllowText', 'allowCount');
}

document.getElementById('regexBlockText').addEventListener('input', function () {
    countPatterns('regexBlockText', 'blockCount');
});
document.getElementById('regexAllowText').addEventListener('input', function () {
    countPatterns('regexAllowText', 'allowCount');
});

async function saveRegexRules() {
    if (!currentProfile) return;
    const btn = document.getElementById('saveRegexBtn');
    setButtonLoading(btn, true);
    try {
        const blockRules = document.getElementById('regexBlockText').value
            .split('\n').map(function (s) { return s.trim(); }).filter(Boolean);
        const allowRules = document.getElementById('regexAllowText').value
            .split('\n').map(function (s) { return s.trim(); }).filter(Boolean);
        await apiCall('POST', API_PATHS.regexRules, {
            profile: currentProfile,
            regexBlockRules: blockRules,
            regexAllowRules: allowRules,
        });
        profiles[currentProfile].regexBlockRules = blockRules;
        profiles[currentProfile].regexAllowRules = allowRules;
        countPatterns('regexBlockText', 'blockCount');
        countPatterns('regexAllowText', 'allowCount');
        showToast('Regex rules saved successfully.', 'success');
    } catch (err) {
        // error already shown by apiCall
    } finally {
        setButtonLoading(btn, false);
    }
}

initProfilePicker('profilePicker', profiles, loadProfile);
