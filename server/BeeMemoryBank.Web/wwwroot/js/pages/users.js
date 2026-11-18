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

    // The one busy helper (BMB-311): the button spins while the node answers and the reason of a refusal comes back in full.
    // Resolves { ok, error }; the dialog that asked writes the error next to the button.
    async function postJson(url, body, method, btn) {
        var outcome = { ok: false, error: 'This is already in progress.' };
        outcome.ok = await bmbBusy.run(btn || null, function () {
            return bmbBusy.request(url, {
                method: method || 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body)
            });
        }, { quiet: true, error: function (err) { outcome.error = err.message; } });
        return outcome;
    }

    // Choosing superadmin in the Add dialog is a promotion too: say in plain words what it gives.
    function updateAddSuperadminWarning() {
        var warning = document.getElementById('add-superadmin-warning');
        var roleEl = document.getElementById('add-role');
        if (warning && roleEl) warning.style.display = roleEl.value === 'superadmin' ? '' : 'none';
    }

    function openAddUser() {
        hideDlgError('add-user-error');
        document.getElementById('add-username').value = '';
        document.getElementById('add-display-name').value = '';
        document.getElementById('add-password').value = '';
        document.getElementById('add-password-hint').value = '';
        document.getElementById('add-role').value = 'user';
        updateAddSuperadminWarning();
        document.getElementById('dlg-add-user')?.show();
        setTimeout(function () { document.getElementById('add-username')?.focus(); }, 100);
    }

    async function submitAddUser() {
        hideDlgError('add-user-error');
        var username = document.getElementById('add-username').value.trim();
        var displayName = document.getElementById('add-display-name').value.trim();
        var password = document.getElementById('add-password').value;
        var passwordHint = (document.getElementById('add-password-hint').value || '').trim();
        var role = document.getElementById('add-role').value;

        if (!username) { showDlgError('add-user-error', 'Username is required.'); return; }
        if (!displayName) displayName = username;
        var pwdErr = validatePasswordClient(password);
        if (pwdErr) { showDlgError('add-user-error', pwdErr); return; }
        if (!passwordHint) { showDlgError('add-user-error', 'A password hint is required: it is what the sign-in page shows if this person forgets the password.'); return; }

        var btn = document.getElementById('btn-add-user-submit');
        var result = await postJson('/api-proxy/users', { username: username, displayName: displayName, password: password, role: role, passwordHint: passwordHint }, 'POST', btn);
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
        document.getElementById('cp-password-hint').value = '';
        document.getElementById('dlg-change-password')?.show();
        setTimeout(function () { document.getElementById('cp-new-password')?.focus(); }, 100);
    }

    async function submitChangePassword() {
        hideDlgError('change-password-error');
        var id = document.getElementById('cp-user-id').value;
        var newPassword = document.getElementById('cp-new-password').value;
        var passwordHint = (document.getElementById('cp-password-hint').value || '').trim();
        var pwdErr = validatePasswordClient(newPassword);
        if (pwdErr) { showDlgError('change-password-error', pwdErr); return; }
        if (!passwordHint) { showDlgError('change-password-error', 'A password hint is required: it is what the sign-in page shows if this person forgets the password.'); return; }

        var btn = document.getElementById('btn-change-password-submit');
        var result = await postJson('/api-proxy/users/' + id + '/change-password', { newPassword: newPassword, passwordHint: passwordHint }, 'POST', btn);
        if (result.ok) {
            window.location = '?msg=' + encodeURIComponent('Password changed');
        } else {
            showDlgError('change-password-error', result.error);
        }
    }

    var editUserOriginalRole = null;
    var rolesByName = {};

    // The role of the person being edited is a superadmin's or it is not. The Edit dialog does not move anybody across that line, either way: a superadmin's role is
    // shown, not chosen, and the choice of the others has no superadmin in it. "Make superadmin..." and "Remove superadmin rights..." are separate actions with their own
    // dialogs, because each says what it gives or takes away and the second needs an answer to "what may they see afterwards?".
    function editIsSuperadmin() { return editUserOriginalRole === 'superadmin'; }

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
            updateRoleHint();
        });
    }

    var addRoleEl = document.getElementById('add-role');
    if (addRoleEl) addRoleEl.addEventListener('sl-change', updateAddSuperadminWarning);

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
            // Add keeps the whole list (a new person may be created as a superadmin, and the dialog says what that is); Edit never offers superadmin.
            var htmlFor = {
                'add-role': optionsHtmlFor(roles),
                'edit-role': optionsHtmlFor(roles.filter(function (r) { return r.name !== 'superadmin'; })),
            };
            Object.keys(htmlFor).forEach(function (id) {
                var sel = document.getElementById(id);
                if (!sel) return;
                var keep = sel.value;
                sel.innerHTML = htmlFor[id];
                sel.value = keep && rolesByName[keep] && !(id === 'edit-role' && keep === 'superadmin') ? keep : 'user';
            });
            fillRemovalRoles();
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
        editUserOriginalRole = role;
        var fixed = document.getElementById('edit-role-fixed');
        var choice = document.getElementById('edit-role-choice');
        if (fixed) fixed.style.display = role === 'superadmin' ? '' : 'none';
        if (choice) choice.style.display = role === 'superadmin' ? 'none' : '';
        if (role !== 'superadmin') document.getElementById('edit-role').value = role;
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
        var chatAccess = document.getElementById('edit-chat-access').checked;
        if (!displayName) { showDlgError('edit-user-error', 'Display Name is required.'); return; }

        // A superadmin's role is not part of this form: no role is sent, so the role is not touched. For everybody else the choice never holds 'superadmin'.
        var body = { displayName: displayName, chatAccess: chatAccess };
        if (!editIsSuperadmin()) {
            var role = document.getElementById('edit-role').value;
            if (role === 'superadmin') { showDlgError('edit-user-error', 'Use "Make superadmin..." for that: it says what it gives.'); return; }
            body.role = role;
        }

        var btn = document.getElementById('btn-edit-user-submit');
        var result = await postJson('/api-proxy/users/' + id, body, 'PUT', btn);
        if (result.ok) {
            window.location = '?msg=' + encodeURIComponent('User updated');
        } else {
            showDlgError('edit-user-error', result.error);
        }
    }

    // ---- Make superadmin...: a confirmation that says in plain words what the person gets (the text is the dialog's) ----------------------------------------------

    function openMakeSuperadmin(userId, username, displayName) {
        hideDlgError('make-superadmin-error');
        document.getElementById('make-sa-user-id').value = userId;
        document.getElementById('make-sa-display').value = displayName || username;
        document.getElementById('make-sa-name').textContent = username;
        document.getElementById('dlg-make-superadmin')?.show();
    }

    async function submitMakeSuperadmin() {
        hideDlgError('make-superadmin-error');
        var id = document.getElementById('make-sa-user-id').value;
        var displayName = document.getElementById('make-sa-display').value;
        var btn = document.getElementById('btn-make-sa-confirm');
        var result = await postJson('/api-proxy/users/' + id, { displayName: displayName, role: 'superadmin' }, 'PUT', btn);
        if (result.ok) {
            window.location = '?msg=' + encodeURIComponent('Made a superadmin. They keep their password; the key to unlock this server is created at their next sign-in.');
        } else {
            showDlgError('make-superadmin-error', result.error);
        }
    }

    // ---- Remove superadmin rights...: cannot be confirmed before the answer to "what may they see afterwards?" is complete --------------------------------------

    var removeChosenFolders = [];   // [{ id, path, readOnly }]
    var removeSearchTimer = null;

    function removeChoice() {
        var group = document.getElementById('remove-sa-choice');
        return group ? group.value || '' : '';
    }

    // What happens to the account at the end of the removal: 'demote' (an ordinary user, with an access choice) or 'delete'. Nothing is pre-selected.
    function removeEnd() {
        var group = document.getElementById('remove-sa-end');
        return group ? group.value || '' : '';
    }

    function removePassword() {
        var input = document.getElementById('remove-sa-password');
        return input ? input.value || '' : '';
    }

    // The access choice the dialog holds, or null while it is incomplete: folders need at least one folder, a role needs one picked, the open role needs the tick.
    // What comes back is what the API is sent (the same fields as PUT /api/users/{id}), so there is one reading of "complete" and it is this one.
    function removeAccessChoice() {
        var choice = removeChoice();
        if (choice === 'folders') {
            if (removeChosenFolders.length === 0) return null;
            return {
                role: 'user',
                folderRules: removeChosenFolders.map(function (f) { return { folderId: f.id, effect: 'allow', isReadOnly: !!f.readOnly }; }),
            };
        }
        if (choice === 'role') {
            var role = (document.getElementById('remove-sa-role') || {}).value;
            return role ? { role: role } : null;
        }
        if (choice === 'open') {
            var ack = document.getElementById('remove-sa-open-ack');
            return ack && ack.checked ? { role: 'user', allowOpenRole: true } : null;
        }
        return null;
    }

    function refreshRemoveDialog() {
        var choice = removeChoice();
        var end = removeEnd();
        var access = document.getElementById('remove-sa-panel-access');
        if (access) access.style.display = end === 'demote' ? '' : 'none';
        [['folders', 'remove-sa-panel-folders'], ['role', 'remove-sa-panel-role'], ['open', 'remove-sa-panel-open']].forEach(function (pair) {
            var panel = document.getElementById(pair[1]);
            if (panel) panel.style.display = choice === pair[0] ? '' : 'none';
        });
        var confirmBtn = document.getElementById('btn-remove-sa-confirm');
        if (confirmBtn) confirmBtn.disabled = removeRequest() === null;
    }

    // The request the dialog sends (its verb and body; postJson sends it behind the busy helper), or null while it is incomplete: the end is chosen, a
    // demotion carries a complete access choice, and the password is typed.
    function removeRequest() {
        var end = removeEnd();
        if (end !== 'demote' && end !== 'delete') return null;
        if (!removePassword()) return null;
        if (end === 'delete') return { verb: 'DELETE', body: { adminPassword: removePassword() } };
        var choice = removeAccessChoice();
        if (choice === null) return null;
        return { verb: 'PUT', body: Object.assign({ displayName: document.getElementById('remove-sa-display').value, adminPassword: removePassword() }, choice) };
    }

    function renderRemoveFolders() {
        var list = document.getElementById('remove-sa-folder-list');
        if (!list) return;
        list.innerHTML = removeChosenFolders.map(function (f, i) {
            return '<div style="display:flex;align-items:center;gap:8px;padding:4px 0;">' +
                '<sl-icon name="folder2"></sl-icon><span style="flex:1;word-break:break-all;">' + escapeHtml(f.path) + '</span>' +
                '<label style="font-size:0.82rem;"><input type="checkbox" class="remove-sa-readonly" data-i="' + i + '"' + (f.readOnly ? ' checked' : '') + '> read-only</label>' +
                '<sl-icon-button name="x-lg" label="Take this folder out" class="remove-sa-folder-out" data-i="' + i + '"></sl-icon-button></div>';
        }).join('');
        list.querySelectorAll('.remove-sa-folder-out').forEach(function (btn) {
            btn.addEventListener('click', function () {
                removeChosenFolders.splice(parseInt(btn.dataset.i, 10), 1);
                renderRemoveFolders();
                refreshRemoveDialog();
            });
        });
        list.querySelectorAll('.remove-sa-readonly').forEach(function (box) {
            box.addEventListener('change', function () {
                removeChosenFolders[parseInt(box.dataset.i, 10)].readOnly = box.checked;
            });
        });
    }

    function addRemoveFolder(folder) {
        if (!folder || !folder.id || removeChosenFolders.some(function (f) { return f.id === folder.id; })) return;
        removeChosenFolders.push({ id: folder.id, path: folder.path, readOnly: false });
        var search = document.getElementById('remove-sa-folder-search');
        var dropdown = document.getElementById('remove-sa-folder-dropdown');
        if (search) search.value = '';
        if (dropdown) { dropdown.style.display = 'none'; dropdown.innerHTML = ''; }
        renderRemoveFolders();
        refreshRemoveDialog();
    }

    async function searchRemoveFolders(q) {
        var dropdown = document.getElementById('remove-sa-folder-dropdown');
        if (!dropdown) return;
        if (!q) { dropdown.style.display = 'none'; dropdown.innerHTML = ''; return; }
        try {
            var resp = await fetch('/api-proxy/folders/search?q=' + encodeURIComponent(q) + '&limit=50');
            var data = resp.ok ? await resp.json() : { folders: [] };
            var taken = removeChosenFolders.map(function (f) { return f.id; });
            var folders = (data.folders || []).filter(function (f) { return taken.indexOf(f.id) < 0; });
            folders.sort(function (a, b) { return a.path.length - b.path.length || a.path.localeCompare(b.path); });
            dropdown.innerHTML = folders.length === 0
                ? '<div style="padding:8px 12px;color:var(--sl-color-neutral-500);font-size:0.9rem;">No matching folders</div>'
                : folders.map(function (f) {
                    return '<div class="remove-sa-folder-item" data-id="' + escapeHtml(f.id) + '" data-path="' + escapeHtml(f.path) +
                        '" style="padding:8px 12px;cursor:pointer;border-bottom:1px solid var(--sl-color-neutral-100);"><sl-icon name="folder2" style="margin-right:6px;"></sl-icon>' + escapeHtml(f.path) + '</div>';
                }).join('');
            dropdown.style.display = 'block';
            dropdown.querySelectorAll('.remove-sa-folder-item').forEach(function (item) {
                item.addEventListener('click', function () { addRemoveFolder({ id: item.dataset.id, path: item.dataset.path }); });
            });
        } catch (e) {
            dropdown.innerHTML = '<div style="padding:8px 12px;color:var(--sl-color-danger-600);">The search failed. Try again.</div>';
            dropdown.style.display = 'block';
        }
    }

    // The roles that hide folders by default are the ones that are an access choice on their own; an open role is the third option, said out loud.
    function fillRemovalRoles() {
        var sel = document.getElementById('remove-sa-role');
        if (!sel) return;
        var closed = Object.keys(rolesByName).map(function (n) { return rolesByName[n]; })
            .filter(function (r) { return r.name !== 'superadmin' && r.basePolicy === 'closed'; });
        sel.innerHTML = optionsHtmlFor(closed);
        sel.value = '';
        var empty = document.getElementById('remove-sa-role-empty');
        if (empty) empty.style.display = closed.length === 0 ? '' : 'none';
    }

    function openRemoveSuperadmin(userId, username, displayName, end) {
        hideDlgError('remove-superadmin-error');
        document.getElementById('remove-sa-user-id').value = userId;
        document.getElementById('remove-sa-display').value = displayName || username;
        document.getElementById('remove-sa-name').textContent = username;
        var endGroup = document.getElementById('remove-sa-end');
        if (endGroup) endGroup.value = end === 'delete' ? 'delete' : '';   // the trash button asks for a deletion; the shield button chooses nothing
        var password = document.getElementById('remove-sa-password');
        if (password) password.value = '';
        var group = document.getElementById('remove-sa-choice');
        if (group) group.value = '';   // no choice is made for them: the button stays off until one is
        removeChosenFolders = [];
        renderRemoveFolders();
        fillRemovalRoles();
        var ack = document.getElementById('remove-sa-open-ack');
        if (ack) ack.checked = false;
        refreshRemoveDialog();
        document.getElementById('dlg-remove-superadmin')?.show();
    }

    async function submitRemoveSuperadmin() {
        hideDlgError('remove-superadmin-error');
        var request = removeRequest();
        if (request === null) { refreshRemoveDialog(); return; }   // the button is off then; this is for Enter and for a script
        var id = document.getElementById('remove-sa-user-id').value;
        var btn = document.getElementById('btn-remove-sa-confirm');
        var result = await postJson('/api-proxy/users/' + id, request.body, request.verb, btn);
        var password = document.getElementById('remove-sa-password');
        if (password) password.value = '';
        if (result.ok) {
            window.location = '?msg=' + encodeURIComponent('The removal has started: they are signed out and locked out now. Follow its steps under Admin - People being removed.');
        } else {
            showDlgError('remove-superadmin-error', result.error);
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

    var btnMakeSa = document.getElementById('btn-make-sa-confirm');
    if (btnMakeSa) btnMakeSa.addEventListener('click', submitMakeSuperadmin);

    var btnRemoveSa = document.getElementById('btn-remove-sa-confirm');
    if (btnRemoveSa) btnRemoveSa.addEventListener('click', submitRemoveSuperadmin);
    ['remove-sa-choice', 'remove-sa-role', 'remove-sa-open-ack', 'remove-sa-end'].forEach(function (id) {
        var el = document.getElementById(id);
        if (el) el.addEventListener('sl-change', refreshRemoveDialog);
    });
    var removePasswordInput = document.getElementById('remove-sa-password');
    if (removePasswordInput) removePasswordInput.addEventListener('sl-input', refreshRemoveDialog);
    var removeSearch = document.getElementById('remove-sa-folder-search');
    if (removeSearch) removeSearch.addEventListener('sl-input', function () {
        clearTimeout(removeSearchTimer);
        var q = (removeSearch.value || '').trim();
        removeSearchTimer = setTimeout(function () { searchRemoveFolders(q); }, 200);
    });

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

    document.querySelectorAll('.btn-user-make-superadmin').forEach(function (btn) {
        btn.addEventListener('click', function () {
            openMakeSuperadmin(btn.dataset.uid, btn.dataset.name, btn.dataset.display);
        });
    });

    document.querySelectorAll('.btn-user-remove-superadmin').forEach(function (btn) {
        btn.addEventListener('click', function () {
            openRemoveSuperadmin(btn.dataset.uid, btn.dataset.name, btn.dataset.display, btn.dataset.end);
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
                bmbBusy.submit(document.getElementById(formId));
            }
        });
    });

    // A link from the folder marks of the tree (BMB-338) is /Users?access=<user id>: open that user's Folder Access dialog right away.
    // The custom elements are upgraded after this script has run, and a dialog is shown or filled before that only half-way, so wait for them.
    (function openRequestedFolderAccess() {
        var url = new URL(window.location.href);
        var wanted = url.searchParams.get('access');
        if (!wanted) return;
        var button = Array.prototype.find.call(document.querySelectorAll('.btn-user-folder-access'), function (b) { return b.dataset.uid === wanted; });
        // Take it out of the address so that a reload (or a bookmark) does not reopen the dialog.
        url.searchParams.delete('access');
        try { history.replaceState(history.state, '', url.pathname + url.search + url.hash); } catch (e) { }
        if (!button) return;
        Promise.all(['sl-dialog', 'sl-input', 'sl-select', 'sl-switch', 'sl-icon-button', 'sl-badge'].map(function (tag) { return customElements.whenDefined(tag); }))
            .then(function () { openUserFolderAccess(button.dataset.uid, button.dataset.name, button.dataset.role); });
    })();

    // Form submit or Enter key submits the active dialog
    ['form-add-user', 'form-change-password', 'form-edit-user'].forEach(function(fid) {
        var handlers = { 'form-add-user': submitAddUser, 'form-change-password': submitChangePassword, 'form-edit-user': submitEditUser };
        var form = document.getElementById(fid);
        if (form) {
            form.addEventListener('submit', function(e) {
                e.preventDefault();
                handlers[fid]();
            });
            form.addEventListener('keydown', function(e) {
                if (e.key === 'Enter' && e.target.tagName && e.target.tagName.toLowerCase() !== 'textarea') {
                    e.preventDefault();
                    handlers[fid]();
                }
            });
        }
    });
})();
