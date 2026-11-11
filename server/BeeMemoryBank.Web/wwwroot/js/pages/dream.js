// ===== Dream page =====
// The state of Dream with its Start and Stop, the live feed of the run that is going (advice appears one by one), the open advice, and the history of the runs with
// rows that open. What the words and the numbers say is dream-model.js (run on its own by DreamPageModelTests); this file draws and talks to /api-proxy/dream.
// Every part loads on its own and says in words when it cannot: nothing here may leave the page blank when an endpoint answers with a stub or an error.
(function () {
    'use strict';

    var M = window.bmbDreamModel;
    var stateCard = document.getElementById('dream-state');
    if (!stateCard || !M || !window.bmbBusy) return;

    var API = '/api-proxy/dream';
    var OPEN_SIZE = 20;
    var HISTORY_SIZE = 10;
    var FEED_BATCH = 50;
    var LOG_BATCH = 200;
    var MAX_FEED = 200;       // the most advice the node gives in one read of a run (MaxFeedLimit)
    var POLL_MS = 2000;       // the live feed: every two seconds while a run goes on
    var IDLE_MS = 15000;      // the state, when nothing is running: a run may have been started by the schedule
    var VISIBLE_GAP_MS = 5000; // the tab comes back: the state, the open advice and the history are read again, at most once in this time

    var st = {
        status: null,
        settings: null,
        lastRun: null,
        modelDefaultSet: false,
        canStartNow: false,       // the state allows a start (nothing is running); the start form adds its own condition
        feed: { runId: null, run: null, items: [], lastSeq: 0, queue: [], revealing: false, finished: false, failures: 0, timer: null },
        open: { page: 1, kind: 'all', minScore: 0, sort: 'score', seq: 0, sig: null },
        history: { page: 1, mode: 'all', seq: 0, counts: {}, sig: null, runSigs: {}, openIds: {} },
        advice: {},               // the latest of every advice shown, by id: what the buttons act on
        runModels: {},            // the model of each run seen, by id: "Found by the AI model: <label>" for an advice that does not name its own
        focus: { id: null, sig: null }, // /Dream?advice=<id>: the advice shown on its own at the top
        rules: { seq: 0, loaded: false },
        visibleAt: 0,
        applyId: null,
        dismissId: null
    };
    var timers = { idle: null, elapsed: null, reveal: null };
    var gone = false;         // the page was left (single-page navigation): nothing that arrives afterwards may touch it

    // ---- small helpers ---------------------------------------------------------------------------------------------------------------
    function $(id) { return document.getElementById(id); }
    function esc(text) {
        return String(text == null ? '' : text).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }
    function arr(a) { return Array.isArray(a) ? a : []; }
    function show(el, on) { if (el) el.hidden = !on; }

    function query(params) {
        var parts = [];
        Object.keys(params || {}).forEach(function (k) {
            var v = params[k];
            if (v !== undefined && v !== null && v !== '') parts.push(encodeURIComponent(k) + '=' + encodeURIComponent(v));
        });
        return parts.length ? '?' + parts.join('&') : '';
    }

    // A read: the body of the answer as an object, or an Error with the node's words (bmbBusy.request). An answer that comes after the page was left is
    // dropped (the promise never settles), so no handler of this page runs against elements that are gone.
    function getJson(path, params) {
        return new Promise(function (resolve, reject) {
            bmbBusy.request(API + path + query(params), { headers: { 'Accept': 'application/json' }, cache: 'no-store' }).then(function (r) {
                if (!gone) resolve(r.body || {});
            }, function (err) {
                if (!gone) reject(err);
            });
        });
    }

    function antiforgery() {
        var input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : '';
    }

    // A change. Every caller sits inside bmbBusy.run(...): the button spins, a second click does nothing, a refusal is written next to the button.
    function send(verb, path, body) {
        var headers = { 'Accept': 'application/json', 'RequestVerificationToken': antiforgery() };
        var init = { headers: headers, body: undefined };
        if (body !== undefined) { headers['Content-Type'] = 'application/json'; init.body = JSON.stringify(body); }
        // bmb-busy-exempt: the one helper of this page for a change; each of its callers runs inside bmbBusy.run(...)
        init.method = verb;
        return bmbBusy.request(API + path, init).then(function (r) { return r.body || {}; });
    }

    function toast(variant, icon, text) {
        var alert = document.createElement('sl-alert');
        alert.variant = variant;
        alert.setAttribute('variant', variant);
        alert.setAttribute('closable', '');
        alert.setAttribute('duration', '6000');
        var i = document.createElement('sl-icon');
        i.slot = 'icon';
        i.name = icon;
        alert.appendChild(i);
        alert.appendChild(document.createTextNode(text));
        document.body.appendChild(alert);
        if (typeof alert.toast === 'function') alert.toast(); else alert.setAttribute('open', '');
    }

    function errorBox(text, retry) {
        return '<div class="dream-error"><sl-icon name="exclamation-octagon"></sl-icon><span>' + esc(text) + '</span>' +
            (retry ? '<sl-button size="small" data-dream-retry="' + esc(retry) + '">Try again</sl-button>' : '') + '</div>';
    }
    function loadingBox() { return '<div class="dream-loading"><sl-spinner></sl-spinner></div>'; }

    // ---- the state card --------------------------------------------------------------------------------------------------------------
    function activeRun() { return st.status && M.isActive(st.status.activeRun) ? st.status.activeRun : null; }

    function renderStatus() {
        var view = M.statusView(st.status, st.lastRun, Date.now());
        $('dream-state-title').textContent = view.title;
        $('dream-dot').className = 'dream-dot dream-dot-' + (view.tone === 'idle' ? 'idle' : view.tone);
        $('dream-state-lines').innerHTML = view.lines.map(function (l) { return '<div>' + esc(l) + '</div>'; }).join('');
        show($('dream-start'), view.tone !== 'running' && view.tone !== 'stopping');
        st.canStartNow = !!view.canStart;
        $('dream-start').disabled = !view.canStart;
        show($('dream-stop'), view.canStop);
        show($('dream-start-form'), view.canStart);
        renderStartForm();
        renderWarnings();
        tickElapsed();
    }

    function renderWarnings() {
        var host = $('dream-warnings');
        if (!st.status) return;   // a failed load put its own notice here
        var html = M.warnings(st.status).map(function (w) {
            return '<sl-alert variant="' + esc(w.variant) + '" open><sl-icon slot="icon" name="' + esc(w.icon) + '"></sl-icon>' + esc(w.text) +
                (w.link ? ' <a href="' + esc(w.link.href) + '">' + esc(w.link.text) + '</a>' : '') + '</sl-alert>';
        }).join('');
        if (host.dataset.html !== html) { host.dataset.html = html; host.innerHTML = html; }   // not redrawn when it says the same: the alerts do not blink at every poll
    }

    // The two switches of a run: the built-in rules (on by default) and the AI model (as the settings say, when a model and a key are set up).
    function formNow() {
        var model = $('dream-use-model');
        return { useRules: !!$('dream-use-rules').checked, useModel: !model.disabled && !!model.checked, trial: !!$('dream-trial').checked };
    }

    function renderStartForm() {
        var s = st.status || {};
        var settings = s.settings || {};
        var useModel = $('dream-use-model');
        if (s.modelReady && !st.modelDefaultSet) { useModel.checked = !!settings.useModel; st.modelDefaultSet = true; }
        var form = M.startForm(st.status, formNow());
        $('dream-model-label').textContent = form.model.label;
        useModel.disabled = form.model.disabled;
        if (form.model.disabled) useModel.checked = false;
        var why = $('dream-model-why');
        var html = form.model.reason ? esc(form.model.reason) + ' ' + form.model.links.map(function (l) {
            return 'For ' + esc(l.what) + ': <a href="' + esc(l.href) + '">' + esc(l.text) + '</a>.';
        }).join(' ') : '';
        if (why.dataset.html !== html) { why.dataset.html = html; why.innerHTML = html; }
        show(why, !!html);
        $('dream-max-advice').placeholder = 'default: ' + (settings.maxAdviceManual > 0 ? settings.maxAdviceManual : 'no limit');
        var minutes = st.settings ? st.settings.manualMaxMinutes : null;
        $('dream-max-minutes').placeholder = 'default: ' + (minutes == null ? 'from the settings' : minutes > 0 ? minutes : 'no limit');
        renderStartHelp();
    }

    // What will run and what leaves this node; Start is off, with the reason next to it, when neither switch is on.
    function renderStartHelp() {
        var form = M.startForm(st.status, formNow());
        $('dream-start-help').textContent = form.help;
        var reason = $('dream-start-reason');
        reason.textContent = form.startReason;
        show(reason, !form.canStart);
        $('dream-start').disabled = !st.canStartNow || !form.canStart;
    }

    function tickElapsed() {
        var run = activeRun();
        var out = $('dream-elapsed');
        if (!run) { out.textContent = ''; return; }
        var ms = M.runDurationMs(run, Date.now());
        out.textContent = ms == null ? '' : M.formatDuration(ms);
    }

    function applyStatus(status) {
        // The feed knows a run that the state does not know yet (just started): do not let a late answer say "idle" over it.
        if (!M.isActive(status.activeRun) && st.feed.runId && !st.feed.finished && st.feed.run && M.isActive(st.feed.run)) status.activeRun = st.feed.run;
        // The state names the last finished run too: a run that another tab finished shows in the state card without a read of its own.
        if (status.lastRun && status.lastRun.id && !M.isActive(status.lastRun)) st.lastRun = status.lastRun;
        st.status = status;
        renderStatus();
        var run = status.activeRun;
        if (M.isActive(run) && st.feed.runId !== run.id) startFeed(run.id, run);
    }

    function loadStatus() {
        return getJson('/status').then(function (status) {
            applyStatus(status);
            return status;
        }, function (err) {
            st.status = null;
            renderStatus();
            $('dream-warnings').innerHTML = errorBox(M.loadFailure('the state of Dream', err), 'status');
            return null;
        });
    }

    function loadSettings() {
        return getJson('/settings').then(function (s) { st.settings = s; renderStartForm(); }, function () { /* the placeholders say "from the settings" */ });
    }

    function loadLastRun() {
        return getJson('/runs', { page: 1, pageSize: 1, mode: 'all' }).then(function (r) {
            var first = arr(r.items)[0];
            st.lastRun = first && !M.isActive(first) ? first : st.lastRun;
            if (st.status) renderStatus();
        }, function () { /* the line says that Dream has not run yet */ });
    }

    // ---- start and stop --------------------------------------------------------------------------------------------------------------
    function onStart() {
        var btn = $('dream-start');
        var form = formNow();
        var built = M.buildStartRequest({
            trial: form.trial,
            useRules: form.useRules,
            withModel: form.useModel,
            modelReady: !!(st.status && st.status.modelReady),
            maxAdvice: $('dream-max-advice').value,
            maxMinutes: $('dream-max-minutes').value
        });
        if (!built.ok) { bmbBusy.showResult(btn, false, built.error); return; }
        var started = null;
        bmbBusy.showResult(btn, true, '');
        bmbBusy.run(btn, function () {
            return send('POST', '/runs', built.body).then(function (body) { started = body; });
        }, {
            quiet: true,
            error: function (err) {
                bmbBusy.showResult(btn, false, M.errorText(err));
                if (err && err.body && err.body.code === 'already_running') loadStatus();
            }
        }).then(function (ok) {
            if (!ok || gone) return;
            var id = started && (started.runId || started.id);
            if (id) startFeed(id, { id: id, state: 'queued', mode: built.body.mode, trigger: 'manual', useRules: built.body.useRules, useModel: built.body.useModel, counters: {} });
            loadStatus();
        });
    }

    function onStop() {
        var btn = $('dream-stop');
        var run = activeRun();
        if (!run) return;
        bmbBusy.run(btn, function () { return send('POST', '/runs/' + encodeURIComponent(run.id) + '/stop'); }, { quiet: true }).then(function (ok) {
            if (ok && !gone) pollFeedNow();
        });
    }

    // ---- the live feed ---------------------------------------------------------------------------------------------------------------
    function startFeed(runId, run) {
        var f = st.feed;
        if (gone) return;
        clearTimeout(f.timer);
        clearTimeout(timers.reveal);
        f.runId = runId;
        f.run = run || null;
        f.items = [];
        f.lastSeq = 0;
        f.queue = [];
        f.revealing = false;
        f.finished = false;
        f.failures = 0;
        $('dream-feed-list').innerHTML = '';
        show($('dream-feed-empty'), true);
        show($('dream-feed'), true);
        $('dream-activity').classList.remove('is-done');
        $('dream-activity-text').textContent = 'Starting...';
        $('dream-feed-title').textContent = run && run.mode === 'trial' ? 'This trial run' : 'This run';
        $('dream-feed-meta').textContent = '';
        pollFeedNow();
    }

    function pollFeedNow() {
        clearTimeout(st.feed.timer);
        pollFeed();
    }

    function scheduleFeed(ms) {
        if (gone) return;
        clearTimeout(st.feed.timer);
        st.feed.timer = setTimeout(pollFeed, ms);
    }

    function pollFeed() {
        var f = st.feed;
        var runId = f.runId;
        if (gone || !runId || f.finished) return;
        if (document.hidden) { scheduleFeed(POLL_MS * 2); return; }
        // a read, polled every two seconds: no button starts it, so there is nothing to lock
        getJson('/runs/' + encodeURIComponent(runId) + '/advice', { afterSeq: f.lastSeq, limit: FEED_BATCH }).then(function (data) {
            if (f.runId !== runId) return;   // another run took the feed
            f.failures = 0;
            var merged = M.mergeFeed(f.items, data.items);
            f.items = merged.items;
            f.lastSeq = M.lastSeqOf(data.items, Math.max(f.lastSeq, Number(data.lastSeq) || 0));
            queueReveal(merged.added);
            var run = data.run || null;
            if (run) {
                f.run = run;
                rememberRun(run);
                if (st.status) { st.status.activeRun = M.isActive(run) ? run : null; renderStatus(); }
            }
            setActivity(data.activity, run);
            if (run && !M.isActive(run)) { finishFeed(run); return; }
            scheduleFeed(arr(data.items).length >= FEED_BATCH ? 50 : POLL_MS);
        }, function (err) {
            if (f.runId !== runId) return;
            f.failures++;
            if (f.failures >= 8) {
                $('dream-activity').classList.add('is-done');
                $('dream-activity-text').textContent = 'Lost touch with the node (' + M.errorText(err) + '). The run may still be going: reload this page to follow it.';
                return;
            }
            $('dream-activity-text').textContent = 'Lost touch with the node (' + M.errorText(err) + '). Trying again...';
            scheduleFeed(Math.min(POLL_MS * (f.failures + 1), 15000));
        });
    }

    function setActivity(text, run) {
        var line = String(text || '').trim();
        if (!line && run) line = M.isActive(run) ? (M.phaseLabel(run.phase) ? M.phaseLabel(run.phase) + '...' : 'Working...') : '';
        if (line) $('dream-activity-text').textContent = line;
        var meta = run ? M.counterLine(run.counters) : '';
        $('dream-feed-meta').textContent = meta;
    }

    function finishFeed(run) {
        var f = st.feed;
        f.finished = true;
        clearTimeout(f.timer);
        var o = M.outcomeInfo(run);
        $('dream-activity').classList.add('is-done');
        $('dream-activity-text').textContent = 'Finished: ' + o.label.toLowerCase() + (o.text ? '. ' + o.text : '.') + (run.errorText ? ' ' + run.errorText : '');
        st.lastRun = run;
        if (st.status) { st.status.activeRun = null; }
        loadStatus();
        loadOpen();
        loadHistory();
    }

    // The cards of a poll come one by one, not all at once: a person watching sees each arrive.
    function queueReveal(items) {
        var f = st.feed;
        f.queue = f.queue.concat(items);
        if (!f.revealing) revealNext();
    }

    function revealNext() {
        var f = st.feed;
        if (gone) return;
        var a = f.queue.shift();
        if (!a) { f.revealing = false; return; }
        f.revealing = true;
        show($('dream-feed-empty'), false);
        var list = $('dream-feed-list');
        var holder = document.createElement('div');
        holder.innerHTML = cardHtml(a, { isNew: true });
        list.insertBefore(holder.firstElementChild, list.firstChild);
        timers.reveal = setTimeout(revealNext, M.revealDelay(f.queue.length));
    }

    // ---- a card of advice ------------------------------------------------------------------------------------------------------------
    function remember(a) { if (a && a.id) st.advice[a.id] = a; }
    function rememberRun(run) { if (run && run.id && run.modelLabel) st.runModels[run.id] = run.modelLabel; }
    function chatAvailable() { return !(st.status && st.status.chatAvailable === false); }

    function cardView(a) {
        return M.adviceView(a, { chatAvailable: chatAvailable(), runModelLabel: a && a.runId ? st.runModels[a.runId] : '' });
    }

    // What a drawn card was drawn from (data-sig on the card): the same string means the card shows the advice as it is, so it is left alone.
    function cardSig(a) { return M.adviceSignature([a]) + '\n' + chatAvailable(); }

    var CHAT_TIP = 'Opens the built-in chat in a new tab with this task as a draft. Nothing is sent until you press Run there.';

    // opts: isNew (it has just arrived in the live feed), focus (the advice the page was opened for: /Dream?advice=<id>)
    function cardHtml(a, opts) {
        remember(a);
        opts = opts || {};
        var v = cardView(a);
        var cls = 'dream-card band-' + v.band + (v.expired ? ' is-expired' : v.closed ? ' is-closed' : '') + (opts.isNew ? ' is-new' : '') + (opts.focus ? ' is-focus' : '');
        var h = '<article class="' + cls + '" data-advice-id="' + esc(v.id) + '" data-sig="' + esc(cardSig(a)) + '" data-busy-scope' + (opts.focus ? ' tabindex="-1"' : '') + '>';

        h += '<div class="dream-card-top">' +
            '<div class="dream-score" title="' + esc('Score ' + (v.score == null ? 'unknown' : v.score + ' of 100') + ' (' + v.bandLabel.toLowerCase() + '): how much this is worth doing') + '">' +
            '<span>' + (v.score == null ? '-' : v.score) + '</span><small>' + esc(v.bandLabel) + '</small></div>' +
            '<div class="dream-card-kind"><sl-icon name="' + esc(v.kind.icon) + '"></sl-icon><span>' + esc(v.kind.label) + '</span>' +
            (v.trial ? '<sl-badge variant="warning" pill>Trial</sl-badge>' : '') +
            '<sl-badge variant="neutral" pill>' + esc(v.source) + '</sl-badge>' +
            (v.status.value !== 'new' ? '<sl-badge variant="' + esc(v.status.variant) + '" pill>' + esc(v.status.label) + '</sl-badge>' : '') +
            '</div></div>';

        h += '<div class="dream-card-title">' + esc(v.title) + '</div>';
        h += '<div class="dream-card-found"><sl-icon name="' + (v.source === 'Model' ? 'stars' : 'list-check') + '"></sl-icon>' + esc(v.foundBy) + '</div>';
        if (v.summary) h += '<p class="dream-card-summary">' + esc(v.summary) + '</p>';
        if (v.lastError) h += '<p class="dream-card-note is-bad"><sl-icon name="exclamation-triangle"></sl-icon> The last attempt failed: ' + esc(v.lastError) + '</p>';
        if (v.expired) h += '<p class="dream-card-note"><sl-icon name="clock-history"></sl-icon> Expired: ' + esc(v.note) + '</p>';
        if (v.applied) h += '<p class="dream-card-note is-good"><sl-icon name="check-circle"></sl-icon> Done' + (v.applied.result ? ': ' + esc(v.applied.result) : '.') + '</p>';

        if (v.objects.length) {
            // The type in words before the name; a link opens in a new tab, so the list of advice stays where it was. A folder that does not exist yet is plain text.
            h += '<ul class="dream-objects">' + v.objects.map(function (o) {
                return '<li class="dream-object"><sl-icon name="' + esc(o.icon) + '"></sl-icon>' +
                    '<span class="dream-object-type">' + esc(o.typeLabel) + '</span>' +
                    (o.role ? '<span class="dream-object-role">' + esc(o.role) + '</span>' : '') +
                    (o.href
                        ? '<a class="dream-object-link" href="' + esc(o.href) + '" target="_blank" rel="noopener">' + esc(o.label) +
                          '<sl-icon class="dream-newtab" name="box-arrow-up-right" aria-hidden="true"></sl-icon><span class="dream-sr"> (opens in a new tab)</span></a>'
                        : '<span class="dream-object-name">' + esc(o.label) + '</span>') +
                    (o.sub ? '<span class="dream-object-sub">' + esc(o.sub) + '</span>' : '') + '</li>';
            }).join('') + '</ul>';
        }

        h += '<sl-details class="dream-card-details" summary="Why, and the details"><ul class="dream-evidence">' + v.evidence.map(function (e) {
            return '<li><span class="dream-evidence-label">' + esc(e.label) + '</span><div>' +
                (e.type === 'quote' ? '<blockquote>' + esc(e.text) + '</blockquote>' : esc(e.text)) + (e.value ? ' <strong>' + esc(e.value) + '</strong>' : '') + '</div></li>';
        }).join('') + '</ul><div class="dream-card-facts">' +
            (v.promptVersion ? '<div>Prompt version: ' + esc(v.promptVersion) + '</div>' : '') +
            '<div>Found ' + esc(M.formatWhen(v.createdAt)) + '</div>' +
            (v.references ? '<div><sl-icon name="link-45deg"></sl-icon> ' + esc(v.references.text) + '</div>' : '') +
            '</div></sl-details>';

        var b = v.buttons;
        var foot = '';
        if (b.apply) foot += '<sl-button size="small" variant="primary" data-dream-action="apply" title="Make the change now, after a confirmation"><sl-icon slot="prefix" name="check2"></sl-icon> Apply</sl-button>';
        // The two buttons of the hand-off package: drawn here, wired by it (see "bmbDream.handoff" below).
        if (b.chat) {
            foot += '<span title="' + esc(b.chatDisabled ? 'The built-in chat is not available on this node.' : CHAT_TIP) + '"><sl-button size="small" data-dream-action="chat"' +
                (b.chatDisabled ? ' disabled' : '') + '><sl-icon slot="prefix" name="chat-dots"></sl-icon> Open in AI chat</sl-button></span>';
        }
        if (b.prompt) foot += '<sl-button size="small" data-dream-action="prompt"><sl-icon slot="prefix" name="terminal"></sl-icon> Show prompt</sl-button>';
        if (b.markDone) foot += '<sl-button size="small" data-dream-action="markdone" title="You did it yourself"><sl-icon slot="prefix" name="check-lg"></sl-icon> Mark done</sl-button>';
        if (b.backToNew) foot += '<sl-button size="small" variant="text" data-dream-action="renew" title="Put it back in the list as new"><sl-icon slot="prefix" name="arrow-return-left"></sl-icon> Back to the list</sl-button>';
        if (b.dismiss) foot += '<span class="dream-spacer"></span><sl-button size="small" variant="text" data-dream-action="dismiss"><sl-icon slot="prefix" name="x-lg"></sl-icon> Dismiss</sl-button>';
        if (foot) h += '<div class="dream-card-foot">' + foot + '</div>';

        return h + '</article>';
    }

    // A card drawn again in the place of the one on the page: the focus mark stays, and so does an open "Why" fold.
    function swapCard(node, a) {
        var details = node.querySelector ? node.querySelector('.dream-card-details') : null;
        var wasOpen = !!(details && details.hasAttribute && details.hasAttribute('open'));
        var holder = document.createElement('div');
        holder.innerHTML = cardHtml(a, { focus: !!(node.classList && node.classList.contains('is-focus')) });
        var fresh = holder.firstElementChild;
        if (wasOpen && fresh.querySelector) {
            var again = fresh.querySelector('.dream-card-details');
            if (again) again.setAttribute('open', '');
        }
        node.replaceWith(fresh);
    }

    // The latest state of one advice, put in every place it is shown. An advice that is no longer open leaves the open list.
    function refreshAdvice(id) {
        return getJson('/advice/' + encodeURIComponent(id)).then(function (a) {
            if (!a || !a.id) throw new Error('empty');
            remember(a);
            document.querySelectorAll('[data-advice-id="' + CSS.escape(id) + '"]').forEach(function (node) { swapCard(node, a); });
            afterChange(a.runId);
            return a;
        }, function (err) {
            var known = st.advice[id];
            if (err && err.status === 404) {
                document.querySelectorAll('[data-advice-id="' + CSS.escape(id) + '"]').forEach(function (node) { node.remove(); });
                if (st.focus.id === id) focusNotice('Dream does not know this advice any more: it went with its run.');
            }
            afterChange(known && known.runId);
        });
    }

    // Something about an advice changed: the open list, the counts in the state card, and the split of the advice in the row of its run.
    function afterChange(runId) {
        loadOpen();
        loadStatusQuietly();
        if (!runId) return;
        delete st.history.counts[runId];
        var row = document.querySelector('.dream-run[data-run-id="' + CSS.escape(runId) + '"]');
        if (row) refreshRunCounts(row);
    }

    function loadStatusQuietly() { getJson('/status').then(function (s) { applyStatus(s); }, function () { }); }

    // ---- what the buttons of a card do -----------------------------------------------------------------------------------------------
    // The hand-off: "Open in AI chat" puts the task into the built-in chat, "Show prompt" opens the text for a personal agent with a Copy button. Both buttons are
    // drawn by cardHtml() according to what the API says the advice can (never "Open in AI chat" on a closed card; "Show prompt" also on an applied or dismissed
    // one, for reference: the dialog then says so in #dream-prompt-closed, set here before the hand-off opens it). pages/dream-handoff.js (loaded after this file)
    // replaces these two functions (an advice as the API gave it, and the button that was pressed) and uses bmbDream.api; if that file does not load, the buttons
    // only say that they are not wired. After a 409 the hand-off calls bmbDream.refreshAdvice(id), which redraws the card.
    var handoff = {
        chat: function (advice, button) { toast('primary', 'info-circle', '"Open in AI chat" is not wired yet.'); },
        prompt: function (advice, button) { toast('primary', 'info-circle', '"Show prompt" is not wired yet.'); }
    };

    function setPromptNote(advice) {
        var note = $('dream-prompt-closed');
        var text = cardView(advice).promptNote;
        note.textContent = text;
        show(note, !!text);
    }

    function onCardAction(e) {
        var btn = e.target.closest('[data-dream-action]');
        if (!btn) return;
        var card = btn.closest('[data-advice-id]');
        var action = btn.dataset.dreamAction;
        if (!card) return;
        var id = card.dataset.adviceId;
        var advice = st.advice[id];
        if (!advice) return;
        if (action === 'apply') openApply(advice);
        else if (action === 'dismiss') openDismiss(advice);
        else if (action === 'chat') handoff.chat(advice, btn);
        else if (action === 'prompt') { setPromptNote(advice); handoff.prompt(advice, btn); }
        else if (action === 'markdone') changeStatus(btn, id, 'applied');
        else if (action === 'renew') changeStatus(btn, id, 'new');
    }

    function changeStatus(btn, id, status) {
        bmbBusy.run(btn, function () { return send('POST', '/advice/' + encodeURIComponent(id) + '/status', { status: status }); }, {
            quiet: true,
            error: function (err) {
                bmbBusy.showResult(btn, false, M.errorText(err));
                if (err && err.status === 409) refreshAdvice(id);
            }
        }).then(function (ok) { if (ok && !gone) refreshAdvice(id); });
    }

    // ---- Apply -----------------------------------------------------------------------------------------------------------------------
    function openApply(advice) {
        var v = M.adviceView(advice, {});
        var plan = v.applyPlan;
        if (!plan) return;
        st.applyId = advice.id;
        $('dream-apply-body').innerHTML =
            '<div class="dream-dialog-title">' + esc(v.kind.label) + ': ' + esc(v.title) + '</div>' +
            '<div class="dream-dialog-lines">' + plan.lines.map(function (l) { return '<p>' + esc(l) + '</p>'; }).join('') + '</div>' +
            (plan.notes && plan.notes.length ? '<ul class="dream-dialog-notes">' + plan.notes.map(function (n) {
                return '<li><span>' + esc('\u201C' + n.title + '\u201D') + '</span>' + (n.path ? ' <span class="dream-dialog-path">' + esc(n.path) + '</span>' : '') + '</li>';
            }).join('') + '</ul>' : '') +
            (plan.warnings.length ? '<ul class="dream-dialog-warn">' + plan.warnings.map(function (w) { return '<li>' + esc(w) + '</li>'; }).join('') + '</ul>' : '');
        $('dream-apply-confirm').textContent = plan.confirm;
        var dlg = $('dlg-dream-apply');
        bmbBusy.showResult($('dream-apply-confirm'), true, '');
        dlg.show();
    }

    function onApplyConfirm() {
        var btn = $('dream-apply-confirm');
        var id = st.applyId;
        if (!id) return;
        bmbBusy.showResult(btn, true, '');
        bmbBusy.run(btn, function () { return send('POST', '/advice/' + encodeURIComponent(id) + '/apply'); }, {
            quiet: true,
            error: function (err) {
                bmbBusy.showResult(btn, false, M.errorText(err));
                var code = err && err.body && err.body.code;
                if (M.isClosedConflict(err) || code === 'expired' || code === 'not_applicable' || code === 'not_found') refreshAdvice(id);
            }
        }).then(function (ok) {
            if (!ok || gone) return;
            $('dlg-dream-apply').hide();
            refreshAdvice(id);
        });
    }

    // ---- Dismiss ---------------------------------------------------------------------------------------------------------------------
    function openDismiss(advice) {
        st.dismissId = advice.id;
        var v = M.adviceView(advice, {});
        $('dream-dismiss-title').textContent = v.kind.label + ': ' + v.title;
        var group = $('dream-dismiss-scope');
        group.innerHTML = M.dismissOptions(advice).map(function (o) {
            return '<sl-radio value="' + esc(o.value) + '"' + (o.disabled ? ' disabled' : '') + '>' + esc(o.label) +
                '<span class="dream-radio-hint">' + esc(o.hint) + '</span></sl-radio>';
        }).join('');
        group.value = 'advice';
        $('dream-dismiss-expiry').value = '180';
        $('dream-dismiss-reason').value = '';
        bmbBusy.showResult($('dream-dismiss-confirm'), true, '');
        $('dlg-dream-dismiss').show();
    }

    function onDismissConfirm() {
        var btn = $('dream-dismiss-confirm');
        var id = st.dismissId;
        if (!id) return;
        var body = { scope: $('dream-dismiss-scope').value || 'advice', expiresDays: $('dream-dismiss-expiry').value === 'never' ? null : 180 };
        var reason = ($('dream-dismiss-reason').value || '').trim();
        if (reason) body.reason = reason;
        bmbBusy.showResult(btn, true, '');
        bmbBusy.run(btn, function () { return send('POST', '/advice/' + encodeURIComponent(id) + '/dismiss', body); }, {
            quiet: true,
            error: function (err) {
                bmbBusy.showResult(btn, false, M.errorText(err));
                // closed meanwhile (another tab, the sweep) or gone: the card behind the dialog says so too
                if (err && (err.status === 409 || err.status === 404)) refreshAdvice(id);
            }
        }).then(function (ok) {
            if (!ok || gone) return;
            $('dlg-dream-dismiss').hide();
            refreshAdvice(id);
        });
    }

    // ---- the open advice -------------------------------------------------------------------------------------------------------------
    // quiet: the tab came back; what is on the page stays when the node cannot be reached, and a list that did not change is not drawn again.
    function loadOpen(quiet) {
        var o = st.open;
        var seq = ++o.seq;
        quiet = quiet === true;
        var list = $('dream-open-list');
        if (!list.children.length) { list.innerHTML = loadingBox(); o.sig = null; }
        var filter = { sort: o.sort, kind: o.kind === 'all' ? '' : o.kind, minScore: o.minScore > 0 ? o.minScore : '' };
        var fresh = getJson('/advice', Object.assign({ status: 'new', page: o.page, pageSize: OPEN_SIZE }, filter));
        // What was handed to the chat is still open: it is listed first, on the first page.
        var inChat = getJson('/advice', Object.assign({ status: 'in_chat', page: 1, pageSize: 50 }, filter)).then(null, function () { return { total: 0, items: [] }; });
        return Promise.all([fresh, inChat]).then(function (r) {
            if (seq !== o.seq) return;
            var items = (o.page === 1 ? arr(r[1].items) : []).concat(arr(r[0].items));
            var total = (Number(r[0].total) || 0) + (Number(r[1].total) || 0);
            renderOpen(items, Number(r[0].total) || 0, total);
        }, function (err) {
            if (seq !== o.seq || quiet) return;
            o.sig = null;
            list.innerHTML = errorBox(M.loadFailure('the open advice', err), 'open');
            show($('dream-open-empty'), false);
            show($('dream-open-pager'), false);
        });
    }

    function renderOpen(items, freshTotal, total) {
        var o = st.open;
        var list = $('dream-open-list');
        var empty = $('dream-open-empty');
        var badge = $('dream-open-count');
        badge.textContent = M.formatCount(total);
        show(badge, total > 0);
        // The same advice in the same states: nothing is drawn again (no blink, an open "Why" stays open); the buttons still act on the latest.
        var sig = M.adviceSignature(items) + '\n' + chatAvailable();
        if (sig !== o.sig) { o.sig = sig; list.innerHTML = items.map(function (a) { return cardHtml(a); }).join(''); }
        else items.forEach(remember);
        var filtered = o.kind !== 'all' || o.minScore > 0;
        if (!items.length) {
            empty.textContent = filtered ? 'No open advice matches these filters.' : 'No open advice. Start a run above, or turn the night run on in the Dream settings.';
            show(empty, true);
        } else {
            show(empty, false);
        }
        var info = M.pageInfo(freshTotal, o.page, OPEN_SIZE, 'new advice');
        if (o.page > info.pages) { o.page = info.pages; loadOpen(); return; }
        $('dream-open-pager-info').textContent = info.text;
        $('dream-open-prev').disabled = !info.hasPrev;
        $('dream-open-next').disabled = !info.hasNext;
        show($('dream-open-pager'), info.pages > 1);
    }

    // ---- the history -----------------------------------------------------------------------------------------------------------------
    // quiet: the tab came back (see loadOpen); the split of the advice of every row is read again, since advice may have been applied elsewhere.
    function loadHistory(quiet) {
        var h = st.history;
        var seq = ++h.seq;
        quiet = quiet === true;
        var host = $('dream-history');
        if (!host.children.length) { host.innerHTML = loadingBox(); h.sig = null; }
        return getJson('/runs', { page: h.page, pageSize: HISTORY_SIZE, mode: h.mode }).then(function (r) {
            if (seq !== h.seq) return;
            if (quiet) h.counts = {};
            renderHistory(arr(r.items), Number(r.total) || 0);
        }, function (err) {
            if (seq !== h.seq || quiet) return;
            h.sig = null;
            host.innerHTML = errorBox(M.loadFailure('the history', err), 'history');
            show($('dream-history-empty'), false);
            show($('dream-history-pager'), false);
        });
    }

    function renderHistory(runs, total) {
        var h = st.history;
        var host = $('dream-history');
        var empty = $('dream-history-empty');
        var now = Date.now();
        runs.forEach(rememberRun);
        // The same runs in the same states: the rows are not drawn again, so a row that is open stays open with its advice and its log.
        // Otherwise they are, and a row that was open opens again (a run whose record changed is read again).
        var sig = M.runSignature(runs) + '\n' + h.page + '|' + h.mode;
        var redrawn = sig !== h.sig;
        if (redrawn) {
            h.sig = sig;
            var runSigs = {};
            runs.forEach(function (run) {
                var one = M.runSignature([run]);
                if (h.runSigs[run.id] !== undefined && h.runSigs[run.id] !== one) delete h.counts[run.id];
                runSigs[run.id] = one;
            });
            h.runSigs = runSigs;
            host.innerHTML = runs.map(function (run) { return runHtml(run, now); }).join('');
            runs.forEach(function (run) {
                if (!h.openIds[run.id]) return;
                var row = document.querySelector('.dream-run[data-run-id="' + CSS.escape(run.id) + '"]');
                if (row) loadRunBody(row);
            });
        }
        if (!runs.length) {
            empty.textContent = h.mode === 'trial' ? 'No trial runs yet.' : 'No runs yet. Press Start above to make the first one.';
            show(empty, true);
        } else {
            show(empty, false);
        }
        var info = M.pageInfo(total, h.page, HISTORY_SIZE, 'run');
        if (h.page > info.pages) { h.page = info.pages; loadHistory(); return; }
        $('dream-history-pager-info').textContent = info.text;
        $('dream-history-prev').disabled = !info.hasPrev;
        $('dream-history-next').disabled = !info.hasNext;
        show($('dream-history-pager'), info.pages > 1);
        // The split of the advice of each run (open, applied, dismissed) is in the run's own record: asked for the runs that made any.
        runs.forEach(function (run) {
            if (M.isActive(run) || !(run.counters && run.counters.adviceCreated > 0)) return;
            runDetail(run.id).then(function (d) { fillStats(run, d); }, function () { });
        });
        // A row that was kept (the same run, nothing to draw again: the tab came back, or Refresh was pressed) still shows the advice as it was when the row
        // was opened: the advice of every open row is read again.
        if (!redrawn) refreshOpenRows();
    }

    // The advice of each row that is open, read again, card by card: a card that shows the advice as it is stays untouched (no blink, no scroll, an open
    // "Why" fold stays open), a card of advice that was applied, dismissed or marked done elsewhere is drawn again and loses Apply and Open in AI chat.
    function refreshOpenRows() {
        Object.keys(st.history.openIds).forEach(function (runId) {
            var row = document.querySelector('.dream-run[data-run-id="' + CSS.escape(runId) + '"]');
            var body = row && row.querySelector('.dream-run-body');
            if (body && body.dataset.loaded === '1') refreshRunAdvice(body, runId);
        });
    }

    function refreshRunAdvice(body, runId) {
        var list = body.querySelector('[data-run-advice]');
        if (!list) return;
        var shown = list.querySelectorAll('[data-advice-id]').length;
        if (!shown) return;      // "This run left no advice", or the first read has not come yet: nothing on screen to bring up to date
        // The cards on screen are the first ones of the run, as many as "Show more advice" brought: one read covers them (the node gives at most MAX_FEED).
        getJson('/runs/' + encodeURIComponent(runId) + '/advice', { afterSeq: 0, limit: Math.min(MAX_FEED, Math.max(FEED_BATCH, shown)) }).then(function (data) {
            arr(data.items).forEach(function (a) {
                if (!a || !a.id) return;
                var node = list.querySelector('[data-advice-id="' + CSS.escape(a.id) + '"]');
                if (!node) return;   // not on screen (past "Show more advice")
                if (node.getAttribute('data-sig') === cardSig(a)) remember(a);
                else swapCard(node, a);
            });
        }, function () { /* the node cannot be reached: what is on the page stays */ });
    }

    function runDetail(id) {
        var counts = st.history.counts;
        if (counts[id]) return Promise.resolve(counts[id]);
        return getJson('/runs/' + encodeURIComponent(id)).then(function (d) { counts[id] = d; return d; });
    }

    function statsHtml(run, detail, now) {
        return M.runStats(run, detail ? detail.adviceCounts : null, now).map(function (s) {
            return '<div class="dream-stat" data-stat="' + esc(s.key) + '"><div class="dream-stat-value">' + esc(s.value) + '</div><div class="dream-stat-label">' + esc(s.label) +
                '</div><div class="dream-stat-sub">' + esc(s.sub) + '</div></div>';
        }).join('');
    }

    function fillStats(run, detail) {
        var row = document.querySelector('.dream-run[data-run-id="' + CSS.escape(run.id) + '"] .dream-run-stats');
        var html = statsHtml(run, detail, Date.now());
        if (row && row.innerHTML !== html) row.innerHTML = html;
    }

    function runHtml(run, now) {
        var t = M.triggerInfo(run);
        var o = M.outcomeInfo(run);
        var what = M.runWhat(run);
        var h = '<sl-details class="dream-run" data-run-id="' + esc(run.id) + '"' + (st.history.openIds[run.id] ? ' open' : '') + '><div slot="summary" class="dream-run-summary">' +
            '<div class="dream-run-when"><span class="dream-run-date">' + esc(M.formatWhen(run.startedAt || run.createdAt)) + '</span>' +
            '<span class="dream-run-badges"><sl-badge variant="' + esc(t.variant) + '" title="' + esc(t.text) + '"><sl-icon name="' + esc(t.icon) + '"></sl-icon>&nbsp;' + esc(t.label) + '</sl-badge>' +
            '<sl-badge variant="neutral" class="dream-run-what" data-what="' + esc(what.key) + '" title="' + esc(what.text) + '">' + esc(what.label) + '</sl-badge>' +
            '<sl-badge variant="' + esc(o.variant) + '"' + (o.text ? ' title="' + esc(o.text) + '"' : '') + '>' + esc(o.label) + '</sl-badge></span></div>' +
            '<div class="dream-run-stats">' + statsHtml(run, st.history.counts[run.id], now) + '</div></div>' +
            '<div class="dream-run-body" data-loaded="">' + loadingBox() + '</div></sl-details>';
        return h;
    }

    // The part of a row that opens: limits, the problem if there was one, the advice of the run (asked for now), and the log.
    function loadRunBody(details) {
        var body = details.querySelector('.dream-run-body');
        if (!body || body.dataset.loaded === '1') return;
        var id = details.dataset.runId;
        body.innerHTML = loadingBox();
        runDetail(id).then(function (d) {
            body.dataset.loaded = '1';
            body.innerHTML = runBodyHtml(d);
            loadRunAdvice(body, id, 0);
        }, function (err) {
            body.innerHTML = errorBox(M.loadFailure('this run', err), '');
        });
    }

    function runBodyHtml(d) {
        var facts = [];
        facts.push(M.limitsList(d.limits).join(' \u00B7 '));
        var counters = M.counterLine(d.counters);
        if (counters) facts.push(counters);
        if (d.counters && d.counters.adviceSuppressed > 0) facts.push(d.counters.adviceSuppressed + ' left out because you dismissed them before');
        facts.push(M.runWhat(d).text);
        if (d.startedBy && d.startedBy.name) facts.push('Started by ' + d.startedBy.name);
        var h = '<div class="dream-run-body-in">';
        h += '<div class="dream-run-facts">' + facts.map(function (f) { return '<span>' + esc(f) + '</span>'; }).join('') + '</div>';
        var o = M.outcomeInfo(d);
        if (o.text) h += '<div class="dream-run-problem">' + esc(o.text) + '</div>';
        if (d.errorText || (d.errorCode && d.outcome !== 'skipped')) {
            h += '<div class="dream-run-problem"><sl-icon name="exclamation-triangle"></sl-icon> ' + esc(d.errorText || d.errorCode) + '</div>';
        }
        h += '<div><h4>Advice of this run</h4><div class="dream-cards" data-run-advice></div><div data-run-advice-more></div></div>';
        h += '<div><sl-button size="small" data-dream-action="log" data-log-available="' + (d.logAvailable ? '1' : '0') + '"><sl-icon slot="prefix" name="journal-text"></sl-icon> Show log</sl-button>' +
            '<div data-run-log style="margin-top:10px;"></div></div>';
        return h + '</div>';
    }

    function loadRunAdvice(body, runId, afterSeq) {
        var list = body.querySelector('[data-run-advice]');
        var more = body.querySelector('[data-run-advice-more]');
        if (!list) return;
        if (afterSeq === 0) list.innerHTML = loadingBox();
        getJson('/runs/' + encodeURIComponent(runId) + '/advice', { afterSeq: afterSeq, limit: FEED_BATCH }).then(function (data) {
            var items = arr(data.items);
            if (afterSeq === 0) list.innerHTML = '';
            if (!items.length && afterSeq === 0) { list.innerHTML = '<p class="dream-empty">This run left no advice.</p>'; more.innerHTML = ''; return; }
            list.insertAdjacentHTML('beforeend', items.map(function (a) { return cardHtml(a); }).join(''));
            more.innerHTML = items.length >= FEED_BATCH ? '<sl-button size="small" data-more-advice="' + esc(runId) + '" data-after="' + esc(M.lastSeqOf(items, afterSeq)) + '">Show more advice</sl-button>' : '';
        }, function (err) {
            list.innerHTML = errorBox(M.loadFailure('the advice of this run', err), '');
        });
    }

    function loadRunLog(btn, afterSeq) {
        var holder = btn.closest('.dream-run-body-in').querySelector('[data-run-log]');
        if (btn.dataset.logAvailable !== '1') {
            holder.innerHTML = '<p class="dream-meta">The log of a run is kept for seven days; this one is gone.</p>';
            return;
        }
        btn.loading = true;
        getJson('/runs/' + encodeURIComponent(btn.closest('.dream-run').dataset.runId) + '/events', { afterSeq: afterSeq, limit: LOG_BATCH }).then(function (data) {
            btn.loading = false;
            if (data.logAvailable === false) { holder.innerHTML = '<p class="dream-meta">The log of a run is kept for seven days; this one is gone.</p>'; return; }
            var items = arr(data.items);
            var pre = holder.querySelector('.dream-log');
            if (!pre) { holder.innerHTML = '<div class="dream-log"></div><div data-log-more style="margin-top:8px;"></div>'; pre = holder.querySelector('.dream-log'); }
            if (!items.length && afterSeq === 0) { holder.innerHTML = '<p class="dream-meta">The log is empty.</p>'; return; }
            pre.insertAdjacentHTML('beforeend', items.map(function (e) {
                var when = new Date(e.at);
                var time = isFinite(when.getTime()) ? when.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' }) : '';
                return '<div><span class="t">' + esc(time) + '</span><span class="k' + (e.kind === 'error' ? ' is-error' : '') + '">' + esc(e.kind) + '</span><span class="x">' + esc(e.text) + '</span></div>';
            }).join(''));
            var more = holder.querySelector('[data-log-more]');
            more.innerHTML = items.length >= LOG_BATCH ? '<sl-button size="small" data-log-next="' + esc(M.lastSeqOf(items, afterSeq)) + '">Show more of the log</sl-button>' : '';
            btn.hidden = true;
        }, function (err) {
            btn.loading = false;
            holder.innerHTML = errorBox(M.loadFailure('the log', err), '');
        });
    }

    function refreshRunCounts(details) {
        var id = details.dataset.runId;
        runDetail(id).then(function (d) {
            var run = { id: id, counters: d.counters, useModel: d.useModel, modelLabel: d.modelLabel, startedAt: d.startedAt, createdAt: d.createdAt, finishedAt: d.finishedAt };
            fillStats(run, d);
        }, function () { });
    }

    // ---- View all rules -------------------------------------------------------------------------------------------------------------
    // Every built-in rule and every step of the AI model, as the node describes them now (GET /rulebook: the numbers are the ones it uses). Read each time the
    // dialog opens; what was shown stays until the new answer is there.
    function openRules() {
        $('dlg-dream-rules').show();
        loadRules();
    }

    function loadRules() {
        var body = $('dream-rules-body');
        var seq = ++st.rules.seq;
        if (!st.rules.loaded) body.innerHTML = loadingBox();
        getJson('/rulebook').then(function (book) {
            if (seq !== st.rules.seq) return;
            st.rules.loaded = true;
            body.innerHTML = ruleBookHtml(M.ruleBookView(book));
        }, function (err) {
            if (seq !== st.rules.seq || st.rules.loaded) return;
            body.innerHTML = errorBox(M.loadFailure('the rules', err), 'rules');
        });
    }

    function paragraphs(lines) { return lines.map(function (l) { return '<p>' + esc(l) + '</p>'; }).join(''); }
    function bullets(lines) { return lines.length ? '<ul>' + lines.map(function (l) { return '<li>' + esc(l) + '</li>'; }).join('') + '</ul>' : ''; }
    function factTable(facts, caption) {
        if (!facts.length) return '';
        return '<table class="dream-rules-facts">' + (caption ? '<caption>' + esc(caption) + '</caption>' : '') + '<tbody>' + facts.map(function (f) {
            return '<tr><th scope="row">' + esc(f.name) + '</th><td>' + esc(f.value) + '</td></tr>';
        }).join('') + '</tbody></table>';
    }
    function part(label, html) { return html ? '<div class="dream-rules-part"><h5>' + esc(label) + '</h5>' + html + '</div>' : ''; }
    function text(s) { return s ? '<p>' + esc(s) + '</p>' : ''; }
    function kindChips(kinds) {
        if (!kinds.length) return '';
        return '<ul class="dream-rules-kinds">' + kinds.map(function (k) {
            return '<li><sl-icon name="' + esc(k.icon) + '"></sl-icon><span>' + esc(k.label) + '</span>' + (k.modes ? '<small>' + esc(k.modes) + '</small>' : '') + '</li>';
        }).join('') + '</ul>';
    }

    function ruleBookHtml(book) {
        if (book.empty) return '<p class="dream-empty">The node sent no rules.</p>';
        var h = '<div class="dream-rules">';
        h += paragraphs(book.intro);
        h += '<nav class="dream-rules-toc" aria-label="Rules and AI steps"><span>Built-in rules:</span>' + book.rules.map(function (r) {
            return '<a href="#dream-rule-' + esc(r.id) + '" data-rules-jump="dream-rule-' + esc(r.id) + '">' + esc(r.name) + '</a>';
        }).join('') + '<span>AI model:</span>' + book.tasks.map(function (t) {
            return '<a href="#dream-task-' + esc(t.id) + '" data-rules-jump="dream-task-' + esc(t.id) + '">' + esc(t.name) + '</a>';
        }).join('') + '<a href="#dream-rules-limits" data-rules-jump="dream-rules-limits">Limits and scores</a></nav>';
        if (book.similarity.length) h += '<div class="dream-rules-note"><h4>What "similar" means</h4>' + paragraphs(book.similarity) + '</div>';

        h += '<h3>The built-in rules <small>(here on this node, nothing leaves it)</small></h3>';
        h += book.rules.map(function (r) {
            if (!r.described) return '<section class="dream-rule" id="dream-rule-' + esc(r.id) + '"><h4>' + esc(r.name) + '</h4><p class="dream-meta">This rule has no description yet.</p>' + kindChips(r.kinds) + '</section>';
            return '<section class="dream-rule" id="dream-rule-' + esc(r.id) + '"><h4>' + esc(r.name) + '</h4>' + kindChips(r.kinds) +
                (r.runsWhen ? '<p class="dream-meta">' + esc(r.runsWhen) + '</p>' : '') +
                part('What it finds', text(r.finds) + (r.example ? '<p class="dream-rules-example"><strong>Example:</strong> ' + esc(r.example) + '</p>' : '')) +
                part('How it decides', bullets(r.how) + factTable(r.thresholds, 'The numbers')) +
                part('The score', text(r.score)) +
                part('What Apply does', text(r.apply)) +
                part('What it never does', text(r.never)) +
                part('What it skips', bullets(r.skips)) + '</section>';
        }).join('');

        h += '<h3>The AI model <small>(only when a run uses it: the notes it looks at go to the model\'s provider)</small></h3>';
        h += book.tasks.map(function (t) {
            var title = esc(t.name) + (t.promptVersion ? ' <small>prompt ' + esc(t.promptVersion) + '</small>' : '');
            if (!t.described) return '<section class="dream-rule" id="dream-task-' + esc(t.id) + '"><h4>' + title + '</h4><p class="dream-meta">This step has no description yet.</p>' + kindChips(t.kinds) + '</section>';
            return '<section class="dream-rule" id="dream-task-' + esc(t.id) + '"><h4>' + title + '</h4>' + kindChips(t.kinds) +
                part('What it looks at, and how many', bullets(t.candidates)) +
                part('What the model is asked', text(t.asked)) +
                part('What it may answer', text(t.answers)) +
                part('What the node checks before it keeps an answer', bullets(t.checks)) +
                part('Limits', factTable(t.limits, '')) + '</section>';
        }).join('');

        h += '<section class="dream-rule" id="dream-rules-limits"><h4>Limits of a run and the score</h4>' + factTable(book.limits, '');
        if (book.bands.length) {
            h += '<table class="dream-rules-facts dream-rules-bands"><caption>Score bands</caption><tbody>' + book.bands.map(function (b) {
                var range = b.from == null ? '' : b.to == null ? b.from + '+' : b.from + '-' + b.to;
                return '<tr><th scope="row"><span class="dream-band-chip band-' + esc(b.band) + '">' + esc(b.label) + '</span> ' + esc(range) + '</th><td>' + esc(b.meaning) + '</td></tr>';
            }).join('') + '</tbody></table>';
        }
        h += '</section>';
        if (book.skips.length) h += '<div class="dream-rules-note"><h4>What no rule and no AI step reads</h4>' + bullets(book.skips) + '</div>';
        return h + '</div>';
    }

    function onRulesClick(e) {
        var retry = e.target.closest('[data-dream-retry]');
        if (retry) { loadRules(); return; }
        var jump = e.target.closest('[data-rules-jump]');
        if (!jump) return;
        if (e.preventDefault) e.preventDefault();   // no #hash in the address: the dialog scrolls inside
        var target = $('dream-rules-body').querySelector('#' + CSS.escape(jump.dataset.rulesJump));
        if (target && typeof target.scrollIntoView === 'function') target.scrollIntoView({ block: 'start', behavior: 'smooth' });
    }

    // ---- one advice on its own: /Dream?advice=<id> -----------------------------------------------------------------------------------
    // The chat links back here. The advice is shown at the top whatever its status, marked, and scrolled to; "Show all advice" puts the page back as it is
    // without the link. An id the node does not know gives a short notice.
    function adviceIdAsked() {
        var search = window.location && typeof window.location.search === 'string' ? window.location.search : '';
        return { asked: /(?:^|[?&])advice=/.test(search), id: M.adviceIdFromQuery(search) };
    }

    function focusNotice(text) {
        $('dream-focus-body').innerHTML = '<p class="dream-focus-notice"><sl-icon name="info-circle"></sl-icon> ' + esc(text) + '</p>';
        show($('dream-focus'), true);
    }

    function loadFocus(quiet) {
        var id = st.focus.id;
        if (!id) return;
        var body = $('dream-focus-body');
        if (!quiet) body.innerHTML = loadingBox();
        getJson('/advice/' + encodeURIComponent(id)).then(function (a) {
            if (st.focus.id !== id) return;
            if (!a || !a.id) throw new Error('The node sent nothing for this advice.');
            var sig = M.adviceSignature([a]);
            if (quiet && sig === st.focus.sig) { remember(a); return; }
            st.focus.sig = sig;
            body.innerHTML = cardHtml(a, { focus: true });
            if (!quiet) {
                var section = $('dream-focus');
                if (typeof section.scrollIntoView === 'function') section.scrollIntoView({ block: 'start', behavior: 'smooth' });
            }
        }, function (err) {
            if (st.focus.id !== id || quiet) return;
            focusNotice(err && err.status === 404
                ? 'Dream does not know this advice. It may have gone with its run; the open advice is below.'
                : M.loadFailure('this advice', err));
        });
    }

    function startFocus() {
        var q = adviceIdAsked();
        if (!q.asked) return;
        if (!q.id) { focusNotice('The link does not name an advice Dream knows. The open advice is below.'); return; }
        st.focus.id = q.id;
        show($('dream-focus'), true);
        loadFocus(false);
    }

    function showAllAdvice() {
        st.focus.id = null;
        st.focus.sig = null;
        $('dream-focus-body').innerHTML = '';
        show($('dream-focus'), false);
        try {
            if (window.history && typeof window.history.replaceState === 'function' && window.location) window.history.replaceState(window.history.state, '', window.location.pathname);
        } catch (e) { /* the address keeps ?advice=; the page is right anyway */ }
        var open = $('dream-open');
        if (typeof open.scrollIntoView === 'function') open.scrollIntoView({ block: 'start', behavior: 'smooth' });
    }

    // ---- events ----------------------------------------------------------------------------------------------------------------------
    function onClick(e) {
        var rules = e.target.closest('[data-dream-open-rules]');
        if (rules) { if (e.preventDefault) e.preventDefault(); openRules(); return; }
        var retry = e.target.closest('[data-dream-retry]');
        if (retry) {
            var what = retry.dataset.dreamRetry;
            if (what === 'status') loadStatus(); else if (what === 'open') loadOpen(); else if (what === 'history') loadHistory();
            return;
        }
        var more = e.target.closest('[data-more-advice]');
        if (more) {
            var holder = more.closest('.dream-run-body');
            var after = Number(more.dataset.after) || 0;
            more.disabled = true;
            loadRunAdvice(holder, more.dataset.moreAdvice, after);
            return;
        }
        var logNext = e.target.closest('[data-log-next]');
        if (logNext) {
            var logBtn = logNext.closest('.dream-run-body-in').querySelector('[data-dream-action="log"]');
            logBtn.hidden = false;
            loadRunLog(logBtn, Number(logNext.dataset.logNext) || 0);
            return;
        }
        var action = e.target.closest('[data-dream-action="log"]');
        if (action) { loadRunLog(action, 0); return; }
        onCardAction(e);
    }

    function bind() {
        $('dream-start').addEventListener('click', onStart);
        $('dream-stop').addEventListener('click', onStop);
        $('dream-trial').addEventListener('sl-change', renderStartHelp);
        $('dream-use-rules').addEventListener('sl-change', renderStartHelp);
        $('dream-use-model').addEventListener('sl-change', renderStartHelp);
        $('dream-rules-open').addEventListener('click', openRules);
        $('dlg-dream-rules').addEventListener('click', onRulesClick);
        $('dream-focus-all').addEventListener('click', showAllAdvice);
        $('dream-apply-confirm').addEventListener('click', onApplyConfirm);
        $('dream-dismiss-confirm').addEventListener('click', onDismissConfirm);
        document.querySelector('.dream-scope').addEventListener('click', onClick);

        // the open advice
        var kind = $('dream-f-kind');
        kind.innerHTML = '<sl-option value="all">Any kind</sl-option>' + M.kindOptions().map(function (k) { return '<sl-option value="' + esc(k.value) + '">' + esc(k.label) + '</sl-option>'; }).join('');
        kind.addEventListener('sl-change', function () { st.open.kind = kind.value || 'all'; st.open.page = 1; loadOpen(); });
        $('dream-f-score').addEventListener('sl-change', function (e) { st.open.minScore = Number(e.target.value) || 0; st.open.page = 1; loadOpen(); });
        $('dream-f-sort').addEventListener('sl-change', function (e) { st.open.sort = e.target.value === 'date' ? 'date' : 'score'; st.open.page = 1; loadOpen(); });
        $('dream-open-refresh').addEventListener('click', loadOpen);
        $('dream-open-prev').addEventListener('click', function () { if (st.open.page > 1) { st.open.page--; loadOpen(); } });
        $('dream-open-next').addEventListener('click', function () { st.open.page++; loadOpen(); });

        // the history
        $('dream-h-mode').addEventListener('sl-change', function (e) { st.history.mode = e.target.value || 'all'; st.history.page = 1; loadHistory(); });
        $('dream-history-refresh').addEventListener('click', function () { st.history.counts = {}; loadHistory(); });
        $('dream-history-prev').addEventListener('click', function () { if (st.history.page > 1) { st.history.page--; loadHistory(); } });
        $('dream-history-next').addEventListener('click', function () { st.history.page++; loadHistory(); });
        // A row opens: its advice and its log are asked for then (sl-show also bubbles up from the details inside a card: only the row itself counts).
        // Which rows are open is kept, so a row drawn again (the tab came back, a run changed) opens again.
        function isRow(e) { return e.target && e.target.classList && e.target.classList.contains('dream-run'); }
        $('dream-history').addEventListener('sl-show', function (e) {
            if (!isRow(e)) return;
            if (e.target.dataset && e.target.dataset.runId) st.history.openIds[e.target.dataset.runId] = true;
            loadRunBody(e.target);
        });
        $('dream-history').addEventListener('sl-hide', function (e) {
            if (isRow(e) && e.target.dataset && e.target.dataset.runId) delete st.history.openIds[e.target.dataset.runId];
        });
    }

    function idleTick() {
        clearTimeout(timers.idle);
        timers.idle = setTimeout(function () {
            if (gone) return;
            if (!document.hidden && !activeRun()) loadStatusQuietly();
            idleTick();
        }, IDLE_MS);
    }

    // The tab comes back: what may have changed in another tab (an advice applied, dismissed or marked done there, a run that finished) is read again, in place:
    // a list that did not change is not drawn again, an open dialog stays open, and this happens at most once in VISIBLE_GAP_MS.
    function onVisible() {
        if (document.hidden || gone) return;
        var now = Date.now();
        if (st.visibleAt && now - st.visibleAt < VISIBLE_GAP_MS) return;
        st.visibleAt = now;
        loadStatusQuietly();
        loadOpen(true);
        loadHistory(true);
        loadFocus(true);
    }

    // The page is left by the single-page navigation: every timer is cancelled, the listener of the document is removed, and what is still on its way is ignored.

    function cleanup() {
        gone = true;
        clearTimeout(timers.idle);
        clearTimeout(timers.reveal);
        clearInterval(timers.elapsed);
        clearTimeout(st.feed.timer);
        st.feed.queue = [];
        st.feed.revealing = false;
        document.removeEventListener('visibilitychange', onVisible);
    }

    function init() {
        $('dream-use-rules').checked = true;   // the rules are on by default (the markup says so too)
        bind();
        timers.elapsed = setInterval(tickElapsed, 1000);
        document.addEventListener('bmb:page-cleanup', cleanup, { once: true });
        document.addEventListener('visibilitychange', onVisible);
        loadStatus().then(function () { loadLastRun(); });
        loadSettings();
        startFocus();
        loadOpen();
        loadHistory();
        idleTick();
    }

    // What a later package may use: the two hand-off functions to replace, the latest advice by id, and the page's own calls.
    window.bmbDream = {
        handoff: handoff,
        advice: function (id) { return st.advice[id] || null; },
        refreshAdvice: refreshAdvice,
        api: { get: getJson, send: send },
        status: function () { return st.status; }
    };

    init();
})();
