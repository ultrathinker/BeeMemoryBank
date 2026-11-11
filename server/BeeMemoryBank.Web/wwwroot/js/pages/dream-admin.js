// Admin -> Dream (plan 3.9): the schedule, the limits, the model, the excluded folders and the dismiss rules of the Dream feature. The form is
// filled and saved by this script against /api-proxy/dream (plan 3.8). The Dream service itself arrives with a later package: until the node
// serves the settings, the node answers "not implemented", the page says so at the top, shows the planned defaults and keeps Save off.
//
// Like every page script it draws only into its own page and dies with it (site-spa.js): every listener sits on an element of the page, every
// read carries the page signal, and every change runs behind the busy helper (bmb-busy.js) with the page's anti-forgery token, which the dream
// proxy entry demands on PUT and DELETE.
(function () {
    'use strict';

    var pageSignal = window.bmbGetPageSignal ? window.bmbGetPageSignal() : null;
    var fetchOpts = pageSignal ? { signal: pageSignal } : {};
    var gone = function () { return !!(pageSignal && pageSignal.aborted); };

    // The planned defaults (dream_settings of plan 3.2). Shown until GET /settings answers; a test keeps them equal to the API's DreamSettings.Default.
    var DEFAULTS = {
        scheduleEnabled: false,
        scheduleStart: '03:00',
        scheduleWindowMinutes: 120,
        maxAdviceScheduled: 10,
        maxAdviceManual: 50,
        maxOpenAdvice: 100,
        minScore: 40,
        manualMaxMinutes: 30,
        maxModelCalls: 200,
        maxTokens: 1500000,
        useModel: false
    };

    // What PUT /settings accepts (DreamSettings' ranges; a test keeps them equal). Checked here so a typo is said before the round trip;
    // the node checks again, and its word is the one shown.
    var RANGES = {
        scheduleWindowMinutes: [15, 720],
        maxAdviceScheduled: [1, 500],
        maxAdviceManual: [0, 1000],
        maxOpenAdvice: [1, 10000],
        minScore: [0, 100],
        manualMaxMinutes: [0, 1440],
        maxModelCalls: [1, 10000],
        maxTokens: [1000, 100000000]
    };

    // What a value out of range is called in the message; the words of the inputs on the page.
    var LABELS = {
        scheduleWindowMinutes: 'Window length',
        maxAdviceScheduled: 'New advice per nightly run',
        maxAdviceManual: 'New advice per manual run',
        maxOpenAdvice: 'Open advice before a nightly run is skipped',
        minScore: 'Lowest score kept',
        manualMaxMinutes: 'Manual run time limit',
        maxModelCalls: 'Model calls per run',
        maxTokens: 'Tokens per run'
    };

    var available = false;          // the node serves the Dream settings yet?
    var state = { analysisModelId: null };

    function byId(id) { return document.getElementById(id); }

    function esc(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function token() {
        var input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : '';
    }

    function val(id) { var el = byId(id); return el ? String(el.value == null ? '' : el.value) : ''; }
    function checked(id) { var el = byId(id); return !!(el && el.checked); }
    function num(id) { var n = parseInt(val(id), 10); return isNaN(n) ? null : n; }

    function setVal(id, value) { var el = byId(id); if (el) el.value = value; }
    function setChecked(id, on) { var el = byId(id); if (el) el.checked = !!on; }

    // ---- the note at the top (the endpoint is not there yet, or the settings could not be read) ------------------------------------------

    function sayNote(variant, icon, text) {
        var host = byId('dream-endpoint-note');
        if (!host) return;
        host.hidden = false;
        host.innerHTML = '';
        var alert = document.createElement('sl-alert');
        alert.variant = variant;
        alert.setAttribute('variant', variant);
        alert.setAttribute('open', '');
        var ic = document.createElement('sl-icon');
        ic.slot = 'icon';
        ic.name = icon;
        alert.appendChild(ic);
        alert.appendChild(document.createTextNode(text));
        host.appendChild(alert);
    }

    // The Dream service is not in this build yet: the form keeps the planned defaults and Save stays off. A GET answered
    // 501 "not implemented" - exactly the case the page is told to name.
    function sayNotAvailable() {
        available = false;
        sayNote('warning', 'hourglass-split',
            'The Dream service is not in this build yet: the node answers every Dream settings call with "not implemented". ' +
            'The form below shows the planned defaults; saving turns on with the update that brings the service.');
        var save = byId('btn-save-dream-settings');
        if (save) { save.disabled = true; save.setAttribute('title', 'Saving turns on when the node serves the Dream settings.'); }
    }

    function sayLoadError(message) {
        available = false;
        sayNote('danger', 'exclamation-octagon', 'The Dream settings could not be read: ' + message);
        var saveBtn = byId('btn-save-dream-settings');
        if (saveBtn) saveBtn.disabled = true;
    }

    // ---- the form -------------------------------------------------------------------------------------------------------------------------

    function applySettings(s) {
        s = s || {};
        setChecked('dream-schedule-enabled', s.scheduleEnabled);
        setVal('dream-schedule-start', s.scheduleStart || DEFAULTS.scheduleStart);
        setVal('dream-schedule-window', s.scheduleWindowMinutes);
        setVal('dream-max-advice-scheduled', s.maxAdviceScheduled);
        setVal('dream-max-advice-manual', s.maxAdviceManual);
        setVal('dream-max-open-advice', s.maxOpenAdvice);
        setVal('dream-min-score', s.minScore);
        setVal('dream-manual-max-minutes', s.manualMaxMinutes);
        setVal('dream-max-model-calls', s.maxModelCalls);
        setVal('dream-max-tokens', s.maxTokens);
        setChecked('dream-use-model', s.useModel);
        state.analysisModelId = s.analysisModelId || null;
        syncModelSelect();
        var excluded = Array.isArray(s.excludedFolders) ? s.excludedFolders.map(function (f) { return f.path || ''; }).filter(Boolean) : [];
        setVal('dream-excluded-folders', excluded.join('\n'));
        var updated = byId('dream-settings-updated');
        if (updated) updated.textContent = s.updatedAt ? 'Last saved ' + new Date(s.updatedAt).toLocaleString() + '.' : '';
    }

    function loadSettings() {
        return fetch('/api-proxy/dream/settings', fetchOpts).then(function (r) {
            if (r.status === 501) return { notThereYet: true };
            if (!r.ok) throw new Error('the node answered ' + r.status + '.');
            return r.json();
        }).then(function (s) {
            if (!s || gone()) return;
            if (s.notThereYet) { applySettings(DEFAULTS); sayNotAvailable(); return; }
            available = true;
            applySettings(s);
        }).catch(function (e) {
            if (gone()) return;
            applySettings(DEFAULTS);
            sayLoadError(e.message || 'the node did not answer.');
        });
    }

    function saveSettings() {
        var saveBtn = byId('btn-save-dream-settings');
        if (!saveBtn || !available || saveBtn.disabled) return;

        var start = val('dream-schedule-start').trim();
        if (!/^([01]\d|2[0-3]):[0-5]\d$/.test(start)) {
            bmbBusy.showResult(saveBtn, false, 'The window opens at must be a time HH:mm (00:00-23:59).');
            return;
        }
        var numbers = {
            scheduleWindowMinutes: num('dream-schedule-window'),
            maxAdviceScheduled: num('dream-max-advice-scheduled'),
            maxAdviceManual: num('dream-max-advice-manual'),
            maxOpenAdvice: num('dream-max-open-advice'),
            minScore: num('dream-min-score'),
            manualMaxMinutes: num('dream-manual-max-minutes'),
            maxModelCalls: num('dream-max-model-calls'),
            maxTokens: num('dream-max-tokens')
        };
        for (var name in RANGES) {
            if (!Object.prototype.hasOwnProperty.call(RANGES, name)) continue;
            var n = numbers[name];
            if (n == null || n < RANGES[name][0] || n > RANGES[name][1]) {
                bmbBusy.showResult(saveBtn, false, LABELS[name] + ' must be between ' + RANGES[name][0] + ' and ' + RANGES[name][1] + '.');
                return;
            }
        }

        var payload = {
            scheduleEnabled: checked('dream-schedule-enabled'),
            scheduleStart: start,
            scheduleWindowMinutes: numbers.scheduleWindowMinutes,
            maxAdviceScheduled: numbers.maxAdviceScheduled,
            maxAdviceManual: numbers.maxAdviceManual,
            maxOpenAdvice: numbers.maxOpenAdvice,
            minScore: numbers.minScore,
            manualMaxMinutes: numbers.manualMaxMinutes,
            maxModelCalls: numbers.maxModelCalls,
            maxTokens: numbers.maxTokens,
            useModel: checked('dream-use-model'),
            analysisModelId: val('dream-analysis-model') || null,
            excludedFolderPaths: excludedPaths()
        };

        // A change through the proxy carries the page's anti-forgery token; a refusal of the node is said in full next to the button.
        bmbBusy.run(saveBtn, function () {
            return bmbBusy.request('/api-proxy/dream/settings', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token() },
                body: JSON.stringify(payload)
            }).then(function () {
                return loadSettings();
            }).then(function () {
                if (!gone()) bmbBusy.showResult(saveBtn, true, 'Saved.');
            });
        }, {
            quiet: true,
            error: function (err) { if (!gone()) bmbBusy.showResult(saveBtn, false, err.message); }
        });
    }

    function excludedPaths() {
        var seen = {};
        var out = [];
        (val('dream-excluded-folders') || '').split('\n').forEach(function (line) {
            var p = line.trim();
            if (!p || Object.prototype.hasOwnProperty.call(seen, p)) return;
            seen[p] = true;
            out.push(p);
        });
        return out;
    }

    // ---- the model choice (the text models of the chat catalogue) ------------------------------------------------------------------------

    function syncModelSelect() {
        var sel = byId('dream-analysis-model');
        if (!sel) return;
        setTimeout(function () { sel.value = state.analysisModelId || ''; }, 0);
    }

    function loadModels() {
        var sel = byId('dream-analysis-model');
        if (!sel) return;
        fetch('/api-proxy/chat/models/all', fetchOpts)
            .then(function (r) { return r.ok ? r.json() : []; })
            .then(function (list) {
                if (gone() || !sel.isConnected) return;
                var text = (Array.isArray(list) ? list : []).filter(function (m) { return !!m.isText; });
                sel.innerHTML = '';
                var def = document.createElement('sl-option');
                def.value = '';
                def.textContent = "Default (the chat's text model)";
                sel.appendChild(def);
                text.forEach(function (m) {
                    var opt = document.createElement('sl-option');
                    opt.value = m.id;
                    // "model - provider": the same model id can be offered by several providers.
                    opt.textContent = (m.label || m.modelId) + (m.providerName ? ' - ' + m.providerName : '');
                    sel.appendChild(opt);
                });
                var hint = byId('dream-models-hint');
                if (hint) {
                    hint.textContent = text.length === 0
                        ? 'No text model in the catalogue yet: add one on the AI / Chat page to let Dream use a model.'
                        : text.length + ' text model' + (text.length === 1 ? '' : 's') + ' from the chat catalogue.';
                }
                syncModelSelect();
            })
            .catch(function () { if (!gone()) syncModelSelect(); });
    }

    // ---- the dismiss rules ----------------------------------------------------------------------------------------------------------------

    function scopeWords(scope) {
        switch (scope) {
            case 'advice': return 'this advice';
            case 'object': return 'this kind, this object';
            case 'folder': return "this kind, this folder's subtree";
            case 'kind': return 'this kind everywhere';
            default: return String(scope);
        }
    }

    function ruleTarget(r) {
        if (r.tagName) return 'tag "' + r.tagName + '"';
        if (r.path) return 'folder ' + r.path;
        if (r.objectId) return (r.title ? '"' + r.title + '" (' : '(') + String(r.objectId).slice(0, 8) + '…)';
        if (r.fingerprint) return 'fingerprint ' + String(r.fingerprint).slice(0, 12) + '…';
        return '—';
    }

    function renderRules(items) {
        var body = byId('dream-rules-body');
        if (!body) return;
        if (!items.length) {
            body.innerHTML = '<tr><td colspan="7" class="col-empty text-muted text-sm">No dismiss rules: nothing has been dismissed with "never suggest again".</td></tr>';
            return;
        }
        body.innerHTML = items.map(function (r) {
            var expires = r.expiresAt ? new Date(r.expiresAt).toLocaleDateString() : 'never';
            return '<tr>'
                + '<td class="col-name text-muted text-sm" data-label="Created">' + esc(r.createdAt ? new Date(r.createdAt).toLocaleString() : '—') + '</td>'
                + '<td data-label="Scope">' + esc(scopeWords(r.scope)) + '</td>'
                + '<td class="text-sm" data-label="Kind">' + (r.kind ? '<code>' + esc(r.kind) + '</code>' : '—') + '</td>'
                + '<td class="wrap-anywhere text-sm" data-label="Applies to">' + esc(ruleTarget(r)) + '</td>'
                + '<td class="wrap-anywhere text-sm" data-label="Reason">' + esc(r.reason || '—') + '</td>'
                + '<td class="text-muted text-sm" data-label="Expires">' + esc(expires) + '</td>'
                + '<td class="col-actions"><div class="row-actions"><sl-icon-button name="trash" label="Delete rule" class="btn-delete-dream-rule" data-rule-id="' + esc(r.id) + '"></sl-icon-button></div></td>'
                + '</tr>';
        }).join('');
        body.querySelectorAll('.btn-delete-dream-rule').forEach(function (btn) {
            btn.addEventListener('click', function () { deleteRule(btn.dataset.ruleId, btn); });
        });
    }

    function rulesNotAvailable() {
        var body = byId('dream-rules-body');
        if (body) body.innerHTML = '<tr><td colspan="7" class="col-empty text-muted text-sm">The rules service is not in this build yet; the rules are listed here once the Dream service arrives.</td></tr>';
    }

    function rulesLoadFailed(message) {
        var body = byId('dream-rules-body');
        if (body) body.innerHTML = '<tr><td colspan="7" class="col-empty text-muted text-sm">The rules could not be read: ' + esc(message) + '</td></tr>';
    }

    function loadRules() {
        var body = byId('dream-rules-body');
        if (!body) return;
        fetch('/api-proxy/dream/rules', fetchOpts).then(function (r) {
            if (r.status === 501) return { notThereYet: true };
            if (!r.ok) throw new Error('the node answered ' + r.status + '.');
            return r.json();
        }).then(function (data) {
            if (!data || gone()) return;
            if (data.notThereYet) { rulesNotAvailable(); return; }
            renderRules(Array.isArray(data.items) ? data.items : []);
        }).catch(function (e) {
            if (!gone()) rulesLoadFailed(e.message || 'the node did not answer.');
        });
    }

    function deleteRule(id, el) {
        if (!confirm('Delete this dismiss rule? The advice it kept away may come back on the next run.')) return;
        bmbBusy.run(el, function () {
            return bmbBusy.request('/api-proxy/dream/rules/' + encodeURIComponent(id), {
                method: 'DELETE',
                headers: { 'RequestVerificationToken': token() }
            }).then(loadRules);
        }, { quiet: true });
    }

    // ---- wiring: everything on elements of this page, so the single-page navigation takes it down with the page ----------------------------

    function wire() {
        var saveBtn = byId('btn-save-dream-settings');
        if (saveBtn) saveBtn.addEventListener('click', saveSettings);
    }

    // Reading .value of a Shoelace element before it is registered says nothing: wait for the ones the form is made of.
    Promise.all([
        customElements.whenDefined('sl-input'),
        customElements.whenDefined('sl-select'),
        customElements.whenDefined('sl-switch'),
        customElements.whenDefined('sl-textarea')
    ]).then(function () {
        if (gone()) return;
        wire();
        loadModels();
        loadSettings();
        loadRules();
    });
})();
