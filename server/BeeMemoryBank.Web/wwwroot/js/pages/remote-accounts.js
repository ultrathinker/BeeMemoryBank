(function () {
    let currentAccountId = null;

    function escapeHtml(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    async function refresh() {
        const resp = await fetch('/api-proxy/remote-accounts');
        const list = await resp.json();
        const container = document.getElementById('accounts-list');
        if (!container) return;
        if (!list || list.length === 0) {
            container.innerHTML = '<div class="card" style="padding:24px;text-align:center;color:var(--text-secondary);">No remote accounts configured yet.</div>';
            return;
        }
        container.innerHTML = list.map(function (a) {
            const status = a.lastSyncStatus || 'pending';
            const statusVariant = status === 'ok' ? 'success' : status === 'auth_failed' ? 'danger' : status === 'unreachable' ? 'warning' : 'neutral';
            return '<div class="card" style="padding:14px;margin-bottom:10px;display:flex;align-items:center;justify-content:space-between;">'
                + '<div><strong>' + escapeHtml(a.displayName) + '</strong> '
                + '<sl-badge variant="' + statusVariant + '" pill style="margin-left:6px;">' + status + '</sl-badge>'
                + '<div style="color:var(--text-secondary);font-size:0.85rem;">'
                + escapeHtml(a.username) + ' @@ ' + escapeHtml(a.baseUrl)
                + (a.lastSyncAt ? ' · last sync ' + new Date(a.lastSyncAt).toLocaleString() : '')
                + (a.lastError ? '<br><span style="color:var(--sl-color-danger-600);">' + escapeHtml(a.lastError) + '</span>' : '')
                + '</div></div>'
                + '<div>'
                + '<sl-button size="small" variant="default" class="subs-open-btn" data-account-id="' + escapeHtml(a.id) + '" data-account-name="' + escapeHtml(a.displayName) + '">'
                + '<sl-icon slot="prefix" name="diagram-2"></sl-icon> Subscriptions</sl-button> '
                + '<sl-icon-button name="trash" label="Delete" class="account-remove-btn" data-account-id="' + escapeHtml(a.id) + '"></sl-icon-button>'
                + '</div></div>';
        }).join('');

        container.querySelectorAll('.subs-open-btn').forEach(function (b) {
            b.addEventListener('click', function () { openSubsDialog(b.dataset.accountId, b.dataset.accountName); });
        });
        container.querySelectorAll('.account-remove-btn').forEach(function (b) {
            b.addEventListener('click', function () { removeAccount(b.dataset.accountId); });
        });
    }

    async function saveAccount() {
        const display = document.getElementById('add-display').value.trim();
        const url = document.getElementById('add-url').value.trim().replace(/\/+$/, '');
        const username = document.getElementById('add-username').value.trim();
        const password = document.getElementById('add-password').value;
        if (!display || !url || !username || !password) {
            showAddErr('All fields are required'); return;
        }
        const btn = document.getElementById('btn-save-account');
        if (btn) btn.loading = true;
        try {
            const resp = await fetch('/api-proxy/remote-accounts', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ displayName: display, baseUrl: url, username: username, password: password })
            });
            if (resp.ok) {
                document.getElementById('dlg-add-account')?.hide();
                ['add-display', 'add-url', 'add-username', 'add-password'].forEach(function (id) {
                    const el = document.getElementById(id);
                    if (el) el.value = '';
                });
                await refresh();
            } else {
                const data = await resp.json().catch(function () { return {}; });
                showAddErr(data.error || ('HTTP ' + resp.status));
            }
        } finally {
            if (btn) btn.loading = false;
        }
    }

    function showAddErr(msg) {
        const el = document.getElementById('add-error');
        if (el) {
            document.getElementById('add-error-msg').textContent = msg;
            el.style.display = '';
            el.open = true;
        }
    }

    async function removeAccount(id) {
        if (!confirm('Remove this remote account and all its mirrored folders?')) return;
        await fetch('/api-proxy/remote-accounts/' + id, { method: 'DELETE' });
        refresh();
    }

    async function openSubsDialog(id, name) {
        currentAccountId = id;
        document.getElementById('dlg-subs-account-info').textContent = 'Account: ' + name;
        document.getElementById('subs-current').innerHTML = '<sl-spinner></sl-spinner>';
        document.getElementById('subs-available').innerHTML = '<sl-spinner></sl-spinner>';
        document.getElementById('dlg-subs')?.show();

        try {
            const [current, accessibleResp] = await Promise.all([
                fetch('/api-proxy/remote-accounts/' + id + '/subscriptions').then(function (r) { return r.json(); }),
                fetch('/api-proxy/remote-accounts/' + id + '/accessible')
            ]);
            renderSubs(current || []);
            if (!accessibleResp.ok) {
                const data = await accessibleResp.json().catch(function () { return {}; });
                document.getElementById('subs-available').innerHTML =
                    '<div style="color:var(--sl-color-danger-600);">Failed to list available folders: ' + escapeHtml(data.error || ('HTTP ' + accessibleResp.status)) + '</div>';
                return;
            }
            const accessible = await accessibleResp.json();
            renderAvailable(accessible.folders || [], (current || []).map(function (s) { return s.remoteFolderId; }));
        } catch (e) {
            const errEl = document.getElementById('subs-error');
            if (errEl) {
                errEl.open = true;
                errEl.style.display = '';
                document.getElementById('subs-error-msg').textContent = String(e);
            }
        }
    }

    function renderSubs(list) {
        const c = document.getElementById('subs-current');
        if (!c) return;
        if (list.length === 0) { c.innerHTML = '<div class="text-muted">No folders mirrored yet.</div>'; return; }
        c.innerHTML = list.map(function (s) {
            return '<div style="display:flex;align-items:center;justify-content:space-between;padding:6px 0;border-bottom:1px solid var(--sl-color-neutral-200);">'
                + '<div><sl-icon name="cloud-check"></sl-icon> '
                + '<strong>' + escapeHtml(s.mountPath) + '</strong>'
                + ' <span class="text-muted">← ' + escapeHtml(s.remoteFolderPath) + '</span></div>'
                + '<sl-icon-button name="trash" label="Unmirror" class="sub-remove-btn" data-sub-id="' + escapeHtml(s.id) + '"></sl-icon-button>'
                + '</div>';
        }).join('');

        c.querySelectorAll('.sub-remove-btn').forEach(function (btn) {
            btn.addEventListener('click', function () {
                removeSub(btn.dataset.subId);
            });
        });
    }

    function renderAvailable(folders, subscribedIds) {
        const a = document.getElementById('subs-available');
        if (!a) return;
        if (folders.length === 0) { a.innerHTML = '<div class="text-muted">Nothing accessible — does this user have ACL on any folders?</div>'; return; }
        a.innerHTML = folders.map(function (f) {
            const already = subscribedIds.indexOf(f.id) !== -1;
            return '<div style="display:flex;align-items:center;justify-content:space-between;padding:6px 0;border-bottom:1px solid var(--sl-color-neutral-100);">'
                + '<div><sl-icon name="folder2"></sl-icon> ' + escapeHtml(f.path)
                + (f.isReadOnly ? ' <sl-badge variant="warning" pill>RO</sl-badge>' : '')
                + ' <sl-badge variant="neutral" pill>' + f.articleCount + '</sl-badge></div>'
                + (already
                    ? '<sl-badge variant="success" pill>Mirrored</sl-badge>'
                    : '<sl-button size="small" variant="primary" class="subs-add-btn" data-folder-id="' + escapeHtml(f.id) + '" data-folder-path="' + escapeHtml(f.path) + '">Mirror</sl-button>')
                + '</div>';
        }).join('');

        a.querySelectorAll('.subs-add-btn').forEach(function (b) {
            b.addEventListener('click', function () { addSub(b.dataset.folderId, b.dataset.folderPath); });
        });
    }

    async function addSub(remoteFolderId, remoteFolderPath) {
        const defaultMount = '/Shared' + remoteFolderPath;
        const mount = prompt('Mount path on this device:', defaultMount);
        if (!mount) return;
        const resp = await fetch('/api-proxy/remote-accounts/subscriptions', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                remoteAccountId: currentAccountId,
                remoteFolderId: remoteFolderId,
                remoteFolderPath: remoteFolderPath,
                mountPath: mount.trim()
            })
        });
        if (resp.ok) {
            openSubsDialog(currentAccountId, document.getElementById('dlg-subs-account-info').textContent.replace(/^Account: /, ''));
            refresh();
        } else {
            const data = await resp.json().catch(function () { return {}; });
            alert(data.error || 'Failed to subscribe');
        }
    }

    async function removeSub(id) {
        if (!confirm('Stop mirroring this folder?')) return;
        await fetch('/api-proxy/remote-accounts/subscriptions/' + id, { method: 'DELETE' });
        openSubsDialog(currentAccountId, document.getElementById('dlg-subs-account-info').textContent.replace(/^Account: /, ''));
    }

    var btnAddAccount = document.getElementById('btn-add-account');
    if (btnAddAccount) {
        btnAddAccount.addEventListener('click', function () {
            document.getElementById('dlg-add-account')?.show();
        });
    }

    var btnSaveAccount = document.getElementById('btn-save-account');
    if (btnSaveAccount) {
        btnSaveAccount.addEventListener('click', saveAccount);
    }

    document.querySelectorAll('[data-dlg-cancel]').forEach(function (btn) {
        btn.addEventListener('click', function () {
            var dlgId = btn.getAttribute('data-dlg-cancel');
            document.getElementById(dlgId)?.hide();
        });
    });

    refresh();
})();
