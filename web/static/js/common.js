const BASE_PATH = document.querySelector('meta[name="base-path"]').content;

function escapeHtml(str) {
    const div = document.createElement('div');
    div.textContent = str;
    return div.innerHTML;
}

async function apiCall(method, url, data) {
    const opts = {
        method,
        headers: {'Content-Type': 'application/json'},
    };
    if (data) opts.body = JSON.stringify(data);
    const resp = await fetch(BASE_PATH + url, opts);
    return resp.json();
}

function showToast(message, type) {
    const container = document.getElementById('toastContainer');
    const toast = document.createElement('div');
    toast.className = 'toast toast-' + (type || 'success');
    toast.textContent = message;
    container.appendChild(toast);
    requestAnimationFrame(() => toast.classList.add('show'));
    setTimeout(() => {
        toast.classList.remove('show');
        setTimeout(() => toast.remove(), 300);
    }, 3000);
}

function initProfilePicker(selectId, profiles, onChange) {
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
    select.addEventListener('change', () => onChange(select.value));

    // Restore from URL hash or use first profile
    const hash = location.hash.slice(1);
    if (hash && profiles[hash]) {
        select.value = hash;
    }
    const selected = select.value;
    onChange(selected);
    return selected;
}
