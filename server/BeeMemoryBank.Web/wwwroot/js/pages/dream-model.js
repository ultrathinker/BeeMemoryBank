// ===== Dream page: the model (what the words and the numbers say) =====
// The page of Dream (dream.js) draws; this file decides what to say. It touches no page and no network, so DreamPageModelTests can run it on its own
// (JsHarness/dream-model-harness.js): the words of a kind, an outcome and a phase, the numbers of a run, what the status card says, what a card of advice
// shows and which buttons it has, what the Apply dialog promises, how the live feed takes new advice, and how the start form becomes a request.
// Every function tolerates a missing field: the answers of the API are read as they come (a stub, an older node, an error), and the page must never be blank.
(function (root) {
    'use strict';

    // ---- small helpers ---------------------------------------------------------------------------------------------------------------
    function isNum(n) { return typeof n === 'number' && isFinite(n); }
    function str(s) { return typeof s === 'string' ? s : (s == null ? '' : String(s)); }
    function arr(a) { return Array.isArray(a) ? a : []; }
    function humanize(wire) {
        var s = str(wire).replace(/[_-]+/g, ' ').trim();
        return s ? s.charAt(0).toUpperCase() + s.slice(1) : '';
    }
    function plural(n, one, many) { return n + ' ' + (n === 1 ? one : (many || one + 's')); }

    // ---- kinds, bands, statuses ------------------------------------------------------------------------------------------------------
    var KINDS = {
        duplicate_exact: { label: 'Exact duplicate', icon: 'files', noun: 'note' },
        near_duplicate: { label: 'Near duplicate', icon: 'files', noun: 'note' },
        duplicate: { label: 'Duplicate', icon: 'copy', noun: 'note' },
        superseded: { label: 'Superseded note', icon: 'clock-history', noun: 'note' },
        overlap: { label: 'Overlapping notes', icon: 'intersect', noun: 'note' },
        contradiction: { label: 'Contradiction', icon: 'exclamation-diamond', noun: 'note' },
        move_article: { label: 'Note in the wrong folder', icon: 'folder-symlink', noun: 'note' },
        split_folder: { label: 'Split a folder', icon: 'diagram-2', noun: 'folder' },
        rename_folder: { label: 'Rename a folder', icon: 'pencil', noun: 'folder' },
        merge_folders: { label: 'Merge folders', icon: 'union', noun: 'folder' },
        structure_note: { label: 'Structure note', icon: 'diagram-3', noun: 'folder' },
        merge_tags: { label: 'Tag variants', icon: 'tags', noun: 'tag' },
        delete_unused_tags: { label: 'Unused tags', icon: 'tag', noun: 'tag' },
        delete_empty_folder: { label: 'Empty folder', icon: 'folder-x', noun: 'folder' },
        test_leftovers: { label: 'Test leftovers', icon: 'bug', noun: 'folder' }
    };
    var KIND_ORDER = ['duplicate_exact', 'near_duplicate', 'duplicate', 'superseded', 'overlap', 'contradiction', 'move_article', 'split_folder',
        'rename_folder', 'merge_folders', 'structure_note', 'merge_tags', 'delete_unused_tags', 'delete_empty_folder', 'test_leftovers'];

    function kindInfo(kind) {
        var k = KINDS[kind];
        return k ? { value: kind, label: k.label, icon: k.icon, noun: k.noun } : { value: str(kind), label: humanize(kind) || 'Advice', icon: 'lightbulb', noun: 'item' };
    }
    function kindOptions() { return KIND_ORDER.map(kindInfo); }

    function bandOf(score) {
        var n = Number(score);
        if (!isFinite(n)) return 'low';
        return n >= 80 ? 'high' : n >= 60 ? 'medium' : 'low';
    }
    var BAND_LABELS = { high: 'High', medium: 'Medium', low: 'Low' };
    function bandLabel(band) { return BAND_LABELS[band] || BAND_LABELS.low; }

    var STATUSES = {
        'new': { label: 'New', variant: 'neutral', open: true },
        applying: { label: 'Applying...', variant: 'warning', open: true },
        in_chat: { label: 'In chat', variant: 'primary', open: true },
        applied: { label: 'Applied', variant: 'success', open: false },
        dismissed: { label: 'Dismissed', variant: 'neutral', open: false },
        expired: { label: 'Expired', variant: 'neutral', open: false }
    };
    function statusInfo(status) {
        var s = STATUSES[status];
        return s ? { value: status, label: s.label, variant: s.variant, open: s.open } : { value: str(status), label: humanize(status), variant: 'neutral', open: false };
    }

    // ---- runs ------------------------------------------------------------------------------------------------------------------------
    var SKIP_REASONS = {
        locked: 'the vault was locked',
        busy: 'another run or a heavy operation was going on',
        open_list_full: 'there was already too much open advice',
        no_embeddings: 'the notes had no vectors yet'
    };
    var OUTCOMES = {
        completed: { label: 'Completed', variant: 'success', text: 'Every candidate was looked at.' },
        advice_limit: { label: 'Advice limit', variant: 'success', text: 'It stopped because the limit of advice for a run was reached.' },
        time_limit: { label: 'Out of time', variant: 'warning', text: 'It stopped because the time limit (or the night window) ran out.' },
        budget_limit: { label: 'Budget used', variant: 'warning', text: 'It stopped because the limit of model calls or tokens was reached.' },
        stopped: { label: 'Stopped', variant: 'neutral', text: 'It was stopped by a person.' },
        locked: { label: 'Vault locked', variant: 'warning', text: 'It stopped because the vault was locked.' },
        heavy_operation: { label: 'Heavy operation', variant: 'warning', text: 'It stopped because a heavy operation (a key change, a restore, a compaction) began.' },
        provider_error: { label: 'Provider error', variant: 'danger', text: 'The model provider kept answering with errors, or every key was used up.' },
        interrupted: { label: 'Interrupted', variant: 'warning', text: 'The node was restarted while it worked.' },
        failed: { label: 'Failed', variant: 'danger', text: 'It stopped on an unexpected error.' },
        skipped: { label: 'Skipped', variant: 'neutral', text: 'A night run that did not start.' }
    };
    function outcomeInfo(run) {
        run = run || {};
        if (run.state && run.state !== 'finished') {
            var live = { queued: ['Queued', 'neutral'], running: ['Running', 'primary'], stopping: ['Stopping', 'warning'] }[run.state];
            return { label: live ? live[0] : humanize(run.state), variant: live ? live[1] : 'neutral', text: '' };
        }
        var o = OUTCOMES[run.outcome];
        if (!o) return { label: run.outcome ? humanize(run.outcome) : 'Finished', variant: 'neutral', text: '' };
        var text = o.text;
        if (run.outcome === 'skipped' && SKIP_REASONS[run.errorCode]) text = 'The night run did not start: ' + SKIP_REASONS[run.errorCode] + '.';
        return { label: o.label, variant: o.variant, text: text };
    }

    function triggerInfo(run) {
        run = run || {};
        if (run.mode === 'trial') return { label: 'Trial', icon: 'speedometer2', variant: 'warning', text: 'A trial run: its advice stays out of the open list.' };
        if (run.trigger === 'schedule') return { label: 'Night', icon: 'moon-stars', variant: 'neutral', text: 'Started by the schedule.' };
        return { label: 'Manual', icon: 'hand-index-thumb', variant: 'primary', text: 'Started by a person.' };
    }

    var PHASES = {
        embeddings: 'Preparing note vectors',
        detectors: 'Checking tags, folders and exact duplicates',
        pairs: 'Comparing pairs of notes',
        folders: 'Reviewing folders',
        tree: 'Reviewing the folder tree'
    };
    function phaseLabel(phase) { return PHASES[phase] || (phase ? humanize(phase) : ''); }

    // ---- numbers and times -----------------------------------------------------------------------------------------------------------
    function formatCount(n) {
        if (!isNum(n)) return '-';
        return String(Math.round(n)).replace(/\B(?=(\d{3})+(?!\d))/g, ',');
    }
    function formatTokens(n) {
        if (!isNum(n)) return '-';
        if (n < 1000) return String(Math.round(n));
        if (n < 1000000) return trim1(n / 1000) + 'k';
        return trim1(n / 1000000) + 'M';
    }
    function trim1(x) { return (Math.round(x * 10) / 10).toFixed(1).replace(/\.0$/, ''); }

    function formatDuration(ms) {
        if (!isNum(ms) || ms < 0) return '-';
        var s = Math.round(ms / 1000);
        if (s < 1) return '< 1 s';
        if (s < 60) return s + ' s';
        var m = Math.floor(s / 60);
        if (m < 60) return m + ' min' + (s % 60 ? ' ' + (s % 60) + ' s' : '');
        var h = Math.floor(m / 60);
        return h + ' h' + (m % 60 ? ' ' + (m % 60) + ' min' : '');
    }
    function runDurationMs(run, nowMs) {
        run = run || {};
        var start = Date.parse(run.startedAt || run.createdAt || '');
        if (!isFinite(start)) return null;
        var end = run.finishedAt ? Date.parse(run.finishedAt) : nowMs;
        if (!isFinite(end)) return null;
        return Math.max(0, end - start);
    }
    function formatWhen(iso) {
        var t = Date.parse(iso || '');
        if (!isFinite(t)) return '-';
        return new Date(t).toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
    }
    function relativeWhen(iso, nowMs) {
        var t = Date.parse(iso || '');
        if (!isFinite(t)) return '-';
        var d = nowMs - t;
        if (d < 0) {
            var ahead = -d;
            if (ahead < 90000) return 'in a moment';
            if (ahead < 3600000) return 'in ' + Math.round(ahead / 60000) + ' min';
            if (ahead < 86400000) return 'in ' + Math.round(ahead / 3600000) + ' h';
            return formatWhen(iso);
        }
        if (d < 60000) return 'just now';
        if (d < 3600000) return Math.round(d / 60000) + ' min ago';
        if (d < 86400000) return Math.round(d / 3600000) + ' h ago';
        if (d < 172800000) return 'yesterday';
        if (d < 30 * 86400000) return Math.round(d / 86400000) + ' days ago';
        return formatWhen(iso);
    }

    function counterLine(c) {
        c = c || {};
        var parts = [];
        if (isNum(c.articlesInScope)) parts.push(plural(c.articlesInScope, 'note') + ' in scope');
        if (isNum(c.candidatesExamined)) parts.push(plural(c.candidatesExamined, 'candidate') + ' looked at');
        if (isNum(c.adviceCreated)) parts.push(c.adviceCreated + ' advice');
        if (c.modelCalls > 0) parts.push(plural(c.modelCalls, 'model call'));
        var tokens = (c.promptTokens || 0) + (c.completionTokens || 0);
        if (tokens > 0) parts.push(formatTokens(tokens) + ' tokens');
        if (c.errors > 0) parts.push(plural(c.errors, 'error'));
        return parts.join(' \u00B7 ');
    }

    function limitsList(limits) {
        limits = limits || {};
        return [
            'Advice: ' + (limits.maxAdvice > 0 ? limits.maxAdvice : 'no limit'),
            'Time: ' + (limits.maxMinutes > 0 ? limits.maxMinutes + ' min' : 'no limit'),
            'Model calls: ' + (isNum(limits.maxModelCalls) ? formatCount(limits.maxModelCalls) : '-'),
            'Tokens: ' + (isNum(limits.maxTokens) ? formatTokens(limits.maxTokens) : '-')
        ];
    }

    // One row of the history: the big numbers. `counts` is adviceCounts of GET /runs/{id}; until it has come the advice line stays empty.
    function runStats(run, counts, nowMs) {
        run = run || {};
        var c = run.counters || {};
        var tokens = (c.promptTokens || 0) + (c.completionTokens || 0);
        var sub = '';
        if (counts) sub = (counts['new'] + counts.inChat) + ' open \u00B7 ' + counts.applied + ' applied \u00B7 ' + counts.dismissed + ' dismissed';
        return [
            { key: 'viewed', label: 'Notes viewed', value: formatCount(c.articlesInScope), sub: '' },
            { key: 'sent', label: 'Sent to model', value: run.useModel ? formatCount(c.articlesSent) : '-', sub: run.useModel ? '' : 'no model' },
            { key: 'advice', label: 'Advice', value: formatCount(c.adviceCreated), sub: sub },
            { key: 'tokens', label: 'Tokens', value: run.useModel ? formatTokens(tokens) : '-', sub: run.useModel ? (run.modelLabel || '') : '' },
            { key: 'took', label: 'Took', value: formatDuration(runDurationMs(run, nowMs)), sub: '' }
        ];
    }

    function pageInfo(total, page, pageSize, noun) {
        var size = pageSize > 0 ? pageSize : 10;
        var pages = Math.max(1, Math.ceil((total || 0) / size));
        var p = Math.min(Math.max(1, page || 1), pages);
        return { page: p, pages: pages, hasPrev: p > 1, hasNext: p < pages, text: 'Page ' + p + ' of ' + pages + ' \u00B7 ' + plural(total || 0, noun || 'item') };
    }

    // ---- the state card --------------------------------------------------------------------------------------------------------------
    var REASONS = {
        locked: 'The vault is locked. Unlock it to use Dream.',
        sealed: 'The vault is locked. Unlock it to use Dream.',
        unavailable: 'Dream does not run on this node.',
        blind: 'Dream does not run on a blind node.'
    };

    function isActive(run) { return !!run && run.state !== 'finished'; }

    // tone: idle | running | stopping | unavailable | error. canStart / canStop say what the buttons may do now.
    function statusView(status, lastRun, nowMs) {
        if (!status) return { tone: 'error', title: 'Dream state unknown', lines: [], canStart: false, canStop: false };
        if (status.available === false) {
            return { tone: 'unavailable', title: 'Dream cannot run now', lines: [REASONS[status.reason] || str(status.reason) || 'The node did not say why.'], canStart: false, canStop: false };
        }
        var run = status.activeRun;
        if (isActive(run)) {
            var stopping = run.state === 'stopping';
            var lines = [];
            if (run.state === 'queued') lines.push('Waiting to start.');
            else if (stopping) lines.push('Finishing the candidate in hand, then stopping.');
            else if (run.phase) lines.push(phaseLabel(run.phase) + '...');
            var counters = counterLine(run.counters);
            if (counters) lines.push(counters);
            return {
                tone: stopping ? 'stopping' : 'running',
                title: (run.mode === 'trial' ? 'Trial run' : run.trigger === 'schedule' ? 'Night run' : 'Run') + (stopping ? ' is stopping' : ' is going'),
                lines: lines, canStart: false, canStop: !stopping, elapsedMs: runDurationMs(run, nowMs)
            };
        }
        var idle = [];
        if (lastRun) {
            var o = outcomeInfo(lastRun);
            idle.push('Last run ' + relativeWhen(lastRun.finishedAt || lastRun.startedAt || lastRun.createdAt, nowMs) + ': ' + o.label.toLowerCase() + '.');
        } else {
            idle.push('Dream has not run yet.');
        }
        var settings = status.settings || {};
        if (settings.scheduleEnabled) idle.push(status.nextScheduledAt ? 'Next night run ' + relativeWhen(status.nextScheduledAt, nowMs) + ' (' + formatWhen(status.nextScheduledAt) + ').' : 'The night run is on.');
        else idle.push('The night run is off.');
        return { tone: 'idle', title: 'Idle', lines: idle, canStart: true, canStop: false };
    }

    // Notices under the state line: variant (sl-alert), text, and an optional link. A model that is not set up is said on the start form, at its
    // own checkbox (startForm), not here as well.
    function warnings(status) {
        var list = [];
        if (!status || status.available === false) return list;
        var emb = status.embeddings || {};
        if (emb.pending > 0) {
            list.push({ variant: 'primary', icon: 'hourglass-split', text: 'Preparing note vectors: ' + formatCount(emb.pending) + ' of ' + formatCount(emb.total) + ' notes are still waiting. Checks that compare notes start once most are ready.' });
        }
        return list;
    }

    // ---- the start form: two switches, the built-in rules and the AI model --------------------------------------------------------------
    var MODEL_PROBLEMS = {
        no_model: 'No text model is set up for Dream.',
        no_key: 'No usable key for the model\'s provider.',
        provider_not_approved: 'The model\'s provider is not approved yet.'
    };
    var NOTHING_CHOSEN = 'Choose at least one: the built-in rules or the AI model.';
    var LEAVES = 'the text of your notes (all except notes locked with a second password and notes in excluded folders) is sent to the model\'s provider';

    // What the start form shows for the state and the two checkboxes: whether the model can be chosen (and if not, why and where to set it up),
    // whether Start can be pressed (and if not, why), and the line under the form that says what will run and what leaves this node.
    // status: GET /status; form: { useRules, useModel, trial } as the checkboxes are now.
    function startForm(status, form) {
        status = status || {};
        form = form || {};
        var ready = !!status.modelReady;
        var label = str(status.modelLabel);
        var model = { ready: ready, label: label || 'not set up', disabled: !ready, reason: '', links: [] };
        if (!ready) {
            model.reason = MODEL_PROBLEMS[status.modelProblem] || 'The AI model is not ready.';
            model.links = [
                { href: '/Admin/AiChat', text: 'Admin, AI / Chat', what: 'models and keys' },
                { href: '/Admin/Dream', text: 'Admin, Dream', what: 'the model Dream uses' }
            ];
        }
        var rules = form.useRules !== false;
        var useModel = ready && !!form.useModel;
        var name = label ? label : 'the model chosen in the settings';
        var help;
        if (!rules && !useModel) help = NOTHING_CHOSEN;
        else if (rules && !useModel) help = 'The built-in rules run here on this node. Nothing leaves this node.';
        else if (!rules) help = 'Only the AI model runs: ' + LEAVES + ', to ' + name + '. The built-in rules are skipped; note vectors are still prepared here on this node.';
        else help = 'The built-in rules run here on this node, and the AI model looks at pairs of notes, folders and the tree: ' + LEAVES + ', to ' + name + '.';
        if (form.trial) help += ' A trial run does not count: its advice stays out of the open list and nothing it looked at is remembered. It is for comparing models.';
        var canStart = rules || useModel;
        return { rules: rules, model: model, useModel: useModel, canStart: canStart, startReason: canStart ? '' : NOTHING_CHOSEN, help: help };
    }

    // What ran in a run: the built-in rules, the AI model, or both. Runs from before the switch say nothing of the rules: they ran them.
    function runWhat(run) {
        run = run || {};
        var rules = run.useRules !== false;
        var model = !!run.useModel;
        var name = str(run.modelLabel);
        if (rules && model) return { key: 'both', label: 'Rules + AI', text: 'Built-in rules and the AI model' + (name ? ' (' + name + ')' : '') };
        if (model) return { key: 'model', label: 'AI only', text: 'Only the AI model' + (name ? ' (' + name + ')' : '') + '; the built-in rules were off' };
        if (rules) return { key: 'rules', label: 'Rules only', text: 'Only the built-in rules; no model' };
        return { key: 'none', label: 'Nothing', text: 'Neither the rules nor a model ran' };
    }

    // The start form -> the body of POST /runs, or a refusal in words.
    function buildStartRequest(form) {
        form = form || {};
        var useRules = form.useRules !== false;
        var useModel = !!(form.withModel && form.modelReady);
        if (!useRules && !useModel) return { ok: false, error: NOTHING_CHOSEN };
        var body = { mode: form.trial ? 'trial' : 'normal', useRules: useRules, useModel: useModel };
        var advice = intField(form.maxAdvice);
        var minutes = intField(form.maxMinutes);
        if (advice === 'bad') return { ok: false, error: 'The number of advice must be a whole number, 0 or more (0 means no limit).' };
        if (minutes === 'bad') return { ok: false, error: 'The number of minutes must be a whole number, 0 or more (0 means no limit).' };
        if (advice > 1000) return { ok: false, error: 'At most 1000 advice per run.' };
        if (minutes > 1440) return { ok: false, error: 'At most 1440 minutes (a day) per run.' };
        if (advice !== null) body.maxAdvice = advice;
        if (minutes !== null) body.maxMinutes = minutes;
        return { ok: true, body: body };
    }
    function intField(v) {
        if (v == null || String(v).trim() === '') return null;
        if (!/^\d+$/.test(String(v).trim())) return 'bad';
        return parseInt(String(v).trim(), 10);
    }

    // ---- advice ----------------------------------------------------------------------------------------------------------------------
    function enc(s) { return encodeURIComponent(str(s)); }

    function roleLabel(obj, action) {
        action = action || {};
        var id = obj.id;
        if (id && action.type === 'trash_article') { if (id === action.articleId) return 'to the trash'; if (id === action.keepId) return 'kept'; }
        if (id && action.type === 'merge_articles') { if (id === action.keepId) return 'kept'; if (id === action.otherId) return 'merged in'; }
        if (id && action.type === 'mark_superseded') { if (id === action.currentId) return 'current'; if (id === action.oldId) return 'old'; }
        if (id && action.type === 'move_article' && id === action.articleId) return 'to move';
        if (obj.role === 'target') return 'target';
        return '';
    }

    // Every object of a card: its type in words (typeLabel), a link that opens in a new tab so the list of advice is not lost, or plain text
    // for a folder that does not exist yet.
    function objectLinks(objects, action) {
        return arr(objects).map(function (o) {
            o = o || {};
            if (o.type === 'folder') {
                var path = str(o.path) || '/';
                return { type: 'folder', typeLabel: 'Folder', icon: 'folder2', label: path, sub: o.id ? '' : 'does not exist yet', href: o.id ? '/Folder?path=' + enc(path) : '', role: roleLabel(o, action) };
            }
            if (o.type === 'tag') {
                return { type: 'tag', typeLabel: 'Tag', icon: 'tag', label: str(o.name), sub: isNum(o.articleCount) ? plural(o.articleCount, 'note') : '', href: '/Graph?concept=' + enc(o.name), role: '' };
            }
            return {
                type: 'article', typeLabel: 'Note', icon: 'file-earmark-text', label: str(o.title) || 'Untitled note', sub: str(o.treePath),
                href: o.id ? '/Article/View?id=' + enc(o.id) : '', role: roleLabel(o, action)
            };
        });
    }

    // "Found by ..." under the title of a card: the built-in rules, or the AI model by name (the advice's own model, else the run's).
    function foundBy(a, runModelLabel) {
        a = a || {};
        if (a.source !== 'model') return 'Found by the built-in rules';
        var name = a.model && str(a.model.label) ? str(a.model.label) : str(runModelLabel);
        return name ? 'Found by the AI model: ' + name : 'Found by the AI model';
    }

    function evidenceLines(evidence) {
        return arr(evidence).map(function (e) {
            e = e || {};
            var value = '';
            if (isNum(e.value)) value = e.type === 'similarity' ? Math.round(e.value * 100) + '%' : formatCount(e.value);
            return { type: str(e.type), label: ({ neighbours: 'Neighbours', similarity: 'Similarity', quote: 'Quote', count: 'Count', note: 'Note' })[e.type] || humanize(e.type), text: str(e.text), value: value };
        });
    }

    function titleOf(a, id) {
        var found = arr(a.objects).filter(function (o) { return o && o.type === 'article' && o.id === id; })[0];
        return found && found.title ? found.title : 'a note';
    }
    function pathOf(a, id) {
        var found = arr(a.objects).filter(function (o) { return o && o.type === 'article' && o.id === id; })[0];
        return found && found.treePath ? found.treePath : '';
    }
    function quote(s) { return '\u201C' + str(s) + '\u201D'; }

    var MOVE_WARNINGS = [
        'The new folder may have other access rules than the old one.',
        'Links that point to the old path are not corrected.'
    ];

    // What the Apply dialog says it will do, in the exact terms of the action, and every note of the advice by title and path (for a move these are exactly the notes
    // that move, so the whole list is in front of the person who presses Apply). null: this advice has no button for it (its action is not done by code).
    function applyPlan(a) {
        var plan = applyPlanOf(a);
        if (plan) plan.notes = articleNotes(a);
        return plan;
    }

    function articleNotes(a) {
        return arr(a && a.objects).filter(function (o) { return o && o.type === 'article'; })
            .map(function (o) { return { title: str(o.title) || 'Untitled note', path: str(o.treePath) }; });
    }

    function applyPlanOf(a) {
        if (!a || !a.action) return null;
        var act = a.action;
        var names = function (list, max) {
            var shown = list.slice(0, max).map(quote).join(', ');
            return list.length > max ? shown + ' and ' + (list.length - max) + ' more' : shown;
        };
        switch (act.type) {
            case 'trash_article':
                return {
                    confirm: 'Move to trash',
                    lines: ['Move ' + quote(titleOf(a, act.articleId)) + ' to the trash.', 'The note that stays: ' + quote(titleOf(a, act.keepId)) + '.'],
                    warnings: ['The note stays in the trash for 30 days and can be restored from there.']
                };
            case 'move_article': {
                var from = pathOf(a, act.articleId);
                return {
                    confirm: 'Move note',
                    lines: ['Move ' + quote(titleOf(a, act.articleId)) + (from ? ' from ' + from : '') + ' to ' + str(act.toPath) + '.'],
                    warnings: MOVE_WARNINGS
                };
            }
            case 'move_articles': {
                var ids = arr(act.articleIds);
                var titles = ids.map(function (id) { return titleOf(a, id); });
                return {
                    confirm: 'Move ' + plural(ids.length, 'note'),
                    lines: ['Move ' + plural(ids.length, 'note') + ' into ' + str(act.toPath) + ' (the folder is created if it does not exist): ' + names(titles, 5) + '. Every note is listed below.'],
                    warnings: MOVE_WARNINGS.concat(['A note that has changed since the advice was made is left where it is, the others are still moved, and the result lists the ones that were not.',
                        'Nothing is moved if the folder, or the folder a note is in now, is one Dream leaves alone or a system folder.'])
                };
            }
            case 'merge_tags': {
                var sources = arr(act.sources);
                return {
                    confirm: 'Merge tags',
                    lines: ['Merge ' + names(sources, 10) + ' into ' + quote(act.target) + ': every note that has one of them gets ' + quote(act.target) + ', and the merged tags are deleted.'],
                    warnings: []
                };
            }
            case 'delete_tags': {
                var tags = arr(act.names);
                return {
                    confirm: 'Delete ' + plural(tags.length, 'tag'),
                    lines: ['Delete ' + plural(tags.length, 'tag') + ' that no note uses: ' + names(tags, 20) + '.'],
                    warnings: ['A tag that has been given to a note since the advice was made is left alone.']
                };
            }
            case 'delete_folder':
                return {
                    confirm: 'Delete folder',
                    lines: ['Delete the empty folder ' + str(act.path) + '.'],
                    warnings: ['The folder is deleted only if it is still empty when you confirm.']
                };
            default:
                return null;
        }
    }

    // The choices of the Dismiss dialog; every one makes a rule "do not suggest this again" of a different reach.
    function dismissOptions(a) {
        a = a || {};
        var objs = arr(a.objects);
        var kind = kindInfo(a.kind);
        // The folder the advice is about, never where it would send something (role target): the rule leaves what is in that
        // folder alone and still lets Dream suggest moving things into it (DreamDismissRules.FolderOf picks the same one).
        var folderObj = objs.filter(function (o) { return o && o.type === 'folder' && o.path && o.role !== 'target'; })[0];
        var articleObj = objs.filter(function (o) { return o && o.type === 'article' && o.treePath && o.role !== 'target'; })[0];
        var folderPath = folderObj ? folderObj.path : articleObj ? articleObj.treePath : '';
        var noun = kind.noun;
        var firstType = objs.length ? objs[0].type : '';
        if (firstType === 'article') noun = 'note'; else if (firstType === 'folder') noun = 'folder'; else if (firstType === 'tag') noun = 'tag';
        return [
            { value: 'advice', label: 'This advice only', hint: 'The same suggestion is not made again.', disabled: false },
            { value: 'object', label: 'Anything of this kind about this ' + noun, hint: 'This kind of advice is not made about this ' + noun + ' again.', disabled: !objs.length },
            { value: 'folder', label: folderPath ? 'This kind of advice in ' + folderPath : 'This kind of advice in this folder', hint: 'What is under that folder is left alone for this kind of advice; moving something from elsewhere into it can still be suggested.', disabled: !folderPath },
            { value: 'kind', label: 'This kind of advice anywhere', hint: 'Dream stops making "' + kind.label.toLowerCase() + '" advice at all.', disabled: false }
        ];
    }
    var EXPIRY_OPTIONS = [{ value: '180', label: 'For 180 days' }, { value: 'never', label: 'For good' }];

    // The view of one card: everything the page draws, decided here.
    function adviceView(a, opts) {
        a = a || {};
        opts = opts || {};
        var st = statusInfo(a.status);
        var can = a.can || {};
        var kind = kindInfo(a.kind);
        var expired = a.status === 'expired';
        var closed = expired || a.status === 'applied' || a.status === 'dismissed';
        var score = isNum(a.score) ? Math.max(0, Math.min(100, Math.round(a.score))) : null;
        var band = BAND_LABELS[a.band] ? a.band : bandOf(score);
        var busy = a.status === 'applying';
        var plan = applyPlan(a);
        // Apply and the chat only for what is still to do; the prompt also for what was applied or dismissed (for reference: the node says
        // whether it gives one, can.prompt, and it does not for an expired advice).
        var chatOn = !!can.chat && !closed && !busy;
        return {
            id: str(a.id),
            score: score,
            band: band,
            bandLabel: bandLabel(band),
            kind: kind,
            source: a.source === 'model' ? 'Model' : 'Rules',
            trial: !!a.trial,
            status: st,
            expired: expired,
            closed: closed,
            title: str(a.title) || kind.label,
            summary: str(a.summary),
            objects: objectLinks(a.objects, a.action),
            evidence: evidenceLines(a.evidence),
            references: a.references && a.references.checked && a.references.count > 0
                ? { count: a.references.count, text: plural(a.references.count, 'other note') + ' mention this note by its id.', samples: arr(a.references.samples) } : null,
            model: a.model ? (str(a.model.label) + (a.model.promptVersion ? ' \u00B7 ' + a.model.promptVersion : '')) : '',
            promptVersion: a.model ? str(a.model.promptVersion) : '',
            foundBy: foundBy(a, opts.runModelLabel),
            promptNote: closed && !expired ? 'This advice is already ' + st.label.toLowerCase() + ': the prompt is for reference only. Nothing on the node was checked again for it.' : '',
            note: expired ? (str(a.expiredReason) || str(a.statusNote) || 'What this advice describes has changed.') : '',
            applied: a.applied ? { when: a.applied.at, result: str(a.applied.result) } : null,
            lastError: str(a.lastError),
            createdAt: a.createdAt,
            buttons: {
                apply: !!can.apply && !closed && !busy && !!plan,
                chat: chatOn,
                chatDisabled: chatOn && opts.chatAvailable === false,
                prompt: !!can.prompt && !busy && !expired,
                dismiss: !!can.dismiss && !closed,
                markDone: !!can.markDone && !closed && !busy,
                backToNew: a.status === 'in_chat'
            },
            applyPlan: plan
        };
    }

    // ---- the live feed ---------------------------------------------------------------------------------------------------------------
    // current: the advice shown so far (by seq); incoming: what the poll brought. Returns the new list, and `added`: what is new, in order, to be shown one by one.
    function mergeFeed(current, incoming) {
        var have = {};
        arr(current).forEach(function (x) { if (x && x.id) have[x.id] = true; });
        var added = [];
        arr(incoming).filter(function (x) { return x && x.id; }).sort(function (p, q) { return (p.seq || 0) - (q.seq || 0); }).forEach(function (x) {
            if (have[x.id]) return;
            have[x.id] = true;
            added.push(x);
        });
        return { items: arr(current).concat(added), added: added };
    }
    function lastSeqOf(items, fallback) {
        var max = isNum(fallback) ? fallback : 0;
        arr(items).forEach(function (x) { if (x && isNum(x.seq) && x.seq > max) max = x.seq; });
        return max;
    }
    // ---- reloading in place (the tab comes back) -------------------------------------------------------------------------------------
    // What a list of advice or runs looks like, in one string: the same string means there is nothing to redraw, so a list that did not change
    // does not blink, and a card whose details are open stays open.
    function adviceSignature(items) {
        return arr(items).map(function (a) {
            a = a || {};
            return [a.id, a.status, a.statusAt, a.score, a.lastError, a.applied ? 1 : 0, a.can ? [a.can.apply, a.can.chat, a.can.prompt, a.can.dismiss, a.can.markDone].join('') : ''].join('|');
        }).join('\n');
    }
    function runSignature(runs) {
        return arr(runs).map(function (r) {
            r = r || {};
            var c = r.counters || {};
            return [r.id, r.state, r.outcome, r.finishedAt, c.adviceCreated, c.articlesInScope, c.modelCalls, r.useRules, r.useModel].join('|');
        }).join('\n');
    }

    // ?advice=<id> (the chat links back to an advice): the id, or '' when there is none or it is not a guid.
    function adviceIdFromQuery(search) {
        var m = /(?:^|[?&])advice=([^&#]*)/.exec(str(search));
        if (!m) return '';
        var id = '';
        try { id = decodeURIComponent(m[1]).trim(); } catch (e) { return ''; }
        return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id) ? id.toLowerCase() : '';
    }

    // How long to wait before showing the next card of a queue: a person should see them come one by one, but a long queue must not drag.
    function revealDelay(queueLength) {
        if (queueLength > 12) return 80;
        if (queueLength > 6) return 160;
        return 320;
    }

    // ---- words for failures ----------------------------------------------------------------------------------------------------------
    var CODE_WORDS = {
        locked: 'The vault is locked. Unlock it and try again.',
        already_running: 'A run is already going on. Wait for it to finish, or stop it.',
        expired: 'This advice is out of date: what it describes has changed. It has been marked as expired.',
        not_applicable: 'This advice cannot be applied with a button. Use "Open in AI chat" or the prompt.',
        not_found: 'The node does not know this advice any more.'
    };

    // ---- an advice that was closed meanwhile (another tab, the sweep) ------------------------------------------------------------------
    var CLOSED = { applied: true, dismissed: true, expired: true, applying: true };

    // A 409 that says the advice is no longer open: the body has its status and the time (DreamAdviceConflictResponse).
    function isClosedConflict(err) {
        var b = err && err.body;
        return !!(err && err.status === 409 && b && typeof b === 'object' && CLOSED[b.status]);
    }

    // What happened to the advice, with the time in the person's own time zone. Falls back to the node's own sentence.
    function conflictText(body) {
        body = body || {};
        var when = body.statusAt && isFinite(Date.parse(body.statusAt)) ? formatWhen(body.statusAt) : '';
        var on = when ? ' on ' + when : '';
        var note = str(body.statusNote);
        switch (body.status) {
            case 'applied':
                if (note === 'changed_after_chat') return 'A note it is about changed after it went to the chat, so it counts as done' + (when ? ' (' + when + ').' : '.');
                if (note === 'marked_done') return 'This advice was already marked done' + on + '.';
                return 'This advice was already applied' + on + '.';
            case 'dismissed': return 'This advice was already dismissed' + on + '.';
            case 'expired': return 'This advice expired' + on + ': ' + expiredReason(note);
            default: return str(body.error) || 'This advice changed a moment ago.';
        }
    }
    function expiredReason(note) {
        var r = note === 'superseded' ? 'a newer verdict on the same pair of notes replaced it.' : (note || 'what it describes has changed.');
        if (r.length > 1 && /[A-Z]/.test(r.charAt(0)) && /[a-z]/.test(r.charAt(1))) r = r.charAt(0).toLowerCase() + r.slice(1);
        return /[.!?]$/.test(r) ? r : r + '.';
    }

    // ---- the rule book (GET /rulebook) -----------------------------------------------------------------------------------------------
    var MODE_WORDS = { code: 'Apply', chat: 'Open in AI chat', prompt: 'Show prompt' };

    // The dialog "View all rules", read from the node's answer: every list tolerates a stub, every kind gets the label of the kind filter.
    function ruleBookView(book) {
        book = book || {};
        function kinds(x) {
            var details = {};
            arr(x.kindDetails).forEach(function (d) { if (d && d.kind) details[d.kind] = d; });
            return arr(x.kinds).map(function (k) {
                var info = kindInfo(k);
                var modes = arr(details[k] && details[k].applyModes).map(function (m) { return MODE_WORDS[m] || humanize(m); });
                return { value: info.value, label: info.label, icon: info.icon, modes: modes.length ? modes.join(' or ') : '' };
            });
        }
        function facts(list) {
            return arr(list).filter(function (f) { return f && (f.name || f.value); }).map(function (f) { return { name: str(f.name), value: str(f.value) }; });
        }
        function lines(list) { return arr(list).map(str).filter(function (l) { return l.trim(); }); }
        var rules = arr(book.rules).filter(Boolean).map(function (r) {
            return {
                id: str(r.id), name: str(r.name) || str(r.id), described: r.described !== false, kinds: kinds(r), runsWhen: str(r.runsWhen),
                finds: str(r.finds), example: str(r.example), how: lines(r.how), thresholds: facts(r.thresholds), score: str(r.score),
                apply: str(r.apply), never: str(r.never), skips: lines(r.skips)
            };
        });
        var tasks = arr(book.modelTasks).filter(Boolean).map(function (t) {
            return {
                id: str(t.id), name: str(t.name) || str(t.id), described: t.described !== false, promptVersion: str(t.promptVersion), kinds: kinds(t),
                candidates: lines(t.candidates), asked: str(t.asked), answers: str(t.answers), checks: lines(t.checks), limits: facts(t.limits)
            };
        });
        var bands = arr(book.bands).filter(Boolean).map(function (b) {
            return { band: BAND_LABELS[b.band] ? b.band : 'low', label: bandLabel(b.band), from: isNum(b.from) ? b.from : null, to: isNum(b.to) ? b.to : null, meaning: str(b.meaning) };
        });
        return {
            intro: lines(book.intro), similarity: lines(book.similarity), skips: lines(book.skips), rules: rules, tasks: tasks,
            limits: facts(book.limits), bands: bands, empty: !rules.length && !tasks.length
        };
    }
    // What the node said, as the API puts it: ErrorResponse is { error, code } (ProblemDetails has detail and title; a proxy may say message).
    function nodeWords(body) {
        if (!body || typeof body !== 'object') return '';
        var keys = ['error', 'message', 'detail', 'title'];
        for (var i = 0; i < keys.length; i++) {
            var v = body[keys[i]];
            if (typeof v === 'string' && v.trim()) return v.trim();
            if (v && typeof v === 'object' && typeof v.message === 'string' && v.message.trim()) return v.message.trim();
        }
        return '';
    }
    function errorText(err) {
        if (!err) return 'Something went wrong.';
        if (isClosedConflict(err)) return conflictText(err.body);
        var said = nodeWords(err.body);
        if (said) return said;
        var code = err.body && typeof err.body === 'object' ? err.body.code : '';
        if (CODE_WORDS[code]) return CODE_WORDS[code];
        return err.message || String(err);
    }
    function loadFailure(what, err) { return 'Could not load ' + what + ': ' + errorText(err); }

    root.bmbDreamModel = {
        kindInfo: kindInfo, kindOptions: kindOptions, bandOf: bandOf, bandLabel: bandLabel, statusInfo: statusInfo,
        outcomeInfo: outcomeInfo, triggerInfo: triggerInfo, phaseLabel: phaseLabel,
        formatCount: formatCount, formatTokens: formatTokens, formatDuration: formatDuration, runDurationMs: runDurationMs, formatWhen: formatWhen, relativeWhen: relativeWhen,
        counterLine: counterLine, limitsList: limitsList, runStats: runStats, pageInfo: pageInfo,
        isActive: isActive, statusView: statusView, warnings: warnings, buildStartRequest: buildStartRequest, startForm: startForm, runWhat: runWhat,
        objectLinks: objectLinks, foundBy: foundBy, evidenceLines: evidenceLines, applyPlan: applyPlan, dismissOptions: dismissOptions, expiryOptions: EXPIRY_OPTIONS, adviceView: adviceView,
        mergeFeed: mergeFeed, lastSeqOf: lastSeqOf, revealDelay: revealDelay,
        errorText: errorText, loadFailure: loadFailure, isClosedConflict: isClosedConflict, conflictText: conflictText, ruleBookView: ruleBookView,
        adviceSignature: adviceSignature, runSignature: runSignature, adviceIdFromQuery: adviceIdFromQuery
    };
})(window);
