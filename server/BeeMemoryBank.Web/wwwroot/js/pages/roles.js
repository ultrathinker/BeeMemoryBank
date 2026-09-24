(function () {
    var editRoleHasAllowRules = null;

    function hideDlgError(id) {
        var el = document.getElementById(id);
        if (el) el.style.display = 'none';
    }

    function showDlgError(id, msg) {
        var el = document.getElementById(id);
        if (el) {
            var msgEl = el.querySelector('.msg');
            if (msgEl) msgEl.textContent = msg;
            el.style.display = 'block';
        }
    }

    async function postJson(url, body, method) {
        var resp = await fetch(url, {
            method: method || 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: body === null ? undefined : JSON.stringify(body)
        });
        if (resp.ok) return { ok: true };
        var msg = 'Request failed (HTTP ' + resp.status + ')';
        try {
            var text = await resp.text();
            if (text) {
                try {
                    var data = JSON.parse(text);
                    if (data && data.error) msg = data.error;
                    else msg += ': ' + text.slice(0, 300);
                } catch (e) { msg += ': ' + text.slice(0, 300); }
            }
        } catch (e) { }
        return { ok: false, error: msg };
    }

    function normalizeRoleName(raw) {
        return (raw || '')
            .toLowerCase()
            .replace(/[\s.]+/g, '-')
            .replace(/[^a-z0-9_-]/g, '')
            .replace(/^[^a-z0-9]+/, '');
    }

    function roleNameProblem(name) {
        if (name.length < 2) return 'Name must be at least 2 characters.';
        if (name.length > 32) return 'Name must be at most 32 characters.';
        return null;
    }

    var addRoleInput = document.getElementById('add-role-name');
    var addRolePreview = document.getElementById('add-role-name-preview');
    if (addRoleInput) {
        addRoleInput.addEventListener('sl-input', function () {
            var raw = addRoleInput.value;
            var norm = normalizeRoleName(raw);
            if (norm !== raw) addRoleInput.value = norm;
            if (addRolePreview) {
                addRolePreview.textContent = norm ? "Will be created as '" + norm + "'." : '';
                addRolePreview.style.display = norm ? '' : 'none';
            }
        });
    }

    function openAddRole() {
        hideDlgError('add-role-error');
        document.getElementById('add-role-name').value = '';
        document.getElementById('add-role-display').value = '';
        document.getElementById('add-role-description').value = '';
        document.getElementById('add-role-policy').value = 'closed';
        if (addRolePreview) { addRolePreview.textContent = ''; addRolePreview.style.display = 'none'; }
        document.getElementById('dlg-add-role')?.show();
        setTimeout(function () { document.getElementById('add-role-name')?.focus(); }, 100);
    }

    async function submitAddRole() {
        hideDlgError('add-role-error');
        var name = normalizeRoleName(document.getElementById('add-role-name').value.trim());
        if (!name) { showDlgError('add-role-error', 'Name is required.'); return; }
        var problem = roleNameProblem(name);
        if (problem) { showDlgError('add-role-error', problem); return; }

        var btn = document.getElementById('btn-add-role-submit');
        if (btn) btn.loading = true;
        var result = await postJson('/api-proxy/roles', {
            name: name,
            displayName: document.getElementById('add-role-display').value.trim() || name,
            description: document.getElementById('add-role-description').value.trim() || null,
            basePolicy: document.getElementById('add-role-policy').value
        });
        if (btn) btn.loading = false;
        if (result.ok) window.location = '?msg=' + encodeURIComponent("Role '" + name + "' created");
        else showDlgError('add-role-error', result.error);
    }

    function updatePolicyWarning() {
        var warn = document.getElementById('edit-role-policy-warning');
        if (!warn) return;
        var closing = document.getElementById('edit-role-policy').value === 'closed';
        warn.style.display = (closing && editRoleHasAllowRules === false) ? '' : 'none';
    }

    var editRolePolicy = document.getElementById('edit-role-policy');
    if (editRolePolicy) {
        editRolePolicy.addEventListener('sl-change', updatePolicyWarning);
    }

    function openEditRole(name, displayName, description, basePolicy) {
        hideDlgError('edit-role-error');
        document.getElementById('edit-role-name').value = name;
        document.getElementById('edit-role-name-label').textContent = 'Name: ' + name + ' (cannot be changed)';
        document.getElementById('edit-role-display').value = displayName || '';
        document.getElementById('edit-role-description').value = description || '';
        document.getElementById('edit-role-policy').value = basePolicy || 'closed';
        editRoleHasAllowRules = null;
        updatePolicyWarning();
        document.getElementById('dlg-edit-role')?.show();

        fetch('/api-proxy/restrictions/role/' + encodeURIComponent(name))
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (rules) {
                if (!rules) return;
                editRoleHasAllowRules = rules.some(function (x) { return x.effect === 'allow'; });
                if (document.getElementById('edit-role-name').value === name) updatePolicyWarning();
            }).catch(function () {});
    }

    async function submitEditRole() {
        hideDlgError('edit-role-error');
        var name = document.getElementById('edit-role-name').value;
        var displayName = document.getElementById('edit-role-display').value.trim();
        if (!displayName) { showDlgError('edit-role-error', 'Display Name is required.'); return; }

        var btn = document.getElementById('btn-edit-role-submit');
        if (btn) btn.loading = true;
        var result = await postJson('/api-proxy/roles/' + encodeURIComponent(name), {
            displayName: displayName,
            description: document.getElementById('edit-role-description').value.trim() || null,
            basePolicy: document.getElementById('edit-role-policy').value
        }, 'PUT');
        if (btn) btn.loading = false;
        if (result.ok) window.location = '?msg=' + encodeURIComponent('Role updated');
        else showDlgError('edit-role-error', result.error);
    }

    async function deleteRole(name, userCount) {
        var count = parseInt(userCount, 10) || 0;
        if (count > 0) {
            alert("'" + name + "' is still assigned to " + count + " user(s). Reassign them first — " +
                  "a user whose role no longer exists loses access to every folder.");
            return;
        }
        if (!confirm("Delete role '" + name + "' and all of its folder rules?")) return;

        var result = await postJson('/api-proxy/roles/' + encodeURIComponent(name), null, 'DELETE');
        if (result.ok) window.location = '?msg=' + encodeURIComponent("Role '" + name + "' deleted");
        else window.location = '?err=' + encodeURIComponent(result.error);
    }

    function openRoleFolderAccess(name, displayName) {
        if (window.folderAccess && window.folderAccess.open) {
            window.folderAccess.open('role', name,
                'Role: ' + displayName + '  —  applies to every user with this role');
        }
    }

    // Attach listeners
    var btnOpenAdd = document.getElementById('btn-open-add-role');
    if (btnOpenAdd) btnOpenAdd.addEventListener('click', openAddRole);

    var btnAddSubmit = document.getElementById('btn-add-role-submit');
    if (btnAddSubmit) btnAddSubmit.addEventListener('click', submitAddRole);

    var btnEditSubmit = document.getElementById('btn-edit-role-submit');
    if (btnEditSubmit) btnEditSubmit.addEventListener('click', submitEditRole);

    document.querySelectorAll('[data-dlg-cancel]').forEach(function (btn) {
        btn.addEventListener('click', function () {
            var dlgId = btn.getAttribute('data-dlg-cancel');
            document.getElementById(dlgId)?.hide();
        });
    });

    document.querySelectorAll('.btn-role-folder-access').forEach(function (btn) {
        btn.addEventListener('click', function () {
            openRoleFolderAccess(btn.dataset.name, btn.dataset.display);
        });
    });

    document.querySelectorAll('.btn-role-edit').forEach(function (btn) {
        btn.addEventListener('click', function () {
            openEditRole(btn.dataset.name, btn.dataset.display, btn.dataset.description, btn.dataset.policy);
        });
    });

    document.querySelectorAll('.btn-role-delete').forEach(function (btn) {
        btn.addEventListener('click', function () {
            deleteRole(btn.dataset.name, btn.dataset.users);
        });
    });

    ['form-add-role', 'form-edit-role'].forEach(function (fid) {
        var handlers = { 'form-add-role': submitAddRole, 'form-edit-role': submitEditRole };
        var form = document.getElementById(fid);
        if (!form) return;
        form.addEventListener('submit', function (e) {
            e.preventDefault();
            handlers[fid]();
        });
        form.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && e.target.tagName && e.target.tagName.toLowerCase() !== 'textarea') {
                e.preventDefault();
                handlers[fid]();
            }
        });
    });
})();
