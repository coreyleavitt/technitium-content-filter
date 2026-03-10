const config = JSON.parse(document.getElementById('page-data').textContent);
let blocking = config.enableBlocking;

// Auto-detect timezone from browser if the config still has the default (UTC)
const browserTz = Intl.DateTimeFormat().resolvedOptions().timeZone;
const effectiveTz = (!config.timeZone || config.timeZone === 'UTC') ? browserTz : config.timeZone;
if ((!config.timeZone || config.timeZone === 'UTC') && browserTz && browserTz !== 'UTC') {
    config.timeZone = browserTz;
    apiCall('POST', '/api/settings', {
        enableBlocking: config.enableBlocking,
        timeZone: browserTz,
        defaultProfile: config.defaultProfile,
        baseProfile: config.baseProfile || null,
        scheduleAllDay: config.scheduleAllDay ?? true,
    });
}

// Populate timezone dropdown
const commonTimezones = [
    'America/New_York', 'America/Chicago', 'America/Denver', 'America/Los_Angeles',
    'America/Phoenix', 'America/Anchorage', 'Pacific/Honolulu', 'America/Boise',
    'America/Detroit', 'America/Indiana/Indianapolis',
];
const tzSelect = document.getElementById('timeZone');
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
    tzSelect.appendChild(opt);
}

// Protection toggle -- saves immediately
document.getElementById('toggleBlocking').addEventListener('click', async () => {
    blocking = !blocking;
    await apiCall('POST', '/api/settings', {
        enableBlocking: blocking,
        timeZone: config.timeZone,
        defaultProfile: config.defaultProfile,
        baseProfile: config.baseProfile || null,
        scheduleAllDay: config.scheduleAllDay ?? true,
    });
    location.reload();
});

// Settings form
document.getElementById('settingsForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    await apiCall('POST', '/api/settings', {
        enableBlocking: blocking,
        timeZone: document.getElementById('timeZone').value,
        defaultProfile: document.getElementById('defaultProfile').value || null,
        baseProfile: document.getElementById('baseProfile').value || null,
        scheduleAllDay: document.getElementById('scheduleAllDay').checked,
    });
    location.reload();
});
