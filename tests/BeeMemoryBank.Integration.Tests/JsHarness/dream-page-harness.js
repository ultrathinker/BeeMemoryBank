// Runs wwwroot/js/pages/dream.js (the Dream page) in a fake page: a small DOM that records what is written into it, timers that move when the test says so,
// and a scripted fetch that answers like the API does (or like a stub, or an error). Nobody can look at the page, so this is what it DOES that is checked: what it
// asks the node, what it puts on the page, what it sends when a button is pressed, and that no part is ever left blank. It prints one JSON line
// {"ok": true|false, "detail": "..."} for the scenario named on the command line. The scenarios are the ones of DreamPageScriptTests.cs.
//
//   node dream-page-harness.js <dream.js> <dream-model.js> <Dream.cshtml> <scenario>
//
// The scenarios of the hand-off ("Open in AI chat" and "Show prompt") run the real dream.js together with pages/dream-handoff.js, which is found next to
// dream.js; they add a fake window.open, location, navigator and document.execCommand to the page.
'use strict';
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const [, , dreamPath, modelPath, cshtmlPath, scenario] = process.argv;

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

// ---------------------------------------------------------------- timers that move when told to
let clock = 0;
let nextTimer = 1;
const timers = new Map();
const problems = [];
const fakeSetTimeout = (fn, ms) => { const id = nextTimer++; timers.set(id, { id, at: clock + (ms || 0), fn }); return id; };
const fakeClearTimeout = (id) => { timers.delete(id); };
const openIntervals = new Set();
const fakeSetInterval = (fn) => { const id = nextTimer++; openIntervals.add(id); return id; };
const fakeClearInterval = (id) => { openIntervals.delete(id); };
async function flush() { for (let i = 0; i < 80; i++) await Promise.resolve(); }
async function advance(ms) {
    const target = clock + ms;
    for (;;) {
        let next = null;
        for (const t of timers.values()) if (t.at <= target && (!next || t.at < next.at || (t.at === next.at && t.id < next.id))) next = t;
        if (!next) break;
        timers.delete(next.id);
        clock = next.at;
        try { next.fn(); } catch (e) { problems.push('a timer threw: ' + e.message); }
        await flush();
    }
    clock = target;
    await flush();
}
process.on('unhandledRejection', (e) => problems.push('an unhandled rejection: ' + (e && e.message ? e.message : e)));

// ---------------------------------------------------------------- a small DOM
class El {
    constructor(id, tag) {
        this.id = id || '';
        this.localName = tag || 'div';
        this._html = '';
        this.textContent = '';
        this.hidden = startsHidden.has(this.id);
        this.disabled = false;
        this.value = '';
        this.checked = false;
        this.placeholder = '';
        this.className = '';
        this.dataset = {};
        this.listeners = {};
        this.attrs = {};
        this.loading = false;
        this.style = {};
        this.shown = 0;
        this.hides = 0;
        this.insertions = 0;
        this.first = null;
        this.subs = {};
        this.closests = {};
        this.replacedWith = null;
        this.removed = false;
    }
    get innerHTML() { return this._html; }
    set innerHTML(v) { this._html = String(v); this.first = null; }
    get children() { return this._html ? [{}] : []; }
    get firstChild() { return this.first; }
    get firstElementChild() {
        const n = new El('', 'article'); n.outerHTML = this._html; n.dataset = {};
        if (/class="dream-card-details"/.test(this._html)) n.subs['.dream-card-details'] = new El('', 'sl-details');   // the "Why" fold of a card
        return n;
    }
    get classList() {
        const el = this;
        return {
            add(c) { if (!el.className.split(/\s+/).includes(c)) el.className = (el.className + ' ' + c).trim(); },
            remove(c) { el.className = el.className.split(/\s+/).filter((x) => x && x !== c).join(' '); },
            contains(c) { return el.className.split(/\s+/).includes(c); }
        };
    }
    setAttribute(n, v) { this.attrs[n] = String(v); }
    getAttribute(n) { return n in this.attrs ? this.attrs[n] : null; }
    hasAttribute(n) { return n in this.attrs; }
    appendChild(c) { this.appended = (this.appended || []).concat([c]); return c; }
    addEventListener(type, fn) { (this.listeners[type] = this.listeners[type] || []).push(fn); }
    fire(type, event) { for (const fn of this.listeners[type] || []) fn(event || { target: this }); }
    insertBefore(node, ref) { this.insertions++; this._html = (node.outerHTML || '') + this._html; this.first = node; }
    insertAdjacentHTML(pos, html) { this._html += html; }
    // cardNodes: the cards drawn in a list, as nodes (a scenario fills them from the html with cardsDrawnIn)
    querySelector(sel) {
        const m = /^\[data-advice-id="([^"]+)"\]$/.exec(sel);
        if (m && this.cardNodes) return this.cardNodes.find((n) => n.dataset.adviceId === m[1]) || null;
        return this.subs[sel] || null;
    }
    querySelectorAll(sel) {
        if (sel === '[data-advice-id]' && this.cardNodes) return this.cardNodes;
        return this.subs[sel] ? [this.subs[sel]] : [];
    }
    closest(sel) { return this.closests[sel] || null; }
    show() { this.shown++; }
    hide() { this.hides++; }
    toast() { this.toasted = true; }
    replaceWith(node) { this.replacedWith = node; }
    remove() { this.removed = true; }
}

const els = {};
let pageGone = false;
const missing = [];
const cards = {};          // data-advice-id -> the nodes that show it
const rows = {};           // data-run-id -> { row, stats }
const docListeners = {};
const scope = new El('', 'div');
const body = new El('body', 'body');
const document = {
    hidden: false,
    body,
    getElementById(id) {
        if (pageGone) return null;   // the page was replaced: whatever still asks for its elements gets nothing, and throws
        if (!pageIds.has(id)) { missing.push(id); return null; }
        return els[id] || (els[id] = new El(id));
    },
    querySelector(sel) {
        if (sel === 'input[name="__RequestVerificationToken"]') return { value: 'token-1' };
        if (sel === '.dream-scope') return scope;
        let m = /^\.dream-run\[data-run-id="([^"]+)"\] \.dream-run-stats$/.exec(sel);
        if (m) return (rows[m[1]] = rows[m[1]] || new El('', 'div'));
        m = /^\.dream-run\[data-run-id="([^"]+)"\]$/.exec(sel);
        if (m) { if (!rows[m[1]]) return null; rows[m[1]].dataset = { runId: m[1] }; return rows[m[1]]; }
        return null;
    },
    querySelectorAll(sel) {
        const m = /^\[data-advice-id="([^"]+)"\]$/.exec(sel);
        if (m) return cards[m[1]] || [];
        return [];
    },
    createElement(tag) { return new El('', tag); },
    createTextNode(text) { const n = new El('', '#text'); n.textContent = String(text); return n; },
    addEventListener(type, fn) { (docListeners[type] = docListeners[type] || []).push(fn); },
    removeEventListener(type, fn) { docListeners[type] = (docListeners[type] || []).filter((f) => f !== fn); }
};

// ---------------------------------------------------------------- a scripted fetch
const calls = [];
const routes = {};                      // 'GET /status' -> function (call) -> { status, body } | body
let failEverything = null;              // 'network' | { status, body }
const unmatched = [];
function route(method, path, reply) { routes[method + ' ' + path] = reply; }

function fakeFetch(url, init) {
    init = init || {};
    const u = new URL(url, 'http://node');
    const method = (init.method || 'GET').toUpperCase();
    const path = u.pathname.replace('/api-proxy/dream', '') || '/';
    const query = Object.fromEntries(u.searchParams.entries());
    let bodyObj = null;
    try { bodyObj = init.body ? JSON.parse(init.body) : null; } catch (e) { bodyObj = init.body; }
    const call = { method, path, query, body: bodyObj, headers: init.headers || {}, url };
    calls.push(call);
    if (failEverything === 'network') return Promise.reject(new Error('network down'));
    let reply;
    if (failEverything) reply = failEverything;
    else {
        const fn = routes[method + ' ' + path];
        if (!fn) { unmatched.push(method + ' ' + path); reply = { status: 404, body: { error: 'harness: no route for ' + method + ' ' + path } }; }
        else reply = typeof fn === 'function' ? fn(call) : fn;
    }
    // a route may answer later: it returns a promise of its answer
    if (reply && typeof reply.then === 'function') return reply.then(toResponse);
    return Promise.resolve(toResponse(reply));
}
function toResponse(reply) {
    if (!(reply && typeof reply.status === 'number' && 'body' in reply)) reply = { status: 200, body: reply };   // an answer is { status: 500, body } or just the body
    const text = reply.body === undefined || reply.body === null ? '' : JSON.stringify(reply.body);
    return { ok: reply.status >= 200 && reply.status < 300, status: reply.status, text: () => Promise.resolve(text) };
}

// bmbBusy as the page uses it (bmb-busy.js has its own tests): request() turns a refusal into an Error with the node's words; run() locks nothing here.
const busy = { results: [], runs: 0 };
const bmbBusy = {
    request(url, init) {
        return fakeFetch(url, init).then((response) => response.text().then((text) => {
            let parsed = null;
            try { parsed = text ? JSON.parse(text) : null; } catch (e) { parsed = null; }
            if (response.ok) return { status: response.status, body: parsed, text };
            const said = parsed && typeof parsed.error === 'string' ? parsed.error : '';   // ErrorResponse(error, code)
            const error = new Error(said || ('The node answered ' + response.status + '.'));
            error.status = response.status;
            error.body = parsed;
            throw error;
        }), () => { throw new Error('The node did not answer. Check that it is running and try again.'); });
    },
    run(target, work, options) {
        options = options || {};
        busy.runs++;
        return Promise.resolve().then(work).then(() => true, (error) => {
            if (options.error) options.error(error); else busy.results.push([false, error.message]);
            return false;
        });
    },
    showResult(anchor, kind, text) { busy.results.push([kind, text]); }
};

// ---------------------------------------------------------------- the data
const ago = (s) => new Date(Date.now() - s * 1000).toISOString();
const A1 = '11111111-1111-1111-1111-111111111111';
const A2 = '22222222-2222-2222-2222-222222222222';

function run(extra) {
    return Object.assign({
        id: 'run-1', trigger: 'schedule', mode: 'normal', state: 'finished', outcome: 'completed', phase: null, createdAt: ago(7200), startedAt: ago(7100), finishedAt: ago(6900),
        startedBy: null, useModel: false, modelLabel: null,
        counters: { articlesInScope: 120, articlesSent: 0, candidatesExamined: 30, adviceCreated: 3, adviceSuppressed: 1, modelCalls: 0, promptTokens: 0, completionTokens: 0, errors: 0 },
        limits: { maxAdvice: 10, maxMinutes: 0, maxModelCalls: 200, maxTokens: 1500000 }, errorCode: null, errorText: null
    }, extra || {});
}
function advice(extra) {
    return Object.assign({
        id: 'adv-1', runId: 'run-1', seq: 1, kind: 'duplicate_exact', source: 'rule', score: 95, band: 'high', status: 'new', applyMode: 'code', trial: false,
        createdAt: ago(60), title: 'Two notes say the same', summary: 'They are identical.',
        evidence: [{ type: 'similarity', text: 'The texts match', value: 0.99 }],
        objects: [
            { role: 'subject', type: 'article', id: A1, title: 'Backup notes', treePath: '/Ops', lamportTs: 5 },
            { role: 'other', type: 'article', id: A2, title: 'Backup notes (copy)', treePath: '/Ops/Old', lamportTs: 6 }
        ],
        action: { type: 'trash_article', articleId: A2, keepId: A1 },
        can: { apply: true, chat: true, prompt: true, dismiss: true, markDone: true }
    }, extra || {});
}
function status(extra) {
    return Object.assign({
        available: true, activeRun: null, openAdvice: { total: 1, high: 1, medium: 0, low: 0 },
        settings: { scheduleEnabled: false, useModel: false, maxAdviceScheduled: 10, maxAdviceManual: 50 },
        modelReady: true, modelLabel: 'Some model', chatAvailable: true, embeddings: { total: 100, pending: 0 }, nextScheduledAt: null
    }, extra || {});
}

function goodRoutes() {
    route('GET', '/status', () => status());
    route('GET', '/settings', () => ({ manualMaxMinutes: 30, maxAdviceManual: 50 }));
    route('GET', '/runs', (c) => ({ total: 1, page: Number(c.query.page) || 1, pageSize: Number(c.query.pageSize) || 10, items: [run()] }));
    route('GET', '/runs/run-1', () => Object.assign(run(), { adviceCounts: { new: 2, inChat: 0, applied: 1, dismissed: 0, expired: 0 }, logAvailable: true }));
    route('GET', '/runs/run-1/advice', () => ({ items: [advice()], lastSeq: 1, run: run(), activity: null }));
    route('GET', '/runs/run-1/events', () => ({ items: [{ seq: 1, at: ago(7100), kind: 'phase', text: 'Started the detectors', data: null }], lastSeq: 1, logAvailable: true }));
    route('GET', '/advice', (c) => c.query.status === 'in_chat' ? { total: 0, items: [] } : { total: 1, items: [advice()] });
    route('GET', '/advice/adv-1', () => advice());
}

// ---------------------------------------------------------------- the boot
async function boot(handoff, extra) {
    const sandbox = {
        document, CSS: { escape: (s) => String(s) }, console, setTimeout: fakeSetTimeout, clearTimeout: fakeClearTimeout, setInterval: fakeSetInterval, clearInterval: fakeClearInterval, Date
    };
    sandbox.window = sandbox;
    sandbox.bmbBusy = bmbBusy;
    vm.createContext(sandbox);
    const load = (file) => vm.runInContext(fs.readFileSync(file, 'utf8'), sandbox, { filename: file });
    if (handoff) Object.assign(sandbox, handoff.globals);
    if (extra) Object.assign(sandbox, extra);
    load(modelPath);
    load(dreamPath);                                                                              // the order of the page: model, dream, hand-off
    if (handoff) load(path.join(path.dirname(dreamPath), 'dream-handoff.js'));
    await flush();
    return sandbox;
}

function say(ok, detail) { console.log(JSON.stringify({ ok, detail })); process.exit(0); }
function assert(cond, message) { if (!cond) throw new Error(message); }
function eq(actual, expected, what) {
    const a = JSON.stringify(actual), e = JSON.stringify(expected);
    if (a !== e) throw new Error(what + ': expected ' + e + ' but got ' + a);
}
function has(text, part, what) { if (String(text).indexOf(part) < 0) throw new Error(what + ': "' + part + '" is not in "' + String(text).slice(0, 400) + '"'); }
function hasNot(text, part, what) { if (String(text).indexOf(part) >= 0) throw new Error(what + ': "' + part + '" must not be in "' + String(text).slice(0, 400) + '"'); }
function callsTo(method, path) { return calls.filter((c) => c.method === method && c.path === path); }
// The API contract of plan 3.8, as far as this page uses it: the routes, the names of the query parameters, the names of the fields of a body. Every scenario ends
// by checking that all it sent is in it (so a path made of an undefined id, or a field the API does not know, fails here and not on the node).
const CONTRACT = [
    ['GET', /^\/status$/, []],
    ['GET', /^\/settings$/, []],
    ['POST', /^\/runs$/, [], ['mode', 'useRules', 'useModel', 'modelId', 'maxAdvice', 'maxMinutes', 'phases', 'sample']],
    ['GET', /^\/rulebook$/, []],
    ['POST', /^\/runs\/[^/]+\/stop$/, []],
    ['GET', /^\/runs$/, ['page', 'pageSize', 'mode']],
    ['GET', /^\/runs\/[^/]+$/, []],
    ['GET', /^\/runs\/[^/]+\/advice$/, ['afterSeq', 'limit']],
    ['GET', /^\/runs\/[^/]+\/events$/, ['afterSeq', 'limit']],
    ['GET', /^\/advice$/, ['status', 'kind', 'minScore', 'sort', 'page', 'pageSize']],
    ['GET', /^\/advice\/[^/]+$/, []],
    ['GET', /^\/advice\/[^/]+\/prompt$/, ['target']],
    ['POST', /^\/advice\/[^/]+\/apply$/, []],
    ['POST', /^\/advice\/[^/]+\/dismiss$/, [], ['scope', 'reason', 'expiresDays']],
    ['POST', /^\/advice\/[^/]+\/status$/, [], ['status']]
];
function checkContract() {
    for (const c of calls) {
        const entry = CONTRACT.find((e) => e[0] === c.method && e[1].test(c.path));
        if (!entry) throw new Error('a request that is not in the API contract: ' + c.method + ' ' + c.path);
        if (/\/(undefined|null|NaN)(\/|$)/.test(c.path)) throw new Error('a request with no id in it: ' + c.method + ' ' + c.path);
        for (const key of Object.keys(c.query)) if (!entry[2].includes(key)) throw new Error(c.method + ' ' + c.path + ' sends the query parameter "' + key + '", which the contract does not have');
        if (c.body && typeof c.body === 'object') for (const key of Object.keys(c.body)) if (!(entry[3] || []).includes(key)) throw new Error(c.method + ' ' + c.path + ' sends the field "' + key + '", which the contract does not have');
        if (c.method !== 'GET' && !c.headers['RequestVerificationToken']) throw new Error(c.method + ' ' + c.path + ' has no anti-forgery token');
        if (c.method === 'GET' && c.headers['RequestVerificationToken']) throw new Error('a read must not carry the token: ' + c.path);
    }
}
function clean() {
    eq(missing, [], 'the script asked for ids that are not in the page');
    eq(problems, [], 'something threw');
    checkContract();
}

// A button of a card, as a click on it reaches the page's one handler.
function press(action, adviceId, extra) {
    const card = new El('', 'article');
    card.dataset = { adviceId };
    const btn = new El('', 'sl-button');
    btn.dataset = { dreamAction: action };
    btn.closests['[data-dream-action]'] = btn;
    btn.closests['[data-advice-id]'] = card;
    Object.assign(btn.closests, extra || {});
    scope.fire('click', { target: btn });
    return btn;
}

// The cards the page drew into a list (the fake DOM keeps html, not nodes): one node for each <article>, with the id and the signature the page put on it.
function cardsDrawnIn(list) {
    const unescape = (t) => t.replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');
    const nodes = [];
    for (const m of list.innerHTML.matchAll(/<article [^>]*data-advice-id="([^"]*)"(?: data-sig="([^"]*)")?/g)) {
        const n = new El('', 'article');
        n.dataset = { adviceId: unescape(m[1]) };
        n.attrs['data-sig'] = unescape(m[2] || '');
        nodes.push(n);
    }
    list.cardNodes = nodes;
    return nodes;
}

// ---------------------------------------------------------------- the hand-off: the tabs it opens, a fake location, the clipboard a scenario chooses
const G1 = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';     // the ids of real advice are guids
const G2 = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
function handoffGlobals(options) {
    options = options || {};
    const order = [];
    const navigations = [];
    const opened = [];
    const copied = [];
    const globals = {
        open: (url, target, features) => { opened.push([url, target, features]); order.push('open'); return null; },   // what a browser gives back for noopener
        location: { assign: (u) => { navigations.push(u); }, href: 'http://node/Dream' },
        navigator: options.clipboard === false ? {} : { clipboard: { writeText: (t) => (options.clipboard === 'refuse' ? Promise.reject(new Error('denied')) : (copied.push(t), Promise.resolve())) } }
    };
    if (options.secure === false) globals.isSecureContext = false;
    return { navigations, opened, copied, order, globals };
}
// The routes of a page that holds two pieces of advice with real ids, and the answers of the prompt and status endpoints (what they were asked is in h.order).
function handoffRoutes(h, extra) {
    goodRoutes();
    const a1 = advice(Object.assign({ id: G1, status: 'new' }, (extra && extra.first) || {}));
    const a2 = advice({ id: G2, title: 'Another one', status: 'new' });
    route('GET', '/advice', (c) => c.query.status === 'in_chat' ? { total: 0, items: [] } : { total: 2, items: [a1, a2] });
    route('GET', '/advice/' + G1, () => a1);
    route('GET', '/advice/' + G2, () => a2);
    for (const id of [G1, G2]) {
        route('GET', '/advice/' + id + '/prompt', (c) => { h.order.push('prompt ' + c.query.target); return { prompt: 'The ' + c.query.target + ' task for ' + id, target: c.query.target }; });
        route('POST', '/advice/' + id + '/status', (c) => { h.order.push('status ' + c.body.status); return { status: c.body.status }; });
    }
}

// "Open in AI chat" on an advice the card already shows as closed: no tab, nothing asked or changed, and the reason at the button.
async function closedAdviceIsNotOpened(first, words) {
    const h = handoffGlobals();
    handoffRoutes(h, { first });
    await boot(h);
    const before = calls.length;
    press('chat', G1);
    await flush();
    eq(h.opened, [], 'no tab for an advice that is ' + first.status);
    eq(calls.length, before, 'nothing is asked or changed');
    assert(busy.results.some((r) => r[0] === false && words.test(r[1])), 'the reason is at the button: ' + JSON.stringify(busy.results));
    clean();
}

const scenarios = {
    async the_page_asks_for_everything_and_draws_it() {
        goodRoutes();
        await boot();
        ['/status', '/settings'].forEach((p) => assert(callsTo('GET', p).length === 1, 'GET ' + p + ' once'));
        const runCalls = callsTo('GET', '/runs');
        assert(runCalls.some((c) => c.query.pageSize === '1' && c.query.mode === 'all'), 'the last run, for the state line');
        assert(runCalls.some((c) => c.query.pageSize === '10' && c.query.page === '1' && c.query.mode === 'all'), 'the history, ten to a page');
        const advCalls = callsTo('GET', '/advice');
        assert(advCalls.some((c) => c.query.status === 'new' && c.query.pageSize === '20' && c.query.sort === 'score'), 'the new advice, twenty to a page, best first');
        assert(advCalls.some((c) => c.query.status === 'in_chat'), 'the advice that is in chat');
        eq(unmatched, [], 'a request nobody answers');

        eq(els['dream-state-title'].textContent, 'Idle', 'the state');
        has(els['dream-state-lines'].innerHTML, 'Last run 2 h ago: completed.', 'the last run');
        eq([els['dream-start'].disabled, els['dream-start'].hidden, els['dream-stop'].hidden, els['dream-start-form'].hidden], [false, false, true, false], 'Start is usable, Stop is not shown');
        eq([els['dream-use-model'].disabled, els['dream-use-rules'].checked, els['dream-model-why'].hidden], [false, true, true], 'a model is ready, so it can be chosen; the rules are on');
        has(els['dream-start-help'].textContent, 'Nothing leaves this node', 'the rules alone: nothing leaves the node');
        has(els['dream-model-label'].textContent, 'Some model', 'its name');
        eq(els['dream-max-minutes'].placeholder, 'default: 30', 'the minutes come from the settings');
        eq(els['dream-max-advice'].placeholder, 'default: 50', 'the advice from the state');

        const open = els['dream-open-list'].innerHTML;
        has(open, 'Two notes say the same', 'the open advice');
        has(open, 'data-dream-action="apply"', 'Apply');
        has(open, 'data-dream-action="chat"', 'Open in AI chat');
        has(open, 'data-dream-action="prompt"', 'Show prompt');
        eq(els['dream-open-count'].textContent, '1', 'the count');

        const history = els['dream-history'].innerHTML;
        has(history, 'data-run-id="run-1"', 'the run');
        has(history, 'Notes viewed', 'the big numbers');
        has(history, 'Night', 'the kind of run');
        has(history, 'Completed', 'the outcome');
        // the split of the advice of the run comes from the run's own record
        has(rows['run-1'].innerHTML, '2 open', 'the advice of the run, open');
        has(rows['run-1'].innerHTML, '1 applied', 'and applied');
        clean();
    },

    async a_stub_answer_never_leaves_a_part_blank() {
        ['/status', '/settings', '/runs', '/advice'].forEach((p) => route('GET', p, {}));
        await boot();
        assert(els['dream-state-title'].textContent.length > 0, 'the state has a title');
        assert(els['dream-state-lines'].innerHTML.length > 0, 'and a line');
        has(els['dream-open-empty'].textContent, 'No open advice', 'the open list says it is empty');
        eq(els['dream-open-empty'].hidden, false, 'and shows it');
        has(els['dream-history-empty'].textContent, 'No runs yet', 'the history says it is empty');
        eq(els['dream-history-empty'].hidden, false, 'and shows it');
        eq(els['dream-open-list'].innerHTML, '', 'no cards out of nothing');
        clean();
    },

    async a_node_that_fails_is_said_in_every_part() {
        failEverything = { status: 500, body: { error: 'boom' } };
        await boot();
        eq(els['dream-state-title'].textContent, 'Dream state unknown', 'the state');
        has(els['dream-warnings'].innerHTML, 'Could not load the state of Dream: boom', 'the reason, in words');
        has(els['dream-warnings'].innerHTML, 'data-dream-retry="status"', 'and a way to try again');
        eq([els['dream-start'].disabled, els['dream-start-form'].hidden], [true, true], 'nothing can be started');
        has(els['dream-open-list'].innerHTML, 'Could not load the open advice: boom', 'the open advice');
        has(els['dream-history'].innerHTML, 'Could not load the history: boom', 'the history');
        has(els['dream-history'].innerHTML, 'data-dream-retry="history"', 'with its own retry');
        clean();
    },

    async a_node_that_does_not_answer_is_said_too() {
        failEverything = 'network';
        await boot();
        has(els['dream-warnings'].innerHTML, 'The node did not answer', 'the state');
        has(els['dream-open-list'].innerHTML, 'The node did not answer', 'the open advice');
        has(els['dream-history'].innerHTML, 'The node did not answer', 'the history');
        clean();
    },

    async retry_asks_again_and_the_part_comes_back() {
        failEverything = { status: 500, body: { error: 'boom' } };
        await boot();
        failEverything = null;
        goodRoutes();
        const retry = new El('', 'sl-button');
        retry.dataset = { dreamRetry: 'history' };
        retry.closests['[data-dream-retry]'] = retry;
        scope.fire('click', { target: retry });
        await flush();
        has(els['dream-history'].innerHTML, 'data-run-id="run-1"', 'the history is back');
        const again = new El('', 'sl-button');
        again.dataset = { dreamRetry: 'open' };
        again.closests['[data-dream-retry]'] = again;
        scope.fire('click', { target: again });
        await flush();
        has(els['dream-open-list'].innerHTML, 'Two notes say the same', 'the open advice is back');
        clean();
    },

    async a_run_in_progress_is_followed_and_its_advice_comes_one_card_at_a_time() {
        goodRoutes();
        const running = run({ id: 'run-2', trigger: 'manual', state: 'running', outcome: null, phase: 'pairs', finishedAt: null, startedAt: ago(30) });
        let polls = 0;
        route('GET', '/status', () => status({ activeRun: polls >= 2 ? null : running }));
        route('GET', '/runs/run-2/advice', (c) => {
            polls++;
            if (polls === 1) {
                return { items: [advice({ id: 'a1', seq: 1, title: 'First' }), advice({ id: 'a2', seq: 2, title: 'Second' }), advice({ id: 'a3', seq: 3, title: 'Third' })], lastSeq: 3, run: running, activity: 'Comparing note pairs: 4 of 30' };
            }
            return { items: [], lastSeq: 3, run: run({ id: 'run-2', state: 'finished', outcome: 'completed', trigger: 'manual' }), activity: null };
        });
        await boot();

        eq(els['dream-feed'].hidden, false, 'the feed is shown while a run goes on');
        eq(els['dream-state-title'].textContent, 'Run is going', 'the state card says so');
        eq(els['dream-stop'].hidden, false, 'and Stop is shown');
        eq(els['dream-start'].hidden, true, 'and Start is not');
        eq(els['dream-start-form'].hidden, true, 'nor its form');
        eq(callsTo('GET', '/runs/run-2/advice')[0].query.afterSeq, '0', 'the first look asks for everything');
        eq(els['dream-activity-text'].textContent, 'Comparing note pairs: 4 of 30', 'what it is doing now');
        eq(els['dream-feed-empty'].hidden, true, 'the empty note goes when the first card comes');
        eq(els['dream-feed-list'].insertions, 1, 'one card at once ...');
        await advance(320);
        eq(els['dream-feed-list'].insertions, 2, '... the second after a moment ...');
        await advance(320);
        eq(els['dream-feed-list'].insertions, 3, '... the third after another');
        const html = els['dream-feed-list'].innerHTML;
        assert(html.indexOf('Third') < html.indexOf('Second') && html.indexOf('Second') < html.indexOf('First'), 'the newest is on top');

        await advance(2000);
        eq(callsTo('GET', '/runs/run-2/advice')[1].query.afterSeq, '3', 'the next look asks only for what is after the last card');
        has(els['dream-activity-text'].textContent, 'Finished: completed', 'the run ended, and the feed says so');
        eq(els['dream-activity'].className.indexOf('is-done') >= 0, true, 'the spinner goes');
        eq(els['dream-feed'].hidden, false, 'the cards stay after the end');
        const statusCalls = callsTo('GET', '/status').length;
        await advance(20000);
        eq(callsTo('GET', '/runs/run-2/advice').length, 2, 'a finished run is not asked about again');
        assert(callsTo('GET', '/status').length > statusCalls, 'the state is asked again after the end (and then now and then)');
        eq(els['dream-state-title'].textContent, 'Idle', 'idle again');
        clean();
    },

    async a_run_that_is_lost_is_not_followed_for_ever() {
        goodRoutes();
        route('GET', '/status', () => status({ activeRun: run({ id: 'run-3', state: 'running', outcome: null, finishedAt: null }) }));
        route('GET', '/runs/run-3/advice', () => ({ status: 500, body: { error: 'gone' } }));
        await boot();
        for (let i = 0; i < 20; i++) await advance(15000);
        const n = callsTo('GET', '/runs/run-3/advice').length;
        assert(n >= 4 && n <= 9, 'it tries a few times and gives up: ' + n);
        has(els['dream-activity-text'].textContent, 'reload this page', 'and says what to do');
        has(els['dream-activity-text'].textContent, 'gone', 'with the reason');
        clean();
    },

    async start_sends_the_form_and_follows_the_new_run() {
        goodRoutes();
        route('POST', '/runs', { status: 202, body: { runId: 'run-9' } });
        route('GET', '/runs/run-9/advice', () => ({ items: [], lastSeq: 0, run: run({ id: 'run-9', state: 'running', outcome: null, mode: 'trial', trigger: 'manual', finishedAt: null, phase: 'embeddings' }), activity: 'Preparing note vectors' }));
        await boot();
        els['dream-trial'].checked = true;
        els['dream-use-model'].checked = true;
        els['dream-max-advice'].value = '25';
        els['dream-max-minutes'].value = '';
        els['dream-start'].fire('click');
        await flush();
        const post = callsTo('POST', '/runs')[0];
        eq(post.body, { mode: 'trial', useRules: true, useModel: true, maxAdvice: 25 }, 'what is sent');
        eq(post.headers['RequestVerificationToken'], 'token-1', 'the anti-forgery token of the page');
        eq(post.headers['Content-Type'], 'application/json', 'as JSON');
        assert(callsTo('GET', '/runs/run-9/advice').length >= 1, 'the new run is followed at once');
        eq(els['dream-feed'].hidden, false, 'the feed is shown');
        eq(els['dream-feed-title'].textContent, 'This trial run', 'a trial says so');
        eq(els['dream-state-title'].textContent, 'Trial run is going', 'the state card too');
        clean();
    },

    async a_start_that_is_refused_is_said_next_to_the_button() {
        goodRoutes();
        route('POST', '/runs', { status: 409, body: { error: 'A run is already going on.', code: 'already_running' } });
        await boot();
        const before = callsTo('GET', '/status').length;
        els['dream-start'].fire('click');
        await flush();
        eq(busy.results.filter((r) => r[0] === false).map((r) => r[1]), ['A run is already going on.'], 'the node\'s words, next to the button');
        assert(callsTo('GET', '/status').length > before, 'the state is read again: the page may have been stale');
        clean();
    },

    async a_form_that_is_wrong_sends_nothing() {
        goodRoutes();
        await boot();
        els['dream-max-advice'].value = 'abc';
        els['dream-start'].fire('click');
        await flush();
        eq(callsTo('POST', '/runs').length, 0, 'nothing sent');
        has(busy.results.filter((r) => r[0] === false)[0][1], 'whole number', 'said in words');
        clean();
    },

    async stop_posts_to_the_run_that_is_going() {
        goodRoutes();
        const running = run({ id: 'run-2', state: 'running', outcome: null, finishedAt: null });
        route('GET', '/status', () => status({ activeRun: running }));
        route('GET', '/runs/run-2/advice', () => ({ items: [], lastSeq: 0, run: running, activity: null }));
        route('POST', '/runs/run-2/stop', { status: 202, body: {} });
        await boot();
        els['dream-stop'].fire('click');
        await flush();
        const post = callsTo('POST', '/runs/run-2/stop')[0];
        assert(post, 'Stop is sent to the run');
        eq(post.headers['RequestVerificationToken'], 'token-1', 'with the token');
        clean();
    },

    async apply_asks_first_and_then_changes_the_note() {
        goodRoutes();
        route('POST', '/advice/adv-1/apply', { status: 200, body: { status: 'applied', result: 'moved to the trash' } });
        route('GET', '/advice/adv-1', () => advice({ status: 'applied', applied: { at: ago(1), byUserId: 1, result: 'moved to the trash' }, can: {} }));
        await boot();
        const node = new El('', 'article');
        cards['adv-1'] = [node];

        press('apply', 'adv-1');
        eq(els['dlg-dream-apply'].shown, 1, 'the dialog is opened');
        eq(callsTo('POST', '/advice/adv-1/apply').length, 0, 'and nothing is changed yet');
        has(els['dream-apply-body'].innerHTML, 'to the trash', 'it says what it will do');
        has(els['dream-apply-body'].innerHTML, '30 days', 'and that the trash is kept');
        has(els['dream-apply-body'].innerHTML, 'dream-dialog-notes', 'it lists the notes of the advice');
        has(els['dream-apply-body'].innerHTML, '/Ops/Old', 'each with the folder it is in');
        eq(els['dream-apply-confirm'].textContent, 'Move to trash', 'the button says it too');

        els['dream-apply-confirm'].fire('click');
        await flush();
        const post = callsTo('POST', '/advice/adv-1/apply')[0];
        assert(post, 'it is applied after the confirmation');
        eq(post.headers['RequestVerificationToken'], 'token-1', 'with the token');
        eq(els['dlg-dream-apply'].hides, 1, 'the dialog closes');
        assert(node.replacedWith, 'the card is replaced by the current one');
        has(node.replacedWith.outerHTML, 'Done: moved to the trash', 'which says what was done');
        hasNot(node.replacedWith.outerHTML, 'data-dream-action="apply"', 'and has no Apply any more');
        clean();
    },

    async an_apply_that_is_refused_stays_in_the_dialog_and_the_card_is_refreshed() {
        goodRoutes();
        route('POST', '/advice/adv-1/apply', { status: 409, body: { error: 'The note was changed after this advice was made.', code: 'expired' } });
        route('GET', '/advice/adv-1', () => advice({ status: 'expired', expiredReason: 'The note was changed.', can: {} }));
        await boot();
        const node = new El('', 'article');
        cards['adv-1'] = [node];
        press('apply', 'adv-1');
        els['dream-apply-confirm'].fire('click');
        await flush();
        eq(busy.results.filter((r) => r[0] === false).map((r) => r[1]), ['The note was changed after this advice was made.'], 'the reason, in the dialog');
        eq(els['dlg-dream-apply'].hides, 0, 'the dialog stays open');
        assert(callsTo('GET', '/advice/adv-1').length >= 1, 'the card is read again');
        has(node.replacedWith.outerHTML, 'Expired: The note was changed.', 'and shows that it expired');
        clean();
    },

    async dismiss_sends_the_reach_the_reason_and_the_time() {
        goodRoutes();
        route('POST', '/advice/adv-1/dismiss', { status: 200, body: {} });
        await boot();
        press('dismiss', 'adv-1');
        eq(els['dlg-dream-dismiss'].shown, 1, 'the dialog is opened');
        const radios = els['dream-dismiss-scope'].innerHTML;
        ['advice', 'object', 'folder', 'kind'].forEach((v) => has(radios, 'value="' + v + '"', 'the choice ' + v));
        els['dream-dismiss-scope'].value = 'kind';
        els['dream-dismiss-expiry'].value = 'never';
        els['dream-dismiss-reason'].value = '  not for me  ';
        els['dream-dismiss-confirm'].fire('click');
        await flush();
        eq(callsTo('POST', '/advice/adv-1/dismiss')[0].body, { scope: 'kind', expiresDays: null, reason: 'not for me' }, 'what is sent');
        eq(els['dlg-dream-dismiss'].hides, 1, 'the dialog closes');

        els['dream-dismiss-scope'].value = 'advice';
        els['dream-dismiss-expiry'].value = '180';
        els['dream-dismiss-reason'].value = '';
        press('dismiss', 'adv-1');
        els['dream-dismiss-confirm'].fire('click');
        await flush();
        eq(callsTo('POST', '/advice/adv-1/dismiss')[1].body, { scope: 'advice', expiresDays: 180 }, 'the default: this advice, for 180 days, no reason');
        clean();
    },

    async mark_done_and_back_to_the_list_send_the_status() {
        goodRoutes();
        route('POST', '/advice/adv-1/status', { status: 200, body: {} });
        await boot();
        press('markdone', 'adv-1');
        await flush();
        press('renew', 'adv-1');
        await flush();
        const posts = callsTo('POST', '/advice/adv-1/status');
        eq(posts.map((p) => p.body), [{ status: 'applied' }, { status: 'new' }], 'the two moves');
        clean();
    },

    async the_two_buttons_of_the_hand_off_are_placeholders_that_can_be_replaced() {
        goodRoutes();
        const sandbox = await boot();
        const before = calls.length;
        press('chat', 'adv-1');
        press('prompt', 'adv-1');
        eq(calls.length, before, 'they send nothing yet');
        eq((body.appended || []).filter((n) => n.toasted).length, 2, 'they say that they are not wired');
        const got = [];
        sandbox.window.bmbDream.handoff.chat = (a, b) => got.push(['chat', a.id]);
        sandbox.window.bmbDream.handoff.prompt = (a, b) => got.push(['prompt', a.id]);
        press('chat', 'adv-1');
        press('prompt', 'adv-1');
        eq(got, [['chat', 'adv-1'], ['prompt', 'adv-1']], 'a later package replaces them and gets the advice');
        clean();
    },

    async markup_in_what_a_model_wrote_is_drawn_as_text() {
        goodRoutes();
        const evil = advice({
            title: '<img src=x onerror=alert(1)>', summary: '<script>alert(2)</script>',
            evidence: [{ type: 'quote', text: '"><svg onload=alert(3)>', value: 0.5 }],
            objects: [{ type: 'article', id: A1, title: '<b>bold</b>', treePath: '"><x>' }, { type: 'folder', id: A2, path: '/a"b<c>' }, { type: 'tag', name: '<i>t</i>', articleCount: 1 }],
            model: { label: '<u>label</u>', promptVersion: 'pair-1' }, lastError: '<em>failed</em>', source: 'model'
        });
        route('GET', '/advice', (c) => c.query.status === 'in_chat' ? { total: 0, items: [] } : { total: 1, items: [evil] });
        await boot();
        const html = els['dream-open-list'].innerHTML;
        ['<img', '<script', '<svg', '<b>', '<u>', '<em>', '<i>', '<x>', '"><svg', '"><x>'].forEach((t) => hasNot(html, t, 'markup that was written by a model'));
        has(html, '&lt;img src=x onerror=alert(1)&gt;', 'it is shown as text');
        has(html, 'href="/Folder?path=%2Fa%22b%3Cc%3E"', 'and an address is made safe');
        clean();
    },

    async the_filters_change_the_question_that_is_asked() {
        goodRoutes();
        route('GET', '/advice', (c) => c.query.kind ? { total: 0, items: [] } : c.query.status === 'in_chat' ? { total: 0, items: [] } : { total: 1, items: [advice()] });
        await boot();
        const kind = els['dream-f-kind'];
        has(kind.innerHTML, '<sl-option value="merge_tags">Tag variants</sl-option>', 'the kinds are offered');
        has(kind.innerHTML, 'value="all"', 'and all of them');
        kind.value = 'merge_tags';
        kind.fire('sl-change', { target: kind });
        els['dream-f-score'].value = '80';
        els['dream-f-score'].fire('sl-change', { target: els['dream-f-score'] });
        els['dream-f-sort'].value = 'date';
        els['dream-f-sort'].fire('sl-change', { target: els['dream-f-sort'] });
        await flush();
        const asked = callsTo('GET', '/advice').filter((c) => c.query.status === 'new').pop();
        eq([asked.query.kind, asked.query.minScore, asked.query.sort, asked.query.page, asked.query.pageSize], ['merge_tags', '80', 'date', '1', '20'], 'the last question');
        const inChat = callsTo('GET', '/advice').filter((c) => c.query.status === 'in_chat').pop();
        eq([inChat.query.kind, inChat.query.minScore], ['merge_tags', '80'], 'the advice in chat is filtered the same way');
        has(els['dream-open-empty'].textContent, 'No open advice', 'an empty answer is said');
        clean();
    },

    async advice_in_chat_is_listed_first_and_the_pager_follows_the_new_ones() {
        goodRoutes();
        route('GET', '/advice', (c) => c.query.status === 'in_chat'
            ? { total: 1, items: [advice({ id: 'c1', status: 'in_chat', title: 'In the chat' })] }
            : { total: 45, items: [advice({ id: 'n1', title: 'A new one' })] });
        await boot();
        const html = els['dream-open-list'].innerHTML;
        assert(html.indexOf('In the chat') >= 0 && html.indexOf('In the chat') < html.indexOf('A new one'), 'in chat comes first on the first page');
        eq(els['dream-open-count'].textContent, '46', 'the count is of both');
        has(els['dream-open-pager-info'].textContent, 'Page 1 of 3', 'the pager follows the new advice');
        eq([els['dream-open-prev'].disabled, els['dream-open-next'].disabled, els['dream-open-pager'].hidden], [true, false, false], 'the buttons');
        els['dream-open-next'].fire('click');
        await flush();
        eq(callsTo('GET', '/advice').filter((c) => c.query.status === 'new').pop().query.page, '2', 'the next page is asked for');
        assert(els['dream-open-list'].innerHTML.indexOf('In the chat') < 0, 'and the ones in chat are not repeated on it');
        clean();
    },

    async a_history_row_opens_and_asks_for_its_advice_and_its_log() {
        goodRoutes();
        await boot();
        const details = new El('', 'sl-details');
        details.className = 'dream-run';
        details.dataset = { runId: 'run-1' };
        const bodyEl = new El('', 'div');
        bodyEl.dataset = { loaded: '' };
        const list = new El('', 'div');
        const more = new El('', 'div');
        bodyEl.subs['[data-run-advice]'] = list;
        bodyEl.subs['[data-run-advice-more]'] = more;
        details.subs['.dream-run-body'] = bodyEl;
        els['dream-history'].fire('sl-show', { target: details });
        await flush();
        const adv = callsTo('GET', '/runs/run-1/advice').filter((c) => c.query.afterSeq === '0' && c.query.limit === '50');
        assert(adv.length >= 1, 'the advice of the run is asked for when the row opens');
        has(bodyEl.innerHTML, 'Advice: 10', 'the limits');
        has(bodyEl.innerHTML, '30 candidates looked at', 'the counters');
        has(bodyEl.innerHTML, 'left out because you dismissed them before', 'what was left out');
        has(bodyEl.innerHTML, 'Show log', 'the log button');
        has(bodyEl.innerHTML, 'data-log-available="1"', 'which knows that the log is there');
        has(list.innerHTML, 'Two notes say the same', 'the advice of the run');

        // a card inside the row also fires sl-show (its details): only the row itself counts
        const calls0 = calls.length;
        const inner = new El('', 'sl-details');
        inner.className = 'dream-card-details';
        els['dream-history'].fire('sl-show', { target: inner });
        await flush();
        eq(calls.length, calls0, 'the details of a card do not reload the row');

        // the log
        const inside = new El('', 'div');
        const holder = new El('', 'div');
        const pre = new El('', 'div');
        holder.subs['.dream-log'] = pre;
        holder.subs['[data-log-more]'] = new El('', 'div');
        inside.subs['[data-run-log]'] = holder;
        const logBtn = new El('', 'sl-button');
        logBtn.dataset = { dreamAction: 'log', logAvailable: '1' };
        logBtn.closests['.dream-run-body-in'] = inside;
        logBtn.closests['.dream-run'] = details;
        logBtn.closests['[data-dream-action="log"]'] = logBtn;
        scope.fire('click', { target: logBtn });
        await flush();
        assert(callsTo('GET', '/runs/run-1/events').length === 1, 'the log is asked for on the button');
        has(pre.innerHTML, 'Started the detectors', 'and shown');
        has(pre.innerHTML, 'phase', 'with what kind of line it is');
        clean();
    },

    async a_log_that_was_cleared_says_so_without_asking() {
        goodRoutes();
        await boot();
        const inside = new El('', 'div');
        const holder = new El('', 'div');
        inside.subs['[data-run-log]'] = holder;
        const logBtn = new El('', 'sl-button');
        logBtn.dataset = { dreamAction: 'log', logAvailable: '0' };
        logBtn.closests['.dream-run-body-in'] = inside;
        logBtn.closests['[data-dream-action="log"]'] = logBtn;
        scope.fire('click', { target: logBtn });
        await flush();
        has(holder.innerHTML, 'seven days', 'the log is kept for seven days');
        eq(callsTo('GET', '/runs/run-1/events').length, 0, 'nothing is asked for');
        clean();
    },

    async the_history_pages_and_filters() {
        goodRoutes();
        route('GET', '/runs', (c) => ({ total: 25, page: Number(c.query.page), pageSize: 10, items: c.query.pageSize === '1' ? [run()] : [run({ id: 'run-' + c.query.page + '0' })] }));
        await boot();
        has(els['dream-history-pager-info'].textContent, 'Page 1 of 3', 'three pages');
        els['dream-history-next'].fire('click');
        await flush();
        eq(callsTo('GET', '/runs').filter((c) => c.query.pageSize === '10').pop().query.page, '2', 'the next page');
        has(els['dream-history-pager-info'].textContent, 'Page 2 of 3', 'page two');
        els['dream-h-mode'].value = 'trial';
        els['dream-h-mode'].fire('sl-change', { target: els['dream-h-mode'] });
        await flush();
        const last = callsTo('GET', '/runs').filter((c) => c.query.pageSize === '10').pop();
        eq([last.query.mode, last.query.page], ['trial', '1'], 'a filter goes back to the first page');
        clean();
    },

    async the_state_is_asked_again_now_and_then_and_a_run_that_started_elsewhere_is_followed() {
        goodRoutes();
        route('GET', '/runs/run-5/advice', () => ({ items: [], lastSeq: 0, run: run({ id: 'run-5', state: 'running', outcome: null, trigger: 'schedule', finishedAt: null }), activity: 'Checking tags' }));
        await boot();
        const first = callsTo('GET', '/status').length;
        route('GET', '/status', () => status({ activeRun: run({ id: 'run-5', state: 'running', outcome: null, trigger: 'schedule', finishedAt: null }) }));
        await advance(15000);
        assert(callsTo('GET', '/status').length > first, 'the state is asked again after a while');
        assert(callsTo('GET', '/runs/run-5/advice').length >= 1, 'a night run that began meanwhile is followed');
        eq(els['dream-feed'].hidden, false, 'and its feed is shown');
        clean();
    },

    async leaving_the_page_stops_every_timer_and_listener_and_ignores_what_is_still_on_its_way() {
        goodRoutes();
        const running = run({ id: 'run-2', trigger: 'manual', state: 'running', outcome: null, phase: 'pairs', finishedAt: null, startedAt: ago(30) });
        route('GET', '/status', () => status({ activeRun: running }));
        route('GET', '/runs/run-2/advice', () => ({
            items: [advice({ id: 'a1', seq: 1, title: 'First' }), advice({ id: 'a2', seq: 2, title: 'Second' }), advice({ id: 'a3', seq: 3, title: 'Third' })],
            lastSeq: 3, run: running, activity: 'Comparing note pairs'
        }));
        await boot();
        eq(els['dream-feed-list'].insertions, 1, 'one card is on the page, two wait their turn');
        eq(els['dream-state-title'].textContent, 'Run is going', 'the page shows the run');
        eq((docListeners['visibilitychange'] || []).length, 1, 'the page listens to the visibility of the window');

        // an answer on its way: the page asks for the state, and the node answers only after the page has been left
        let answer;
        route('GET', '/status', () => new Promise((resolve) => { answer = resolve; }));
        docListeners['visibilitychange'][0]();
        await flush();
        assert(answer, 'the state was asked for');

        // the single-page navigation says that the page is left, and replaces its content
        for (const fn of (docListeners['bmb:page-cleanup'] || []).slice()) fn();
        pageGone = true;
        eq(timers.size, 0, 'no timer is left: the reveal of the waiting cards, the poll, the idle check');
        eq(openIntervals.size, 0, 'and no interval');
        eq((docListeners['visibilitychange'] || []).length, 0, 'the listener of the document is removed, so visits do not pile them up');

        // the late answer, and a long time passing
        answer(status({ activeRun: null }));
        await advance(120000);
        document.hidden = true;
        document.hidden = false;
        for (const fn of (docListeners['visibilitychange'] || []).slice()) fn();
        await flush();
        eq(callsTo('GET', '/status').length, 2, 'nothing asked for after the page was left (the first load and the one that was on its way)');
        eq(callsTo('GET', '/runs/run-2/advice').length, 1, 'the feed is not polled any more');
        eq(els['dream-feed-list'].insertions, 1, 'the waiting cards are not drawn into a page that is gone');
        eq(els['dream-state-title'].textContent, 'Run is going', 'and the late answer does not redraw it');
        eq(problems, [], 'nothing threw against elements that are gone');
    },

    async nothing_is_asked_while_the_window_is_hidden() {
        goodRoutes();
        await boot();
        document.hidden = true;
        const before = calls.length;
        await advance(60000);
        eq(calls.length, before, 'a hidden page does not poll');
        clean();
    },

    // ---- package ux2a: the two switches, the rule book, the cards, advice closed elsewhere, the tab that comes back, /Dream?advice=<id> ----
    async the_rules_and_the_model_are_two_switches_and_start_needs_one() {
        goodRoutes();
        route('POST', '/runs', { status: 202, body: { runId: 'run-9' } });
        route('GET', '/runs/run-9/advice', () => ({ items: [], lastSeq: 0, run: run({ id: 'run-9', state: 'running', outcome: null, trigger: 'manual', finishedAt: null }), activity: null }));
        await boot();
        eq([els['dream-use-rules'].checked, els['dream-use-model'].checked, els['dream-start'].disabled], [true, false, false], 'the rules on, the model as the settings say (off)');

        els['dream-use-rules'].checked = false;
        els['dream-use-rules'].fire('sl-change');
        eq(els['dream-start'].disabled, true, 'neither: Start is off');
        eq(els['dream-start-reason'].hidden, false, 'with the reason next to it');
        has(els['dream-start-reason'].textContent, 'at least one', 'in words');

        els['dream-use-model'].checked = true;
        els['dream-use-model'].fire('sl-change');
        eq([els['dream-start'].disabled, els['dream-start-reason'].hidden], [false, true], 'the model alone can run');
        has(els['dream-start-help'].textContent, 'Only the AI model runs', 'the help says what runs');
        has(els['dream-start-help'].textContent, 'sent to OpenRouter', 'and what leaves the node');

        els['dream-start'].fire('click');
        await flush();
        eq(callsTo('POST', '/runs')[0].body, { mode: 'normal', useRules: false, useModel: true }, 'what is sent: the model alone');
        clean();
    },

    async a_model_that_is_not_set_up_is_a_disabled_switch_with_the_reason_and_the_links() {
        goodRoutes();
        route('GET', '/status', () => status({ modelReady: false, modelLabel: null, modelProblem: 'no_key', settings: { scheduleEnabled: false, useModel: true, maxAdviceManual: 50 } }));
        await boot();
        eq([els['dream-use-model'].disabled, els['dream-use-model'].checked], [true, false], 'shown, off and not usable, even though the settings want the model');
        eq(els['dream-model-why'].hidden, false, 'the reason is shown');
        has(els['dream-model-why'].innerHTML, 'No usable OpenRouter key', 'why');
        has(els['dream-model-why'].innerHTML, 'href="/Admin/AiChat"', 'where models and keys are');
        has(els['dream-model-why'].innerHTML, 'href="/Admin/Dream"', 'where Dream chooses its model');
        has(els['dream-model-label'].textContent, 'not set up', 'the label says so');
        eq(els['dream-warnings'].innerHTML.indexOf('only the rules run'), -1, 'and the state card does not say it a second time');

        els['dream-use-rules'].checked = false;
        els['dream-use-rules'].fire('sl-change');
        eq(els['dream-start'].disabled, true, 'the rules off and no model: Start is off');
        els['dream-start'].fire('click');
        await flush();
        eq(callsTo('POST', '/runs').length, 0, 'and a click on it anyway sends nothing');
        clean();
    },

    async view_all_rules_opens_the_book_of_the_node_and_its_link_in_the_details_does_too() {
        goodRoutes();
        let book = 0;
        route('GET', '/rulebook', () => {
            book++;
            return {
                intro: ['Dream reads the notes on this node.'], similarity: ['Similarity is the cosine of two note vectors.'], skips: ['Notes locked with a second password.'],
                rules: [{ id: 'TagVariants', name: 'Tag variants', kinds: ['tag_variants'], kindDetails: [{ kind: 'tag_variants', action: 'merge_tags', applyModes: ['code', 'prompt'] }],
                    finds: 'Tags that are the same word written differently.', example: 'docker and Docker.', how: ['Compared without case.'], thresholds: [{ name: 'Edit distance, at most', value: '1' }],
                    score: 'From 55 up.', apply: 'Merges the tags.', never: 'Never touches the text of a note.', skips: ['Tags of locked notes.'] }],
                modelTasks: [{ id: 'pair_verdict', name: 'A verdict on a pair of notes', promptVersion: 'pair-1', kinds: ['overlap'], candidates: ['The closest pairs, 40 at most.'], asked: 'Which of these is it?',
                    answers: 'One verdict.', checks: ['The ids are the ones it was sent.'], limits: [{ name: 'Characters of a note', value: '6,000' }] }],
                limits: [{ name: 'Advice of a manual run', value: '50' }], bands: [{ band: 'high', from: 70, to: 100, meaning: 'Worth doing.' }]
            };
        });
        await boot();
        eq(callsTo('GET', '/rulebook').length, 0, 'nothing is read until it is asked for');
        els['dream-rules-open'].fire('click');
        await flush();
        eq(els['dlg-dream-rules'].shown, 1, 'the dialog opens');
        const html = els['dream-rules-body'].innerHTML;
        has(html, 'Tag variants', 'the rule');
        has(html, 'Edit distance, at most', 'its numbers, as the node says them');
        has(html, 'Never touches the text of a note.', 'what it never does');
        has(html, 'A verdict on a pair of notes', 'the step of the model');
        has(html, 'pair-1', 'with its prompt version');
        has(html, 'What the node checks', 'and what the node checks');
        has(html, 'Score bands', 'the bands');
        has(html, 'Advice of a manual run', 'the limits of a run');

        const link = new El('', 'a');
        link.closests['[data-dream-open-rules]'] = link;
        scope.fire('click', { target: link, preventDefault() { link.prevented = true; } });
        await flush();
        eq([els['dlg-dream-rules'].shown, book, !!link.prevented], [2, 2, true], 'the link in "What Dream reads" opens the same dialog, read again');
        clean();
    },

    async a_rule_book_that_cannot_be_read_says_so_and_tries_again() {
        goodRoutes();
        route('GET', '/rulebook', { status: 500, body: { error: 'boom' } });
        await boot();
        els['dream-rules-open'].fire('click');
        await flush();
        has(els['dream-rules-body'].innerHTML, 'Could not load the rules: boom', 'in words');
        has(els['dream-rules-body'].innerHTML, 'data-dream-retry="rules"', 'with a retry');
        route('GET', '/rulebook', { rules: [{ id: 'EmptyFolder', name: 'Empty folders', kinds: ['empty_folder'] }], modelTasks: [], bands: [] });
        const retry = new El('', 'sl-button');
        retry.closests['[data-dream-retry]'] = retry;
        els['dlg-dream-rules'].fire('click', { target: retry });
        await flush();
        has(els['dream-rules-body'].innerHTML, 'Empty folders', 'the retry reads it');
        clean();
    },

    async a_card_says_what_each_object_is_who_found_it_and_its_links_open_a_new_tab() {
        goodRoutes();
        const withFolder = advice({
            id: 'adv-1', kind: 'move_article', source: 'model', model: { label: 'Some model', promptVersion: 'folder-1' },
            objects: [{ role: 'subject', type: 'article', id: A1, title: 'Backup notes', treePath: '/Ops' }, { role: 'target', type: 'folder', id: null, path: '/New place' }, { type: 'tag', name: 'ops', articleCount: 3 }],
            action: { type: 'move_article', articleId: A1, targetPath: '/New place' }
        });
        route('GET', '/advice', (c) => c.query.status === 'in_chat' ? { total: 0, items: [] } : { total: 2, items: [advice({ id: 'adv-2' }), withFolder] });
        await boot();
        const open = els['dream-open-list'].innerHTML;
        has(open, '<span class="dream-object-type">Note</span>', 'a note says it is a note');
        has(open, '<span class="dream-object-type">Folder</span>', 'a folder');
        has(open, '<span class="dream-object-type">Tag</span>', 'a tag');
        has(open, 'href="/Article/View?id=' + A1 + '" target="_blank" rel="noopener"', 'a link opens in a new tab');
        has(open, '(opens in a new tab)', 'and says so to a screen reader');
        has(open, '<span class="dream-object-name">/New place</span>', 'a folder that does not exist yet is plain text');
        has(open, 'Found by the built-in rules', 'who found the first');
        has(open, 'Found by the AI model: Some model', 'and the second');
        hasNot(open, 'Found by: ', 'the fold does not say it again');
        has(open, 'Prompt version: folder-1', 'the fold keeps the prompt version');
        has(open, '> Open in AI chat</sl-button>', 'the chat button has its new name');
        has(open, 'Nothing is sent until you press Run there.', 'and its tooltip');
        hasNot(open, 'Do it with AI', 'the old name is gone');
        has(els['dream-history'].innerHTML, 'Rules only', 'a history row says what ran');
        clean();
    },

    async every_button_on_an_advice_closed_elsewhere_says_what_happened_and_redraws_the_card() {
        goodRoutes();
        const at = '2026-11-09T18:30:00Z';
        const closed = { status: 409, body: { error: 'This advice was already applied on 9 Nov 2026, 18:30 UTC.', code: 'not_applicable', status: 'applied', statusAt: at, statusNote: null } };
        route('POST', '/advice/adv-1/apply', closed);
        route('POST', '/advice/adv-1/dismiss', closed);
        route('POST', '/advice/adv-1/status', closed);
        route('GET', '/advice/adv-1', () => advice({ status: 'applied', statusAt: at, applied: { at, byUserId: 1, result: 'moved to the trash' }, can: { prompt: true } }));
        await boot();
        const sandboxWhen = 'This advice was already applied on ';
        const presses = [
            () => { press('apply', 'adv-1'); els['dream-apply-confirm'].fire('click'); },
            () => { press('dismiss', 'adv-1'); els['dream-dismiss-confirm'].fire('click'); },
            () => { press('markdone', 'adv-1'); }
        ];
        for (const go of presses) {
            const node = new El('', 'article');
            cards['adv-1'] = [node];
            busy.results.length = 0;
            const before = callsTo('GET', '/advice/adv-1').length;
            go();
            await flush();
            const said = busy.results.filter((r) => r[0] === false).map((r) => r[1]);
            eq(said.length, 1, 'one message, at the button');
            has(said[0], sandboxWhen, 'what happened');
            assert(said[0].indexOf('UTC') < 0, 'in local time, not the node\'s UTC: ' + said[0]);
            assert(callsTo('GET', '/advice/adv-1').length > before, 'the card is read again');
            has(node.replacedWith.outerHTML, 'Done: moved to the trash', 'and redrawn as it is now');
            hasNot(node.replacedWith.outerHTML, 'data-dream-action="apply"', 'with no Apply');
            hasNot(node.replacedWith.outerHTML, 'data-dream-action="chat"', 'and no chat');
            has(node.replacedWith.outerHTML, 'data-dream-action="prompt"', 'but its prompt, for reference');
        }
        eq([els['dlg-dream-apply'].hides, els['dlg-dream-dismiss'].hides], [0, 0], 'the dialogs stay open with the message');
        clean();
    },

    async the_tab_that_comes_back_reads_the_page_again_in_place_at_most_every_few_seconds() {
        goodRoutes();
        const same = run();   // the node says the same run the same way each time (run() takes its times from the clock)
        route('GET', '/runs', (c) => ({ total: 1, page: 1, pageSize: Number(c.query.pageSize) || 10, items: [same] }));
        const realNow = Date.now;
        let shift = 0;
        Date.now = () => realNow() + shift;
        await boot();
        // what is on the page is marked: a list that did not change must not be drawn again
        els['dream-open-list'].innerHTML = 'KEPT-OPEN';
        els['dream-history'].innerHTML = 'KEPT-HISTORY';
        document.getElementById('dlg-dream-apply').shown = 1;   // a dialog that is open while the tab is away
        const fire = async () => { for (const fn of (docListeners['visibilitychange'] || []).slice()) fn(); await flush(); };

        let before = calls.length;
        await fire();
        const asked = calls.slice(before).map((c) => c.method + ' ' + c.path);
        ['GET /status', 'GET /advice', 'GET /runs'].forEach((r) => assert(asked.includes(r), 'the tab comes back: ' + r + ' is read again (' + asked.join(', ') + ')'));
        eq([els['dream-open-list'].innerHTML, els['dream-history'].innerHTML], ['KEPT-OPEN', 'KEPT-HISTORY'], 'nothing changed, nothing is drawn again');
        eq(els['dlg-dream-apply'].hides, 0, 'an open dialog stays open');

        before = calls.length;
        shift = 2000;
        await fire();
        eq(calls.length, before, 'twice within a few seconds: read once');

        // in another tab the advice was applied and a new run finished
        route('GET', '/advice', (c) => c.query.status === 'in_chat' ? { total: 0, items: [] } : { total: 1, items: [advice({ id: 'adv-7', title: 'A new one' })] });
        route('GET', '/runs', () => ({ total: 2, items: [run({ id: 'run-2' }), run()] }));
        route('GET', '/runs/run-2', () => Object.assign(run({ id: 'run-2' }), { adviceCounts: { new: 1 } }));
        shift = 6000;
        await fire();
        has(els['dream-open-list'].innerHTML, 'A new one', 'the open list is drawn again once it changed');
        has(els['dream-history'].innerHTML, 'data-run-id="run-2"', 'and the history');
        eq(problems, [], 'nothing threw');
        Date.now = realNow;
        clean();
    },

    async a_history_row_that_was_open_opens_again_when_the_rows_are_drawn_again() {
        goodRoutes();
        const realNow = Date.now;
        let shift = 0;
        Date.now = () => realNow() + shift;
        await boot();
        const row = new El('', 'sl-details');
        row.className = 'dream-run';
        row.dataset = { runId: 'run-1' };
        els['dream-history'].fire('sl-show', { target: row });
        await flush();
        route('GET', '/runs', () => ({ total: 1, items: [run({ outcome: 'stopped' })] }));
        shift = 6000;
        for (const fn of (docListeners['visibilitychange'] || []).slice()) fn();
        await flush();
        has(els['dream-history'].innerHTML, 'data-run-id="run-1" open', 'the row is drawn open');
        els['dream-history'].fire('sl-hide', { target: row });
        route('GET', '/runs', () => ({ total: 1, items: [run({ outcome: 'completed' })] }));
        shift = 12000;
        for (const fn of (docListeners['visibilitychange'] || []).slice()) fn();
        await flush();
        hasNot(els['dream-history'].innerHTML, 'data-run-id="run-1" open', 'a row that was closed stays closed');
        Date.now = realNow;
        clean();
    },

    async an_open_history_row_reads_its_advice_again_when_the_tab_comes_back_card_by_card_without_a_blink() {
        goodRoutes();
        const at = '2026-11-09T18:30:00Z';
        const runs = [run(), run({ id: 'run-2' }), run({ id: 'run-3' })];   // the same three runs the same way each time: only the advice changes
        const adviceOf = {
            'run-1': [advice({ id: 'a-1', runId: 'run-1', seq: 1 }), advice({ id: 'a-2', runId: 'run-1', seq: 2, title: 'The second one' })],
            'run-2': [advice({ id: 'a-3', runId: 'run-2', seq: 1, title: 'In the other row' })]
        };
        route('GET', '/runs', (c) => ({ total: 3, page: 1, pageSize: Number(c.query.pageSize) || 10, items: runs }));
        for (const r of runs) {
            route('GET', '/runs/' + r.id, () => Object.assign({}, r, { adviceCounts: { new: 2, inChat: 0, applied: 0, dismissed: 0, expired: 0 }, logAvailable: true }));
            if (adviceOf[r.id]) route('GET', '/runs/' + r.id + '/advice', () => ({ items: adviceOf[r.id], lastSeq: 2, run: r, activity: null }));
        }
        const realNow = Date.now;
        let shift = 0;
        Date.now = () => realNow() + shift;
        const shown = {};
        for (const id of ['run-1', 'run-2']) {
            const row = new El('', 'sl-details');
            row.className = 'dream-run';
            row.dataset = { runId: id };
            const body = new El('', 'div');
            body.dataset = {};
            const list = new El('', 'div');
            body.subs['[data-run-advice]'] = list;
            body.subs['[data-run-advice-more]'] = new El('', 'div');
            row.subs['.dream-run-body'] = body;
            rows[id] = row;
            shown[id] = { row, list };
        }
        const sandbox = await boot();
        for (const id of ['run-1', 'run-2']) els['dream-history'].fire('sl-show', { target: shown[id].row });   // both rows are opened
        await flush();
        const cards1 = cardsDrawnIn(shown['run-1'].list);
        const cards2 = cardsDrawnIn(shown['run-2'].list);
        eq([cards1.length, cards2.length], [2, 1], 'the advice of both open rows is on the page');
        const fold = new El('', 'sl-details');
        fold.setAttribute('open', '');
        cards1[0].subs['.dream-card-details'] = fold;   // the person has the "Why" fold of the first card open
        const before = [shown['run-1'].list.innerHTML, shown['run-2'].list.innerHTML];
        els['dream-history'].innerHTML = 'KEPT-HISTORY';

        // in another tab the first advice was applied; this tab was hidden
        adviceOf['run-1'][0] = advice({ id: 'a-1', runId: 'run-1', seq: 1, status: 'applied', statusAt: at, applied: { at, byUserId: 1, result: 'moved to the trash' }, can: { prompt: true } });
        shift = 6000;
        const mark = calls.length;
        for (const fn of (docListeners['visibilitychange'] || []).slice()) fn();
        await flush();
        const asked = calls.slice(mark).filter((c) => /^\/runs\/run-\d\/advice$/.test(c.path));
        eq(asked.map((c) => c.path).sort(), ['/runs/run-1/advice', '/runs/run-2/advice'], 'the advice of each open row is read again, and of no other row');
        eq(asked.map((c) => [c.query.afterSeq, c.query.limit]), [['0', '50'], ['0', '50']], 'from the first, as many as the batch');
        const fresh = cards1[0].replacedWith;
        assert(fresh, 'the card applied elsewhere is drawn again');
        has(fresh.outerHTML, 'Done: moved to the trash', 'as it is now');
        hasNot(fresh.outerHTML, 'data-dream-action="apply"', 'with no Apply');
        hasNot(fresh.outerHTML, 'data-dream-action="chat"', 'and no Open in AI chat');
        hasNot(fresh.outerHTML, 'data-dream-action="markdone"', 'and no Mark done');
        has(fresh.outerHTML, 'data-dream-action="prompt"', 'but its prompt, for reference');
        eq(fresh.subs['.dream-card-details'].attrs.open, '', 'its "Why" fold stays open');
        eq([cards1[1].replacedWith, cards2[0].replacedWith], [null, null], 'a card that shows the advice as it is stays untouched');
        eq([shown['run-1'].list.innerHTML, shown['run-2'].list.innerHTML], before, 'no loading box, no list written again: nothing blinks');
        eq(els['dream-history'].innerHTML, 'KEPT-HISTORY', 'and the rows are not drawn again, so they stay open');
        eq(sandbox.bmbDream.advice('a-1').status, 'applied', 'the buttons act on the latest');

        // the node cannot be reached the next time: what is on the page stays
        route('GET', '/runs/run-1/advice', { status: 500, body: { error: 'down' } });
        route('GET', '/runs/run-2/advice', { status: 500, body: { error: 'down' } });
        adviceOf['run-2'][0] = advice({ id: 'a-3', runId: 'run-2', status: 'dismissed', statusAt: at, can: { prompt: true } });
        shift = 12000;
        for (const fn of (docListeners['visibilitychange'] || []).slice()) fn();
        await flush();
        eq([cards2[0].replacedWith, shown['run-2'].list.innerHTML], [null, before[1]], 'an unreachable node leaves the row as it was');
        Date.now = realNow;
        clean();
    },

    async a_row_that_was_closed_is_not_asked_for_its_advice_again() {
        goodRoutes();
        const same = run();
        route('GET', '/runs', () => ({ total: 1, page: 1, pageSize: 10, items: [same] }));
        const realNow = Date.now;
        let shift = 0;
        Date.now = () => realNow() + shift;
        const row = new El('', 'sl-details');
        row.className = 'dream-run';
        row.dataset = { runId: 'run-1' };
        const body = new El('', 'div');
        body.dataset = {};
        body.subs['[data-run-advice]'] = new El('', 'div');
        body.subs['[data-run-advice-more]'] = new El('', 'div');
        row.subs['.dream-run-body'] = body;
        rows['run-1'] = row;
        await boot();
        els['dream-history'].fire('sl-show', { target: row });
        await flush();
        els['dream-history'].fire('sl-hide', { target: row });   // the person closes the row
        shift = 6000;
        const mark = calls.length;
        for (const fn of (docListeners['visibilitychange'] || []).slice()) fn();
        await flush();
        eq(calls.slice(mark).filter((c) => /^\/runs\/[^/]+\/advice$/.test(c.path)).length, 0, 'a closed row asks for nothing');
        Date.now = realNow;
        clean();
    },

    async the_state_card_takes_the_last_run_from_the_state_when_the_tab_comes_back() {
        goodRoutes();
        const realNow = Date.now;
        let shift = 0;
        Date.now = () => realNow() + shift;
        await boot();
        has(els['dream-state-lines'].innerHTML, 'Last run 2 h ago: completed.', 'the run the page knew');
        // another tab finished a run while this one was hidden: the state names it
        route('GET', '/status', () => status({ lastRun: run({ id: 'run-9', outcome: 'stopped', finishedAt: ago(120) }) }));
        shift = 6000;
        for (const fn of (docListeners['visibilitychange'] || []).slice()) fn();
        await flush();
        has(els['dream-state-lines'].innerHTML, 'Last run 2 min ago: stopped.', 'the state card reports the run another tab finished');
        hasNot(els['dream-state-lines'].innerHTML, 'completed', 'and not the one before');
        // a state that names no run (a stub) leaves the line as it is
        route('GET', '/status', () => status());
        shift = 12000;
        for (const fn of (docListeners['visibilitychange'] || []).slice()) fn();
        await flush();
        has(els['dream-state-lines'].innerHTML, 'stopped', 'a state without a last run does not blank it');
        Date.now = realNow;
        clean();
    },

    async a_link_to_one_advice_shows_it_on_top_whatever_its_status_with_a_way_back() {
        goodRoutes();
        const at = '2026-11-09T18:30:00Z';
        route('GET', '/advice/' + G1, () => advice({ id: G1, status: 'applied', statusAt: at, title: 'The one from the chat', applied: { at, byUserId: 1, result: 'moved' }, can: { prompt: true } }));
        const replaced = [];
        let scrolled = 0;
        els['dream-focus'] = new El('dream-focus');
        els['dream-focus'].scrollIntoView = () => { scrolled++; };
        els['dream-open'] = new El('dream-open');
        els['dream-open'].scrollIntoView = () => { scrolled++; };
        await boot(null, {
            location: { search: '?advice=' + G1.toUpperCase(), pathname: '/Dream' },
            history: { state: { k: 1 }, replaceState: (st, t, url) => replaced.push(url) }
        });
        eq(els['dream-focus'].hidden, false, 'the section is shown');
        const html = els['dream-focus-body'].innerHTML;
        has(html, 'The one from the chat', 'with the advice');
        has(html, 'is-focus', 'marked');
        has(html, 'Done: moved', 'as it is now, applied');
        hasNot(html, 'data-dream-action="apply"', 'with no Apply');
        hasNot(html, 'data-dream-action="chat"', 'and no chat');
        eq(scrolled, 1, 'and scrolled to');
        assert(callsTo('GET', '/advice').length >= 1, 'the rest of the page loads as always');

        // Show prompt on it: the dialog will say it is for reference only
        press('prompt', G1);
        eq(els['dream-prompt-closed'].hidden, false, 'a note in the prompt dialog');
        has(els['dream-prompt-closed'].textContent, 'for reference only', 'that says so');
        press('prompt', 'adv-1');
        eq(els['dream-prompt-closed'].hidden, true, 'an open advice: no note');

        els['dream-focus-all'].fire('click');
        eq([els['dream-focus'].hidden, els['dream-focus-body'].innerHTML], [true, ''], 'Show all advice puts the page back');
        eq(replaced, ['/Dream'], 'and takes the id out of the address');
        clean();
    },

    async a_link_to_an_advice_dream_does_not_know_gives_a_short_notice() {
        goodRoutes();
        route('GET', '/advice/' + G2, { status: 404, body: { error: 'Not found.', code: 'not_found' } });
        await boot(null, { location: { search: '?advice=' + G2, pathname: '/Dream' } });
        eq(els['dream-focus'].hidden, false, 'the section is shown');
        has(els['dream-focus-body'].innerHTML, 'Dream does not know this advice', 'with a short notice');
        hasNot(els['dream-focus-body'].innerHTML, 'dream-error', 'not an error');
        clean();
    },

    async a_link_that_names_no_advice_asks_for_nothing() {
        goodRoutes();
        await boot(null, { location: { search: '?advice=../../status', pathname: '/Dream' } });
        has(els['dream-focus-body'].innerHTML, 'does not name an advice', 'a short notice');
        eq(calls.filter((c) => c.path.indexOf('/advice/') === 0).length, 0, 'and nothing is asked for it');
        clean();
    },

    async show_prompt_asks_for_the_text_for_an_agent_and_shows_it_with_a_copy_button() {
        const h = handoffGlobals();
        handoffRoutes(h);
        route('GET', '/advice/' + G1 + '/prompt', { prompt: 'Move the note "Backup notes (copy)" to the trash.\nIf it changed since 12:00, do nothing and say so.', target: 'agent' });
        await boot(h);
        const before = calls.length;
        press('prompt', G1);
        await flush();
        eq(calls.slice(before).map((c) => c.method + ' ' + c.path + ' ' + JSON.stringify(c.query)), ['GET /advice/' + G1 + '/prompt {"target":"agent"}'], 'one read, for an agent');
        eq(els['dlg-dream-prompt'].shown, 1, 'the dialog opens');
        has(els['dream-prompt-text'].value, 'Move the note "Backup notes (copy)" to the trash.\nIf it changed', 'with the text of the node, as it is');
        has(els['dream-prompt-title'].textContent, 'Two notes say the same', 'and what it is about');
        has(els['dream-prompt-note'].textContent, 'characters', 'and how long it is');
        eq([h.navigations, h.opened], [[], []], 'the page stays and no tab opens');
        eq(callsTo('POST', '/advice/' + G1 + '/status').length, 0, 'showing a prompt changes nothing on the node');
        clean();
    },

    async the_text_of_a_prompt_goes_in_as_text_and_never_as_markup() {
        const h = handoffGlobals();
        handoffRoutes(h, { first: { title: '<img src=x onerror=alert(1)>' } });
        const evil = 'Trash "<img src=x onerror=alert(1)>" and <script>alert(2)</script>';
        route('GET', '/advice/' + G1 + '/prompt', { prompt: evil, target: 'agent' });
        await boot(h);
        press('prompt', G1);
        await flush();
        eq(els['dream-prompt-text'].value, evil, 'the field holds the text exactly');
        eq(els['dream-prompt-text'].innerHTML, '', 'it is not written into the page as markup');
        hasNot(els['dream-prompt-title'].innerHTML, '<img', 'and neither is the title of the advice');
        has(els['dream-prompt-title'].textContent, '<img src=x onerror=alert(1)>', 'that is shown as text');
        clean();
    },

    async copy_puts_the_text_on_the_clipboard_and_says_so() {
        const h = handoffGlobals();
        handoffRoutes(h);
        await boot(h);
        press('prompt', G1);
        await flush();
        els['dream-prompt-copy'].fire('click');
        await flush();
        eq(h.copied, ['The agent task for ' + G1], 'the whole text was copied');
        has(els['dream-prompt-note'].textContent, 'Copied', 'and the dialog says so');
        clean();
    },

    async copy_falls_back_to_selecting_the_text_where_the_page_has_no_clipboard() {
        const h = handoffGlobals({ clipboard: false });
        handoffRoutes(h);
        const executed = [];
        document.execCommand = (c) => { executed.push(c); return true; };
        await boot(h);
        press('prompt', G1);
        await flush();
        let selected = 0;
        els['dream-prompt-text'].select = () => { selected++; };
        els['dream-prompt-copy'].fire('click');
        await flush();
        eq([selected, executed], [1, ['copy']], 'the text is selected and the browser is asked to copy it');
        has(els['dream-prompt-note'].textContent, 'Copied', 'and the dialog says so');
        // nothing works: the person is told what to do
        document.execCommand = () => false;
        els['dream-prompt-copy'].fire('click');
        await flush();
        has(els['dream-prompt-note'].textContent, 'press Ctrl+C', 'the keyboard is the way out when the browser refuses');
        delete document.execCommand;
        clean();
    },

    async a_clipboard_that_refuses_is_followed_by_the_fallback() {
        const h = handoffGlobals({ clipboard: 'refuse' });
        handoffRoutes(h);
        document.execCommand = () => true;
        await boot(h);
        press('prompt', G1);
        await flush();
        els['dream-prompt-copy'].fire('click');
        await flush();
        has(els['dream-prompt-note'].textContent, 'Copied', 'the refusal of the clipboard is not the end');
        delete document.execCommand;
        clean();
    },

    async the_prompt_dialog_is_emptied_when_it_closes() {
        const h = handoffGlobals();
        handoffRoutes(h);
        await boot(h);
        press('prompt', G1);
        await flush();
        const dialog = els['dlg-dream-prompt'];
        dialog.fire('sl-after-hide', { target: new El('', 'sl-textarea') });
        assert(els['dream-prompt-text'].value !== '', 'the hide event of something inside the dialog does not empty it');
        dialog.fire('sl-after-hide', { target: dialog });
        eq([els['dream-prompt-text'].value, els['dream-prompt-note'].textContent], ['', ''], 'the closing of the dialog itself does');
        clean();
    },

    async a_prompt_the_node_refuses_is_said_in_its_words_and_the_card_is_read_again() {
        const h = handoffGlobals();
        handoffRoutes(h);
        route('GET', '/advice/' + G1 + '/prompt', { status: 409, body: { error: 'This advice is out of date.', code: 'expired' } });
        await boot(h);
        const before = calls.length;
        press('prompt', G1);
        await flush();
        eq(els['dlg-dream-prompt'].shown, 0, 'no dialog for a refusal');
        assert(busy.results.some((r) => r[0] === false && r[1] === 'This advice is out of date.'), 'the words of the node are shown next to the button');
        assert(calls.slice(before).some((c) => c.method === 'GET' && c.path === '/advice/' + G1), 'the card is read again, so it shows what happened to it');
        clean();
    },

    async a_prompt_that_comes_back_empty_is_not_shown_as_a_blank_dialog() {
        const h = handoffGlobals();
        handoffRoutes(h);
        route('GET', '/advice/' + G1 + '/prompt', {});
        await boot(h);
        press('prompt', G1);
        await flush();
        eq(els['dlg-dream-prompt'].shown, 0, 'nothing to show');
        assert(busy.results.some((r) => r[0] === false && /no prompt/i.test(r[1])), 'and the person is told so');
        clean();
    },

    async open_in_ai_chat_opens_a_new_tab_with_only_the_id_and_changes_nothing() {
        const h = handoffGlobals();
        handoffRoutes(h);
        await boot(h);
        const before = calls.length;
        press('chat', G1);
        eq(h.opened, [['/AI?dreamAdvice=' + G1, '_blank', 'noopener']], 'one new tab, at the chat, with the id of the advice and nothing else, and no handle on this page');
        await flush();
        eq(calls.length, before, 'the node is asked nothing: the chat page asks for the task itself');
        eq(callsTo('POST', '/advice/' + G1 + '/status').length, 0, 'and the advice is not marked "in chat" by opening the tab');
        eq(h.navigations, [], 'this tab stays where it is');
        assert(busy.results.some((r) => r[0] === true && /new tab/.test(r[1])), 'the button says that the chat opens in a new tab');
        clean();
    },

    async the_tab_is_opened_in_the_click_itself_so_no_pop_up_blocker_stops_it() {
        const h = handoffGlobals();
        handoffRoutes(h);
        await boot(h);
        press('chat', G1);
        eq(h.opened.length, 1, 'the tab is open before anything is awaited');
        clean();
    },

    async each_press_opens_the_tab_of_its_own_advice() {
        const h = handoffGlobals();
        handoffRoutes(h);
        await boot(h);
        press('chat', G1);
        press('chat', G2);
        press('chat', G1);
        eq(h.opened.map((o) => o[0]), ['/AI?dreamAdvice=' + G1, '/AI?dreamAdvice=' + G2, '/AI?dreamAdvice=' + G1], 'nothing is shared between the tabs, so nothing can cross');
        clean();
    },

    async an_applied_advice_is_not_opened_and_the_button_says_when_it_was_applied() {
        await closedAdviceIsNotOpened({ status: 'applied', statusAt: '2026-11-03T10:00:00Z' }, /^This advice was already applied on .+\.$/);
    },

    async a_dismissed_advice_is_not_opened_and_the_button_says_when_it_was_dismissed() {
        await closedAdviceIsNotOpened({ status: 'dismissed', statusAt: '2026-11-02T09:12:00Z' }, /^This advice was dismissed on .+\.$/);
    },

    async an_expired_advice_is_not_opened_and_the_button_gives_the_reason() {
        await closedAdviceIsNotOpened({ status: 'expired', expiredReason: 'The note "Backup notes" changed.' }, /^This advice expired: The note "Backup notes" changed\.$/);
    },

    async an_advice_being_applied_is_not_opened() {
        await closedAdviceIsNotOpened({ status: 'applying' }, /being applied/);
    },

    async an_advice_that_is_already_in_chat_opens_again() {
        const h = handoffGlobals();
        handoffRoutes(h, { first: { status: 'in_chat' } });
        await boot(h);
        press('chat', G1);
        eq(h.opened.map((o) => o[0]), ['/AI?dreamAdvice=' + G1], 'it is still open: the chat page shows its task once more');
        clean();
    },

    async without_a_chat_on_the_node_nothing_is_opened_and_the_person_is_pointed_at_the_prompt() {
        const h = handoffGlobals();
        handoffRoutes(h);
        route('GET', '/status', () => status({ chatAvailable: false }));
        await boot(h);
        const before = calls.length;
        press('chat', G1);
        await flush();
        eq(calls.length, before, 'no request');
        eq([h.opened, h.navigations], [[], []], 'no tab, no move');
        assert(busy.results.some((r) => r[0] === false && /not available/.test(r[1]) && /Show prompt/.test(r[1])), 'the person is told what to use instead');
        clean();
    },

    async the_two_buttons_keep_their_placeholders_when_the_hand_off_script_is_not_there() {
        // dream.js alone (the hand-off script did not load, or the dialog is missing from the page): the placeholders stay and say so
        goodRoutes();
        const sandbox = await boot();
        eq(typeof sandbox.window.bmbDream.handoff.chat, 'function', 'the placeholder is a function');
        has(String(sandbox.window.bmbDream.handoff.chat), 'not wired yet', 'and it is the one of dream.js');
        clean();
    },

    async the_hand_off_calls_only_the_routes_of_the_contract() {
        const h = handoffGlobals();
        handoffRoutes(h);
        await boot(h);
        press('prompt', G2);
        await flush();
        press('chat', G1);
        await flush();
        const mine = calls.filter((c) => /\/prompt$/.test(c.path) || (c.method === 'POST' && /\/status$/.test(c.path)));
        eq(mine.map((c) => c.method + ' ' + c.path.replace(G1, 'G1').replace(G2, 'G2') + (c.query.target ? ' ' + c.query.target : '')),
            ['GET /advice/G2/prompt agent'], 'a prompt for an agent; "Open in AI chat" asks nothing and changes nothing here');
        clean();
    }
};

(async () => {
    try {
        if (!scenarios[scenario]) say(false, 'no such scenario: ' + scenario);
        await scenarios[scenario]();
        say(true, 'ok');
    } catch (e) {
        const extra = (problems.length ? ' [problems: ' + problems.join('; ') + ']' : '') + (process.env.DREAM_HARNESS_TRACE ? ' [calls: ' + calls.map((c) => c.method + ' ' + c.path).join(', ') + ']' : '');
        say(false, (e && e.message ? e.message : String(e)) + extra);
    }
})();
