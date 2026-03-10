const config = JSON.parse(document.getElementById('page-data').textContent);
let blocking = config.enableBlocking;

// #82: Cache DOM elements
const tzSelectEl = document.getElementById('timeZone');
const toggleBlockingBtn = document.getElementById('toggleBlocking');
const settingsFormEl = document.getElementById('settingsForm');

// #90: Shared settings payload builder
function buildSettingsPayload(overrides) {
    return Object.assign({
        enableBlocking: blocking,
        timeZone: config.timeZone,
        defaultProfile: config.defaultProfile,
        baseProfile: config.baseProfile || null,
        scheduleAllDay: config.scheduleAllDay ?? true,
    }, overrides || {});
}

// #80: Await timezone auto-save and handle errors
const browserTz = Intl.DateTimeFormat().resolvedOptions().timeZone;
const effectiveTz = (!config.timeZone || config.timeZone === 'UTC') ? browserTz : config.timeZone;
if ((!config.timeZone || config.timeZone === 'UTC') && browserTz && browserTz !== 'UTC') {
    config.timeZone = browserTz;
    (async function () {
        try {
            await apiCall('POST', API_PATHS.settings, buildSettingsPayload({ timeZone: browserTz }));
        } catch (err) {
            console.error('Failed to auto-save timezone:', err);
        }
    })();
}

// Populate timezone dropdown
const commonTimezones = [
    'America/New_York', 'America/Chicago', 'America/Denver', 'America/Los_Angeles',
    'America/Phoenix', 'America/Anchorage', 'Pacific/Honolulu', 'America/Boise',
    'America/Detroit', 'America/Indiana/Indianapolis',
];
let allTz;
try {
    allTz = Intl.supportedValuesOf('timeZone');
} catch {
    allTz = commonTimezones;
}
const seen = new Set();
for (const tz of [...commonTimezones, ...allTz]) {
    if (seen.has(tz)) continue;
    seen.add(tz);
    const opt = document.createElement('option');
    opt.value = tz;
    opt.textContent = tz.replace(/_/g, ' ');
    if (tz === effectiveTz) opt.selected = true;
    tzSelectEl.appendChild(opt);
}

// Protection toggle -- saves immediately; #76: loading state
toggleBlockingBtn.addEventListener('click', async function handleToggleBlocking() {
    setButtonLoading(toggleBlockingBtn, true, 'Saving...');
    blocking = !blocking;
    try {
        await apiCall('POST', API_PATHS.settings, buildSettingsPayload());
        location.reload();
    } catch (err) {
        blocking = !blocking; // revert on failure
    } finally {
        setButtonLoading(toggleBlockingBtn, false);
    }
});

// Settings form; #76: loading state
settingsFormEl.addEventListener('submit', async function handleSettingsSave(e) {
    e.preventDefault();
    const submitBtn = settingsFormEl.querySelector('button[type="submit"]');
    setButtonLoading(submitBtn, true);
    try {
        await apiCall('POST', API_PATHS.settings, buildSettingsPayload({
            timeZone: tzSelectEl.value,
            defaultProfile: document.getElementById('defaultProfile').value || null,
            baseProfile: document.getElementById('baseProfile').value || null,
            scheduleAllDay: document.getElementById('scheduleAllDay').checked,
        }));
        location.reload();
    } catch (err) {
        // error shown by apiCall
    } finally {
        setButtonLoading(submitBtn, false, 'Save Settings');
    }
});
