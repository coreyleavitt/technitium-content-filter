const BASE_PATH = document.querySelector('meta[name="base-path"]').content;

async function apiCall(method, url, data) {
    const opts = {
        method,
        headers: {'Content-Type': 'application/json'},
    };
    if (data) opts.body = JSON.stringify(data);
    const resp = await fetch(BASE_PATH + url, opts);
    return resp.json();
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
