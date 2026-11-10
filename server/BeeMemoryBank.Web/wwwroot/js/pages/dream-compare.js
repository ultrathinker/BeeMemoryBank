// ===== Dream page: comparing trial runs =====
// The section under the history: tick two or three trial runs, press Compare, and read one table of what each run made of each candidate. The columns
// are lettered A, B, C in a random order and the names of the models stay hidden until the switch is turned on, so the comparison can be blind. What the
// words and the numbers say is dream-compare-model.js (run on its own by DreamCompareModelTests); this file draws and asks the node, through the calls
// of dream.js (bmbDream.api), which drops an answer that arrives after the page was left. It only reads: nothing here changes anything on the node.
(function () {
    'use strict';

    var C = window.bmbDreamCompare;
    var M = window.bmbDreamModel;
    var dream = window.bmbDream;
    var section = document.getElementById('dream-compare-section');
    if (!section || !C || !M || !dream || !dream.api) return;

    var RUNS_SIZE = 20;       // the trial runs offered for picking: the newest twenty (the journal that feeds the table is kept seven days anyway)
    var ROWS_SHOWN = 100;     // rows drawn at first and per "Show more"

    var st = {
        runs: [], runsLoaded: false, selected: [], order: [], response: null, table: null,
        revealed: false, filter: 'all', sort: 'order', shown: ROWS_SHOWN, seq: 0, runsSeq: 0
    };
    var gone = false;

    // ---- small helpers ---------------------------------------------------------------------------------------------------------------
    function $(id) { return document.getElementById(id); }
    function esc(text) {
        return String(text == null ? '' : text).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }
    function arr(a) { return Array.isArray(a) ? a : []; }
    function show(el, on) { if (el) el.hidden = !on; }
    function loadingBox() { return '<div class="dream-loading"><sl-spinner></sl-spinner></div>'; }
    function errorBox(text, retry) {
        return '<div class="dream-error"><sl-icon name="exclamation-octagon"></sl-icon><span>' + esc(text) + '</span>' +
            (retry ? '<sl-button size="small" data-cmp-retry="' + esc(retry) + '">Try again</sl-button>' : '') + '</div>';
    }

    // A random number in [0, 1) from the browser's own source where there is one: the order of the columns is what keeps the comparison blind.
    function rand() {
        var c = window.crypto;
        if (c && typeof c.getRandomValues === 'function') {
            var a = new Uint32Array(1);
            c.getRandomValues(a);
            return a[0] / 4294967296;
        }
        return Math.random();
    }

    // ---- the trial runs to pick from -------------------------------------------------------------------------------------------------
    function loadRuns() {
        var seq = ++st.runsSeq;
        if (!st.runsLoaded) $('dream-cmp-picker').innerHTML = loadingBox();
        return dream.api.get('/runs', { page: 1, pageSize: RUNS_SIZE, mode: 'trial' }).then(function (r) {
            if (gone || seq !== st.runsSeq) return;
            st.runs = arr(r.items);
            st.runsLoaded = true;
            // A run that left the list (a newer twenty) cannot stay ticked.
            var known = {};
            st.runs.forEach(function (run) { known[run.id] = true; });
            st.selected = st.selected.filter(function (id) { return known[id]; });
            renderPicker();
        }, function (err) {
            if (gone || seq !== st.runsSeq) return;
            $('dream-cmp-picker').innerHTML = errorBox(M.loadFailure('the trial runs', err), 'runs');
            $('dream-cmp-hint').textContent = '';
            $('dream-cmp-go').disabled = true;
            show($('dream-cmp-latest'), false);
        });
    }

    function renderPicker() {
        var host = $('dream-cmp-picker');
        if (!st.runs.length) {
            host.innerHTML = '<p class="dream-empty">No trial runs yet. Start one above with "Trial run" ticked; to compare models, start one per model.</p>';
            $('dream-cmp-hint').textContent = '';
            $('dream-cmp-go').disabled = true;
            show($('dream-cmp-latest'), false);
            return;
        }
        var pick = C.pickState(st.selected, st.runs);
        host.innerHTML = st.runs.map(function (run) {
            var o = C.runOption(run, st.revealed);
            var checked = st.selected.indexOf(o.id) >= 0;
            return '<div class="dream-cmp-run"><sl-checkbox data-cmp-run="' + esc(o.id) + '"' + (checked ? ' checked' : '') + (!checked && pick.full ? ' disabled' : '') + '>' +
                '<span class="dream-cmp-run-title">' + esc(o.title) + '</span></sl-checkbox>' +
                '<sl-badge variant="' + esc(o.badge.variant) + '"' + (o.badge.text ? ' title="' + esc(o.badge.text) + '"' : '') + '>' + esc(o.badge.label) + '</sl-badge>' +
                '<div class="dream-cmp-run-sub">' + esc(o.sub) + (o.note ? ' \u00B7 ' + esc(o.note) : '') + '</div></div>';
        }).join('');
        show($('dream-cmp-latest'), C.latestPick(st.runs).length >= C.MIN_RUNS);
        updatePickControls();
    }

    function updatePickControls() {
        var pick = C.pickState(st.selected, st.runs);
        $('dream-cmp-hint').textContent = pick.hint + (pick.warnings.length ? ' ' + pick.warnings.join(' ') : '');
        $('dream-cmp-go').disabled = !pick.canCompare;
        Array.prototype.slice.call($('dream-cmp-picker').querySelectorAll('sl-checkbox')).forEach(function (cb) {
            cb.disabled = !cb.checked && pick.full;
        });
    }

    function onPick(e) {
        var cb = e.target && e.target.closest ? e.target.closest('[data-cmp-run]') : null;
        if (!cb) return;
        var id = cb.dataset.cmpRun;
        st.selected = C.toggleSelection(st.selected, id, !!cb.checked);
        cb.checked = st.selected.indexOf(id) >= 0;   // a fourth tick is refused: the box takes it back
        updatePickControls();
    }

    function onLatest() {
        st.selected = C.latestPick(st.runs);
        renderPicker();
    }

    // ---- the table -------------------------------------------------------------------------------------------------------------------
    function onCompare() {
        if (!C.pickState(st.selected, st.runs).canCompare) return;
        st.order = C.shuffleIds(st.selected, rand);   // the person did not choose which run is A
        loadCompare();
    }

    function loadCompare() {
        var seq = ++st.seq;
        var btn = $('dream-cmp-go');
        btn.loading = true;
        $('dream-cmp-summary').innerHTML = loadingBox();
        $('dream-cmp-rows').innerHTML = '';
        show($('dream-cmp-tools'), false);
        dream.api.get('/compare', { runs: st.order.join(',') }).then(function (response) {
            if (gone || seq !== st.seq) return;
            btn.loading = false;
            st.response = response;
            st.shown = ROWS_SHOWN;
            render();
        }, function (err) {
            if (gone || seq !== st.seq) return;
            btn.loading = false;
            st.response = null;
            st.table = null;
            $('dream-cmp-summary').innerHTML = errorBox(M.loadFailure('the comparison', err), 'compare');
        });
    }

    function onShuffle() {
        if (!st.response) return;
        st.order = C.reshuffle(st.order, rand);
        render();
    }

    function render() {
        if (!st.response) return;
        var table = C.tableView(st.response, st.order, st.revealed);
        st.table = table;
        st.order = table.columns.map(function (c) { return c.id; });
        $('dream-cmp-summary').innerHTML = summaryHtml(table);
        renderFilter(table);
        show($('dream-cmp-tools'), true);
        renderRows();
    }

    function renderFilter(table) {
        var filter = $('dream-cmp-filter');
        filter.innerHTML = C.FILTERS.map(function (f) {
            return '<sl-option value="' + esc(f.value) + '">' + esc(f.label + ' (' + (table.counts[f.value] || 0) + ')') + '</sl-option>';
        }).join('');
        filter.value = st.filter;
    }

    function summaryHtml(t) {
        var h = '';
        t.columns.forEach(function (c) {
            if (c.journalGone) h += '<sl-alert variant="warning" open><sl-icon slot="icon" name="clock-history"></sl-icon>The log of run ' + esc(c.letter) +
                ' is gone (a run keeps its log for seven days), so its column is empty.</sl-alert>';
            else if (c.run && c.run.state && c.run.state !== 'finished') h += '<sl-alert variant="primary" open><sl-icon slot="icon" name="hourglass-split"></sl-icon>Run ' + esc(c.letter) +
                ' is still going: reload the table to see more of it.</sl-alert>';
        });
        h += '<div class="dream-cmp-summary">' +
            stat(t.common, 'Common candidates', 'examined by every run') +
            stat(t.agreementText, 'Agreement', t.agreementNote) +
            stat(t.disagreements, 'Different verdicts', 'among the common ones') +
            stat(t.partial, 'Not examined by all', 'one run went further than another') + '</div>';
        h += '<div class="dream-cmp-cols">' + t.columns.map(columnCardHtml).join('') + '</div>';
        return h;
    }

    function stat(value, label, sub) {
        return '<div class="dream-stat"><div class="dream-stat-value">' + esc(value) + '</div><div class="dream-stat-label">' + esc(label) + '</div><div class="dream-stat-sub">' + esc(sub) + '</div></div>';
    }

    function columnCardHtml(c) {
        var oc = c.run ? M.outcomeInfo(c.run) : null;
        var lines = [
            ['Examined', c.examined], ['Advice made', c.advice], ['Mean score', c.meanScore == null ? '-' : c.meanScore], ['Advice only here', c.foundAlone], ['Failed', c.errors]
        ].map(function (p) { return '<div><dt>' + esc(p[0]) + '</dt><dd>' + esc(p[1]) + '</dd></div>'; }).join('');
        var facts = c.facts.map(function (f) { return '<div><dt>' + esc(f.label) + '</dt><dd>' + esc(f.value) + '</dd></div>'; }).join('');
        return '<div class="dream-cmp-col"><div class="dream-cmp-col-head"><span class="dream-cmp-letter">' + esc(c.letter) + '</span>' +
            (c.title.sub ? '<span class="dream-cmp-model">' + esc(c.title.sub) + '</span>' : '') +
            (oc && c.title.sub ? '<sl-badge variant="' + esc(oc.variant) + '">' + esc(oc.label) + '</sl-badge>' : '') + '</div>' +
            '<dl class="dream-cmp-facts">' + lines + facts + '</dl></div>';
    }

    function renderRows() {
        var t = st.table;
        var host = $('dream-cmp-rows');
        if (!t || !host) return;
        var rows = C.sortRows(C.filterRows(t.rows, st.filter), st.sort);
        if (!rows.length) {
            var words = !t.rows.length ? C.emptyWords(t) : 'No candidate matches this filter.';
            host.innerHTML = '<p class="dream-empty">' + esc(words) + '</p>';
            return;
        }
        var cols = t.columns;
        var head = '<div class="dream-cmp-head" role="row"><div class="dream-cmp-first" role="columnheader">Candidate</div>' + cols.map(function (c) {
            return '<div class="dream-cmp-h" role="columnheader"><span class="dream-cmp-letter">' + esc(c.letter) + '</span>' +
                (c.title.sub ? '<span class="dream-cmp-model">' + esc(c.title.sub) + '</span>' : '') + '</div>';
        }).join('') + '</div>';
        var shown = rows.slice(0, st.shown);
        var more = rows.length > shown.length ? '<div class="dream-cmp-morerows"><sl-button size="small" data-cmp-more-rows>Show ' + Math.min(ROWS_SHOWN, rows.length - shown.length) +
            ' more (' + (rows.length - shown.length) + ' left)</sl-button></div>' : '';
        host.innerHTML = '<div class="dream-cmp-scroll"><div class="dream-cmp-grid" role="table" aria-label="What each trial run made of each candidate" style="--cols:' + cols.length + '">' +
            head + shown.map(function (v) { return rowHtml(v, cols); }).join('') + '</div></div>' + more +
            '<p class="dream-meta">' + esc(shown.length + ' of ' + rows.length + (rows.length === 1 ? ' candidate' : ' candidates')) + '</p>';
    }

    function rowHtml(v, cols) {
        var cls = 'dream-cmp-row' + (v.disagree ? ' is-disagree' : '') + (v.foundOnly >= 0 ? ' is-only-one' : '') + (v.missing.length ? ' is-partial' : '');
        var h = '<div class="' + cls + '" role="row" data-cmp-key="' + esc(v.key) + '"><div class="dream-cmp-first" role="cell">';
        h += '<ul class="dream-cmp-objects">' + v.objects.slice(0, 4).map(function (o) {
            return '<li><sl-icon name="' + esc(o.icon) + '"></sl-icon>' + (o.href ? '<a href="' + esc(o.href) + '">' + esc(o.label) + '</a>' : '<span>' + esc(o.label) + '</span>') +
                (o.sub ? '<span class="dream-object-sub">' + esc(o.sub) + '</span>' : '') + '</li>';
        }).join('') + (v.objects.length > 4 ? '<li class="dream-object-sub">and ' + (v.objects.length - 4) + ' more</li>' : '') + '</ul>';
        if (v.flags.length) h += '<div class="dream-cmp-flags">' + v.flags.map(function (f) { return '<sl-badge variant="' + esc(f.variant) + '">' + esc(f.text) + '</sl-badge>'; }).join('') + '</div>';
        h += '<sl-button size="small" variant="text" data-cmp-toggle aria-expanded="false"><sl-icon slot="prefix" name="chevron-down"></sl-icon>What each run said</sl-button></div>';
        v.cells.forEach(function (c, i) { h += cellHtml(c, v.odd.indexOf(i) >= 0, cols[i]); });
        h += '<div class="dream-cmp-more" hidden>' + v.cells.map(function (c, i) {
            return '<div class="dream-cmp-said"><span class="dream-cmp-letter">' + esc(cols[i].letter) + '</span>' +
                '<p>' + esc(c.tone === 'none' ? 'Did not examine this candidate.' : c.tone === 'error' ? 'The run got no usable answer for this candidate.' : (c.summary || 'No explanation.')) + '</p></div>';
        }).join('') + '</div></div>';
        return h;
    }

    function cellHtml(c, odd, col) {
        var label = 'Run ' + col.letter + ': ' + (c.tone === 'none' ? 'not examined' : c.text + (c.score != null ? ', score ' + c.score : ''));
        if (c.tone === 'none') return '<div class="dream-cmp-cell tone-none" role="cell" aria-label="' + esc(label) + '"><span class="dream-cmp-none">-</span><span class="dream-cmp-kind">not examined</span></div>';
        return '<div class="dream-cmp-cell tone-' + esc(c.tone) + (c.band ? ' band-' + esc(c.band) : '') + (odd ? ' is-odd' : '') + '" role="cell" aria-label="' + esc(label) + '">' +
            '<sl-badge variant="' + esc(c.variant) + '">' + esc(c.text) + '</sl-badge>' +
            (c.score != null ? '<span class="dream-cmp-score" title="Score 0-100: how much the model thinks this is worth doing">' + esc(c.score) + '</span>' : '') +
            (c.kindLabel ? '<span class="dream-cmp-kind">' + esc(c.kindLabel) + '</span>' : '') + '</div>';
    }

    // ---- events ----------------------------------------------------------------------------------------------------------------------
    function onClick(e) {
        var t = e.target && e.target.closest ? e.target : null;
        if (!t) return;
        var retry = t.closest('[data-cmp-retry]');
        if (retry) { if (retry.dataset.cmpRetry === 'runs') loadRuns(); else if (st.order.length) loadCompare(); return; }
        var toggle = t.closest('[data-cmp-toggle]');
        if (toggle) {
            var row = toggle.closest('.dream-cmp-row');
            var more = row ? row.querySelector('.dream-cmp-more') : null;
            if (more) {
                more.hidden = !more.hidden;
                toggle.setAttribute('aria-expanded', more.hidden ? 'false' : 'true');
            }
            return;
        }
        if (t.closest('[data-cmp-more-rows]')) { st.shown += ROWS_SHOWN; renderRows(); }
    }

    function bind() {
        section.addEventListener('click', onClick);
        $('dream-cmp-picker').addEventListener('sl-change', onPick);
        $('dream-cmp-go').addEventListener('click', onCompare);
        $('dream-cmp-latest').addEventListener('click', onLatest);
        $('dream-cmp-shuffle').addEventListener('click', onShuffle);
        $('dream-cmp-refresh').addEventListener('click', function () {
            loadRuns();
            if (st.order.length) loadCompare();
        });
        $('dream-cmp-reveal').addEventListener('sl-change', function (e) {
            st.revealed = !!e.target.checked;
            if (st.runsLoaded) renderPicker();
            if (st.response) render();
        });
        $('dream-cmp-filter').addEventListener('sl-change', function (e) { st.filter = e.target.value || 'all'; st.shown = ROWS_SHOWN; renderRows(); });
        $('dream-cmp-sort').addEventListener('sl-change', function (e) { st.sort = e.target.value || 'order'; renderRows(); });
    }

    function cleanup() {
        gone = true;
        st.seq++;
        st.runsSeq++;
    }

    function init() {
        $('dream-cmp-sort').innerHTML = C.SORTS.map(function (s) { return '<sl-option value="' + esc(s.value) + '">' + esc(s.label) + '</sl-option>'; }).join('');
        $('dream-cmp-sort').value = st.sort;
        bind();
        document.addEventListener('bmb:page-cleanup', cleanup, { once: true });
        loadRuns();
    }

    init();
})();
