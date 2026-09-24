// Admin Page Module
(function () {
    'use strict';

    // Auto-refresh for in-progress software update
    var updateContainer = document.querySelector('[data-is-updating="true"]');
    if (updateContainer && !window._updateRefreshing) {
        window._updateRefreshing = true;
        setTimeout(function () { location.reload(); }, 2000);
    }

    // Auto-refresh for active DEK rotation
    var dekContainer = document.querySelector('[data-dek-active="true"]');
    if (dekContainer && !window._dekRotationRefreshing) {
        window._dekRotationRefreshing = true;
        setTimeout(function () { location.reload(); }, 3000);
    }

    // Check for updates dialog
    var btnOpenCheckUpdate = document.getElementById('btn-open-check-update');
    if (btnOpenCheckUpdate) {
        btnOpenCheckUpdate.addEventListener('click', function () {
            var dlg = document.getElementById('dlg-check-update');
            if (dlg) dlg.show();
        });
    }

    // Local sync events counter
    var localCounter = document.getElementById('local-events-counter');
    if (localCounter) {
        fetch('/api-proxy/sync/status')
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (localCounter && data && data.totalLocalEvents != null) {
                    localCounter.textContent = 'Local events: ' + data.totalLocalEvents;
                }
            })
            .catch(function () {});
    }

    // Compaction info dialog
    var compactionBtn = document.getElementById('compaction-info-btn');
    var compactionDlg = document.getElementById('compaction-info-dialog');
    var compactionClose = document.getElementById('compaction-info-close');
    if (compactionBtn && compactionDlg) {
        compactionBtn.addEventListener('click', function () { compactionDlg.show(); });
    }
    if (compactionClose && compactionDlg) {
        compactionClose.addEventListener('click', function () { compactionDlg.hide(); });
    }

    // AI / Chat Settings
    var keysBody = document.getElementById('chat-keys-body');
    var modelsBody = document.getElementById('chat-models-body');

    function esc(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function rel(t) {
        if (!t) return '—';
        var d = new Date(t);
        if (isNaN(d)) return esc(t);
        var s = (Date.now() - d.getTime()) / 1000;
        if (s < 60) return Math.floor(s) + 's ago';
        if (s < 3600) return Math.floor(s / 60) + 'm ago';
        if (s < 86400) return Math.floor(s / 3600) + 'h ago';
        return Math.floor(s / 86400) + 'd ago';
    }

    function keyStatus(k) {
        if (!k.enabled) return { label: 'Disabled', color: 'var(--sl-color-danger-600)' };
        if (k.disabledUntil) {
            var d = new Date(k.disabledUntil);
            if (!isNaN(d) && d.getTime() > Date.now()) {
                return { label: 'Cooling down', color: 'var(--sl-color-warning-600)', title: 'auto-retries after ' + rel(k.disabledUntil) };
            }
        }
        if (k.lastError) return { label: 'Active', color: 'var(--sl-color-success-600)', title: 'last error: ' + k.lastError };
        return { label: 'Active', color: 'var(--sl-color-success-600)' };
    }

    function loadKeys() {
        if (!keysBody) return;
        fetch('/api-proxy/chat/keys')
            .then(function (r) { return r.json(); })
            .then(function (keys) {
                if (!Array.isArray(keys) || keys.length === 0) {
                    keysBody.innerHTML = '<tr><td colspan="7" class="text-muted text-sm" style="text-align:center;padding:16px;">No keys. Add one to enable AI chat.</td></tr>';
                    return;
                }
                keysBody.innerHTML = keys.map(function (k) {
                    var st = keyStatus(k);
                    return '<tr>'
                        + '<td><strong>' + esc(k.label) + '</strong></td>'
                        + '<td><code class="text-sm">' + esc(k.keyPrefix) + '</code></td>'
                        + '<td class="text-muted text-sm">' + (k.priority || 0) + '</td>'
                        + '<td><sl-switch data-key-id="' + k.id + '" class="switch-chat-key" ' + (k.enabled ? 'checked' : '') + '></sl-switch></td>'
                        + '<td class="text-sm" style="color:' + st.color + ';"' + (st.title ? ' title="' + esc(st.title) + '"' : '') + '>' + st.label + '</td>'
                        + '<td class="text-muted text-sm">' + rel(k.lastUsedAt) + '</td>'
                        + '<td style="text-align:right;"><sl-icon-button name="trash" label="Delete" class="btn-delete-chat-key" data-key-id="' + k.id + '"></sl-icon-button></td>'
                        + '</tr>';
                }).join('');
            })
            .catch(function () {
                if (keysBody) keysBody.innerHTML = '<tr><td colspan="7" class="text-muted text-sm">Failed to load keys.</td></tr>';
            });
    }

    var modelsCache = [];
    var editingModelId = null;

    function loadModels() {
        if (!modelsBody) return;
        fetch('/api-proxy/chat/models/all')
            .then(function (r) { return r.json(); })
            .then(function (models) {
                modelsCache = Array.isArray(models) ? models : [];
                if (modelsCache.length === 0) {
                    modelsBody.innerHTML = '<tr><td colspan="6" class="text-muted text-sm" style="text-align:center;padding:16px;">No models. Add one (with the Text capability) to enable AI chat.</td></tr>';
                    loadDefaults();
                    return;
                }
                modelsBody.innerHTML = modelsCache.map(function (m) {
                    return '<tr>'
                        + '<td><strong>' + esc(m.label) + '</strong></td>'
                        + '<td><code class="text-sm">' + esc(m.modelId) + '</code></td>'
                        + '<td style="text-align:center;"><sl-checkbox disabled ' + (m.isText ? 'checked' : '') + '></sl-checkbox></td>'
                        + '<td style="text-align:center;"><sl-checkbox disabled ' + (m.isVision ? 'checked' : '') + '></sl-checkbox></td>'
                        + '<td style="text-align:center;"><sl-checkbox disabled ' + (m.isImageGen ? 'checked' : '') + '></sl-checkbox></td>'
                        + '<td style="text-align:right;">'
                        + '<sl-icon-button name="pencil" label="Edit" class="btn-edit-chat-model" data-edit-id="' + m.id + '"></sl-icon-button>'
                        + '<sl-icon-button name="trash" label="Delete" class="btn-delete-chat-model" data-model-id="' + m.id + '"></sl-icon-button>'
                        + '</td>'
                        + '</tr>';
                }).join('');
                loadDefaults();
            })
            .catch(function () {
                if (modelsBody) modelsBody.innerHTML = '<tr><td colspan="6" class="text-muted text-sm">Failed to load models.</td></tr>';
            });
    }

    function editChatModel(id) {
        var m = modelsCache.find(function (x) { return x.id === id; });
        if (!m) return;
        document.getElementById('edit-chat-model-id').value = m.modelId || '';
        document.getElementById('edit-chat-model-label').value = m.label || '';
        document.getElementById('edit-chat-model-text').checked = !!m.isText;
        document.getElementById('edit-chat-model-vision').checked = !!m.isVision;
        document.getElementById('edit-chat-model-imagegen').checked = !!m.isImageGen;
        document.getElementById('edit-chat-model-context').value = m.contextWindow || '';
        editingModelId = id;
        document.getElementById('dlg-edit-chat-model').show();
    }

    function toggleChatKey(keyId, enabled) {
        fetch('/api-proxy/chat/keys/' + keyId, {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ enabled: enabled })
        }).catch(function () { loadKeys(); });
    }

    function deleteChatKey(id) {
        if (!confirm('Delete this API key?')) return;
        fetch('/api-proxy/chat/keys/' + id, { method: 'DELETE' }).then(function () { loadKeys(); });
    }

    function deleteChatModel(id) {
        if (!confirm('Delete this model?')) return;
        fetch('/api-proxy/chat/models/' + id, { method: 'DELETE' }).then(function () { loadModels(); });
    }

    var btnAddChatKey = document.getElementById('btn-add-chat-key');
    if (btnAddChatKey) {
        btnAddChatKey.addEventListener('click', function () {
            document.getElementById('dlg-add-chat-key').show();
        });
    }

    var btnSaveChatKey = document.getElementById('btn-save-chat-key');
    if (btnSaveChatKey) {
        btnSaveChatKey.addEventListener('click', function () {
            var label = document.getElementById('chat-key-label').value.trim();
            var key = document.getElementById('chat-key-value').value.trim();
            var priority = parseInt(document.getElementById('chat-key-priority').value || '0', 10) || 0;
            if (!label || !key) return;
            fetch('/api-proxy/chat/keys', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ label: label, apiKey: key, priority: priority })
            })
                .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, body: b }; }); })
                .then(function (res) {
                    document.getElementById('dlg-add-chat-key').hide();
                    document.getElementById('chat-key-label').value = '';
                    document.getElementById('chat-key-value').value = '';
                    document.getElementById('chat-key-priority').value = '0';
                    loadKeys();
                    if (res.ok && res.body && res.body.apiKey) {
                        alert('Key created. Copy it now — it will not be shown again:\n\n' + res.body.apiKey);
                    } else if (!res.ok) {
                        alert((res.body && res.body.error) ? res.body.error : 'Failed to save key (is the vault unlocked?).');
                    }
                });
        });
    }

    var btnAddChatModel = document.getElementById('btn-add-chat-model');
    if (btnAddChatModel) {
        btnAddChatModel.addEventListener('click', function () {
            document.getElementById('dlg-add-chat-model').show();
        });
    }

    var btnSaveChatModel = document.getElementById('btn-save-chat-model');
    if (btnSaveChatModel) {
        btnSaveChatModel.addEventListener('click', function () {
            var modelIdInput = document.getElementById('chat-model-id');
            var modelId = modelIdInput.value.trim();
            var label = document.getElementById('chat-model-label').value.trim();
            var isText = document.getElementById('chat-model-text').checked;
            var isVision = document.getElementById('chat-model-vision').checked;
            var isImageGen = document.getElementById('chat-model-imagegen').checked;
            var cw = parseInt(document.getElementById('chat-model-context').value, 10);
            modelIdInput.setCustomValidity('');
            if (/[\s:]/.test(modelId)) {
                modelIdInput.setCustomValidity('Model ID must not contain spaces — use the exact slug from OpenRouter, like provider/model-name');
                modelIdInput.reportValidity();
                return;
            }
            if (!modelId || !label) return;
            fetch('/api-proxy/chat/models', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    modelId: modelId,
                    label: label,
                    isText: isText,
                    isVision: isVision,
                    isImageGen: isImageGen,
                    contextWindow: (isNaN(cw) || cw <= 0) ? null : cw
                })
            })
                .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, body: b }; }); })
                .then(function (res) {
                    document.getElementById('dlg-add-chat-model').hide();
                    document.getElementById('chat-model-id').value = '';
                    document.getElementById('chat-model-label').value = '';
                    document.getElementById('chat-model-text').checked = true;
                    document.getElementById('chat-model-vision').checked = false;
                    document.getElementById('chat-model-imagegen').checked = false;
                    document.getElementById('chat-model-context').value = '';
                    if (res.ok) {
                        loadModels();
                    } else {
                        alert((res.body && res.body.error) ? res.body.error : 'Failed to save model.');
                    }
                });
        });
    }

    var btnSaveEditChatModel = document.getElementById('btn-save-edit-chat-model');
    if (btnSaveEditChatModel) {
        btnSaveEditChatModel.addEventListener('click', function () {
            if (!editingModelId) return;
            var isText = document.getElementById('edit-chat-model-text').checked;
            var isVision = document.getElementById('edit-chat-model-vision').checked;
            var isImageGen = document.getElementById('edit-chat-model-imagegen').checked;
            var editCw = parseInt(document.getElementById('edit-chat-model-context').value, 10);
            fetch('/api-proxy/chat/models/' + editingModelId, {
                method: 'PATCH',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    isText: isText,
                    isVision: isVision,
                    isImageGen: isImageGen,
                    contextWindow: (isNaN(editCw) || editCw <= 0) ? null : editCw
                })
            }).then(function (r) {
                document.getElementById('dlg-edit-chat-model').hide();
                editingModelId = null;
                if (r.ok) loadModels();
            });
        });
    }

    var suppressSaveDefaults = false;

    function loadDefaults() {
        fetch('/api-proxy/chat/settings/defaults')
            .then(function (r) {
                return r.ok ? r.json() : { defaultTextModelId: null, defaultVisionModelId: null, defaultImageGenModelId: null };
            })
            .then(function (defaults) {
                suppressSaveDefaults = true;
                populateDefaultSelect('default-text-model', function (m) { return m.isText; }, defaults.defaultTextModelId);
                populateDefaultSelect('default-vision-model', function (m) { return m.isVision; }, defaults.defaultVisionModelId);
                populateDefaultSelect('default-imagegen-model', function (m) { return m.isImageGen; }, defaults.defaultImageGenModelId);
                setTimeout(function () { suppressSaveDefaults = false; }, 0);
            })
            .catch(function () {});
    }

    function populateDefaultSelect(selectId, filterFn, selectedId) {
        var sel = document.getElementById(selectId);
        if (!sel) return;
        sel.innerHTML = '';
        var defOpt = document.createElement('sl-option');
        defOpt.value = '';
        defOpt.textContent = 'Default (oldest)';
        sel.appendChild(defOpt);
        modelsCache.filter(filterFn).forEach(function (m) {
            var opt = document.createElement('sl-option');
            opt.value = m.id;
            opt.textContent = m.label || m.modelId;
            sel.appendChild(opt);
        });
        setTimeout(function () { sel.value = selectedId || ''; }, 0);
    }

    function saveDefaults() {
        if (suppressSaveDefaults) return;
        var textEl = document.getElementById('default-text-model');
        var visionEl = document.getElementById('default-vision-model');
        var imgEl = document.getElementById('default-imagegen-model');
        fetch('/api-proxy/chat/settings/defaults', {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                defaultTextModelId: (textEl ? textEl.value : '') || null,
                defaultVisionModelId: (visionEl ? visionEl.value : '') || null,
                defaultImageGenModelId: (imgEl ? imgEl.value : '') || null
            })
        }).catch(function () {});
    }

    ['default-text-model', 'default-vision-model', 'default-imagegen-model'].forEach(function (id) {
        var sel = document.getElementById(id);
        if (sel) sel.addEventListener('sl-change', saveDefaults);
    });

    var chatEnabledToggle = document.getElementById('chat-globally-enabled-toggle');
    function loadChatEnabled() {
        if (!chatEnabledToggle) return;
        fetch('/api-proxy/chat/settings/chat-enabled', { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok ? r.json() : { chatGloballyEnabled: true }; })
            .then(function (data) { chatEnabledToggle.checked = !!data.chatGloballyEnabled; })
            .catch(function () {});
    }
    if (chatEnabledToggle) {
        chatEnabledToggle.addEventListener('sl-change', function () {
            var enabled = chatEnabledToggle.checked;
            fetch('/api-proxy/chat/settings/chat-enabled', {
                method: 'PATCH',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ enabled: enabled })
            }).catch(function () { chatEnabledToggle.checked = !enabled; });
        });
    }

    // Recovery Key
    var issueRecoveryBtn = document.getElementById('issue-recovery-key-btn');
    if (issueRecoveryBtn) {
        issueRecoveryBtn.addEventListener('click', function () {
            if (!confirm('Issue a new recovery key?\n\nIt will be shown once and cannot be retrieved afterwards. Existing passwords keep working.')) return;
            issueRecoveryBtn.loading = true;
            fetch('/api-proxy/keys/add-recovery', { method: 'POST', headers: { 'Accept': 'application/json' } })
                .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, body: b }; }); })
                .then(function (res) {
                    if (res.ok && res.body && res.body.recoveryKey) {
                        document.getElementById('recovery-key-value').value = res.body.recoveryKey;
                        document.getElementById('recovery-key-dialog').show();
                    } else {
                        alert((res.body && res.body.error) ? res.body.error : 'Failed to issue a recovery key (is the vault unlocked?).');
                    }
                })
                .catch(function () { alert('Failed to issue a recovery key.'); })
                .finally(function () { issueRecoveryBtn.loading = false; });
        });

        var recoveryCopyBtn = document.getElementById('recovery-key-copy');
        if (recoveryCopyBtn) {
            recoveryCopyBtn.addEventListener('click', function () {
                var input = document.getElementById('recovery-key-value');
                if (input) navigator.clipboard.writeText(input.value).catch(function () {});
            });
        }

        var recoveryCloseBtn = document.getElementById('recovery-key-close');
        if (recoveryCloseBtn) {
            recoveryCloseBtn.addEventListener('click', function () {
                var input = document.getElementById('recovery-key-value');
                if (input) input.value = '';
                var dlg = document.getElementById('recovery-key-dialog');
                if (dlg) dlg.hide();
            });
        }
    }

    // Embeddings & Search
    var embeddingsToggle = document.getElementById('embeddings-enabled-toggle');
    function loadEmbeddingsEnabled() {
        if (!embeddingsToggle) return;
        fetch('/api-proxy/admin/search/embeddings-enabled', { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok ? r.json() : { enabled: false }; })
            .then(function (data) { embeddingsToggle.checked = !!data.enabled; })
            .catch(function () {});
    }
    if (embeddingsToggle) {
        embeddingsToggle.addEventListener('sl-change', function () {
            var enabled = embeddingsToggle.checked;
            fetch('/api-proxy/admin/search/embeddings-enabled', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ enabled: enabled })
            }).then(function (r) {
                if (!r.ok) embeddingsToggle.checked = !enabled;
            }).catch(function () { embeddingsToggle.checked = !enabled; });
        });
    }

    var embeddingsBackfillBtn = document.getElementById('embeddings-backfill-btn');
    var embeddingsBackfillStatus = document.getElementById('embeddings-backfill-status');
    if (embeddingsBackfillBtn) {
        embeddingsBackfillBtn.addEventListener('click', function () {
            embeddingsBackfillBtn.loading = true;
            if (embeddingsBackfillStatus) embeddingsBackfillStatus.textContent = '';
            fetch('/api-proxy/admin/search/embeddings/backfill', { method: 'POST' })
                .then(function (r) {
                    if (embeddingsBackfillStatus) {
                        embeddingsBackfillStatus.textContent = r.ok
                            ? 'Started — check server logs or the metrics below in a bit.'
                            : 'Failed to start backfill.';
                    }
                })
                .catch(function () {
                    if (embeddingsBackfillStatus) embeddingsBackfillStatus.textContent = 'Failed to start backfill.';
                })
                .finally(function () { embeddingsBackfillBtn.loading = false; });
        });
    }

    // Web Session Settings
    var sessionHoursInput = document.getElementById('session-expire-hours');
    var sessionSlidingToggle = document.getElementById('session-sliding-toggle');
    var sessionSaveBtn = document.getElementById('btn-save-session-settings');
    var sessionSavedMsg = document.getElementById('session-settings-saved-msg');
    function loadSessionSettings() {
        if (!sessionHoursInput || !sessionSlidingToggle) return;
        fetch('/api-proxy/session/settings', { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok ? r.json() : { expireHours: 48, slidingExpiration: true }; })
            .then(function (data) {
                sessionHoursInput.value = data.expireHours;
                sessionSlidingToggle.checked = !!data.slidingExpiration;
            })
            .catch(function () {});
    }
    if (sessionSaveBtn) {
        sessionSaveBtn.addEventListener('click', function () {
            if (sessionSavedMsg) sessionSavedMsg.style.display = 'none';
            var hours = parseInt(sessionHoursInput.value, 10);
            if (!hours || hours < 1 || hours > 720) {
                alert('Session duration must be between 1 and 720 hours.');
                return;
            }
            sessionSaveBtn.loading = true;
            fetch('/api-proxy/session/settings', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ expireHours: hours, slidingExpiration: sessionSlidingToggle.checked })
            })
                .then(function (r) {
                    sessionSaveBtn.loading = false;
                    if (r.ok && sessionSavedMsg) {
                        sessionSavedMsg.style.display = 'inline';
                        setTimeout(function () { sessionSavedMsg.style.display = 'none'; }, 3000);
                    } else if (!r.ok) {
                        alert('Failed to save session settings.');
                    }
                })
                .catch(function () {
                    sessionSaveBtn.loading = false;
                    alert('Failed to save session settings.');
                });
        });
    }

    // Branding
    var brandInput = document.getElementById('brand-name-input');
    var brandPreview = document.getElementById('brand-preview');
    var brandSaveBtn = document.getElementById('btn-save-branding');
    var brandResetBtn = document.getElementById('btn-reset-branding');
    var brandSavedMsg = document.getElementById('branding-saved-msg');
    var brandDefault = 'Bee Memory Bank';

    function renderBrandPreview() {
        if (!brandPreview) return;
        var typed = (brandInput && brandInput.value || '').trim();
        brandPreview.textContent = typed || brandDefault;
    }
    function loadBranding() {
        if (!brandInput) return;
        fetch('/api-proxy/branding', { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (!data) return;
                brandDefault = data.defaultName || brandDefault;
                brandInput.placeholder = brandDefault;
                brandInput.value = data.isCustom ? data.name : '';
                renderBrandPreview();
            })
            .catch(function () {});
    }
    function saveBranding(name) {
        if (brandSavedMsg) brandSavedMsg.style.display = 'none';
        if (brandSaveBtn) brandSaveBtn.loading = true;
        fetch('/api-proxy/branding', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name: name })
        })
            .then(function (r) {
                if (brandSaveBtn) brandSaveBtn.loading = false;
                if (!r.ok) {
                    return r.json().catch(function () { return {}; }).then(function (e) {
                        alert(e.error || 'Failed to save the product name.');
                    });
                }
                return r.json().then(function (data) {
                    if (brandInput) brandInput.value = data.isCustom ? data.name : '';
                    renderBrandPreview();
                    var headerSpan = document.querySelector('.app-logo span');
                    if (headerSpan) headerSpan.textContent = data.name;
                    if (brandSavedMsg) {
                        brandSavedMsg.style.display = 'inline';
                        setTimeout(function () { brandSavedMsg.style.display = 'none'; }, 3000);
                    }
                });
            })
            .catch(function () {
                if (brandSaveBtn) brandSaveBtn.loading = false;
                alert('Failed to save the product name.');
            });
    }
    if (brandInput) brandInput.addEventListener('sl-input', renderBrandPreview);
    if (brandSaveBtn) brandSaveBtn.addEventListener('click', function () { saveBranding((brandInput.value || '').trim()); });
    if (brandResetBtn) brandResetBtn.addEventListener('click', function () { if (brandInput) brandInput.value = ''; saveBranding(''); });

    // Snapshots: Upload dropzone and progress
    var dropZone = document.getElementById('snapshot-upload-zone');
    var fileInput = document.getElementById('snapshot-file-input');
    if (dropZone && fileInput) {
        var pickFileBtn = document.getElementById('btn-pick-file');
        if (pickFileBtn) pickFileBtn.addEventListener('click', function () { fileInput.click(); });

        dropZone.addEventListener('dragover', function (e) { e.preventDefault(); dropZone.style.background = 'rgba(0,0,0,0.05)'; });
        dropZone.addEventListener('dragleave', function () { dropZone.style.background = ''; });
        dropZone.addEventListener('drop', function (e) {
            e.preventDefault();
            dropZone.style.background = '';
            if (e.dataTransfer.files.length) uploadSnapshotFile(e.dataTransfer.files[0]);
        });
        fileInput.addEventListener('change', function (e) {
            if (e.target.files.length) uploadSnapshotFile(e.target.files[0]);
        });
    }

    function uploadSnapshotFile(file) {
        var fd = new FormData();
        fd.append('file', file);
        var prog = document.getElementById('upload-progress');
        var status = document.getElementById('upload-status');
        var bar = document.getElementById('upload-progress-bar');
        if (prog) prog.style.display = 'block';
        if (status) status.textContent = 'Uploading ' + file.name + '...';

        var xhr = new XMLHttpRequest();
        xhr.upload.addEventListener('progress', function (e) {
            if (e.lengthComputable && bar) {
                bar.value = (e.loaded / e.total) * 100;
            }
        });
        xhr.onload = function () {
            if (xhr.status === 200) {
                if (status) status.textContent = 'Uploaded successfully';
                setTimeout(function () { location.reload(); }, 1500);
            } else {
                if (status) status.textContent = 'Upload failed: ' + xhr.statusText;
            }
        };
        xhr.open('POST', '/Admin?handler=UploadSnapshot');
        var tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');
        if (tokenInput) {
            xhr.setRequestHeader('RequestVerificationToken', tokenInput.value);
        }
        xhr.send(fd);
    }

    function showProgress(label, detail) {
        var lbl = document.getElementById('snapshot-progress-label');
        var det = document.getElementById('snapshot-progress-detail');
        var dlg = document.getElementById('dlg-snapshot-progress');
        if (lbl) lbl.textContent = label;
        if (det) det.textContent = detail || '';
        if (dlg) dlg.show();
    }
    function hideProgress() {
        var dlg = document.getElementById('dlg-snapshot-progress');
        if (dlg) dlg.hide();
    }

    // Restore snapshot confirmation
    var restoreBtn = document.getElementById('btn-restore-confirm');
    if (restoreBtn) {
        restoreBtn.addEventListener('click', function () {
            var fileName = document.getElementById('restore-file-name') ? document.getElementById('restore-file-name').value : '';
            var passwordInput = document.getElementById('restore-master-password');
            var password = passwordInput ? passwordInput.value : '';
            var backupCheckbox = document.getElementById('restore-backup-checkbox');
            var createBackup = backupCheckbox ? backupCheckbox.checked : true;
            var modeEl = document.getElementById('restore-mode');
            var mode = modeEl ? modeEl.value : 'standalone';

            if (mode === 'network') {
                if (!confirm('Distribute snapshot state to entire network? This will affect all peers.')) return;

                restoreBtn.loading = true;
                var restoreDlg = document.getElementById('dlg-restore-snapshot');
                if (restoreDlg) restoreDlg.hide();
                showProgress('Initiating network restore\u2026', 'Please wait.');

                var fd = new FormData();
                fd.append('fileName', fileName);
                var token = document.querySelector('input[name="__RequestVerificationToken"]') ? document.querySelector('input[name="__RequestVerificationToken"]').value : '';
                fetch('/Admin?handler=InitiateNetworkRestore', {
                    method: 'POST',
                    headers: {
                        'RequestVerificationToken': token
                    },
                    body: fd
                })
                .then(function (r) {
                    if (r.ok) return r.json();
                    throw new Error('Failed to initiate network restore');
                })
                .then(function (j) {
                    if (j && j.eventId) {
                        window.location.href = '/Login?restore=true';
                    } else {
                        hideProgress();
                        alert('Restore failed.');
                    }
                })
                .catch(function (e) {
                    hideProgress();
                    alert(e);
                })
                .finally(function () { restoreBtn.loading = false; });
                return;
            }

            if (!password) {
                alert('Master Password is required for standalone restore.');
                return;
            }
            restoreBtn.loading = true;

            var restoreDlg = document.getElementById('dlg-restore-snapshot');
            if (restoreDlg) restoreDlg.hide();

            var label = 'Restoring snapshot\u2026';
            var detail = createBackup
                ? 'Creating safety backup first, then restoring\u2026'
                : 'Restoring snapshot without backup\u2026';
            showProgress(label, detail);

            var token = document.querySelector('input[name="__RequestVerificationToken"]') ? document.querySelector('input[name="__RequestVerificationToken"]').value : '';
            fetch('/Admin?handler=RestoreSnapshot', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/x-www-form-urlencoded',
                    'RequestVerificationToken': token
                },
                body: 'fileName=' + encodeURIComponent(fileName) +
                      '&masterPassword=' + encodeURIComponent(password) +
                      '&createBackupFirst=' + (createBackup ? 'true' : 'false') +
                      '&standaloneMode=' + (mode === 'standalone' ? 'true' : 'false')
            })
            .then(function (r) {
                if (r.redirected) {
                    window.location.href = r.url;
                } else if (r.ok) {
                    window.location.href = '/Login?restore=true';
                } else {
                    hideProgress();
                    alert('Restore failed');
                }
            })
            .catch(function () {
                hideProgress();
                alert('Network error');
            })
            .finally(function () { restoreBtn.loading = false; });
        });
    }

    // Delegated listeners (one-time registration on document)
    if (!window._adminDelegated) {
        window._adminDelegated = true;

        // Block snapshot-progress dialog from being dismissed mid-operation
        document.addEventListener('sl-request-close', function (e) {
            if (e.target && e.target.id === 'dlg-snapshot-progress') e.preventDefault();
        }, true);

        // Document-level clicks
        document.addEventListener('click', function (e) {
            // Upload dropzone toggle
            var uploadToggle = e.target.closest('#btn-show-upload');
            if (uploadToggle) {
                var zone = document.getElementById('snapshot-upload-zone');
                if (zone) zone.style.display = (zone.style.display === 'none' || !zone.style.display) ? 'block' : 'none';
                return;
            }

            // Create snapshot button
            var createBtn = e.target.closest('#btn-create-snapshot');
            if (createBtn) {
                if (createBtn.hasAttribute('data-busy')) return;
                createBtn.setAttribute('data-busy', '');
                createBtn.loading = true;
                showProgress('Creating snapshot\u2026', 'This may take a while for large databases.');
                fetch('/api-proxy/snapshots', { method: 'POST' })
                    .then(function (r) {
                        if (r.ok) {
                            window.location.reload();
                        } else {
                            hideProgress();
                            alert('Failed to create snapshot');
                        }
                    })
                    .catch(function () {
                        hideProgress();
                        alert('Network error');
                    })
                    .finally(function () {
                        createBtn.loading = false;
                        createBtn.removeAttribute('data-busy');
                    });
                return;
            }

            // Open restore snapshot dialog
            var restoreTrigger = e.target.closest('.btn-open-restore-snapshot');
            if (restoreTrigger) {
                var fname = restoreTrigger.dataset.filename;
                var inputFn = document.getElementById('restore-file-name');
                var disp = document.getElementById('restore-snapshot-name-display');
                var dlg = document.getElementById('dlg-restore-snapshot');
                if (inputFn) inputFn.value = fname;
                if (disp) disp.textContent = fname;
                if (dlg) dlg.show();
                return;
            }

            // Change node URL
            var changeUrlBtn = e.target.closest('.btn-open-change-url');
            if (changeUrlBtn) {
                var nid = changeUrlBtn.dataset.nodeId;
                var nname = changeUrlBtn.dataset.nodeName;
                var curl = changeUrlBtn.dataset.currentUrl;
                var nidInput = document.getElementById('change-url-node-id');
                var nnameEl = document.getElementById('change-url-node-name');
                var urlInput = document.getElementById('change-url-input');
                var dlgUrl = document.getElementById('dlg-change-url');
                if (nidInput) nidInput.value = nid;
                if (nnameEl) nnameEl.textContent = 'Node: ' + nname;
                if (urlInput) {
                    urlInput.value = curl;
                    setTimeout(function () { urlInput.value = curl; }, 50);
                }
                if (dlgUrl) dlgUrl.show();
                return;
            }

            // Promote/Demote superadmin
            var superadminBtn = e.target.closest('.btn-confirm-superadmin');
            if (superadminBtn) {
                var sname = superadminBtn.dataset.nodeName;
                var demoting = superadminBtn.dataset.demote === 'true';
                var msg = demoting
                    ? 'Demote "' + sname + '"?\n\nIt will no longer be able to revoke peers, hard-delete content across the network, or restore every node from its own snapshot. It keeps syncing content as usual.'
                    : 'Promote "' + sname + '" to superadmin?\n\nIt will be able to revoke any peer, hard-delete content on every node, and restore the whole network from its own snapshot.';
                if (!confirm(msg)) {
                    e.preventDefault();
                    return;
                }
                var form = superadminBtn.closest('form');
                if (form) form.submit();
                return;
            }

            // Generic confirm button in form
            var confirmBtn = e.target.closest('.btn-confirm-submit');
            if (confirmBtn) {
                var pat = confirmBtn.dataset.confirmPattern || 'Are you sure?';
                var nameVal = confirmBtn.dataset.name || '';
                var text = pat.replace('{name}', nameVal);
                if (!confirm(text)) {
                    e.preventDefault();
                    return;
                }
                var cform = confirmBtn.closest('form');
                if (cform) cform.submit();
                return;
            }

            // Open dialog button
            var openDlgBtn = e.target.closest('.btn-open-dialog');
            if (openDlgBtn) {
                var targetDlgId = openDlgBtn.dataset.dialogId;
                if (targetDlgId) {
                    var targetDlg = document.getElementById(targetDlgId);
                    if (targetDlg) targetDlg.show();
                }
                return;
            }

            // Delete chat key
            var delKeyBtn = e.target.closest('.btn-delete-chat-key');
            if (delKeyBtn) {
                deleteChatKey(delKeyBtn.dataset.keyId);
                return;
            }

            // Edit chat model
            var editModelBtn = e.target.closest('.btn-edit-chat-model');
            if (editModelBtn) {
                editChatModel(editModelBtn.dataset.editId);
                return;
            }

            // Delete chat model
            var delModelBtn = e.target.closest('.btn-delete-chat-model');
            if (delModelBtn) {
                deleteChatModel(delModelBtn.dataset.modelId);
                return;
            }

            // Copy public key
            var copyPubBtn = e.target.closest('.btn-copy-pubkey');
            if (copyPubBtn) {
                var tgtId = copyPubBtn.dataset.target;
                var el = document.getElementById(tgtId);
                if (el) {
                    navigator.clipboard.writeText(el.textContent.trim()).catch(function () {});
                }
                return;
            }
        });

        // Delegated form submission confirmation
        document.addEventListener('submit', function (e) {
            var form = e.target.closest('form.form-confirm');
            if (form) {
                var confirmMsg = form.dataset.confirm;
                if (confirmMsg && !confirm(confirmMsg)) {
                    e.preventDefault();
                }
            }
        });

        // Delegated Shoelace switch changes
        document.addEventListener('sl-change', function (e) {
            // Auto accept restore toggle
            var autoAcceptSwitch = e.target.closest('.switch-auto-accept');
            if (autoAcceptSwitch) {
                var nodeId = autoAcceptSwitch.dataset.nodeId;
                var nodeName = autoAcceptSwitch.dataset.nodeName;
                var newVal = autoAcceptSwitch.checked;
                var proceed = true;
                if (newVal) {
                    proceed = confirm(
                        "Enable AUTO-RESTORE for peer '" + nodeName + "'?\n\n" +
                        "When this peer initiates a snapshot restore, it will apply on this node WITHOUT confirmation. " +
                        "Your articles, folders, and tags from after the restore point will be replaced.\n\n" +
                        "Only enable for peers you fully trust (your own devices, your team's nodes). " +
                        "For peers belonging to other people, keep this OFF.\n\n" +
                        "Continue?"
                    );
                }
                if (!proceed) {
                    autoAcceptSwitch.checked = !newVal;
                    return;
                }
                var valInput = document.getElementById("autoaccept-value-" + nodeId);
                var form = document.getElementById("autoaccept-form-" + nodeId);
                if (valInput && form) {
                    valInput.value = newVal ? "true" : "false";
                    form.submit();
                }
                return;
            }

            // Auto accept DEK toggle
            var autoAcceptDekSwitch = e.target.closest('.switch-auto-accept-dek');
            if (autoAcceptDekSwitch) {
                var dekNodeId = autoAcceptDekSwitch.dataset.nodeId;
                var dekNodeName = autoAcceptDekSwitch.dataset.nodeName;
                var dekNewVal = autoAcceptDekSwitch.checked;
                var dekProceed = true;
                if (dekNewVal) {
                    dekProceed = confirm(
                        "Enable AUTO-ACCEPT DEK ROTATION for peer '" + dekNodeName + "'?\n\n" +
                        "When this peer initiates a DEK rotation, it will apply on this node WITHOUT confirmation. " +
                        "ALL article bodies, versions, and media will be re-encrypted with a new key. " +
                        "This is more destructive than snapshot restore.\n\n" +
                        "Only enable for peers you fully trust (your own devices).\n\n" +
                        "Continue?"
                    );
                }
                if (!dekProceed) {
                    autoAcceptDekSwitch.checked = !dekNewVal;
                    return;
                }
                var dekValInput = document.getElementById("autoaccept-dek-value-" + dekNodeId);
                var dekForm = document.getElementById("autoaccept-dek-form-" + dekNodeId);
                if (dekValInput && dekForm) {
                    dekValInput.value = dekNewVal ? "true" : "false";
                    dekForm.submit();
                }
                return;
            }

            // Chat key toggle
            var chatKeySwitch = e.target.closest('.switch-chat-key');
            if (chatKeySwitch) {
                var kId = chatKeySwitch.dataset.keyId;
                toggleChatKey(kId, chatKeySwitch.checked);
                return;
            }
        });
    }

    // Initial data loads
    loadChatEnabled();
    loadEmbeddingsEnabled();
    loadSessionSettings();
    loadBranding();
    loadKeys();
    loadModels();
})();
