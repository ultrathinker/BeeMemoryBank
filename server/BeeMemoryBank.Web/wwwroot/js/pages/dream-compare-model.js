// ===== Dream page: comparing trial runs (the model: what the table says) =====
// The section "Compare trial runs" of the Dream page (dream-compare.js) draws; this file decides what to say. It touches no page and no network, so
// DreamCompareModelTests can run it on its own (JsHarness/dream-compare-harness.js): which trial runs may be picked, how the columns are lettered A, B, C
// and shuffled so the comparison can be blind, what each cell of the table says, which rows are disagreements, what each column found alone, and the
// sample of rows to judge by hand (the 10 biggest disagreements and the 10 highest scores).
// It needs dream-model.js for the words of a kind, a band, a number and a time. Every function tolerates a missing field: the answer of the API is
// read as it comes (a stub, an older node, an error), and the page must never be blank.
(function (root) {
    'use strict';

    var D = root.bmbDreamModel || {};

    var MIN_RUNS = 2;
    var MAX_RUNS = 3;
    var SAMPLE_DISAGREEMENTS = 10;
    var SAMPLE_TOP_SCORES = 10;

    // ---- small helpers ---------------------------------------------------------------------------------------------------------------
    function isNum(n) { return typeof n === 'number' && isFinite(n); }
    function str(s) { return typeof s === 'string' ? s : (s == null ? '' : String(s)); }
    function arr(a) { return Array.isArray(a) ? a : []; }
    function humanize(wire) {
        var s = str(wire).replace(/[_-]+/g, ' ').trim();
        return s ? s.charAt(0).toUpperCase() + s.slice(1) : '';
    }
    function plural(n, one, many) { return n + ' ' + (n === 1 ? one : (many || one + 's')); }
    function fmt(name, value) { return typeof D[name] === 'function' ? D[name](value) : str(value); }

    // ---- the columns: A, B, C, in an order the person did not choose -------------------------------------------------------------------
    function letters(count) {
        var out = [];
        for (var i = 0; i < count && i < 26; i++) out.push(String.fromCharCode(65 + i));
        return out;
    }

    // A fair shuffle (Fisher-Yates) of the run ids; <rand> gives numbers in [0, 1) (Math.random, or a crypto-backed source). The input is not changed.
    function shuffleIds(ids, rand) {
        var out = arr(ids).slice();
        var next = typeof rand === 'function' ? rand : Math.random;
        for (var i = out.length - 1; i > 0; i--) {
            var j = Math.floor(next() * (i + 1));
            if (j > i) j = i;
            if (j < 0) j = 0;
            var t = out[i]; out[i] = out[j]; out[j] = t;
        }
        return out;
    }

    function sameOrder(a, b) {
        a = arr(a); b = arr(b);
        if (a.length !== b.length) return false;
        for (var i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
        return true;
    }

    // A new order of the same runs that is not the one they had (when there is more than one way to put them).
    function reshuffle(order, rand) {
        order = arr(order);
        if (order.length < 2) return order.slice();
        var next = order;
        for (var tries = 0; tries < 20 && sameOrder(next, order); tries++) next = shuffleIds(order, rand);
        if (sameOrder(next, order)) next = order.slice(1).concat(order.slice(0, 1));   // the dice kept saying "the same": rotate, which is always different
        return next;
    }

    // ---- picking the runs ------------------------------------------------------------------------------------------------------------
    function isFinished(run) { return !!run && run.state === 'finished'; }
    function examinedOf(run) { return run && run.counters && isNum(run.counters.candidatesExamined) ? run.counters.candidatesExamined : 0; }

    // A model's name, and whether the built-in rules ran beside it (a run from before the switch ran them).
    function withRules(run) { return run.modelLabel + (run.useRules === false ? ' (AI only, no rules)' : ' + rules'); }

    // One trial run as the picker lists it. The model's name is part of it only when the person asked to see names.
    function runOption(run, revealed) {
        run = run || {};
        var c = run.counters || {};
        var when = fmt('formatWhen', run.startedAt || run.createdAt);
        var title = 'Trial run, ' + when;
        if (revealed && run.modelLabel) title += ' - ' + withRules(run);
        else if (revealed && run.useModel === false) title += ' - rules only';
        var bits = [plural(examinedOf(run), 'candidate') + ' examined'];
        if (isNum(c.adviceCreated)) bits.push(plural(c.adviceCreated, 'advice', 'advice'));
        if (isNum(c.modelCalls)) bits.push(plural(c.modelCalls, 'call'));
        if (isNum(c.promptTokens) || isNum(c.completionTokens)) bits.push(fmt('formatTokens', (c.promptTokens || 0) + (c.completionTokens || 0)) + ' tokens');
        var oc = typeof D.outcomeInfo === 'function' ? D.outcomeInfo(run) : { label: str(run.state), variant: 'neutral', text: '' };
        var note = '';
        if (!isFinished(run)) note = 'Still going: its column fills up as it works.';
        else if (examinedOf(run) === 0) note = 'It examined nothing, so there is nothing to compare.';
        return { id: str(run.id), title: title, sub: bits.join(' \u00B7 '), badge: { label: oc.label, variant: oc.variant, text: oc.text || '' }, note: note, finished: isFinished(run) };
    }

    // The selection after <id> was ticked or unticked: never more than three, never twice, order of ticking kept.
    function toggleSelection(selected, id, on) {
        var out = arr(selected).filter(function (x) { return x !== id; });
        if (on) {
            if (arr(selected).indexOf(id) >= 0) return arr(selected).slice();
            if (out.length >= MAX_RUNS) return arr(selected).slice();
            out.push(id);
        }
        return out;
    }

    // What the picker tells the person and whether Compare may be pressed. <runs> are the trial runs as listed (to name a run still going).
    function pickState(selected, runs) {
        selected = arr(selected);
        var byId = {};
        arr(runs).forEach(function (r) { if (r && r.id) byId[r.id] = r; });
        var count = selected.length;
        var hint;
        if (count === 0) hint = 'Tick two or three trial runs made on the same notes (best with the same sample seed, so they examine the same candidates in the same order).';
        else if (count === 1) hint = 'Tick one more run.';
        else if (count < MAX_RUNS) hint = plural(count, 'run') + ' ticked. Compare them, or add a third.';
        else hint = 'Three runs ticked: the most the table holds.';
        var warnings = [];
        selected.forEach(function (id) {
            var r = byId[id];
            if (r && !isFinished(r)) warnings.push('A ticked run is still going; reload the table to see more of it.');
            else if (r && examinedOf(r) === 0) warnings.push('A ticked run examined nothing.');
        });
        return { count: count, canCompare: count >= MIN_RUNS && count <= MAX_RUNS, full: count >= MAX_RUNS, hint: hint, warnings: warnings.filter(function (w, i, all) { return all.indexOf(w) === i; }) };
    }

    // The ids of the newest finished trial runs that examined something: what "pick the latest three" ticks. <runs> are newest first, as the API lists them.
    function latestPick(runs) {
        return arr(runs).filter(function (r) { return r && r.id && isFinished(r) && examinedOf(r) > 0; }).slice(0, MAX_RUNS).map(function (r) { return r.id; });
    }

    // ---- a cell: what one run made of one candidate ---------------------------------------------------------------------------------
    // tone: 'none' (the run never looked), 'error' (it looked and got no answer), 'nothing' (it looked and proposed nothing), 'advice'.
    function cellView(result) {
        if (!result || typeof result !== 'object') return { tone: 'none', empty: true, text: 'Not examined', variant: 'neutral', verdict: '', score: null, band: '', kind: null, kindLabel: '', summary: '' };
        var outcome = result.outcome === 'advice' || result.outcome === 'error' ? result.outcome : 'nothing';
        var verdict = str(result.verdict);
        var score = isNum(result.score) ? result.score : null;
        var kind = result.kind ? str(result.kind) : null;
        // An older node does not say the outcome: a kind means there was advice, no verdict means it failed.
        if (!result.outcome) outcome = kind ? 'advice' : (verdict ? 'nothing' : 'error');
        var text = outcome === 'error' ? 'Failed' : (humanize(verdict) || (outcome === 'advice' ? 'Advice' : 'No advice'));
        return {
            tone: outcome, empty: false, text: text, variant: outcome === 'advice' ? 'primary' : outcome === 'error' ? 'danger' : 'neutral',
            verdict: verdict, score: score, band: score == null ? '' : (typeof D.bandOf === 'function' ? D.bandOf(score) : ''),
            kind: kind, kindLabel: kind ? (typeof D.kindInfo === 'function' ? D.kindInfo(kind).label : humanize(kind)) : '', summary: str(result.summary)
        };
    }

    // ---- a row ------------------------------------------------------------------------------------------------------------------------
    // <order> are the run ids as the columns follow them. Everything a row shows or is filtered by is decided here.
    function rowView(row, order, index) {
        row = row || {};
        order = arr(order);
        var cols = letters(order.length);
        var results = row.results || {};
        var cells = order.map(function (id) { return cellView(results[id]); });
        var withVerdict = [];
        var missing = [];
        var errors = [];
        var adviceBy = [];
        cells.forEach(function (c, i) {
            if (c.tone === 'none') missing.push(i);
            else if (c.tone === 'error') errors.push(i);
            else withVerdict.push(i);
            if (c.tone === 'advice') adviceBy.push(i);
        });

        var counts = {};
        withVerdict.forEach(function (i) { var v = cells[i].verdict; counts[v] = (counts[v] || 0) + 1; });
        var kinds = Object.keys(counts);
        var disagree = withVerdict.length >= 2 && kinds.length > 1;
        var agree = withVerdict.length >= 2 && kinds.length === 1;
        var adviceDiffers = withVerdict.length >= 2 && adviceBy.some(function (i) { return withVerdict.indexOf(i) >= 0; }) &&
            withVerdict.some(function (i) { return cells[i].tone !== 'advice'; });
        var foundOnly = adviceDiffers && adviceBy.length === 1 ? adviceBy[0] : -1;

        // Whose verdict is the odd one out: the ones that are not the strict majority; with no strict majority (two against two, or all different) all of them.
        var odd = [];
        if (disagree) {
            var top = 0, topKinds = 0;
            kinds.forEach(function (k) { if (counts[k] > top) { top = counts[k]; topKinds = 1; } else if (counts[k] === top) topKinds++; });
            var majority = topKinds === 1 && top > 1 ? kinds.filter(function (k) { return counts[k] === top; })[0] : null;
            withVerdict.forEach(function (i) { if (majority === null || cells[i].verdict !== majority) odd.push(i); });
        }

        var scores = cells.map(function (c) { return c.score; }).filter(isNum);
        var scoreMax = scores.length ? Math.max.apply(null, scores) : null;
        var scoreMin = scores.length ? Math.min.apply(null, scores) : null;
        var spread = scores.length >= 2 ? scoreMax - scoreMin : 0;

        var flags = [];
        if (foundOnly >= 0) flags.push({ text: 'Only ' + cols[foundOnly] + ' found advice', variant: 'warning', key: 'only-one' });
        else if (disagree) flags.push({ text: 'Different verdicts', variant: 'warning', key: 'disagree' });
        else if (agree) flags.push({ text: 'Same verdict', variant: 'success', key: 'agree' });
        if (missing.length) flags.push({ text: (missing.length === cells.length ? 'Not examined' : 'Not examined by ' + missing.map(function (i) { return cols[i]; }).join(', ')), variant: 'neutral', key: 'partial' });
        if (errors.length) flags.push({ text: 'Failed in ' + errors.map(function (i) { return cols[i]; }).join(', '), variant: 'danger', key: 'error' });

        var objects = typeof D.objectLinks === 'function' ? D.objectLinks(row.objects) : [];
        return {
            key: str(row.candidateKey), index: isNum(index) ? index : 0, task: str(row.task), objects: objects, cells: cells, flags: flags,
            examined: cells.length - missing.length, withVerdict: withVerdict.length, missing: missing, errors: errors, adviceBy: adviceBy,
            disagree: disagree, agree: agree, adviceDiffers: adviceDiffers, foundOnly: foundOnly, odd: odd,
            scoreMax: scoreMax, scoreMin: scoreMin, spread: spread,
            common: missing.length === 0 && cells.length > 0
        };
    }

    // ---- filters, sorting, the sample to judge by hand ------------------------------------------------------------------------------
    var FILTERS = [
        { value: 'all', label: 'All candidates' },
        { value: 'disagree', label: 'Different verdicts' },
        { value: 'advice-differs', label: 'Some found advice, some did not' },
        { value: 'only-one', label: 'Advice from one run only' },
        { value: 'partial', label: 'Not examined by every run' },
        { value: 'errors', label: 'Failed somewhere' },
        { value: 'sample', label: 'Sample to judge by hand (' + SAMPLE_DISAGREEMENTS + ' + ' + SAMPLE_TOP_SCORES + ')' }
    ];
    var SORTS = [
        { value: 'order', label: 'In the order examined' },
        { value: 'spread', label: 'Biggest score gap first' },
        { value: 'top', label: 'Highest score first' }
    ];

    // The 10 biggest disagreements (by score gap, then in table order) and the 10 highest scores among the rest, in table order. Only rows that at least two
    // runs gave a verdict on can be judged against each other, so only those are taken for the disagreements; the top scores come from any row that has one.
    function reviewSample(views, perGroup) {
        views = arr(views);
        var dn = isNum(perGroup) ? perGroup : SAMPLE_DISAGREEMENTS;
        var tn = isNum(perGroup) ? perGroup : SAMPLE_TOP_SCORES;
        var disagreements = views.filter(function (v) { return v.disagree; }).sort(function (a, b) { return (b.spread - a.spread) || (a.index - b.index); }).slice(0, dn);
        var chosen = {};
        disagreements.forEach(function (v) { chosen[v.index] = true; });
        var top = views.filter(function (v) { return !chosen[v.index] && v.scoreMax !== null; })
            .sort(function (a, b) { return (b.scoreMax - a.scoreMax) || (a.index - b.index); }).slice(0, tn);
        top.forEach(function (v) { chosen[v.index] = true; });
        return views.filter(function (v) { return chosen[v.index]; });
    }

    function filterRows(views, mode) {
        views = arr(views);
        switch (mode) {
            case 'disagree': return views.filter(function (v) { return v.disagree; });
            case 'advice-differs': return views.filter(function (v) { return v.adviceDiffers; });
            case 'only-one': return views.filter(function (v) { return v.foundOnly >= 0; });
            case 'partial': return views.filter(function (v) { return v.missing.length > 0; });
            case 'errors': return views.filter(function (v) { return v.errors.length > 0; });
            case 'sample': return reviewSample(views);
            default: return views.slice();
        }
    }

    function filterCounts(views) {
        var out = {};
        FILTERS.forEach(function (f) { out[f.value] = filterRows(views, f.value).length; });
        return out;
    }

    function sortRows(views, sort) {
        var out = arr(views).slice();
        if (sort === 'spread') out.sort(function (a, b) { return (b.spread - a.spread) || (a.index - b.index); });
        else if (sort === 'top') out.sort(function (a, b) { return ((b.scoreMax === null ? -1 : b.scoreMax) - (a.scoreMax === null ? -1 : a.scoreMax)) || (a.index - b.index); });
        else out.sort(function (a, b) { return a.index - b.index; });
        return out;
    }

    // ---- the whole table --------------------------------------------------------------------------------------------------------------
    function columnTitle(letter, run, revealed) {
        if (!revealed) return { letter: letter, text: 'Run ' + letter, sub: '' };
        var model = run && run.modelLabel ? withRules(run) : (run && run.useModel === false ? 'Rules only' : 'No model');
        return { letter: letter, text: letter, sub: model };
    }

    function runFacts(run) {
        run = run || {};
        var c = run.counters || {};
        var facts = [];
        if (isNum(c.modelCalls)) facts.push({ label: 'Model calls', value: String(c.modelCalls) });
        if (isNum(c.promptTokens) || isNum(c.completionTokens)) facts.push({ label: 'Tokens in / out', value: fmt('formatTokens', c.promptTokens || 0) + ' / ' + fmt('formatTokens', c.completionTokens || 0) });
        var ms = typeof D.runDurationMs === 'function' ? D.runDurationMs(run, Date.now()) : null;
        if (ms != null && isFinished(run)) facts.push({ label: 'Time', value: fmt('formatDuration', ms) });
        var oc = typeof D.outcomeInfo === 'function' ? D.outcomeInfo(run) : null;
        if (oc) facts.push({ label: 'Ended', value: oc.label });
        return facts;
    }

    // The response of GET /compare, put in the order of <order> (run ids), read into what the page draws. <revealed>: show the names of the models.
    function tableView(response, order, revealed) {
        response = response || {};
        var runs = arr(response.runs);
        var byId = {};
        runs.forEach(function (r) { if (r && r.id) byId[r.id] = r; });
        var ids = arr(order).filter(function (id) { return byId[id]; });
        runs.forEach(function (r) { if (r && r.id && ids.indexOf(r.id) < 0) ids.push(r.id); });   // a run the order forgot is still a column
        var cols = letters(ids.length);
        var noLog = {};
        arr(response.runsWithoutLog).forEach(function (id) { noLog[id] = true; });

        var rows = arr(response.rows).map(function (row, i) { return rowView(row, ids, i); });

        var columns = ids.map(function (id, i) {
            var run = byId[id];
            var examined = 0, advice = 0, errors = 0, alone = 0, scoreSum = 0, scored = 0;
            rows.forEach(function (v) {
                var c = v.cells[i];
                if (c.tone !== 'none') examined++;
                if (c.tone === 'advice') advice++;
                if (c.tone === 'error') errors++;
                if (v.foundOnly === i) alone++;
                if (c.score !== null) { scoreSum += c.score; scored++; }
            });
            return {
                id: id, letter: cols[i], title: columnTitle(cols[i], run, revealed), run: run, facts: runFacts(run), journalGone: !!noLog[id],
                examined: examined, advice: advice, errors: errors, foundAlone: alone, meanScore: scored ? Math.round(scoreSum / scored) : null
            };
        });

        var common = isNum(response.common) ? response.common : rows.filter(function (v) { return v.common; }).length;
        var basis = isNum(response.agreementBasis) ? response.agreementBasis : common;
        var agreement = isNum(response.agreement) ? response.agreement : 0;
        return {
            columns: columns, rows: rows, common: common, agreementBasis: basis,
            agreementText: basis > 0 ? Math.round(agreement * 100) + '%' : '-',
            agreementNote: basis > 0 ? 'of ' + plural(basis, 'candidate') + ' that every run gave a verdict on' : 'no candidate that every run gave a verdict on',
            disagreements: rows.filter(function (v) { return v.disagree; }).length,
            partial: rows.filter(function (v) { return v.missing.length > 0; }).length,
            journalGone: columns.filter(function (c) { return c.journalGone; }).map(function (c) { return c.letter; }),
            counts: filterCounts(rows)
        };
    }

    function emptyWords(table) {
        if (!table || !table.rows.length) {
            return table && table.journalGone.length
                ? 'The log of ' + (table.journalGone.length === 1 ? 'run ' : 'runs ') + table.journalGone.join(', ') + ' is gone: a run keeps its log for seven days, and this table is built from it.'
                : 'The runs have no examined candidates to compare.';
        }
        return '';
    }

    root.bmbDreamCompare = {
        MIN_RUNS: MIN_RUNS, MAX_RUNS: MAX_RUNS, FILTERS: FILTERS, SORTS: SORTS,
        letters: letters, shuffleIds: shuffleIds, reshuffle: reshuffle, sameOrder: sameOrder,
        runOption: runOption, toggleSelection: toggleSelection, pickState: pickState, latestPick: latestPick,
        cellView: cellView, rowView: rowView, reviewSample: reviewSample, filterRows: filterRows, filterCounts: filterCounts, sortRows: sortRows,
        columnTitle: columnTitle, runFacts: runFacts, tableView: tableView, emptyWords: emptyWords
    };
})(window);
