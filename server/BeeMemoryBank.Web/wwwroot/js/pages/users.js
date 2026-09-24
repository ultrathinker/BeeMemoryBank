(function () {
    var chatGloballyEnabled = true;
    fetch('/api-proxy/chat/settings/chat-enabled').then(function (r) { return r.ok ? r.json() : { chatGloballyEnabled: true }; })
        .then(function (data) {
            chatGloballyEnabled = !!data.chatGloballyEnabled;
            var cb = document.getElementById('edit-chat-access');
            var hint = document.getElementById('edit-chat-access-hint');
            if (cb) cb.disabled = !chatGloballyEnabled;
            if (hint) hint.style.display = chatGloballyEnabled ? 'none' : '';
        }).catch(function () {});

    function escapeHtml(text) {
        return String(text == null ? '' : text)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

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

    function validatePasswordClient(pwd) {
        if (!pwd || pwd.length < 8) return 'Password must be at least 8 characters long.';
        if (!/[A-Z]/.test(pwd)) return 'Password must contain at least one uppercase letter.';
        if (!/[a-z]/.test(pwd)) return 'Password must contain at least one lowercase letter.';
        if (!/[0-9]/.test(pwd)) return 'Password must contain at least one digit.';
        return null;
    }

    async function postJson(url, body, method) {
        var resp = await fetch(url, {
            method: method || 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
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
        } catch (e) {}
        return { ok: false, error: msg };
    }

    function openAddUser() {
        hideDlgError('add-user-error');
        document.getElementById('add-username').value = '';
        document.getElementById('add-display-name').value = '';
        document.getElementById('add-password').value = '';
        document.getElementById('add-role').value = 'user';
        document.getElementById('dlg-add-user')?.show();
        setTimeout(function () { document.getElementById('add-username')?.focus(); }, 100);
    }

    async function submitAddUser() {
        hideDlgError('add-user-error');
        var username = document.getElementById('add-username').value.trim();
        var displayName = document.getElementById('add-display-name').value.trim();
        var password = document.getElementById('add-password').value;
        var role = document.getElementById('add-role').value;

        if (!username) { showDlgError('add-user-error', 'Username is required.'); return; }
        if (!displayName) displayName = username;
        var pwdErr = validatePasswordClient(password);
        if (pwdErr) { showDlgError('add-user-error', pwdErr); return; }

        var btn = document.getElementById('btn-add-user-submit');
        if (btn) btn.loading = true;
        var result = await postJson('/api-proxy/users', { username: username, displayName: displayName, password: password, role: role });
        if (btn) btn.loading = false;
        if (result.ok) {
            window.location = '?msg=' + encodeURIComponent("User '" + username + "' created");
        } else {
            showDlgError('add-user-error', result.error);
        }
    }

    function openChangePassword(userId, username) {
        hideDlgError('change-password-error');
        document.getElementById('cp-user-id').value = userId;
        document.getElementById('cp-user-name').textContent = 'User: ' + username;
        document.getElementById('cp-new-password').value = '';
        document.getElementById('dlg-change-password')?.show();
        setTimeout(function () { document.getElementById('cp-new-password')?.focus(); }, 100);
    }

    async function submitChangePassword() {
        hideDlgError('change-password-error');
        var id = document.getElementById('cp-user-id').value;
        var newPassword = document.getElementById('cp-new-password').value;
        var pwdErr = validatePasswordClient(newPassword);
        if (pwdErr) { showDlgError('change-password-error', pwdErr); return; }

        var btn = document.getElementById('btn-change-password-submit');
        if (btn) btn.loading = true;
        var result = await postJson('/api-proxy/users/' + id + '/change-password', { newPassword: newPassword });
        if (btn) btn.loading = false;
        if (result.ok) {
            window.location = '?msg=' + encodeURIComponent('Password changed');
        } else {
            showDlgError('change-password-error', result.error);
        }
    }

    var editUserOriginalRole = null;
    var rolesByName = {};

    function updatePromoteHint() {
        var hint = document.getElementById('edit-promote-hint');
        if (!hint) return;
        var roleEl = document.getElementById('edit-role');
        var promoting = roleEl && roleEl.value === 'superadmin' && editUserOriginalRole !== 'superadmin';
        hint.style.display = promoting ? '' : 'none';
    }

    function updateRoleHint() {
        var hint = document.getElementById('edit-role-hint');
        if (!hint) return;
        var roleEl = document.getElementById('edit-role');
        var name = roleEl ? roleEl.value : '';
        var role = rolesByName[name];
        if (!role || role.isSystem) { hint.style.display = 'none'; return; }
        hint.textContent = "Folder access comes from the '" + role.displayName + "' role (" +
            role.ruleCount + (role.ruleCount === 1 ? ' rule' : ' rules') +
            (role.basePolicy === 'closed' ? ', hidden by default' : '') +
            '). Edit it on the Roles page.';
        hint.style.display = '';
    }

    var editRoleEl = document.getElementById('edit-role');
    if (editRoleEl) {
        editRoleEl.addEventListener('sl-change', function () {
            updatePromoteHint();
            updateRoleHint();
        });
    }

    function optionsHtmlFor(roles) {
        return roles.map(function (r) {
            var label = escapeHtml(r.displayName || r.name);
            if (!r.isSystem) label += ' <small style="opacity:.65;">(' + escapeHtml(r.name) + ')</small>';
            return '<sl-option value="' + escapeHtml(r.name) + '">' + label + '</sl-option>';
        }).join('');
    }

    fetch('/api-proxy/roles').then(function (r) { return r.ok ? r.json() : null; })
        .then(function (roles) {
            if (!roles || roles.length === 0) return;
            roles.forEach(function (r) { rolesByName[r.name] = r; });
            var html = optionsHtmlFor(roles);
            ['add-role', 'edit-role'].forEach(function (id) {
                var sel = document.getElementById(id);
                if (!sel) return;
                var keep = sel.value;
                sel.innerHTML = html;
                sel.value = keep && rolesByName[keep] ? keep : 'user';
            });
            updateRoleHint();
        }).catch(function () {});

    function openUserFolderAccess(userId, username, roleName) {
        var managedByRole = roleName && roleName !== 'user' && roleName !== 'superadmin';
        if (window.folderAccess && window.folderAccess.open) {
            window.folderAccess.open('user', userId,
                managedByRole
                    ? 'User: ' + username + '  —  folder access comes from the ' + roleName + ' role'
                    : 'User: ' + username,
                { inheritedRole: roleName || 'user', readOnly: managedByRole });
        }
    }

    function openEditUser(userId, displayName, role, chatAccess) {
        hideDlgError('edit-user-error');
        document.getElementById('edit-user-id').value = userId;
        document.getElementById('edit-display-name').value = displayName;
        document.getElementById('edit-role').value = role;
        editUserOriginalRole = role;
        updatePromoteHint();
        updateRoleHint();
        var cb = document.getElementById('edit-chat-access');
        var hint = document.getElementById('edit-chat-access-hint');
        if (cb) {
            cb.checked = (chatAccess === 'true');
            cb.disabled = !chatGloballyEnabled;
        }
        if (hint) hint.style.display = chatGloballyEnabled ? 'none' : '';
        document.getElementById('dlg-edit-user')?.show();
    }

    async function submitEditUser() {
        hideDlgError('edit-user-error');
        var id = document.getElementById('edit-user-id').value;
        var displayName = document.getElementById('edit-display-name').value.trim();
        var role = document.getElementById('edit-role').value;
        var chatAccess = document.getElementById('edit-chat-access').checked;
        if (!displayName) { showDlgError('edit-user-error', 'Display Name is required.'); return; }

        var btn = document.getElementById('btn-edit-user-submit');
        if (btn) btn.loading = true;
        var result = await postJson('/api-proxy/users/' + id, { displayName: displayName, role: role, chatAccess: chatAccess }, 'PUT');
        if (btn) btn.loading = false;
        if (result.ok) {
            window.location = '?msg=' + encodeURIComponent('User updated');
        } else {
            showDlgError('edit-user-error', result.error);
        }
    }

    // Attach listeners
    var btnOpenAdd = document.getElementById('btn-open-add-user');
    if (btnOpenAdd) btnOpenAdd.addEventListener('click', openAddUser);

    var btnAddSubmit = document.getElementById('btn-add-user-submit');
    if (btnAddSubmit) btnAddSubmit.addEventListener('click', submitAddUser);

    var btnChangePwSubmit = document.getElementById('btn-change-password-submit');
    if (btnChangePwSubmit) btnChangePwSubmit.addEventListener('click', submitChangePassword);

    var btnEditSubmit = document.getElementById('btn-edit-user-submit');
    if (btnEditSubmit) btnEditSubmit.addEventListener('click', submitEditUser);

    document.querySelectorAll('[data-dlg-cancel]').forEach(function (btn) {
        btn.addEventListener('click', function () {
            var dlgId = btn.getAttribute('data-dlg-cancel');
            document.getElementById(dlgId)?.hide();
        });
    });

    document.querySelectorAll('.btn-user-folder-access').forEach(function (btn) {
        btn.addEventListener('click', function () {
            openUserFolderAccess(btn.dataset.uid, btn.dataset.name, btn.dataset.role);
        });
    });

    document.querySelectorAll('.btn-user-change-pw').forEach(function (btn) {
        btn.addEventListener('click', function () {
            openChangePassword(btn.dataset.uid, btn.dataset.name);
        });
    });

    document.querySelectorAll('.btn-user-edit').forEach(function (btn) {
        btn.addEventListener('click', function () {
            openEditUser(btn.dataset.uid, btn.dataset.display, btn.dataset.role, btn.dataset.chatAccess);
        });
    });

    document.querySelectorAll('.btn-user-delete').forEach(function (btn) {
        btn.addEventListener('click', function () {
            if (confirm('Delete user ' + btn.dataset.name + '?')) {
                var formId = btn.getAttribute('data-form-id');
                document.getElementById(formId)?.submit();
            }
        });
    });

    // Enter key submits the active dialog
    ['form-add-user', 'form-change-password', 'form-edit-user'].forEach(function(fid) {
        var handlers = { 'form-add-user': submitAddUser, 'form-change-password': submitChangePassword, 'form-edit-user': submitEditUser };
        var form = document.getElementById(fid);
        if (form) {
            form.addEventListener('keydown', function(e) {
                if (e.key === 'Enter' && e.target.tagName && e.target.tagName.toLowerCase() !== 'textarea') {
                    e.preventDefault();
                    handlers[fid]();
                }
            });
        }
    });
})();
