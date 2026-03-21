const config = JSON.parse(document.getElementById('page-data').textContent);
let blocking = config.enableBlocking;

const toggleBlockingBtn = document.getElementById('toggleBlocking');

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

// #118: Test Domain form
const testDomainFormEl = document.getElementById('testDomainForm');
const testDomainResultsEl = document.getElementById('testDomainResults');

testDomainFormEl.addEventListener('submit', async function handleTestDomain(e) {
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
    html += '<div class="flex items-center justify-between">';
    html += '<div>';
    html += '<span class="inline-flex items-center rounded-full px-3 py-1 text-sm font-bold ' + colors.badge + '">' + escapeHtml(result.verdict) + '</span>';
    if (result.profile) {
        html += '<span class="ml-3 text-sm ' + colors.text + '">Profile: <strong>' + escapeHtml(result.profile) + '</strong></span>';
    }
    html += '</div>';
    if (result.verdict === 'REWRITE' && result.rewriteAnswer) {
        html += '<span class="text-sm font-mono ' + colors.text + '">' + escapeHtml(result.rewriteAnswer) + '</span>';
    }
    html += '</div></div>';

    // Steps table
    html += '<table class="min-w-full text-sm">';
    html += '<thead><tr class="border-b border-gray-200">';
    html += '<th class="text-left py-2 pr-4 font-medium text-gray-500">Step</th>';
    html += '<th class="text-left py-2 pr-4 font-medium text-gray-500">Result</th>';
    html += '<th class="text-left py-2 font-medium text-gray-500">Detail</th>';
    html += '</tr></thead><tbody>';

    const resultBadge = {
        PASS: 'bg-gray-100 text-gray-600',
        ALLOW: 'bg-green-100 text-green-700',
        BLOCK: 'bg-red-100 text-red-700',
        REWRITE: 'bg-blue-100 text-blue-700',
    };

    for (const step of result.steps) {
        const badge = resultBadge[step.result] || resultBadge.PASS;
        const isFinal = step.result !== 'PASS';
        const rowClass = isFinal ? ' font-medium' : '';
        html += '<tr class="border-b border-gray-100' + rowClass + '">';
        html += '<td class="py-2 pr-4 whitespace-nowrap text-gray-700">' + escapeHtml(step.step) + '</td>';
        html += '<td class="py-2 pr-4 whitespace-nowrap"><span class="inline-flex rounded-full px-2 py-0.5 text-xs font-medium ' + badge + '">' + escapeHtml(step.result) + '</span></td>';
        html += '<td class="py-2 text-gray-600">' + escapeHtml(step.detail) + '</td>';
        html += '</tr>';
    }
    html += '</tbody></table>';

    if (result.blocklistUrls && result.blocklistUrls.length > 0) {
        html += '<div class="mt-3 rounded-md bg-amber-50 border border-amber-200 p-3">';
        html += '<p class="text-xs font-medium text-amber-800">Remote blocklists assigned to this profile (not checked from web UI):</p>';
        html += '<ul class="mt-1 text-xs text-amber-700 list-disc list-inside">';
        for (const url of result.blocklistUrls) {
            html += '<li class="font-mono truncate">' + escapeHtml(url) + '</li>';
        }
        html += '</ul></div>';
    }

    testDomainResultsEl.innerHTML = html;
    testDomainResultsEl.classList.remove('hidden');
}
