// Admin Page Module
(function () {
    'use strict';

    var pageRoot = document.querySelector('#page-content .main-content') || document.getElementById('page-content');
    if (pageRoot) {
        if (pageRoot.dataset.adminInit) return;
        pageRoot.dataset.adminInit = '1';
    }

    // Per-page AbortSignal: every document-level listener below uses pageOpts so that
    // spaCleanup()'s abort on the next SPA navigation tears down this run's listeners
    // together with their closures (keysBody, modelsBody, modelsCache, editingModelId, …).
    var pageSignal = window.bmbGetPageSignal ? window.bmbGetPageSignal() : null;
    var pageOpts = pageSignal ? { signal: pageSignal } : false;
    var pageCaptureOpts = pageSignal ? { signal: pageSignal, capture: true } : true;

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

    // Every button below sends through bmbBusy (BMB-311): it is off and spins until the answer comes, and a refusal is said in full next to it.
    function toggleChatKey(keyId, enabled, el) {
        bmbBusy.run(el, function () {
            return bmbBusy.request('/api-proxy/chat/keys/' + keyId, {
                method: 'PATCH',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ enabled: enabled })
            });
        }, {
            quiet: true,
            scope: keysBody,
            error: function (err) { loadKeys(); bmbBusy.showResult(keysBody, false, err.message); }
        });
    }

    function deleteChatKey(id, el) {
        if (!confirm('Delete this API key?')) return;
        bmbBusy.run(el, function () {
            return bmbBusy.request('/api-proxy/chat/keys/' + id, { method: 'DELETE' }).then(function () { loadKeys(); });
        }, { quiet: true, scope: keysBody });
    }

    function deleteChatModel(id, el) {
        if (!confirm('Delete this model?')) return;
        bmbBusy.run(el, function () {
            return bmbBusy.request('/api-proxy/chat/models/' + id, { method: 'DELETE' }).then(function () { loadModels(); });
        }, { quiet: true, scope: modelsBody });
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
            // The dialog stays open when the node refuses: the reason is written in it, next to the button.
            bmbBusy.run(btnSaveChatKey, function () {
                return bmbBusy.request('/api-proxy/chat/keys', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ label: label, apiKey: key, priority: priority })
                }).then(function (res) {
                    document.getElementById('dlg-add-chat-key').hide();
                    document.getElementById('chat-key-label').value = '';
                    document.getElementById('chat-key-value').value = '';
                    document.getElementById('chat-key-priority').value = '0';
                    loadKeys();
                    // The secret is shown once, so it is shown in a prompt the person has to close, not in a line that fades.
                    if (res.body && res.body.apiKey) {
                        alert('Key created. Copy it now — it will not be shown again:\n\n' + res.body.apiKey);
                    }
                });
            }, { quiet: true });
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
            bmbBusy.run(btnSaveChatModel, function () {
                return bmbBusy.request('/api-proxy/chat/models', {
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
                }).then(function () {
                    document.getElementById('dlg-add-chat-model').hide();
                    document.getElementById('chat-model-id').value = '';
                    document.getElementById('chat-model-label').value = '';
                    document.getElementById('chat-model-text').checked = true;
                    document.getElementById('chat-model-vision').checked = false;
                    document.getElementById('chat-model-imagegen').checked = false;
                    document.getElementById('chat-model-context').value = '';
                    loadModels();
                });
            }, { quiet: true });
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
            bmbBusy.run(btnSaveEditChatModel, function () {
                return bmbBusy.request('/api-proxy/chat/models/' + editingModelId, {
                    method: 'PATCH',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        isText: isText,
                        isVision: isVision,
                        isImageGen: isImageGen,
                        contextWindow: (isNaN(editCw) || editCw <= 0) ? null : editCw
                    })
                }).then(function () {
                    document.getElementById('dlg-edit-chat-model').hide();
                    editingModelId = null;
                    loadModels();
                });
            }, { quiet: true });
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
        bmbBusy.run(null, function () {
            return bmbBusy.request('/api-proxy/chat/settings/defaults', {
                method: 'PATCH',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    defaultTextModelId: (textEl ? textEl.value : '') || null,
                    defaultVisionModelId: (visionEl ? visionEl.value : '') || null,
                    defaultImageGenModelId: (imgEl ? imgEl.value : '') || null
                })
            });
        }, { quiet: true, scope: textEl });
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
            bmbBusy.run(chatEnabledToggle, function () {
                return bmbBusy.request('/api-proxy/chat/settings/chat-enabled', {
                    method: 'PATCH',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ enabled: enabled })
                });
            }, {
                quiet: true,
                error: function (err) { chatEnabledToggle.checked = !enabled; bmbBusy.showResult(chatEnabledToggle, false, err.message); }
            });
        });
    }

    // Recovery Key
    var issueRecoveryBtn = document.getElementById('issue-recovery-key-btn');
    if (issueRecoveryBtn) {
        issueRecoveryBtn.addEventListener('click', function () {
            if (!confirm('Issue a new recovery key?\n\nIt will be shown once and cannot be retrieved afterwards. Existing passwords keep working.')) return;
            // A key change (BMB-311): the button is off and spins; a refusal is written next to it, in full.
            bmbBusy.run(issueRecoveryBtn, function () {
                return bmbBusy.request('/api-proxy/keys/add-recovery', { method: 'POST', headers: { 'Accept': 'application/json' } })
                    .then(function (res) {
                        if (!(res.body && res.body.recoveryKey)) throw new Error('The node did not return a recovery key (is the vault unlocked?).');
                        document.getElementById('recovery-key-value').value = res.body.recoveryKey;
                        document.getElementById('recovery-key-dialog').show();
                    });
            }, { quiet: true });
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
            bmbBusy.run(embeddingsToggle, function () {
                return bmbBusy.request('/api-proxy/admin/search/embeddings-enabled', {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ enabled: enabled })
                });
            }, {
                quiet: true,
                error: function (err) { embeddingsToggle.checked = !enabled; bmbBusy.showResult(embeddingsToggle, false, err.message); }
            });
        });
    }

    var embeddingsBackfillBtn = document.getElementById('embeddings-backfill-btn');
    var embeddingsBackfillStatus = document.getElementById('embeddings-backfill-status');
    if (embeddingsBackfillBtn) {
        embeddingsBackfillBtn.addEventListener('click', function () {
            if (embeddingsBackfillStatus) embeddingsBackfillStatus.textContent = '';
            // The node answers at once and works on in the background; a second press while it works is refused (409) and the reason is said here.
            bmbBusy.run(embeddingsBackfillBtn, function () {
                return bmbBusy.request('/api-proxy/admin/search/embeddings/backfill', { method: 'POST' });
            }, { success: 'Started — check the server logs or the metrics below in a bit.' });
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
            bmbBusy.run(sessionSaveBtn, function () {
                return bmbBusy.request('/api-proxy/session/settings', {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ expireHours: hours, slidingExpiration: sessionSlidingToggle.checked })
                }).then(function () {
                    if (sessionSavedMsg) {
                        sessionSavedMsg.style.display = 'inline';
                        setTimeout(function () { sessionSavedMsg.style.display = 'none'; }, 3000);
                    }
                });
            }, { quiet: true });
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
    function saveBranding(name, btn) {
        if (brandSavedMsg) brandSavedMsg.style.display = 'none';
        bmbBusy.run(btn, function () {
            return bmbBusy.request('/api-proxy/branding', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name: name })
            }).then(function (res) {
                var data = res.body || {};
                if (brandInput) brandInput.value = data.isCustom ? data.name : '';
                renderBrandPreview();
                var headerSpan = document.querySelector('.app-logo-name');
                if (headerSpan && data.name) headerSpan.textContent = data.name;
                if (brandSavedMsg) {
                    brandSavedMsg.style.display = 'inline';
                    setTimeout(function () { brandSavedMsg.style.display = 'none'; }, 3000);
                }
            });
        }, { quiet: true });
    }
    if (brandInput) brandInput.addEventListener('sl-input', renderBrandPreview);
    if (brandSaveBtn) brandSaveBtn.addEventListener('click', function () { saveBranding((brandInput.value || '').trim(), brandSaveBtn); });
    if (brandResetBtn) brandResetBtn.addEventListener('click', function () { if (brandInput) brandInput.value = ''; saveBranding('', brandResetBtn); });

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

        // A long operation (BMB-311): the pop-up says how far the upload is; a failure is written in the upload area, in full.
        bmbBusy.run(document.getElementById('btn-pick-file'), function () {
            return new Promise(function (resolve, reject) {
                var xhr = new XMLHttpRequest();
                xhr.upload.addEventListener('progress', function (e) {
                    if (!e.lengthComputable) return;
                    var pct = Math.round((e.loaded / e.total) * 100);
                    if (bar) bar.value = pct;
                    bmbBusy.setModalMessage('Uploading ' + file.name + ': ' + pct + '%. Do not close this page.');
                });
                xhr.onload = function () {
                    if (xhr.status === 200) {
                        if (status) status.textContent = 'Uploaded successfully';
                        resolve();
                        return;
                    }
                    var said = '';
                    try { said = (JSON.parse(xhr.responseText) || {}).error || ''; } catch (parseError) { said = ''; }
                    reject(new Error(said || ('Upload failed: ' + (xhr.statusText || xhr.status) + '.')));
                };
                xhr.onerror = function () { reject(new Error('The node did not answer. Check that it is running and try again.')); };
                xhr.open('POST', '/Admin?handler=UploadSnapshot');
                var tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');
                if (tokenInput) {
                    xhr.setRequestHeader('RequestVerificationToken', tokenInput.value);
                }
                xhr.send(fd);
            }).then(function () {
                setTimeout(function () { location.reload(); }, 1500);
            });
        }, {
            long: true,
            title: 'Uploading the snapshot',
            text: 'Uploading ' + file.name + '. Do not close this page.',
            quiet: true,
            scope: dropZone,
            error: function (err) {
                if (status) status.textContent = 'Upload failed.';
                bmbBusy.showResult(dropZone, false, err.message);
            }
        });
    }

    // Restore snapshot: the choice between "this node only" and "the whole network"
    var restoreModeEl = document.getElementById('restore-mode');
    function currentRestoreMode() {
        // "standalone" (this node only, the safe default) unless the network option is chosen
        return restoreModeEl && restoreModeEl.value === 'network' ? 'network' : 'standalone';
    }
    function showRestoreMode() {
        var mode = currentRestoreMode();
        var standaloneInfo = document.getElementById('restore-mode-standalone');
        var networkInfo = document.getElementById('restore-mode-network');
        var backupRow = document.getElementById('restore-backup-row');
        if (standaloneInfo) standaloneInfo.hidden = mode !== 'standalone';
        if (networkInfo) networkInfo.hidden = mode !== 'network';
        // A network restore always takes its own safety backup, so the checkbox does not apply to it
        if (backupRow) backupRow.hidden = mode === 'network';
    }
    if (restoreModeEl) restoreModeEl.addEventListener('sl-change', showRestoreMode);

    // Restore snapshot confirmation
    var restoreBtn = document.getElementById('btn-restore-confirm');
    if (restoreBtn) {
        restoreBtn.addEventListener('click', function () {
            var fileName = document.getElementById('restore-file-name') ? document.getElementById('restore-file-name').value : '';
            var passwordInput = document.getElementById('restore-master-password');
            var password = passwordInput ? passwordInput.value : '';
            var backupCheckbox = document.getElementById('restore-backup-checkbox');
            var createBackup = backupCheckbox ? backupCheckbox.checked : true;
            var mode = currentRestoreMode();
            var token = document.querySelector('input[name="__RequestVerificationToken"]') ? document.querySelector('input[name="__RequestVerificationToken"]').value : '';

            if (!password) {
                alert('Master Password is required to restore a snapshot.');
                return;
            }

            if (mode === 'network') {
                if (!confirm('Restore the whole network from this snapshot?\n\nEvery node that accepts it will replace its articles, folders, tags and media with the snapshot content.')) return;

                var fd = new FormData();
                fd.append('fileName', fileName);
                fd.append('masterPassword', password);
                // A long operation (BMB-311): the pop-up covers the page; when the node refuses, the dialog is still here and says why next to the button.
                bmbBusy.run(restoreBtn, function () {
                    return bmbBusy.request('/Admin?handler=InitiateNetworkRestore', {
                        method: 'POST',
                        headers: { 'RequestVerificationToken': token },
                        body: fd
                    }).then(function (res) {
                        if (!(res.body && res.body.eventId)) throw new Error('The node did not start the network restore.');
                        var networkDlg = document.getElementById('dlg-restore-snapshot');
                        if (networkDlg) networkDlg.hide();
                        window.location.href = '/Login?restore=true';
                    });
                }, { quiet: true, long: true, title: 'Starting the network restore', text: 'Please wait. Do not close this page.' });
                return;
            }

            if (!confirm('Restore this node only?\n\nIt becomes a new node: new identity, and it forgets its trusted nodes, blind copies and sync history. Your other devices must be joined to it again.')) return;

            var detail = createBackup
                ? 'Creating a safety backup first, then restoring. Do not close this page.'
                : 'Restoring the snapshot without a backup. Do not close this page.';

            // A long operation (BMB-311): the pop-up covers the page; when the node refuses, the dialog is still here and says why next to the button.
            bmbBusy.run(restoreBtn, function () {
                return bmbBusy.request('/Admin?handler=RestoreSnapshot', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/x-www-form-urlencoded',
                        'RequestVerificationToken': token
                    },
                    body: 'fileName=' + encodeURIComponent(fileName) +
                          '&masterPassword=' + encodeURIComponent(password) +
                          '&createBackupFirst=' + (createBackup ? 'true' : 'false') +
                          '&mode=' + encodeURIComponent(mode)
                }).then(function (res) {
                    var restoreDlg = document.getElementById('dlg-restore-snapshot');
                    if (restoreDlg) restoreDlg.hide();
                    if (res.response.redirected) {
                        // The page answers with a redirect: to the sign-in page, which says what happened, or back here with the error
                        window.location.href = res.response.url;
                    } else {
                        window.location.href = '/Login?restored=standalone';
                    }
                });
            }, { quiet: true, long: true, title: 'Restoring the snapshot', text: detail });
        });
    }

    // Delegated listeners (registered per page run, aborted by spaCleanup via pageSignal)

    // Confirm before executing destructive admin actions (.btn-confirm-click) in capture phase
    document.addEventListener('click', function (e) {
        var btn = e.target.closest('.btn-confirm-click');
        if (!btn || btn._bmbConfirmed) return;
        var msg = btn.dataset.confirm || 'Are you sure?';
        if (!confirm(msg)) {
            e.preventDefault();
            e.stopImmediatePropagation();
            return;
        }
        btn._bmbConfirmed = true;
        setTimeout(function () { delete btn._bmbConfirmed; }, 500);
    }, pageCaptureOpts);

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
            // A long operation (BMB-311): a second press is refused by the page and by the node (409); a refusal is written next to the button.
            bmbBusy.run(createBtn, function () {
                return bmbBusy.request('/api-proxy/snapshots', { method: 'POST' }).then(function () { window.location.reload(); });
            }, { quiet: true, long: true, title: 'Creating the snapshot', text: 'This may take a while for large databases. Do not close this page.' });
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
            // Every opening starts from the default: this node only, backup first, nothing typed
            if (restoreModeEl) restoreModeEl.value = 'standalone';
            var backupDefault = document.getElementById('restore-backup-checkbox');
            if (backupDefault) backupDefault.checked = true;
            var passwordDefault = document.getElementById('restore-master-password');
            if (passwordDefault) passwordDefault.value = '';
            showRestoreMode();
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

        // Let blind copies call this node (ADR 0007)
        var hubBtn = e.target.closest('.btn-open-hub');
        if (hubBtn) {
            var hubIdInput = document.getElementById('hub-node-id');
            var hubNameEl = document.getElementById('hub-node-name');
            var hubAddr = document.getElementById('hub-address');
            var hubTrust = document.getElementById('hub-trust');
            var hubDlg = document.getElementById('dlg-hub');
            var hubUrl = hubBtn.dataset.currentUrl || '';
            if (hubIdInput) hubIdInput.value = hubBtn.dataset.nodeId;
            if (hubNameEl) hubNameEl.textContent = 'Node: ' + hubBtn.dataset.nodeName;
            if (hubAddr) {
                hubAddr.value = /^https:\/\//i.test(hubUrl) ? hubUrl : '';
                setTimeout(function () { if (!hubAddr.value && /^https:\/\//i.test(hubUrl)) hubAddr.value = hubUrl; }, 50);
            }
            if (hubTrust) {
                hubTrust.value = hubBtn.dataset.trust || 'public-ca';
                setHubMode(hubTrust.value);
            }
            if (hubDlg) hubDlg.show();
            return;
        }

        // Open DEK rotation dialog (initial propose + retry after a failed rotation)
        var openDekBtn = e.target.closest('.btn-open-dek-rotation');
        if (openDekBtn) {
            var dlgDek = document.getElementById('dlg-dek-rotation');
            if (dlgDek) dlgDek.show();
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
            if (form) bmbBusy.submit(form);
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
            if (cform) bmbBusy.submit(cform);
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
            deleteChatKey(delKeyBtn.dataset.keyId, delKeyBtn);
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
            deleteChatModel(delModelBtn.dataset.modelId, delModelBtn);
            return;
        }

        // Destructive admin actions with confirmation (.btn-confirm-click)
        var confirmClickBtn = e.target.closest('.btn-confirm-click');
        if (confirmClickBtn) {
            var href = confirmClickBtn.getAttribute('href');
            if (href) {
                e.preventDefault();
                window.location.href = href;
                return;
            }
            // Form submit actions are handled natively by Shoelace once confirmed in capture phase.
        }

        // Dialog cancel buttons (admin fallback)
        var cancelDlgBtn = e.target.closest('[data-dlg-cancel]');
        if (cancelDlgBtn) {
            var cDlgId = cancelDlgBtn.getAttribute('data-dlg-cancel');
            if (cDlgId) {
                var cDlg = document.getElementById(cDlgId);
                if (cDlg && typeof cDlg.hide === 'function') cDlg.hide();
            }
            return;
        }

        // Copy public key
        var copyPubBtn = e.target.closest('.btn-copy-pubkey, #btn-copy-pubkey');
        if (copyPubBtn) {
            var tgtId = copyPubBtn.dataset.target || 'pubkey-val';
            var el = document.getElementById(tgtId);
            if (el) {
                var val = el.textContent.trim();
                if (window.bmbCopyToClipboard) {
                    window.bmbCopyToClipboard(val, copyPubBtn);
                } else if (navigator.clipboard) {
                    navigator.clipboard.writeText(val).catch(function () {});
                }
            }
            return;
        }
    }, pageOpts);

    // The question of a form that asks first is asked by site.js, on every page.

    // Which explanation and fields the "Let blind copies call this node" dialog shows for the mode chosen.
    function setHubMode(mode) {
        var show = function (id, on) {
            var el = document.getElementById(id);
            if (el) el.style.display = on ? '' : 'none';
        };
        show('hub-help-public-ca', mode === 'public-ca');
        show('hub-help-pin', mode === 'pin');
        show('hub-help-off', mode === 'off');
        show('hub-address-row', mode !== 'off');
        show('hub-pin-row', mode === 'pin');
    }

    // Delegated Shoelace switch changes
    document.addEventListener('sl-change', function (e) {
        var hubSelect = e.target.closest ? e.target.closest('#hub-trust') : null;
        if (hubSelect) {
            setHubMode(hubSelect.value);
            return;
        }

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
                bmbBusy.submit(form);
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
                bmbBusy.submit(dekForm);
            }
            return;
        }

        // Chat key toggle
        var chatKeySwitch = e.target.closest('.switch-chat-key');
        if (chatKeySwitch) {
            var kId = chatKeySwitch.dataset.keyId;
            toggleChatKey(kId, chatKeySwitch.checked, chatKeySwitch);
            return;
        }
    }, pageOpts);

    // Initial data loads
    loadChatEnabled();
    loadEmbeddingsEnabled();
    loadSessionSettings();
    loadBranding();
    loadKeys();
    loadModels();
})();
