const BASE_PATH = document.querySelector('meta[name="base-path"]').content;

// #91: API path constants
const API_PATHS = {
    settings: '/api/settings',
    profiles: '/api/profiles',
    profilesRename: '/api/profiles/rename',
    clients: '/api/clients',
    allowlists: '/api/allowlists',
    rules: '/api/rules',
    rewrites: '/api/rewrites',
    blocklists: '/api/blocklists',
    blocklistsRefresh: '/api/blocklists/refresh',
    customServices: '/api/custom-services',
    testDomain: '/api/test-domain',
};

// #92: Global error boundary
window.onerror = function (message) {
    showToast('An unexpected error occurred: ' + message, 'error');
};
window.addEventListener('unhandledrejection', function (event) {
    const reason = event.reason?.message || String(event.reason);
    showToast('Unhandled error: ' + reason, 'error');
});

// #87: escapeHtml via string replacement instead of DOM element creation
function escapeHtml(str) {
    if (str == null) return '';
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

// Toast notification system (#70, #85, etc.)
function showToast(message, type) {
    type = type || 'info';
    const colors = {
        success: 'bg-green-600',
        error: 'bg-red-600',
        info: 'bg-indigo-600',
    };
    const toast = document.createElement('div');
    toast.className = 'fixed bottom-4 right-4 px-4 py-3 rounded-lg text-white text-sm shadow-lg z-50 transition-opacity duration-300 ' + (colors[type] || colors.info);
    toast.textContent = message;
    toast.setAttribute('role', 'alert');
    document.body.appendChild(toast);
    setTimeout(function () {
        toast.style.opacity = '0';
        setTimeout(function () { toast.remove(); }, 300);
    }, 4000);
}

// #70: apiCall with error handling; #78: built-in request dedup
let _activeRequests = new Map();

async function apiCall(method, url, data) {
    const key = method + ':' + url + ':' + JSON.stringify(data);
    if (_activeRequests.has(key)) {
        return _activeRequests.get(key);
    }

    const promise = _apiCallInner(method, url, data);
    _activeRequests.set(key, promise);
    try {
        return await promise;
    } finally {
        _activeRequests.delete(key);
    }
}

async function _apiCallInner(method, url, data) {
    const opts = {
        method,
        headers: { 'Content-Type': 'application/json' },
    };
    if (data) opts.body = JSON.stringify(data);
    let resp;
    try {
        resp = await fetch(BASE_PATH + url, opts);
    } catch (err) {
        showToast('Network error: ' + err.message, 'error');
        throw err;
    }
    // Redirect to login on auth failure
    if (resp.status === 401) {
        window.location.href = BASE_PATH + '/login';
        throw new Error('Session expired');
    }
    let result;
    try {
        result = await resp.json();
    } catch {
        if (!resp.ok) {
            const msg = 'Request failed: ' + resp.status;
            showToast(msg, 'error');
            throw new Error(msg);
        }
        return {};
    }
    if (!resp.ok) {
        const msg = result?.error || result?.detail || ('Request failed: ' + resp.status);
        showToast(msg, 'error');
        throw new Error(msg);
    }
    return result;
}

// #76: Helper to set loading state on a button
function setButtonLoading(btn, loading, originalText) {
    if (!btn) return;
    btn.disabled = loading;
    if (loading) {
        btn._originalText = btn.textContent;
        btn.textContent = originalText || 'Saving...';
    } else {
        btn.textContent = btn._originalText || originalText || 'Save';
    }
}

// #75: Shared domain list editor logic for allowlists and rules
function initDomainListEditor(opts) {
    // opts: { textareaId, countId, countLabel, profileKey, apiPath, filterComments }
    const pageData = JSON.parse(document.getElementById('page-data').textContent);
    const profiles = pageData.profiles || {};
    let currentProfile = null;

    function loadProfile(name) {
        currentProfile = name;
        location.hash = name;
        const profile = profiles[name];
        const items = profile?.[opts.profileKey] || [];
        document.getElementById(opts.textareaId).value = items.join('\n');
        updateCount();
    }

    function updateCount() {
        let lines = document.getElementById(opts.textareaId).value
            .split('\n').map(function (s) { return s.trim(); });
        if (opts.filterComments) {
            lines = lines.filter(function (s) { return s && !s.startsWith('#'); });
        } else {
            lines = lines.filter(Boolean);
        }
        const count = lines.length;
        document.getElementById(opts.countId).textContent = count + ' ' + opts.countLabel + (count !== 1 ? 's' : '');
    }

    document.getElementById(opts.textareaId).addEventListener('input', updateCount);

    async function save() {
        if (!currentProfile) return;
        const saveBtn = document.querySelector('[onclick*="save"]') || document.querySelector('button[type="submit"]');
        if (saveBtn) setButtonLoading(saveBtn, true);
        try {
            const items = document.getElementById(opts.textareaId).value
                .split('\n').map(function (s) { return s.trim(); }).filter(Boolean);
            await apiCall('POST', opts.apiPath, { profile: currentProfile, [opts.payloadKey]: items });
            profiles[currentProfile][opts.profileKey] = items;
            updateCount();
            showToast(opts.countLabel.charAt(0).toUpperCase() + opts.countLabel.slice(1) + 's saved successfully.', 'success');
        } catch (err) {
            // error already shown by apiCall
        } finally {
            if (saveBtn) setButtonLoading(saveBtn, false);
        }
    }

    initProfilePicker('profilePicker', profiles, loadProfile);

    return { save, loadProfile, profiles, getCurrentProfile: function () { return currentProfile; } };
}

// #74: initProfilePicker with AbortController to prevent listener leaks
let _profilePickerAbort = null;

function initProfilePicker(selectId, profiles, onChange) {
    // Clean up previous listeners
    if (_profilePickerAbort) {
        _profilePickerAbort.abort();
    }
    _profilePickerAbort = new AbortController();

    const select = document.getElementById(selectId);
    const names = Object.keys(profiles);
    select.innerHTML = '';
    if (names.length === 0) {
        const opt = document.createElement('option');
        opt.value = '';
        opt.textContent = 'No profiles available';
        select.appendChild(opt);
        select.disabled = true;
        const helpDiv = document.createElement('div');
        helpDiv.className = 'mt-2 text-sm text-gray-500';
        helpDiv.innerHTML = 'Create a profile on the <a href="' + BASE_PATH + '/profiles" class="text-indigo-600 hover:text-indigo-500 font-medium">Profiles page</a> to get started.';
        select.parentNode.appendChild(helpDiv);
        return null;
    }
    for (const name of names) {
        const opt = document.createElement('option');
        opt.value = name;
        opt.textContent = name;
        select.appendChild(opt);
    }

    // #89: Named handler for removability
    function handleProfileChange() {
        onChange(select.value);
    }
    select.addEventListener('change', handleProfileChange, { signal: _profilePickerAbort.signal });

    // Restore from URL hash or use first profile
    const hash = location.hash.slice(1);
    if (hash && profiles[hash]) {
        select.value = hash;
    }
    const selected = select.value;
    onChange(selected);
    return selected;
}

// #88: Keyboard accessibility helper for clickable non-button elements
function makeAccessible(element) {
    if (!element || element.tagName === 'BUTTON' || element.tagName === 'A') return;
    if (!element.getAttribute('role')) element.setAttribute('role', 'button');
    if (!element.getAttribute('tabindex')) element.setAttribute('tabindex', '0');
    element.addEventListener('keydown', function handleKeyboard(e) {
        if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            element.click();
        }
    });
}
