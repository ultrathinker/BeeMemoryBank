// Runs wwwroot/js/pages/dream-compare.js (the "Compare trial runs" section of the Dream page) in a fake page: a small DOM that records what is written into it, and
// a scripted bmbDream.api that answers like the API does (or like a stub, or an error). Nobody can look at the page, so this is what it DOES that is checked: what it
// asks the node, what it puts on the page, that the models stay hidden until the switch is on, that a late answer after the page was left is dropped, and that no part
// is ever left blank. It prints one JSON line {"ok": true|false, "detail": "..."} for the scenario named on the command line. The scenarios are the ones of
// DreamCompareModelTests.cs (the async ones).
//
//   node dream-compare-page-harness.js <dream-compare.js> <dream-model.js> <dream-compare-model.js> <Dream.cshtml> <scenario>
'use strict';
const fs = require('fs');
const vm = require('vm');

const [, , scriptPath, modelPath, compareModelPath, cshtmlPath, scenario] = process.argv;

// ---------------------------------------------------------------- the page: the ids of the markup, and which of them start hidden
const cshtml = fs.readFileSync(cshtmlPath, 'utf8');
const pageIds = new Set();
const startsHidden = new Set();
for (const tag of cshtml.match(/<[a-z][^<>]*>/gi) || []) {
    const id = /\bid="([^"]+)"/.exec(tag);
    if (!id) continue;
    pageIds.add(id[1]);
    if (/\shidden(\s|>|\/)/.test(tag)) startsHidden.add(id[1]);
}

const problems = [];
process.on('unhandledRejection', (e) => problems.push('an unhandled rejection: ' + (e && e.message ? e.message : e)));
async function flush() { for (let i = 0; i < 60; i++) await Promise.resolve(); }

// ---------------------------------------------------------------- a small DOM
class Box {   // a ticked-or-not sl-checkbox of the picker, made from the markup written into it
    constructor(id, checked, disabled) { this.dataset = { cmpRun: id }; this.checked = checked; this.disabled = disabled; }
    closest(sel) { return sel === '[data-cmp-run]' ? this : null; }
}

class El {
    constructor(id) {
        this.id = id;
        this._html = '';
        this.textContent = '';
        this.hidden = startsHidden.has(id);
        this.disabled = false;
        this.loading = false;
        this.checked = false;
        this.value = '';
        this.listeners = {};
        this.attrs = {};
    }
    get innerHTML() { return this._html; }
    set innerHTML(v) { this._html = String(v); }
    addEventListener(type, fn) { (this.listeners[type] = this.listeners[type] || []).push(fn); }
    fire(type, event) { for (const fn of this.listeners[type] || []) fn(event || { target: this }); }
    setAttribute(n, v) { this.attrs[n] = String(v); }
    // The checkboxes the markup written into the picker holds (re-read whenever it is asked, as a real page would).
    querySelectorAll(sel) {
        if (this.id !== 'dream-cmp-picker' || sel !== 'sl-checkbox') return [];
        const boxes = this.boxes = this.boxes || {};
        const found = [];
        for (const m of this._html.matchAll(/<sl-checkbox data-cmp-run="([^"]+)"([^>]*)>/g)) {
            const id = m[1];
            // A new element for new markup; the same element (with what the person and the script did to it) while the markup is the same.
            if (!boxes[id] || boxes[id].html !== this._html) boxes[id] = Object.assign(new Box(id, / checked/.test(m[2]), / disabled/.test(m[2])), { html: this._html });
            found.push(boxes[id]);
        }
        return found;
    }
}

const els = {};
const missing = [];
const docListeners = {};
const document = {
    getElementById(id) {
        if (!pageIds.has(id)) { missing.push(id); return null; }
        return els[id] || (els[id] = new El(id));
    },
    addEventListener(type, fn) { (docListeners[type] = docListeners[type] || []).push(fn); }
};

// ---------------------------------------------------------------- a scripted api
const calls = [];
const routes = {};
const api = {
    get(path, params) {
        calls.push({ path, params: params || {} });
        const route = routes[path];
        if (!route) return Promise.reject(new Error('no route for ' + path));
        try { return Promise.resolve(route(params || {}, calls.filter((c) => c.path === path).length)); } catch (e) { return Promise.reject(e); }
    }
};

let randomValues = [0.5];
let randomIndex = 0;
function setRandom(values) { randomValues = values; randomIndex = 0; }
const sandbox = {
    window: { bmbDream: { api } },
    document,
    console
};
sandbox.window.window = sandbox.window;
vm.createContext(sandbox);
vm.runInContext('Math.random = function () { return window.__rand(); };', sandbox);
sandbox.window.__rand = () => randomValues[Math.min(randomIndex++, randomValues.length - 1)];
for (const p of [modelPath, compareModelPath]) vm.runInContext(fs.readFileSync(p, 'utf8'), sandbox, { filename: p });

function say(ok, detail) { console.log(JSON.stringify({ ok, detail })); process.exit(0); }
function assert(cond, message) { if (!cond) throw new Error(message); }
function eq(actual, expected, what) {
    const a = JSON.stringify(actual), e = JSON.stringify(expected);
    if (a !== e) throw new Error(what + ': expected ' + e + ' but got ' + a);
}
function has(text, part, what) { if (String(text).indexOf(part) < 0) throw new Error(what + ': "' + part + '" is not in "' + String(text).slice(0, 400) + '"'); }
function lacks(text, part, what) { if (String(text).indexOf(part) >= 0) throw new Error(what + ': "' + part + '" should not be in the text'); }

// ---------------------------------------------------------------- the data
const NOW = Date.parse('2026-11-10T12:00:00Z');
const ago = (s) => new Date(NOW - s * 1000).toISOString();
const R1 = '11111111-1111-1111-1111-111111111111';
const R2 = '22222222-2222-2222-2222-222222222222';
const R3 = '33333333-3333-3333-3333-333333333333';
const A1 = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const A2 = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const LABELS = { [R1]: 'Alpha-Model-One', [R2]: 'Beta-Model-Two', [R3]: 'Gamma-Model-Three' };

function run(id, extra) {
    return Object.assign({
        id, trigger: 'manual', mode: 'trial', state: 'finished', outcome: 'completed', useModel: true, modelLabel: LABELS[id] || 'Other-Model',
        createdAt: ago(3600), startedAt: ago(3600), finishedAt: ago(3000),
        counters: { candidatesExamined: 10, adviceCreated: 3, modelCalls: 10, promptTokens: 48000, completionTokens: 2500, errors: 0 },
        limits: { maxAdvice: 50, maxMinutes: 10, maxModelCalls: 200, maxTokens: 1500000 }
    }, extra || {});
}
function res(verdict, score, kind, outcome, summary) {
    return { verdict, kind: kind || null, score, summary: summary === undefined ? (verdict ? 'The model says ' + verdict : null) : summary, outcome: outcome || (kind ? 'advice' : 'nothing') };
}
function row(key, results, extra) {
    return Object.assign({
        candidateKey: key, task: 'pair_verdict',
        objects: [{ type: 'article', id: A1, title: 'Backup policy', treePath: '/Ops' }, { type: 'article', id: A2, title: 'Backup policy (old)', treePath: '/Ops/Old' }],
        results
    }, extra || {});
}
const RUNS = [run(R1), run(R2), run(R3)];
function table(extra) {
    return Object.assign({
        runs: RUNS,
        rows: [
            row('agree', { [R1]: res('duplicate', 90, 'duplicate'), [R2]: res('duplicate', 80, 'duplicate'), [R3]: res('duplicate', 85, 'duplicate') }),
            row('differ', { [R1]: res('duplicate', 88, 'duplicate'), [R2]: res('related', 20), [R3]: res('related', 15) }, { objects: [{ type: 'article', id: A1, title: 'Kids puzzles', treePath: '/Kids' }] }),
            row('short', { [R1]: res('overlap', 60, 'overlap'), [R2]: res('overlap', 64, 'overlap'), [R3]: null })
        ],
        common: 2, agreement: 0.5, agreementBasis: 2, runsWithoutLog: []
    }, extra || {});
}
function standardRoutes() {
    routes['/runs'] = () => ({ total: 3, page: 1, pageSize: 20, items: RUNS });
    // The node answers with the runs that were asked for, in the order they were asked for.
    routes['/compare'] = (params) => table({ runs: String(params.runs || '').split(',').map((id) => RUNS.find((r) => r.id === id)).filter(Boolean) });
}

async function load(init) {
    if (init) init();
    vm.runInContext(fs.readFileSync(scriptPath, 'utf8'), sandbox, { filename: scriptPath });
    await flush();
}
const boxes = () => els['dream-cmp-picker'].querySelectorAll('sl-checkbox');
async function tick(id, on) {
    const box = boxes().find((b) => b.dataset.cmpRun === id);
    assert(box, 'no checkbox for ' + id);
    box.checked = on;
    els['dream-cmp-picker'].fire('sl-change', { target: box });
    await flush();
}
async function pressCompare() { els['dream-cmp-go'].fire('click'); await flush(); }
const result = () => (els['dream-cmp-summary'] ? els['dream-cmp-summary'].innerHTML : '') + (els['dream-cmp-rows'] ? els['dream-cmp-rows'].innerHTML : '');
function clickTarget(attr, node) {
    return { target: { closest: (sel) => (sel === '[' + attr + ']' ? node : null) } };
}
async function reveal(on) { const sw = els['dream-cmp-reveal']; sw.checked = on; sw.fire('sl-change', { target: sw }); await flush(); }
async function choose(id, value) { const el = els[id]; el.value = value; el.fire('sl-change', { target: el }); await flush(); }
const columnLetters = () => [...result().matchAll(/<div class="dream-cmp-h" role="columnheader"><span class="dream-cmp-letter">(\w)<\/span>(?:<span class="dream-cmp-model">([^<]*)<\/span>)?/g)].map((m) => [m[1], m[2] || '']);

const scenarios = {
    async the_page_asks_for_trial_runs_only_and_lists_them_without_their_models() {
        standardRoutes();
        await load();
        eq(calls[0], { path: '/runs', params: { page: 1, pageSize: 20, mode: 'trial' } }, 'the first call');
        eq(boxes().map((b) => b.dataset.cmpRun), [R1, R2, R3], 'one checkbox per run');
        const picker = els['dream-cmp-picker'].innerHTML;
        for (const name of Object.values(LABELS)) lacks(picker, name, 'the picker is blind');
        has(picker, 'Trial run', 'the time tells them apart');
        has(picker, '10 candidates examined', 'the numbers are there');
        eq(els['dream-cmp-go'].disabled, true, 'nothing ticked');
        eq(els['dream-cmp-latest'].hidden, false, 'three finished runs: the shortcut is offered');
        eq(missing, [], 'every id the script asks for is in the markup');
    },

    async two_ticks_enable_compare_and_a_full_selection_shuts_the_other_boxes() {
        standardRoutes();
        routes['/runs'] = () => ({ items: [run(R1), run(R2), run(R3), run('44444444-4444-4444-4444-444444444444')] });
        await load();
        await tick(R1, true);
        eq(els['dream-cmp-go'].disabled, true, 'one tick');
        has(els['dream-cmp-hint'].textContent, 'one more', 'hint');
        await tick(R2, true);
        eq(els['dream-cmp-go'].disabled, false, 'two ticks');
        await tick(R3, true);
        eq(boxes().filter((b) => !b.checked).map((b) => b.disabled), [true], 'the fourth box is shut once three are ticked');
        // A fourth tick that gets through anyway is taken back.
        const fourth = boxes().find((b) => !b.checked);
        fourth.disabled = false;
        await tick(fourth.dataset.cmpRun, true);
        eq(boxes().filter((b) => b.checked).length, 3, 'three at most');
        await tick(R2, false);
        eq([els['dream-cmp-go'].disabled, boxes().filter((b) => b.disabled).length], [false, 0], 'untick opens the rest');
    },

    async compare_asks_for_the_runs_in_a_shuffled_order_and_draws_the_table() {
        standardRoutes();
        setRandom([0.99, 0.0]);   // a fixed shuffle
        await load();
        await tick(R1, true); await tick(R2, true); await tick(R3, true);
        await pressCompare();
        const ask = calls.filter((c) => c.path === '/compare');
        eq(ask.length, 1, 'one request');
        const asked = ask[0].params.runs.split(',');
        eq(asked.slice().sort(), [R1, R2, R3], 'every ticked run, once');
        const expected = sandbox.window.bmbDreamCompare.shuffleIds([R1, R2, R3], (() => { const v = [0.99, 0.0]; let i = 0; return () => v[Math.min(i++, 1)]; })());
        eq(asked, expected, 'the order is the shuffle');
        const html = result();
        has(html, 'Common candidates', 'summary'); has(html, 'Agreement', 'summary'); has(html, '50%', 'the server agreement');
        has(html, 'Backup policy (old)', 'the notes are named'); has(html, 'href="/Article/View?id=' + A1 + '"', 'and linked');
        has(html, 'Different verdicts', 'the disagreement is flagged'); has(html, 'is-disagree', 'row class'); has(html, 'is-odd', 'the odd cell');
        has(html, 'Not examined by C', 'the short row'); has(html, 'role="table"', 'accessible');
        eq(els['dream-cmp-tools'].hidden, false, 'the tools appear');
        eq(els['dream-cmp-go'].loading, false, 'the button stops spinning');
        eq(columnLetters().map((c) => c[0]), ['A', 'B', 'C'], 'lettered');
    },

    async the_models_stay_hidden_until_the_switch_is_turned_on() {
        standardRoutes();
        await load();
        await tick(R1, true); await tick(R2, true);
        await pressCompare();
        for (const name of Object.values(LABELS)) { lacks(result(), name, 'result'); lacks(els['dream-cmp-picker'].innerHTML, name, 'picker'); }
        eq(columnLetters().every((c) => c[1] === ''), true, 'no model under a letter');
        await reveal(true);
        const shown = columnLetters();
        eq(shown.length, 2, 'two columns');
        assert(shown.every((c) => Object.values(LABELS).includes(c[1].replace(/ \+ rules$/, ''))), 'names appear under the letters, with the rules that ran beside them: ' + JSON.stringify(shown));
        has(els['dream-cmp-picker'].innerHTML, 'Model', 'the picker names them too');
        eq(calls.filter((c) => c.path === '/compare').length, 1, 'the switch asks nothing');
        await reveal(false);
        for (const name of Object.values(LABELS)) { lacks(result(), name, 'result again'); lacks(els['dream-cmp-picker'].innerHTML, name, 'picker again'); }
    },

    async shuffling_the_columns_reorders_them_without_asking_again() {
        standardRoutes();
        setRandom([0.3, 0.6, 0.1, 0.8]);
        await load();
        await tick(R1, true); await tick(R2, true); await tick(R3, true);
        await pressCompare();
        await reveal(true);
        const before = columnLetters().map((c) => c[1]);
        els['dream-cmp-shuffle'].fire('click');
        await flush();
        const after = columnLetters().map((c) => c[1]);
        eq(after.slice().sort(), before.slice().sort(), 'the same three models');
        assert(JSON.stringify(after) !== JSON.stringify(before), 'the order changed');
        eq(columnLetters().map((c) => c[0]), ['A', 'B', 'C'], 'the letters stay A B C');
        eq(calls.filter((c) => c.path === '/compare').length, 1, 'no new request');
    },

    async the_filter_and_the_sort_change_the_rows() {
        standardRoutes();
        await load();
        await tick(R1, true); await tick(R2, true); await tick(R3, true);
        await pressCompare();
        const keysShown = () => [...result().matchAll(/data-cmp-key="([^"]+)"/g)].map((m) => m[1]);
        eq(keysShown(), ['agree', 'differ', 'short'], 'all rows');
        has(els['dream-cmp-filter'].innerHTML, 'Different verdicts (1)', 'the filter says how many');
        await choose('dream-cmp-filter', 'disagree');
        eq(keysShown(), ['differ'], 'disagreements only');
        await choose('dream-cmp-filter', 'partial');
        eq(keysShown(), ['short'], 'not examined by all');
        await choose('dream-cmp-filter', 'errors');
        has(result(), 'No candidate matches this filter', 'an empty filter says so');
        await choose('dream-cmp-filter', 'all');
        await choose('dream-cmp-sort', 'spread');
        eq(keysShown()[0], 'differ', 'the biggest gap first');
    },

    async a_row_opens_to_what_each_run_said() {
        standardRoutes();
        await load();
        await tick(R1, true); await tick(R2, true);
        await pressCompare();
        has(result(), 'The model says duplicate', 'the explanation is in the row, hidden');
        has(result(), 'class="dream-cmp-more" hidden', 'closed at first');
        const more = { hidden: true };
        const toggle = { attrs: {}, closest: (sel) => (sel === '[data-cmp-toggle]' ? toggle : sel === '.dream-cmp-row' ? rowEl : null), setAttribute(n, v) { this.attrs[n] = v; } };
        const rowEl = { querySelector: (sel) => (sel === '.dream-cmp-more' ? more : null) };
        els['dream-compare-section'].fire('click', { target: toggle });
        eq([more.hidden, toggle.attrs['aria-expanded']], [false, 'true'], 'opened');
        els['dream-compare-section'].fire('click', { target: toggle });
        eq([more.hidden, toggle.attrs['aria-expanded']], [true, 'false'], 'closed again');
    },

    async a_failed_comparison_says_why_and_tries_again_on_request() {
        standardRoutes();
        let fail = true;
        routes['/compare'] = () => {
            if (fail) { const e = new Error('Request failed'); e.status = 400; e.body = { error: 'Only trial runs can be compared.', code: 'invalid_request' }; throw e; }
            return table();
        };
        await load();
        await tick(R1, true); await tick(R2, true);
        await pressCompare();
        has(result(), 'Only trial runs can be compared.', 'the words of the node');
        has(result(), 'data-cmp-retry="compare"', 'a retry button');
        eq(els['dream-cmp-go'].loading, false, 'the button stops spinning');
        eq(els['dream-cmp-tools'].hidden, true, 'no tools without a table');
        fail = false;
        els['dream-compare-section'].fire('click', clickTarget('data-cmp-retry', { dataset: { cmpRetry: 'compare' } }));
        await flush();
        has(result(), 'Common candidates', 'the table after the retry');
    },

    async a_stub_or_an_empty_answer_never_leaves_a_blank_page() {
        routes['/runs'] = () => ({});
        routes['/compare'] = () => ({});
        await load();
        has(els['dream-cmp-picker'].innerHTML, 'No trial runs yet', 'no runs');
        eq(els['dream-cmp-go'].disabled, true, 'nothing to compare');
        // The runs list fails: the words say so, with a retry.
        routes['/runs'] = () => { const e = new Error('boom'); e.status = 501; e.body = { error: 'This Dream call is not available yet.', code: 'not_implemented' }; throw e; };
        els['dream-cmp-refresh'].fire('click');
        await flush();
        has(els['dream-cmp-picker'].innerHTML, 'not available yet', 'the node said so');
        has(els['dream-cmp-picker'].innerHTML, 'data-cmp-retry="runs"', 'retry');
        // A comparison that comes back empty says why.
        standardRoutes();
        els['dream-cmp-refresh'].fire('click');
        await flush();
        routes['/compare'] = () => ({});
        await tick(R1, true); await tick(R2, true);
        await pressCompare();
        has(result(), 'no examined candidates', 'an empty table is explained');
        routes['/compare'] = () => table({ rows: [], runsWithoutLog: [R2] });
        await pressCompare();
        has(result(), 'is gone', 'the lost log is named');
        has(result(), 'seven days', 'and why');
    },

    async what_the_node_says_is_escaped() {
        standardRoutes();
        routes['/compare'] = () => table({ rows: [row('xss', { [R1]: res('duplicate', 90, 'duplicate', 'advice', '<img src=x onerror=alert(1)>'), [R2]: res('duplicate', 80, 'duplicate') }, { objects: [{ type: 'article', id: A1, title: '<script>alert(1)</script>', treePath: '/<b>' }] })] });
        await load();
        await tick(R1, true); await tick(R2, true);
        await pressCompare();
        lacks(result(), '<script>', 'a title');
        lacks(result(), '<img src=x', 'an explanation');
        has(result(), '&lt;script&gt;', 'is shown as text');
    },

    async a_late_answer_after_the_page_was_left_is_dropped() {
        standardRoutes();
        let release;
        routes['/compare'] = () => new Promise((resolve) => { release = () => resolve(table()); });
        await load();
        await tick(R1, true); await tick(R2, true);
        await pressCompare();
        const waiting = result();
        has(waiting, 'dream-loading', 'waiting');
        (docListeners['bmb:page-cleanup'] || []).forEach((fn) => fn());
        release();
        await flush();
        eq(result(), waiting, 'the late answer wrote nothing');
        eq(els['dream-cmp-tools'].hidden, true, 'and showed nothing');
    },

    async many_rows_are_drawn_in_batches() {
        standardRoutes();
        const rows = [];
        for (let i = 0; i < 230; i++) rows.push(row('r' + i, { [R1]: res('duplicate', 50, 'duplicate'), [R2]: res('duplicate', 50, 'duplicate'), [R3]: res('duplicate', 50, 'duplicate') }));
        routes['/compare'] = () => table({ rows, common: 230, agreement: 1, agreementBasis: 230 });
        await load();
        await tick(R1, true); await tick(R2, true);
        await pressCompare();
        const count = () => (result().match(/data-cmp-key=/g) || []).length;
        eq(count(), 100, 'a hundred rows first');
        has(result(), '100 of 230 candidates', 'it says how many');
        has(result(), 'data-cmp-more-rows', 'more on request');
        els['dream-compare-section'].fire('click', clickTarget('data-cmp-more-rows', {}));
        eq(count(), 200, 'two hundred');
        els['dream-compare-section'].fire('click', clickTarget('data-cmp-more-rows', {}));
        eq(count(), 230, 'all');
        lacks(result(), 'data-cmp-more-rows', 'nothing more to show');
    },

    async the_latest_runs_can_be_ticked_in_one_press() {
        standardRoutes();
        await load();
        els['dream-cmp-latest'].fire('click');
        await flush();
        eq(boxes().filter((b) => b.checked).map((b) => b.dataset.cmpRun), [R1, R2, R3], 'the three newest');
        eq(els['dream-cmp-go'].disabled, false, 'ready');
    }
};

(async () => {
    try {
        if (!scenarios[scenario]) say(false, 'no such scenario: ' + scenario);
        await scenarios[scenario]();
        await flush();
        if (problems.length) say(false, problems.join('; '));
        say(true, 'ok');
    } catch (e) {
        say(false, e && e.message ? e.message : String(e));
    }
})();
