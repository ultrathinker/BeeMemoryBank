(function () {
    var dataEl = document.getElementById('profile-page-data');
    var pageData = dataEl ? JSON.parse(dataEl.textContent || '{}') : {};
    var mcpBaseUrl = pageData.mcpBaseUrl || '';

    function escapeHtml(text) {
        return String(text == null ? '' : text)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function timeAgo(date) {
        var sec = Math.floor((Date.now() - date.getTime()) / 1000);
        if (sec < 60) return sec + 's ago';
        if (sec < 3600) return Math.floor(sec / 60) + 'm ago';
        if (sec < 86400) return Math.floor(sec / 3600) + 'h ago';
        return Math.floor(sec / 86400) + 'd ago';
    }

    function showAlert(variant, message) {
        var alertEl = document.getElementById('cp-alert');
        if (!alertEl) return;
        var icon = variant === 'success' ? 'check-circle' : 'exclamation-triangle';
        alertEl.innerHTML = '<sl-alert variant="' + variant + '" open closable><sl-icon slot="icon" name="' + icon + '"></sl-icon>' + escapeHtml(message) + '</sl-alert>';
        alertEl.style.display = 'block';
        setTimeout(function () { alertEl.style.display = 'none'; }, 5000);
    }

    function copyEl(id, message) {
        var el = document.getElementById(id);
        if (!el) return;
        navigator.clipboard.writeText(el.textContent);
        showAlert('success', message);
    }

    var btnOpenCreate = document.getElementById('btn-open-create-agent');
    if (btnOpenCreate) {
        btnOpenCreate.addEventListener('click', function () {
            document.getElementById('dlg-create-agent')?.show();
        });
    }

    var btnCancelCreate = document.getElementById('btn-cancel-create-agent');
    if (btnCancelCreate) {
        btnCancelCreate.addEventListener('click', function () {
            document.getElementById('dlg-create-agent')?.hide();
        });
    }

    var btnCopySecret = document.getElementById('btn-copy-secret');
    if (btnCopySecret) {
        btnCopySecret.addEventListener('click', function () {
            copyEl('secret-key-code', 'Key copied to clipboard');
        });
    }

    document.querySelectorAll('[data-copy-target]').forEach(function (btn) {
        btn.addEventListener('click', function () {
            var targetId = btn.getAttribute('data-copy-target');
            var msg = btn.getAttribute('data-copy-msg') || 'Copied';
            copyEl(targetId, msg);
        });
    });

    var btnCloseSecret = document.getElementById('btn-close-secret-dlg');
    if (btnCloseSecret) {
        btnCloseSecret.addEventListener('click', function () {
            document.getElementById('dlg-show-secret')?.hide();
        });
    }

    var btnChangePw = document.getElementById('btn-change-password');
    if (btnChangePw) {
        btnChangePw.addEventListener('click', function () {
            var btn = this;
            var oldPw = document.getElementById('cp-old').value;
            var newPw = document.getElementById('cp-new').value;
            var confirmPw = document.getElementById('cp-confirm').value;

            if (!oldPw || !newPw || !confirmPw) {
                showAlert('danger', 'All fields are required.');
                return;
            }
            if (newPw !== confirmPw) {
                showAlert('danger', 'New passwords do not match.');
                return;
            }
            if (newPw.length < 8 || !/[A-Z]/.test(newPw) || !/[a-z]/.test(newPw) || !/[0-9]/.test(newPw)) {
                showAlert('danger', 'Password must be at least 8 characters and contain uppercase, lowercase, and digit.');
                return;
            }

            btn.loading = true;
            fetch('/api-proxy/users/me/change-password', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ oldPassword: oldPw, newPassword: newPw })
            }).then(function (r) {
                if (r.ok) {
                    showAlert('success', 'Password changed successfully.');
                    document.getElementById('cp-old').value = '';
                    document.getElementById('cp-new').value = '';
                    document.getElementById('cp-confirm').value = '';
                } else {
                    return r.json().then(function (data) {
                        showAlert('danger', data.error || 'Failed to change password.');
                    });
                }
            }).catch(function () {
                showAlert('danger', 'Network error.');
            }).finally(function () {
                btn.loading = false;
            });
        });
    }

    function loadAgents() {
        var loadingEl = document.getElementById('agents-loading');
        var tableContainer = document.getElementById('agents-table-container');
        var emptyEl = document.getElementById('agents-empty');
        if (!loadingEl || !tableContainer || !emptyEl) return;

        loadingEl.style.display = 'block';
        tableContainer.style.display = 'none';
        emptyEl.style.display = 'none';

        fetch('/api-proxy/agents')
            .then(function (r) { return r.json(); })
            .then(function (data) {
                loadingEl.style.display = 'none';
                var countEl = document.getElementById('agents-count');
                if (countEl) countEl.textContent = 'Used ' + data.length + ' of 20 agents';

                if (!data || data.length === 0) {
                    emptyEl.style.display = 'block';
                    return;
                }

                var tbody = document.getElementById('agents-tbody');
                tbody.innerHTML = '';
                data.forEach(function (a) {
                    var tr = document.createElement('tr');
                    var autoUnlockCell = a.canAutoUnlock
                        ? '<sl-badge variant="warning" pill>Yes</sl-badge>'
                        : '<span class="text-muted text-sm">No</span>';
                    tr.innerHTML = '<td><strong>' + escapeHtml(a.name) + '</strong><br/><small class="text-muted">' + escapeHtml(a.description || '') + '</small></td>' +
                        '<td><code class="text-sm">' + escapeHtml(a.keyPrefix) + '</code></td>' +
                        '<td>' + autoUnlockCell + '</td>' +
                        '<td class="text-muted text-sm">' + new Date(a.createdAt).toLocaleDateString() + '</td>' +
                        '<td class="text-muted text-sm">' + (a.lastAccessedAt ? timeAgo(new Date(a.lastAccessedAt)) : 'never') + '</td>' +
                        '<td style="text-align:right;"><sl-icon-button name="trash" label="Delete" class="agent-delete-btn" data-agent-id="' + escapeHtml(a.id) + '" data-agent-name="' + escapeHtml(a.name) + '"></sl-icon-button></td>';
                    tbody.appendChild(tr);
                });
                tbody.querySelectorAll('.agent-delete-btn').forEach(function (btn) {
                    btn.addEventListener('click', function () {
                        deleteAgent(btn.dataset.agentId, btn.dataset.agentName);
                    });
                });
                tableContainer.style.display = 'block';
            });
    }

    function deleteAgent(id, name) {
        if (!confirm('Revoke agent "' + name + '"? Any applications using this key will lose access.')) return;

        fetch('/api-proxy/agents/' + id, { method: 'DELETE' })
            .then(function (r) {
                if (r.ok) {
                    showAlert('success', 'Agent revoked');
                    loadAgents();
                } else {
                    showAlert('danger', 'Failed to revoke agent');
                }
            });
    }

    var btnDoCreate = document.getElementById('btn-do-create-agent');
    if (btnDoCreate) {
        btnDoCreate.addEventListener('click', function () {
            var name = document.getElementById('agent-name').value;
            var desc = document.getElementById('agent-desc').value;
            var btn = this;

            if (!name) {
                alert('Name is required');
                return;
            }

            btn.loading = true;
            fetch('/api-proxy/agents', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name: name, description: desc })
            }).then(function (r) {
                if (r.ok) {
                    return r.json();
                } else {
                    return r.json().then(function (data) { throw new Error(data.error || 'Failed to create agent'); });
                }
            }).then(function (data) {
                document.getElementById('dlg-create-agent').hide();
                document.getElementById('agent-name').value = '';
                document.getElementById('agent-desc').value = '';

                var key = data.apiKey;
                var mcpUrl = mcpBaseUrl + '/mcp';

                document.getElementById('secret-key-code').textContent = key;
                document.getElementById('secret-autounlock-note').variant = data.canAutoUnlock ? 'warning' : 'neutral';
                document.getElementById('secret-autounlock-text').textContent = data.canAutoUnlock
                    ? 'This key can wake a locked node by itself, the same as your own login.'
                    : 'This key cannot unlock a locked node by itself — it only works while the vault is already unlocked by someone else.';
                document.getElementById('mcp-cli-code').textContent =
                    'claude mcp add --transport http bee-memory-bank ' + mcpUrl + ' --header "Authorization: Bearer ' + key + '"';
                document.getElementById('mcp-cursor-code').textContent =
                    '{\n  "mcpServers": {\n    "bee-memory-bank": {\n      "type": "http",\n      "url": "' + mcpUrl + '",\n      "headers": {\n        "Authorization": "Bearer ' + key + '"\n      }\n    }\n  }\n}';

                document.getElementById('dlg-show-secret').show();
                loadAgents();
            }).catch(function (err) {
                alert(err.message);
            }).finally(function () {
                btn.loading = false;
            });
        });
    }

    loadAgents();

    var autoApproveToggle = document.getElementById('chat-auto-approve-toggle');
    if (autoApproveToggle) {
        fetch('/api-proxy/chat/settings/auto-approve', { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok ? r.json() : { autoApproveWrites: false }; })
            .then(function (data) { autoApproveToggle.checked = !!data.autoApproveWrites; })
            .catch(function () {});

        autoApproveToggle.addEventListener('sl-change', function () {
            var enabled = autoApproveToggle.checked;
            fetch('/api-proxy/chat/settings/auto-approve', {
                method: 'PATCH',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ enabled: enabled })
            }).then(function (r) {
                if (!r.ok) autoApproveToggle.checked = !enabled;
            }).catch(function () { autoApproveToggle.checked = !enabled; });
        });
    }

    if (new URLSearchParams(window.location.search).get('createAgent') === '1') {
        var dlg = document.getElementById('dlg-create-agent');
        if (dlg && typeof dlg.show === 'function') {
            dlg.show();
        } else if (dlg) {
            customElements.whenDefined('sl-dialog').then(function () { dlg.show(); });
        }
    }
})();
