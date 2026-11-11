// Admin -> AI / Chat: the model providers (each a card with its approval, keys and models), the default models labelled "model - provider",
// the fallback chain of the text model, and the node-wide chat switch. Everything goes through /api-proxy/chat/*; every change through bmbBusy
// (BMB-311): the button spins, the result or the refusal is written in the card (or the dialog) next to it, a dialog keeps what was typed when
// the node refuses, and after a change the page comes back to the same card (#provider-<id>). A key is never shown again after it is saved:
// the node returns only its first characters.
(function () {
    'use strict';

    var list = document.getElementById('providers-list');
    if (!list) return;
    var pageRoot = document.querySelector('#page-content .main-content') || document.getElementById('page-content');
    if (pageRoot) {
        if (pageRoot.dataset.aiProvidersInit) return;
        pageRoot.dataset.aiProvidersInit = '1';
    }
    var pageSignal = window.bmbGetPageSignal ? window.bmbGetPageSignal() : null;
    var pageOpts = pageSignal ? { signal: pageSignal } : false;

    var state = { providers: [], presets: [], keys: [], models: [], defaults: {}, fallbacks: [], fetched: {} };
    var current = { keyProvider: null, modelProvider: null, approve: null, edit: null, editModel: null, preset: null };
    // The wire format a provider speaks, as its card names it.
    var FORMATS = { 'openai-chat': 'OpenAI chat format', 'anthropic-messages': 'Anthropic Messages', 'gemini': 'Google Gemini' };

    function $(id) { return document.getElementById(id); }

    function esc(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
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

    // The page's anti-forgery token: the provider routes refuse a change without it (they let content leave for a host).
    function headers() {
        var input = document.querySelector('input[name="__RequestVerificationToken"]');
        return { 'Content-Type': 'application/json', 'Accept': 'application/json', 'RequestVerificationToken': input ? input.value : '' };
    }

    function getJson(url, fallback) {
        return fetch(url, { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok ? r.json() : fallback; })
            .catch(function () { return fallback; });
    }

    function providerById(id) { return state.providers.find(function (p) { return p.id === id; }) || null; }
    function providerName(id) { var p = providerById(id); return p ? p.name : '?'; }
    function modelName(m) { return (m.label || m.modelId) + ' - ' + (m.providerName || providerName(m.providerId)); }
    function modelById(id) { return state.models.find(function (m) { return m.id === id; }) || null; }

    function keyStatus(k) {
        if (!k.enabled) return { label: 'Disabled', color: 'var(--sl-color-danger-600)' };
        if (k.disabledUntil) {
            var d = new Date(k.disabledUntil);
            if (!isNaN(d) && d.getTime() > Date.now())
                return { label: 'Cooling down', color: 'var(--sl-color-warning-600)', title: 'auto-retries after ' + rel(k.disabledUntil) };
        }
        if (k.lastError) return { label: 'Active', color: 'var(--sl-color-success-600)', title: 'last error: ' + k.lastError };
        return { label: 'Active', color: 'var(--sl-color-success-600)' };
    }

    // ---- loading and drawing ------------------------------------------------------------------------------------------------------------

    function loadAll() {
        return Promise.all([
            getJson('/api-proxy/chat/providers', null),
            getJson('/api-proxy/chat/providers/presets', []),
            getJson('/api-proxy/chat/keys', []),
            getJson('/api-proxy/chat/models/all', []),
            getJson('/api-proxy/chat/settings/defaults', {}),
            getJson('/api-proxy/chat/settings/fallbacks', { fallbackModelIds: [] })
        ]).then(function (r) {
            // A list that did not load says so: "No provider yet" would invite adding one that may already be there.
            state.loadFailed = !Array.isArray(r[0]);
            state.providers = Array.isArray(r[0]) ? r[0] : [];
            state.presets = Array.isArray(r[1]) ? r[1] : [];
            state.keys = Array.isArray(r[2]) ? r[2] : [];
            state.models = Array.isArray(r[3]) ? r[3] : [];
            state.defaults = r[4] || {};
            state.fallbacks = (r[5] && Array.isArray(r[5].fallbackModelIds)) ? r[5].fallbackModelIds.slice() : [];
            renderProviders();
            renderDefaults();
            renderFallbacks();
        });
    }

    function badge(p) {
        if (p.approved) return '<sl-badge variant="success" pill>Approved</sl-badge>';
        if (!p.approvalChecked) return '<sl-badge variant="neutral" pill>Unlock the vault to check its approval</sl-badge>';
        return '<sl-badge variant="warning" pill>Not approved: nothing is sent to it</sl-badge>';
    }

    function providerCard(p) {
        var keys = state.keys.filter(function (k) { return k.providerId === p.id; });
        var models = state.models.filter(function (m) { return m.providerId === p.id; });
        var actions = '';
        // A finished step is not offered again as a button: an approved provider shows its badge, not "Approve".
        if (!p.approved && p.approvalChecked && p.clientAvailable)
            actions += '<sl-button size="small" variant="primary" class="btn-approve-provider" data-id="' + p.id + '"><sl-icon slot="prefix" name="shield-check"></sl-icon> Approve…</sl-button>';
        actions += '<sl-button size="small" class="btn-edit-provider" data-id="' + p.id + '"><sl-icon slot="prefix" name="pencil"></sl-icon> Edit</sl-button>';
        if (p.approved && !p.builtIn)
            actions += '<sl-button size="small" class="btn-withdraw-provider" data-id="' + p.id + '"><sl-icon slot="prefix" name="shield-x"></sl-icon> Withdraw approval</sl-button>';
        actions += '<sl-button size="small" variant="danger" outline class="btn-delete-provider" data-id="' + p.id + '"><sl-icon slot="prefix" name="trash"></sl-icon> Delete</sl-button>';

        var keyRows = keys.length === 0
            ? '<tr><td colspan="7" class="col-empty text-muted text-sm">' + (p.isLocal ? 'No key: a local server is called without one.' : 'No key yet.') + '</td></tr>'
            : keys.map(function (k) {
                var st = keyStatus(k);
                return '<tr>'
                    + '<td class="col-name"><strong>' + esc(k.label) + '</strong></td>'
                    + '<td data-label="Prefix"><code class="text-sm">' + esc(k.keyPrefix) + '</code></td>'
                    + '<td class="text-muted text-sm" data-label="Priority">' + (k.priority || 0) + '</td>'
                    + '<td data-label="Enabled"><sl-switch data-key-id="' + k.id + '" class="switch-chat-key" ' + (k.enabled ? 'checked' : '') + '></sl-switch></td>'
                    + '<td class="text-sm" data-label="Status" style="color:' + st.color + ';"' + (st.title ? ' title="' + esc(st.title) + '"' : '') + '>' + st.label + '</td>'
                    + '<td class="text-muted text-sm" data-label="Last used">' + rel(k.lastUsedAt) + '</td>'
                    + '<td class="col-actions"><div class="row-actions"><sl-icon-button name="trash" label="Delete key" class="btn-delete-chat-key" data-key-id="' + k.id + '"></sl-icon-button></div></td>'
                    + '</tr>';
            }).join('');

        var modelRows = models.length === 0
            ? '<tr><td colspan="6" class="col-empty text-muted text-sm">No model yet. Add one (with the Text capability) to chat with it.</td></tr>'
            : models.map(function (m) {
                return '<tr>'
                    + '<td class="col-name"><strong>' + esc(m.label) + '</strong></td>'
                    + '<td class="wrap-anywhere" data-label="Model ID"><code class="text-sm">' + esc(m.modelId) + '</code></td>'
                    + '<td class="col-center" data-label="Text"><sl-checkbox disabled ' + (m.isText ? 'checked' : '') + '></sl-checkbox></td>'
                    + '<td class="col-center" data-label="Vision"><sl-checkbox disabled ' + (m.isVision ? 'checked' : '') + '></sl-checkbox></td>'
                    + '<td class="col-center" data-label="Image gen"><sl-checkbox disabled ' + (m.isImageGen ? 'checked' : '') + '></sl-checkbox></td>'
                    + '<td class="col-actions"><div class="row-actions">'
                    + '<sl-icon-button name="pencil" label="Edit model" class="btn-edit-chat-model" data-edit-id="' + m.id + '"></sl-icon-button>'
                    + '<sl-icon-button name="trash" label="Delete model" class="btn-delete-chat-model" data-model-id="' + m.id + '"></sl-icon-button>'
                    + '</div></td></tr>';
            }).join('');

        var fetched = state.fetched[p.id];
        var fetchedHtml = '';
        if (fetched) {
            var known = models.map(function (m) { return m.modelId; });
            var fresh = fetched.filter(function (f) { return known.indexOf(f.id) < 0; });
            fetchedHtml = '<div class="provider-fetched" style="margin-top:10px;">'
                + (fresh.length === 0
                    ? '<p class="text-muted text-sm">Every model the provider listed is already added.</p>'
                    : '<div class="text-sm" style="margin-bottom:6px;">The provider lists ' + fresh.length + ' more model(s) — tick to add:</div>'
                    + '<div style="display:flex;flex-direction:column;gap:4px;max-height:220px;overflow:auto;">'
                    + fresh.map(function (f) {
                        return '<sl-checkbox class="fetched-model" data-provider="' + p.id + '" data-model="' + esc(f.id) + '" data-name="' + esc(f.name || f.id) + '"'
                            + ' data-context="' + (f.contextWindow || '') + '">' + esc(f.id) + (f.name && f.name !== f.id ? ' <span class="text-muted">(' + esc(f.name) + ')</span>' : '') + '</sl-checkbox>';
                    }).join('')
                    + '</div><sl-button size="small" class="btn-add-fetched" data-id="' + p.id + '" style="margin-top:8px;"><sl-icon slot="prefix" name="check2-square"></sl-icon> Add ticked models</sl-button>')
                + '</div>';
        }

        return '<div class="card provider-card" id="provider-' + p.id + '" data-busy-scope style="padding:14px 16px;margin-bottom:16px;">'
            + '<div style="display:flex;justify-content:space-between;gap:12px;flex-wrap:wrap;align-items:flex-start;">'
            + '<div><strong style="font-size:1.05rem;">' + esc(p.name) + '</strong> ' + badge(p)
            + (p.builtIn ? ' <sl-badge variant="neutral" pill>Built in (from 2.9.0)</sl-badge>' : '')
            + (p.isLocal ? ' <sl-badge variant="primary" pill>Local</sl-badge>' : '')
            + '<div class="text-muted text-sm" style="margin-top:4px;">Sends to <code>' + esc(p.host) + '</code> · <span class="wrap-anywhere">' + esc(p.baseUrl) + '</span>'
            + ' · ' + esc(FORMATS[p.protocol] || p.protocol)
            + (p.approved && p.approvedHost ? ' · approved ' + rel(p.approvedAt) : '') + '</div>'
            + (p.clientAvailable ? '' : '<div class="text-sm" style="color:var(--sl-color-danger-600);">This node has no client for the ' + esc(p.protocol) + ' format yet.</div>')
            + '</div><div style="display:flex;gap:6px;flex-wrap:wrap;">' + actions + '</div></div>'
            + (p.note ? '<p class="admin-warning text-sm" style="margin:10px 0 0 0;">' + esc(p.note) + '</p>' : '')
            + (p.lastError ? '<p class="text-sm" style="margin:8px 0 0 0;color:var(--sl-color-danger-600);">Last error: ' + esc(p.lastError) + '</p>' : '')
            + '<div style="display:flex;justify-content:space-between;align-items:center;margin:14px 0 6px 0;gap:8px;flex-wrap:wrap;"><span class="text-sm"><strong>Keys</strong> (' + p.availableKeyCount + ' of ' + p.keyCount + ' usable now)</span>'
            + '<sl-button size="small" class="btn-add-key" data-id="' + p.id + '"><sl-icon slot="prefix" name="plus-lg"></sl-icon> Add key</sl-button></div>'
            + '<table class="version-table admin-stack-sm"><thead><tr><th class="col-name">Label</th><th>Prefix</th><th>Priority</th><th>Enabled</th><th>Status</th><th>Last used</th><th class="col-actions">Actions</th></tr></thead>'
            + '<tbody>' + keyRows + '</tbody></table>'
            + '<div style="display:flex;justify-content:space-between;align-items:center;margin:14px 0 6px 0;gap:8px;flex-wrap:wrap;"><span class="text-sm"><strong>Models</strong></span><span style="display:flex;gap:6px;">'
            + '<sl-button size="small" class="btn-add-model" data-id="' + p.id + '"><sl-icon slot="prefix" name="plus-lg"></sl-icon> Add model</sl-button>'
            + (p.approved ? '<sl-button size="small" class="btn-fetch-models" data-id="' + p.id + '"><sl-icon slot="prefix" name="cloud-download"></sl-icon> Fetch models</sl-button>' : '')
            + '</span></div>'
            + '<table class="version-table admin-stack-sm"><thead><tr><th class="col-name">Label</th><th>Model ID</th><th class="col-center">Text</th><th class="col-center">Vision</th><th class="col-center">Image gen</th><th class="col-actions">Actions</th></tr></thead>'
            + '<tbody>' + modelRows + '</tbody></table>'
            + fetchedHtml
            + '</div>';
    }

    function renderProviders() {
        list.innerHTML = state.loadFailed
            ? '<div class="card text-sm" style="padding:14px 16px;color:var(--sl-color-danger-600);">Failed to load the providers. Reload the page to try again.</div>'
            : state.providers.length === 0
            ? '<div class="card text-muted text-sm" style="padding:14px 16px;">No provider yet. Add one: pick it from the list, or add a custom one by its address.</div>'
            : state.providers.map(providerCard).join('');
    }

    function fillSelect(sel, models, selectedId, emptyLabel) {
        if (!sel) return;
        sel.innerHTML = '';
        var none = document.createElement('sl-option');
        none.value = '';
        none.textContent = emptyLabel;
        sel.appendChild(none);
        models.forEach(function (m) {
            var opt = document.createElement('sl-option');
            opt.value = m.id;
            opt.textContent = modelName(m);
            sel.appendChild(opt);
        });
        setTimeout(function () { sel.value = selectedId || ''; }, 0);
    }

    var suppressSaveDefaults = false;

    function renderDefaults() {
        suppressSaveDefaults = true;
        var imageProviders = state.providers.filter(function (p) { return p.options && p.options.imageGeneration; }).map(function (p) { return p.id; });
        fillSelect($('default-text-model'), state.models.filter(function (m) { return m.isText; }), state.defaults.defaultTextModelId, 'Default (oldest)');
        fillSelect($('default-vision-model'), state.models.filter(function (m) { return m.isVision; }), state.defaults.defaultVisionModelId, 'Default (oldest)');
        // Image generation only where the provider offers it (OpenRouter in this version); elsewhere the choice is not offered.
        fillSelect($('default-imagegen-model'), state.models.filter(function (m) { return m.isImageGen && imageProviders.indexOf(m.providerId) >= 0; }),
            state.defaults.defaultImageGenModelId, 'Default (oldest)');
        setTimeout(function () { suppressSaveDefaults = false; }, 0);
    }

    function renderFallbacks() {
        var ol = $('fallback-list');
        if (!ol) return;
        ol.innerHTML = state.fallbacks.length === 0
            ? '<li class="text-muted" style="list-style:none;margin-left:-20px;">No fallback: when the text model\'s provider fails, the chat says so.</li>'
            : state.fallbacks.map(function (id, i) {
                var m = modelById(id);
                return '<li style="margin-bottom:4px;">' + esc(m ? modelName(m) : 'a model that no longer exists')
                    + ' <sl-icon-button name="arrow-up" label="Earlier" class="btn-fb-move" data-index="' + i + '" data-dir="-1"' + (i === 0 ? ' disabled' : '') + '></sl-icon-button>'
                    + '<sl-icon-button name="arrow-down" label="Later" class="btn-fb-move" data-index="' + i + '" data-dir="1"' + (i === state.fallbacks.length - 1 ? ' disabled' : '') + '></sl-icon-button>'
                    + '<sl-icon-button name="x-lg" label="Remove" class="btn-fb-remove" data-index="' + i + '"></sl-icon-button></li>';
            }).join('');
        fillSelect($('fallback-add'), state.models.filter(function (m) { return m.isText && state.fallbacks.indexOf(m.id) < 0; }), '', 'Add a fallback model…');
    }

    // After a change: the page is drawn again and comes back to the card of the provider (its anchor), with the result written in it.
    function reloadAndShow(providerId, message) {
        return loadAll().then(function () {
            var card = providerId ? $('provider-' + providerId) : null;
            if (card) {
                if (history.replaceState) history.replaceState(null, '', '#provider-' + providerId);
                if (card.scrollIntoView) card.scrollIntoView({ block: 'nearest' });
                if (message) bmbBusy.showResult(card, true, message);
            }
        });
    }

    // ---- the chat switch ------------------------------------------------------------------------------------------------------------------

    var chatEnabledToggle = $('chat-globally-enabled-toggle');
    if (chatEnabledToggle) {
        getJson('/api-proxy/chat/settings/chat-enabled', { chatGloballyEnabled: true })
            .then(function (data) { chatEnabledToggle.checked = !!(data && data.chatGloballyEnabled); });
        chatEnabledToggle.addEventListener('sl-change', function () {
            var enabled = chatEnabledToggle.checked;
            bmbBusy.run(chatEnabledToggle, function () {
                return bmbBusy.request('/api-proxy/chat/settings/chat-enabled', { method: 'PATCH', headers: headers(), body: JSON.stringify({ enabled: enabled }) });
            }, {
                quiet: true,
                error: function (err) { chatEnabledToggle.checked = !enabled; bmbBusy.showResult(chatEnabledToggle, false, err.message); }
            });
        });
    }

    // ---- add a provider -----------------------------------------------------------------------------------------------------------------

    function renderPresets(filter) {
        var box = $('provider-presets');
        if (!box) return;
        var q = (filter || '').trim().toLowerCase();
        var items = state.presets.filter(function (p) {
            return !q || p.name.toLowerCase().indexOf(q) >= 0 || p.baseUrl.toLowerCase().indexOf(q) >= 0 || p.id.indexOf(q) >= 0;
        });
        var html = items.map(function (p) {
            var on = current.preset && current.preset.id === p.id;
            return '<div class="preset-item" role="option" tabindex="0" aria-selected="' + on + '" data-preset="' + esc(p.id) + '" style="padding:8px 10px;cursor:pointer;border-bottom:1px solid var(--sl-color-neutral-100);'
                + (on ? 'background:var(--sl-color-primary-50);' : '') + '"><strong>' + esc(p.name) + '</strong>' + (p.local ? ' <sl-badge variant="primary" pill>Local</sl-badge>' : '')
                + '<div class="text-muted text-sm wrap-anywhere">' + esc(p.baseUrl) + '</div>'
                + (p.note ? '<div class="text-sm" style="color:var(--sl-color-warning-700);">' + esc(p.note) + '</div>' : '') + '</div>';
        }).join('');
        var custom = !current.preset;
        html += '<div class="preset-item" role="option" tabindex="0" aria-selected="' + custom + '" data-preset="" style="padding:8px 10px;cursor:pointer;' + (custom ? 'background:var(--sl-color-primary-50);' : '') + '">'
            + '<strong>Custom provider</strong><div class="text-muted text-sm">Any service that speaks the OpenAI chat format, Anthropic Messages or Google Gemini, at an address you type in.</div></div>';
        box.innerHTML = html;
    }

    function pickPreset(id) {
        current.preset = id ? (state.presets.find(function (p) { return p.id === id; }) || null) : null;
        var p = current.preset;
        $('provider-name').value = p ? p.name : '';
        $('provider-base-url').value = p ? p.baseUrl : '';
        $('provider-local').checked = !!(p && p.local);
        // A provider from the list brings its own format; only a custom one is typed in.
        var protocol = $('provider-protocol');
        if (protocol) {
            protocol.value = p ? p.protocol : 'openai-chat';
            protocol.disabled = !!p;
        }
        var note = $('provider-preset-note');
        if (note) {
            note.hidden = !(p && p.note);
            note.textContent = p && p.note ? p.note : '';
        }
        renderPresets($('provider-search') ? $('provider-search').value : '');
    }

    var btnAddProvider = $('btn-add-provider');
    if (btnAddProvider) {
        btnAddProvider.addEventListener('click', function () {
            renderPresets('');
            $('dlg-add-provider').show();
        }, pageOpts);
    }
    var search = $('provider-search');
    if (search) search.addEventListener('sl-input', function () { renderPresets(search.value); }, pageOpts);

    var btnSaveProvider = $('btn-save-provider');
    if (btnSaveProvider) {
        btnSaveProvider.addEventListener('click', function () {
            var p = current.preset;
            var body = {
                presetId: p ? p.id : null,
                name: $('provider-name').value.trim(),
                protocol: $('provider-protocol').value || 'openai-chat',
                baseUrl: $('provider-base-url').value.trim(),
                isLocal: $('provider-local').checked
            };
            // The dialog stays open, with what was typed, when the node refuses: the reason is written in it.
            bmbBusy.run(btnSaveProvider, function () {
                return bmbBusy.request('/api-proxy/chat/providers', { method: 'POST', headers: headers(), body: JSON.stringify(body) })
                    .then(function (res) {
                        $('dlg-add-provider').hide();
                        current.preset = null;
                        $('provider-name').value = '';
                        $('provider-base-url').value = '';
                        $('provider-local').checked = false;
                        $('provider-protocol').value = 'openai-chat';
                        $('provider-protocol').disabled = false;
                        return reloadAndShow(res.body.id, 'Added. Nothing is sent to it until you approve its host.').then(function () { askApproval(res.body); });
                    });
            }, { quiet: true });
        }, pageOpts);
    }

    // ---- approve ------------------------------------------------------------------------------------------------------------------------

    function askApproval(p) {
        if (!p || p.approved || p.builtIn) return;
        current.approve = p;
        $('approve-provider-text').textContent = 'Notes and chat content will be sent to ' + p.host + '. Approve?';
        $('dlg-approve-provider').show();
    }

    var btnConfirmApprove = $('btn-confirm-approve');
    if (btnConfirmApprove) {
        btnConfirmApprove.addEventListener('click', function () {
            var p = current.approve;
            if (!p) return;
            bmbBusy.run(btnConfirmApprove, function () {
                return bmbBusy.request('/api-proxy/chat/providers/' + p.id + '/approve', { method: 'POST', headers: headers(), body: JSON.stringify({ host: p.host }) })
                    .then(function () {
                        $('dlg-approve-provider').hide();
                        current.approve = null;
                        return reloadAndShow(p.id, 'Approved: chat content may now be sent to ' + p.host + '.');
                    });
            }, { quiet: true });
        }, pageOpts);
    }

    // ---- edit a provider ----------------------------------------------------------------------------------------------------------------

    function openEdit(p) {
        current.edit = p;
        $('edit-provider-name').value = p.name;
        $('edit-provider-base-url').value = p.baseUrl;
        $('edit-provider-base-url').disabled = !!p.builtIn;
        $('edit-provider-local').checked = !!p.isLocal;
        $('edit-provider-local').disabled = !!p.builtIn;
        var o = p.options || {};
        // Only the wire options of the provider's own protocol are shown (and a reasoning style of that protocol offered). A Shoelace
        // element draws itself with its own display, which beats the browser's rule for [hidden], so the style is set instead.
        var dialog = $('dlg-edit-provider');
        Array.prototype.forEach.call(dialog.querySelectorAll('[data-protocol]'), function (el) {
            el.style.display = el.getAttribute('data-protocol') === p.protocol ? '' : 'none';
        });
        $('edit-provider-reasoning').value = o.reasoning || 'none';
        $('edit-provider-maxtokens').value = o.maxTokensField || 'max_tokens';
        $('edit-provider-streamusage').checked = !!o.streamUsage;
        $('edit-provider-imagegen').checked = !!o.imageGeneration;
        $('edit-provider-promptcaching').checked = o.promptCaching !== false;
        dialog.show();
    }

    var btnSaveEditProvider = $('btn-save-edit-provider');
    if (btnSaveEditProvider) {
        btnSaveEditProvider.addEventListener('click', function () {
            var p = current.edit;
            if (!p) return;
            var body = {
                name: $('edit-provider-name').value.trim(),
                baseUrl: p.builtIn ? null : $('edit-provider-base-url').value.trim(),
                isLocal: p.builtIn ? null : $('edit-provider-local').checked,
                options: {
                    reasoning: $('edit-provider-reasoning').value || 'none',
                    maxTokensField: $('edit-provider-maxtokens').value || 'max_tokens',
                    streamUsage: $('edit-provider-streamusage').checked,
                    imageGeneration: p.protocol === 'openai-chat' && $('edit-provider-imagegen').checked,
                    promptCaching: $('edit-provider-promptcaching').checked
                }
            };
            bmbBusy.run(btnSaveEditProvider, function () {
                return bmbBusy.request('/api-proxy/chat/providers/' + p.id, { method: 'PATCH', headers: headers(), body: JSON.stringify(body) })
                    .then(function (res) {
                        $('dlg-edit-provider').hide();
                        current.edit = null;
                        var withdrawn = p.approved && res.body && !res.body.approved;
                        return reloadAndShow(p.id, withdrawn ? 'Saved. The new address withdrew the approval.' : 'Saved.')
                            .then(function () { if (withdrawn) askApproval(res.body); });
                    });
            }, { quiet: true });
        }, pageOpts);
    }

    // ---- keys ---------------------------------------------------------------------------------------------------------------------------

    var btnSaveChatKey = $('btn-save-chat-key');
    if (btnSaveChatKey) {
        btnSaveChatKey.addEventListener('click', function () {
            var providerId = current.keyProvider;
            var label = $('chat-key-label').value.trim();
            var key = $('chat-key-value').value.trim();
            var priority = parseInt($('chat-key-priority').value || '0', 10) || 0;
            if (!providerId || !label || !key) return;
            bmbBusy.run(btnSaveChatKey, function () {
                return bmbBusy.request('/api-proxy/chat/keys', {
                    method: 'POST', headers: headers(), body: JSON.stringify({ label: label, apiKey: key, priority: priority, providerId: providerId })
                }).then(function () {
                    $('dlg-add-chat-key').hide();
                    $('chat-key-label').value = '';
                    $('chat-key-value').value = '';
                    $('chat-key-priority').value = '0';
                    return reloadAndShow(providerId, 'Key saved. Only its first characters are shown from now on.');
                });
            }, { quiet: true });
        }, pageOpts);
    }

    // ---- models -------------------------------------------------------------------------------------------------------------------------

    function openAddModel(p) {
        current.modelProvider = p.id;
        $('chat-model-provider').textContent = 'Provider: ' + p.name;
        var preset = state.presets.find(function (x) { return x.id === p.presetId; });
        var added = state.models.filter(function (m) { return m.providerId === p.id; }).map(function (m) { return m.modelId; });
        var known = preset ? (preset.models || []).filter(function (m) { return added.indexOf(m.id) < 0; }) : [];
        var box = $('chat-model-known');
        box.hidden = known.length === 0;
        $('chat-model-known-list').innerHTML = known.map(function (m) {
            return '<sl-checkbox class="known-model" data-model="' + esc(m.id) + '" data-name="' + esc(m.name || m.id) + '" data-context="' + (m.contextWindow || '') + '"'
                + ' data-vision="' + (m.vision ? '1' : '') + '">' + esc(m.name || m.id) + ' <code class="text-sm">' + esc(m.id) + '</code>'
                + (m.contextWindow ? ' <span class="text-muted text-sm">' + Math.round(m.contextWindow / 1000) + 'k</span>' : '') + '</sl-checkbox>';
        }).join('');
        $('chat-model-imagegen').disabled = !(p.options && p.options.imageGeneration);
        if ($('chat-model-imagegen').disabled) $('chat-model-imagegen').checked = false;
        $('dlg-add-chat-model').show();
    }

    function ticked(selector) {
        return Array.prototype.slice.call(document.querySelectorAll(selector)).filter(function (c) { return c.checked; }).map(function (c) {
            var cw = parseInt(c.dataset.context, 10);
            return { modelId: c.dataset.model, label: c.dataset.name || c.dataset.model, isText: true, isVision: c.dataset.vision === '1', isImageGen: false, contextWindow: isNaN(cw) ? null : cw };
        });
    }

    // One after the other: the first refusal stops the rest and is written next to the button; what was added stays added. The request
    // itself is made by the caller, inside its bmbBusy.run.
    function addModels(providerId, models, send) {
        return models.reduce(function (chain, m) {
            return chain.then(function () { m.providerId = providerId; return send(m); });
        }, Promise.resolve());
    }

    var btnAddKnown = $('btn-add-known-models');
    if (btnAddKnown) {
        btnAddKnown.addEventListener('click', function () {
            var providerId = current.modelProvider;
            var models = ticked('#chat-model-known-list .known-model');
            if (!providerId || models.length === 0) return;
            bmbBusy.run(btnAddKnown, function () {
                return addModels(providerId, models, function (m) {
                    return bmbBusy.request('/api-proxy/chat/models', { method: 'POST', headers: headers(), body: JSON.stringify(m) });
                }).then(function () {
                    $('dlg-add-chat-model').hide();
                    return reloadAndShow(providerId, models.length + ' model(s) added.');
                });
            }, { quiet: true });
        }, pageOpts);
    }

    var btnSaveChatModel = $('btn-save-chat-model');
    if (btnSaveChatModel) {
        btnSaveChatModel.addEventListener('click', function () {
            var providerId = current.modelProvider;
            var modelIdInput = $('chat-model-id');
            var modelId = modelIdInput.value.trim();
            var label = $('chat-model-label').value.trim();
            var cw = parseInt($('chat-model-context').value, 10);
            modelIdInput.setCustomValidity('');
            if (/\s/.test(modelId)) {
                modelIdInput.setCustomValidity('Model ID must not contain spaces — use the exact model id of the provider');
                modelIdInput.reportValidity();
                return;
            }
            if (!providerId || !modelId || !label) return;
            var body = {
                modelId: modelId, label: label, providerId: providerId,
                isText: $('chat-model-text').checked, isVision: $('chat-model-vision').checked, isImageGen: $('chat-model-imagegen').checked,
                contextWindow: (isNaN(cw) || cw <= 0) ? null : cw
            };
            bmbBusy.run(btnSaveChatModel, function () {
                return bmbBusy.request('/api-proxy/chat/models', { method: 'POST', headers: headers(), body: JSON.stringify(body) }).then(function () {
                    $('dlg-add-chat-model').hide();
                    $('chat-model-id').value = '';
                    $('chat-model-label').value = '';
                    $('chat-model-text').checked = true;
                    $('chat-model-vision').checked = false;
                    $('chat-model-imagegen').checked = false;
                    $('chat-model-context').value = '';
                    return reloadAndShow(providerId, 'Model added.');
                });
            }, { quiet: true });
        }, pageOpts);
    }

    function openEditModel(id) {
        var m = modelById(id);
        if (!m) return;
        current.editModel = m;
        $('edit-chat-model-id').value = m.modelId || '';
        $('edit-chat-model-label').value = modelName(m);
        $('edit-chat-model-text').checked = !!m.isText;
        $('edit-chat-model-vision').checked = !!m.isVision;
        $('edit-chat-model-imagegen').checked = !!m.isImageGen;
        var p = providerById(m.providerId);
        $('edit-chat-model-imagegen').disabled = !(p && p.options && p.options.imageGeneration) && !m.isImageGen;
        $('edit-chat-model-context').value = m.contextWindow || '';
        $('dlg-edit-chat-model').show();
    }

    var btnSaveEditChatModel = $('btn-save-edit-chat-model');
    if (btnSaveEditChatModel) {
        btnSaveEditChatModel.addEventListener('click', function () {
            var m = current.editModel;
            if (!m) return;
            var cw = parseInt($('edit-chat-model-context').value, 10);
            var body = {
                isText: $('edit-chat-model-text').checked, isVision: $('edit-chat-model-vision').checked, isImageGen: $('edit-chat-model-imagegen').checked,
                contextWindow: (isNaN(cw) || cw <= 0) ? null : cw
            };
            bmbBusy.run(btnSaveEditChatModel, function () {
                return bmbBusy.request('/api-proxy/chat/models/' + m.id, { method: 'PATCH', headers: headers(), body: JSON.stringify(body) }).then(function () {
                    $('dlg-edit-chat-model').hide();
                    current.editModel = null;
                    return reloadAndShow(m.providerId, 'Model saved.');
                });
            }, { quiet: true });
        }, pageOpts);
    }

    // ---- defaults and the fallback chain ------------------------------------------------------------------------------------------------

    function saveDefaults(source) {
        if (suppressSaveDefaults) return;
        bmbBusy.run(source, function () {
            return bmbBusy.request('/api-proxy/chat/settings/defaults', {
                method: 'PATCH', headers: headers(),
                body: JSON.stringify({
                    defaultTextModelId: $('default-text-model').value || null,
                    defaultVisionModelId: $('default-vision-model').value || null,
                    defaultImageGenModelId: $('default-imagegen-model').value || null
                })
            }).then(function () { state.defaults.defaultTextModelId = $('default-text-model').value || null; });
        }, { success: 'Saved.', error: function (err) { renderDefaults(); bmbBusy.showResult(source, false, err.message); } });
    }

    ['default-text-model', 'default-vision-model', 'default-imagegen-model'].forEach(function (id) {
        var sel = $(id);
        if (sel) sel.addEventListener('sl-change', function () { saveDefaults(sel); }, pageOpts);
    });

    var btnFallbackAdd = $('btn-fallback-add');
    if (btnFallbackAdd) {
        btnFallbackAdd.addEventListener('click', function () {
            var id = $('fallback-add').value;
            if (!id || state.fallbacks.indexOf(id) >= 0) return;
            state.fallbacks.push(id);
            renderFallbacks();
        }, pageOpts);
    }

    var btnFallbackSave = $('btn-fallback-save');
    if (btnFallbackSave) {
        btnFallbackSave.addEventListener('click', function () {
            var ids = state.fallbacks.slice();
            bmbBusy.run(btnFallbackSave, function () {
                return bmbBusy.request('/api-proxy/chat/settings/fallbacks', { method: 'PATCH', headers: headers(), body: JSON.stringify({ fallbackModelIds: ids }) });
            }, { success: ids.length === 0 ? 'Saved: no fallback.' : 'Saved: ' + ids.length + ' fallback model(s), tried in this order.' });
        }, pageOpts);
    }

    // ---- the buttons inside the cards (drawn again after every change, so listened to on the list) ---------------------------------------

    document.addEventListener('click', function (e) {
        var t = e.target && e.target.closest ? e.target : null;
        if (!t) return;
        var b;
        if ((b = t.closest('.preset-item'))) { pickPreset(b.dataset.preset || null); return; }
        if ((b = t.closest('.btn-fb-move'))) {
            var i = parseInt(b.dataset.index, 10), j = i + parseInt(b.dataset.dir, 10);
            if (j < 0 || j >= state.fallbacks.length) return;
            var tmp = state.fallbacks[i]; state.fallbacks[i] = state.fallbacks[j]; state.fallbacks[j] = tmp;
            renderFallbacks();
            return;
        }
        if ((b = t.closest('.btn-fb-remove'))) { state.fallbacks.splice(parseInt(b.dataset.index, 10), 1); renderFallbacks(); return; }
        if (!list.contains(t)) return;

        if ((b = t.closest('.btn-approve-provider'))) { askApproval(providerById(b.dataset.id)); return; }
        if ((b = t.closest('.btn-edit-provider'))) { var pe = providerById(b.dataset.id); if (pe) openEdit(pe); return; }
        if ((b = t.closest('.btn-add-key'))) {
            var pk = providerById(b.dataset.id);
            if (!pk) return;
            current.keyProvider = pk.id;
            $('chat-key-provider').textContent = 'Provider: ' + pk.name + ' (' + pk.host + ')';
            $('dlg-add-chat-key').show();
            return;
        }
        if ((b = t.closest('.btn-add-model'))) { var pm = providerById(b.dataset.id); if (pm) openAddModel(pm); return; }
        if ((b = t.closest('.btn-edit-chat-model'))) { openEditModel(b.dataset.editId); return; }

        if ((b = t.closest('.btn-withdraw-provider'))) {
            var pw = providerById(b.dataset.id);
            if (!pw || !confirm('Withdraw the approval of ' + pw.name + '? Nothing is sent to ' + pw.host + ' until it is approved again.')) return;
            var withdrawBtn = b;
            bmbBusy.run(withdrawBtn, function () {
                return bmbBusy.request('/api-proxy/chat/providers/' + pw.id + '/withdraw', { method: 'POST', headers: headers() })
                    .then(function () { return reloadAndShow(pw.id, 'Approval withdrawn.'); });
            }, { quiet: true });
            return;
        }
        if ((b = t.closest('.btn-delete-provider'))) {
            var pd = providerById(b.dataset.id);
            if (!pd || !confirm('Delete ' + pd.name + ' with its ' + pd.keyCount + ' key(s) and ' + pd.modelCount + ' model(s)?')) return;
            var deleteBtn = b;
            bmbBusy.run(deleteBtn, function () {
                return bmbBusy.request('/api-proxy/chat/providers/' + pd.id, { method: 'DELETE', headers: headers() })
                    .then(function () { return loadAll().then(function () { bmbBusy.showResult(list, true, pd.name + ' deleted.'); }); });
            }, { quiet: true });
            return;
        }
        if ((b = t.closest('.btn-fetch-models'))) {
            var pf = b.dataset.id;
            var fetchBtn = b;
            bmbBusy.run(fetchBtn, function () {
                return bmbBusy.request('/api-proxy/chat/providers/' + pf + '/fetch-models', { method: 'POST', headers: headers() })
                    .then(function (res) {
                        state.fetched[pf] = Array.isArray(res.body) ? res.body : [];
                        return reloadAndShow(pf, 'The provider lists ' + state.fetched[pf].length + ' model(s).');
                    });
            }, { quiet: true });
            return;
        }
        if ((b = t.closest('.btn-add-fetched'))) {
            var pa = b.dataset.id;
            var models = ticked('#provider-' + pa + ' .fetched-model');
            if (models.length === 0) return;
            var addBtn = b;
            bmbBusy.run(addBtn, function () {
                return addModels(pa, models, function (m) {
                    return bmbBusy.request('/api-proxy/chat/models', { method: 'POST', headers: headers(), body: JSON.stringify(m) });
                }).then(function () { return reloadAndShow(pa, models.length + ' model(s) added.'); });
            }, { quiet: true });
            return;
        }
        if ((b = t.closest('.btn-delete-chat-key'))) {
            if (!confirm('Delete this API key?')) return;
            var keyBtn = b, keyId = b.dataset.keyId;
            var keyProvider = (state.keys.find(function (k) { return k.id === keyId; }) || {}).providerId;
            bmbBusy.run(keyBtn, function () {
                return bmbBusy.request('/api-proxy/chat/keys/' + keyId, { method: 'DELETE', headers: headers() })
                    .then(function () { return reloadAndShow(keyProvider, 'Key deleted.'); });
            }, { quiet: true });
            return;
        }
        if ((b = t.closest('.btn-delete-chat-model'))) {
            if (!confirm('Delete this model?')) return;
            var modelBtn = b, modelId = b.dataset.modelId;
            var modelProvider = (modelById(modelId) || {}).providerId;
            bmbBusy.run(modelBtn, function () {
                return bmbBusy.request('/api-proxy/chat/models/' + modelId, { method: 'DELETE', headers: headers() })
                    .then(function () { return reloadAndShow(modelProvider, 'Model deleted.'); });
            }, { quiet: true });
        }
    }, pageOpts);

    document.addEventListener('sl-change', function (e) {
        var sw = e.target && e.target.closest ? e.target.closest('.switch-chat-key') : null;
        if (!sw || !list.contains(sw)) return;
        var enabled = sw.checked;
        bmbBusy.run(sw, function () {
            return bmbBusy.request('/api-proxy/chat/keys/' + sw.dataset.keyId, { method: 'PATCH', headers: headers(), body: JSON.stringify({ enabled: enabled }) });
        }, { quiet: true, error: function (err) { sw.checked = !enabled; bmbBusy.showResult(sw, false, err.message); } });
    }, pageOpts);

    // ---- start: draw the page, then come back to the card the address names -----------------------------------------------------------

    loadAll().then(function () {
        var hash = location.hash || '';
        if (/^#provider-[0-9a-f-]{36}$/i.test(hash)) {
            var card = document.getElementById(hash.slice(1));
            if (card && card.scrollIntoView) card.scrollIntoView({ block: 'start' });
        }
    });
})();
