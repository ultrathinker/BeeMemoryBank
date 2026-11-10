// Runs wwwroot/js/chat.js (the AI chat) with wwwroot/js/pages/ai-dream-draft.js in a fake page and checks what they do with the task that "Open in AI chat"
// on the Dream page brings: the Dream page opens /AI?dreamAdvice=<id> in a new tab; the chat page asks the node for that advice's task and shows it as a draft
// that is NOT sent. Run sends it once, in a new conversation, and marks the advice "in chat"; Edit puts it into the composer and sends nothing; Cancel removes
// it; a closed advice gives a notice in the node's words and nothing is sent; a bad or missing id does nothing; the parameter leaves the address at once. It
// also checks that the chat still sends what is typed, and that a turn of the task stops at the chat's own Allow / Deny card. The page is the real markup: the
// ids are read from AI.cshtml (or Tree.cshtml) and the _ChatUi.cshtml partial. It prints one JSON line {"ok": true|false, "detail": "..."} for the scenario
// named on the command line. The scenarios are the ones of DreamHandoffTests.cs.
//
// The scripts are run in the order of the real page, which the test gives (the order of the script tags of /AI and of /Tree as the layout and the page make
// it: the page's own scripts first, the layout's after them). The harness runs chat.js and ai-dream-draft.js where the page has them, puts the fake bmbBusy in
// where the page has bmb-busy.js, ignores the other scripts, and fires DOMContentLoaded when the last one is done. A script is given nothing that the
// scripts before it in that order would not have given it.
//
//   node chat-handoff-harness.js <chat.js> <ai-dream-draft.js> <AI.cshtml> <Tree.cshtml> <_ChatUi.cshtml> <order as JSON: {"ai":[src,...],"tree":[src,...]}> <scenario>
'use strict';
const fs = require('fs');
const vm = require('vm');

const [, , chatPath, draftPath, aiPath, treePath, chatUiPath, orderArg, scenario] = process.argv;
const ORDER = JSON.parse(orderArg);   // the script tags of each page in the order the browser runs them (src without the ?v= of the cache)

function idsOf(...files) {
    const ids = new Set();
    for (const f of files) for (const tag of fs.readFileSync(f, 'utf8').match(/<[a-z][^<>]*>/gi) || []) {
        const id = /\bid="([^"]+)"/.exec(tag);
        if (id) ids.add(id[1]);
    }
    return ids;
}
const PAGES = { ai: idsOf(aiPath, chatUiPath), tree: idsOf(treePath, chatUiPath) };

const problems = [];
const NL = String.fromCharCode(10);
process.on('unhandledRejection', (e) => problems.push('an unhandled rejection: ' + (e && e.message ? e.message : e)));
async function flush() { for (let i = 0; i < 200; i++) await Promise.resolve(); }

// ---------------------------------------------------------------- a small DOM: a tree of nodes, with the markup a script writes kept as it was written
class El {
    constructor(id, tag) {
        this.id = id || '';
        this.localName = tag || 'div';
        this._html = '';
        this._text = '';
        this.value = '';
        this.disabled = false;
        this.className = '';
        this.title = '';
        this.href = '';
        this.dataset = {};
        this.style = {};
        this.listeners = {};
        this.attrs = {};
        this.kids = [];
        this.parentNode = null;
        this.scrollTop = 0;
        this.scrollHeight = 0;
        this._qs = {};
    }
    get textContent() { return this.kids.length ? this.kids.map((k) => k.textContent).join('') : this._text; }
    set textContent(v) { for (const k of this.kids) k.parentNode = null; this.kids = []; this._text = String(v); }
    get innerHTML() { return this._html; }
    set innerHTML(v) { for (const k of this.kids) k.parentNode = null; this._html = String(v); this.kids = []; }
    get childElementCount() { return this.kids.length; }
    get firstChild() { return this.kids[0] || null; }
    get firstElementChild() { return this._qs.first || (this._qs.first = new El('', 'div')); }
    get isConnected() { let n = this; while (n.parentNode) n = n.parentNode; return !!n.isRoot; }
    get classList() {
        const el = this;
        return {
            add(...c) { for (const x of c) if (!el.className.split(/\s+/).includes(x)) el.className = (el.className + ' ' + x).trim(); },
            remove(...c) { el.className = el.className.split(/\s+/).filter((x) => x && !c.includes(x)).join(' '); },
            contains(c) { return el.className.split(/\s+/).includes(c); }
        };
    }
    setAttribute(n, v) { this.attrs[n] = String(v); }
    removeAttribute(n) { delete this.attrs[n]; }
    getAttribute(n) { return n in this.attrs ? this.attrs[n] : null; }
    hasAttribute(n) { return n in this.attrs; }
    appendChild(c) { if (c.parentNode) c.parentNode.removeChild(c); c.parentNode = this; this.kids.push(c); return c; }
    removeChild(c) { this.kids = this.kids.filter((k) => k !== c); c.parentNode = null; return c; }
    insertBefore(c, ref) {
        if (c.parentNode) c.parentNode.removeChild(c);
        const at = ref ? this.kids.indexOf(ref) : -1;
        c.parentNode = this;
        if (at < 0) this.kids.push(c); else this.kids.splice(at, 0, c);
        return c;
    }
    after() { }
    addEventListener(type, fn) { (this.listeners[type] = this.listeners[type] || []).push(fn); }
    removeEventListener() { }
    fire(type, event) { for (const fn of this.listeners[type] || []) fn(event || { target: this }); }
    querySelector(sel) {
        // the body of a bubble is made by the script itself out of the markup it wrote: what it holds is what follows the marker
        if (sel === '.chat-bubble-body') {
            const marker = '<div class="chat-bubble-body">';
            const at = this._html.indexOf(marker);
            if (at < 0) return null;
            const body = this._qs[sel] || (this._qs[sel] = new El('', 'div'));
            body._html = this._html.slice(at + marker.length).replace(/<\/div>$/, '');
            return body;
        }
        return this._qs[sel] || null;
    }
    querySelectorAll(sel) { return this.kids.filter((k) => sel === 'sl-button' && k.localName === 'sl-button'); }
    closest() { return null; }
    getBoundingClientRect() { return { left: 0, width: 0, top: 0 }; }
    focus() { this.focused = (this.focused || 0) + 1; }
    getContext() { return { drawImage() { } }; }
    toDataURL() { return 'data:image/jpeg;base64,QUJD'; }
    setSelectionRange(a, b) { this.selection = [a, b]; }
    click() { }
    select() { }
    show() { }
    hide() { }
}
// Every node under `root` (itself included) whose class list has `cls`.
function find(root, cls) {
    const out = [];
    (function walk(n) { if (n.classList.contains(cls)) out.push(n); for (const k of n.kids) walk(k); })(root);
    return out;
}
function buttonsOf(root) {
    const out = [];
    (function walk(n) { if (n.localName === 'sl-button') out.push(n); for (const k of n.kids) walk(k); })(root);
    return out;
}
function buttonNamed(root, label) { return buttonsOf(root).find((b) => b.textContent.trim() === label) || null; }

// ---------------------------------------------------------------- one run of the chat page in a fake page
// options: page ('ai' | 'tree'), search, hash (the address), draft (false: the draft script is not on the page), routes (the Dream API: see dreamRoutes),
//          stream (the frames the node answers a turn with, or { status, body } for a refusal before any stream), state (history.state),
//          without (scripts of the real order that are left out, by src: e.g. the busy helper), hold (true: stop before the page has loaded; the page's
//          finishLoading() ends it), swapped (true: the page is swapped in by the SPA navigation into a document that loaded long ago: the layout's scripts
//          have run, DOMContentLoaded is over)
const GUID = '0f8fad5b-d9cb-469f-a165-70867728950e';
const TASK = 'Put the note "Backup notes (copy)" in the trash.\nIf it changed since 12:00, do nothing and say so.';
const TITLE = 'Two notes say the same';
const TOKEN = 'token-1';

function dreamRoutes(extra) {
    return Object.assign({
        ['GET /advice/' + GUID + '/prompt']: () => ({ prompt: TASK, target: 'chat' }),
        ['GET /advice/' + GUID]: () => ({ id: GUID, title: TITLE, status: 'new', kind: 'duplicate_exact' }),
        ['POST /advice/' + GUID + '/status']: (c) => ({ id: GUID, status: c.body.status })
    }, extra || {});
}

async function openPage(options) {
    options = options || {};
    const ids = PAGES[options.page || 'ai'];
    const els = {};
    const created = [];
    const calls = [];
    const replaced = [];
    const documentListeners = {};
    const windowListeners = {};
    const root = new El('body', 'body');
    root.isRoot = true;
    const document = {
        hidden: false,
        readyState: 'loading',
        body: root,
        getElementById(id) {
            if (!ids.has(id)) return null;
            if (!els[id]) { els[id] = new El(id); root.appendChild(els[id]); }
            return els[id];
        },
        createElement(tag) { const e = new El('', tag); created.push(e); return e; },
        createTextNode(text) { const n = new El('', '#text'); n._text = String(text); return n; },
        querySelector(sel) { return sel === 'input[name="__RequestVerificationToken"]' ? { value: TOKEN } : null; },
        querySelectorAll() { return []; },
        addEventListener(type, fn) { (documentListeners[type] = documentListeners[type] || []).push(fn); },
        removeEventListener() { }
    };
    // the composer sits in its own box, which is where the line of an edited task goes (before the composer)
    const composer = document.getElementById('chat-composer');
    if (composer) { const outer = new El('', 'div'); outer.className = 'chat-composer-outer'; root.appendChild(outer); outer.appendChild(composer); }

    const localStorage = { getItem: () => null, setItem() { }, removeItem() { } };
    const routes = options.routes || dreamRoutes();
    let stream = options.stream || [
        'event: conversation\ndata: {"conversationId":"conv-1"}',
        'event: delta\ndata: {"text":"Working on it."}',
        'event: done\ndata: {"content":"Working on it.","conversationId":"conv-1"}'
    ];
    const json = (data, status) => Promise.resolve({
        ok: (status || 200) < 300, status: status || 200, redirected: false,
        headers: { get: (h) => (h.toLowerCase() === 'content-type' ? 'application/json' : null) },
        json: () => Promise.resolve(data), text: () => Promise.resolve(data === undefined ? '' : JSON.stringify(data))
    });
    function fakeFetch(url, init) {
        init = init || {};
        const method = (init.method || 'GET').toUpperCase();
        let body = null;
        try { body = init.body ? JSON.parse(init.body) : null; } catch (e) { body = init.body; }
        const u = new URL(url, 'http://node');
        const call = { method, url, path: u.pathname, query: Object.fromEntries(u.searchParams.entries()), body, headers: init.headers || {} };
        calls.push(call);
        if (url === '/api-proxy/chat/stream' || /\/api-proxy\/chat\/[^/]+\/confirm$/.test(url)) {
            const frames = typeof stream === 'function' ? stream(call) : stream;
            if (frames === 'hang') {   // an answer that has begun and does not end
                let first = true;
                return Promise.resolve({
                    ok: true, status: 200, redirected: false, url,
                    headers: { get: (h) => (h.toLowerCase() === 'content-type' ? 'text/event-stream' : null) },
                    body: { getReader: () => ({ read: () => (first ? (first = false, Promise.resolve({ done: false, value: new TextEncoder().encode('event: conversation' + NL + 'data: {"conversationId":"conv-9"}' + NL + NL) })) : new Promise(() => { })), cancel() { } }) }
                });
            }
            const sse = (read) => Promise.resolve({
                ok: true, status: 200, redirected: false, url,
                headers: { get: (h) => (h.toLowerCase() === 'content-type' ? 'text/event-stream' : null) },
                body: { getReader: () => ({ read, cancel() { } }) }
            });
            if (frames === 'offline') return Promise.reject(new TypeError('Failed to fetch'));                        // no answer at all
            if (frames === 'login') {    // the Web's sign-in check: a redirect that fetch follows to the page of /Login
                return Promise.resolve({
                    ok: true, status: 200, redirected: true, url: 'http://node/Login?ReturnUrl=%2Fapi-proxy%2Fchat%2Fstream',
                    headers: { get: (h) => (h.toLowerCase() === 'content-type' ? 'text/html; charset=utf-8' : null) },
                    text: () => Promise.resolve('<!DOCTYPE html><title>Login</title>')
                });
            }
            if (frames === 'drop') return sse(() => Promise.reject(new TypeError('network error')));                 // the stream breaks before any frame
            if (frames === 'empty') return sse(() => Promise.resolve({ done: true }));                                // a stream that ends with no frame
            if (frames === 'silent') {   // a stream that sends nothing until it is stopped: then the read fails as a browser's does
                return sse(() => new Promise((resolve, reject) => {
                    const fail = () => { const e = new Error('The operation was aborted.'); e.name = 'AbortError'; reject(e); };
                    if (init.signal && init.signal.aborted) fail(); else if (init.signal) init.signal.addEventListener('abort', fail);
                }));
            }
            if (!Array.isArray(frames)) return json(frames.body, frames.status);   // a refusal: JSON, not a stream
            const text = frames.map((f) => f + '\n\n').join('');
            let sent = false;
            return Promise.resolve({
                ok: true, status: 200, redirected: false, url,
                headers: { get: (h) => (h.toLowerCase() === 'content-type' ? 'text/event-stream' : null) },
                body: { getReader: () => ({ read: () => (sent ? Promise.resolve({ done: true }) : (sent = true, Promise.resolve({ done: false, value: new TextEncoder().encode(text) }))), cancel() { } }) }
            });
        }
        if (u.pathname.indexOf('/api-proxy/dream/') === 0) {
            const key = method + ' ' + u.pathname.slice('/api-proxy/dream'.length);
            const fn = routes[key];
            if (!fn) return json({ error: 'harness: no route for ' + key }, 404);
            const answer = (reply) => {
                if (reply === 'network') return Promise.reject(new TypeError('Failed to fetch'));
                if (!(reply && typeof reply.status === 'number' && 'body' in reply)) reply = { status: 200, body: reply };
                return json(reply.body, reply.status);
            };
            const reply = fn(call);
            return reply && typeof reply.then === 'function' ? reply.then(answer) : answer(reply);   // a route may answer when the scenario says so
        }
        if (url === '/api-proxy/chat/conversations') return json([]);
        if (/^\/api-proxy\/chat\/conversations\/[^/]+\/messages$/.test(url)) return json([]);
        if (url === '/api-proxy/chat/settings/effective-text-model') return json({ label: 'A model' });
        if (url === '/api-proxy/chat/home-pinned') return json({ conversationId: null });
        return json({}, 404);
    }

    // bmbBusy as the page uses it (bmb-busy.js has its own tests): request() turns a refusal into an Error with the node's words; run() locks its target while
    // the work goes on (a second press does nothing) and shows a failure through options.error, or as a line next to the target.
    const results = [];
    const running = new Set();
    const bmbBusy = {
        request(url, init) {
            return fakeFetch(url, init).then((response) => response.text().then((text) => {
                let parsed = null;
                try { parsed = text ? JSON.parse(text) : null; } catch (e) { parsed = null; }
                if (response.ok) return { status: response.status, body: parsed, text };
                const said = parsed && typeof parsed.error === 'string' ? parsed.error : '';
                const error = new Error(said || ('The node answered ' + response.status + '.'));
                error.status = response.status;
                error.body = parsed;
                throw error;
            }), () => { throw new Error('The node did not answer. Check that it is running and try again.'); });
        },
        run(target, work, opts) {
            opts = opts || {};
            if (target && running.has(target)) return Promise.resolve(false);
            if (target) running.add(target);
            return Promise.resolve().then(work).then(() => { running.delete(target); return true; }, (error) => {
                running.delete(target);
                if (opts.error) opts.error(error); else results.push({ anchor: target, ok: false, text: error.message });
                return false;
            });
        },
        showResult(anchor, kind, text) { results.push({ anchor, ok: kind === true || kind === 'success', text }); }
    };

    const path = options.page === 'tree' ? '/Tree' : '/AI';
    const location = { pathname: path, search: options.search || '', hash: options.hash || '', href: 'http://node' + path + (options.search || '') + (options.hash || '') };
    const history = {
        state: options.state === undefined ? { spa: true } : options.state,
        replaceState(state, title, url) {
            replaced.push({ state, url });
            const u = new URL(url, 'http://node');
            location.pathname = u.pathname; location.search = u.search; location.hash = u.hash; location.href = u.href;
            history.state = state;
        }
    };
    const sandbox = {
        document, fetch: fakeFetch, localStorage, console, Date, AbortController, TextDecoder, TextEncoder, URL, URLSearchParams, Promise,
        setTimeout: (fn) => { try { fn(); } catch (e) { problems.push('a timer threw: ' + e.message); } return 1; }, clearTimeout() { },
        marked: { parse: (t) => String(t) }, DOMPurify: { sanitize: (h) => String(h) },
        requestAnimationFrame: (fn) => fn(), location, history,
        FileReader: class { readAsDataURL() { this.result = 'data:image/png;base64,QUJD'; if (this.onload) this.onload(); } },
        Image: class { set src(v) { this._src = v; this.width = 10; this.height = 10; if (this.onload) this.onload(); } get src() { return this._src; } }
    };
    sandbox.window = sandbox;
    sandbox.addEventListener = (type, fn) => { (windowListeners[type] = windowListeners[type] || []).push(fn); };
    sandbox.removeEventListener = () => { };
    sandbox.innerWidth = 1200;
    vm.createContext(sandbox);
    // The scripts, as the browser runs them. A script is given nothing before the script that makes it has run: the busy helper exists from bmb-busy.js on,
    // and the document is 'loading' until the last script is done.
    const run = (file) => vm.runInContext(fs.readFileSync(file, 'utf8'), sandbox, { filename: file });
    const known = {
        '/js/bmb-busy.js': () => { sandbox.bmbBusy = bmbBusy; },
        '/js/pages/ai-dream-draft.js': () => { if (options.draft !== false) run(draftPath); },
        '/js/chat.js': () => run(chatPath)
    };
    const order = (ORDER[options.page || 'ai'] || []).filter((src) => !(options.without || []).includes(src));
    const runScripts = (list) => { for (const src of list) if (known[src]) known[src](); };
    let loaded = false;
    const finishLoading = async () => {
        if (loaded) return;
        loaded = true;
        document.readyState = 'interactive';
        for (const fn of documentListeners.DOMContentLoaded || []) fn({ type: 'DOMContentLoaded' });
        document.readyState = 'complete';
        await flush();
    };
    if (options.swapped) {
        // the layout is long loaded: its scripts have run; the page's own scripts are then run into it
        runScripts(order.includes('/js/bmb-busy.js') ? ['/js/bmb-busy.js'] : []);
        document.readyState = 'complete';
        runScripts(order.filter((src) => src !== '/js/bmb-busy.js'));
        loaded = true;
        await flush();
    } else {
        runScripts(order);
        if (options.hold) await flush(); else await finishLoading();
    }

    const messages = els['chat-messages'];
    const page = {
        els, calls, created, results, replaced, location, history, finishLoading,
        loaded: () => loaded,
        hasBusyHelper: () => typeof sandbox.bmbBusy !== 'undefined',
        setStream(s) { stream = s; },
        sends: () => calls.filter((c) => c.method === 'POST' && c.url === '/api-proxy/chat/stream'),
        dreamCalls: () => calls.filter((c) => c.path.indexOf('/api-proxy/dream/') === 0).map((c) => c.method + ' ' + c.path.slice('/api-proxy/dream'.length).replace(GUID, 'G') + (c.query.target ? '?target=' + c.query.target : '') + (c.body && c.body.status ? ' ' + c.body.status : '')),
        statusPosts: () => calls.filter((c) => c.method === 'POST' && /\/api-proxy\/dream\/advice\/[^/]+\/status$/.test(c.path)).map((c) => c.body.status),
        // the block of the draft (or of its notice) in the conversation, as it is on the page now
        drafts: () => find(messages, 'chat-dream-draft'),
        notices: () => find(messages, 'chat-dream-notice'),
        cards: () => find(messages, 'chat-dream-card'),
        link: () => (composer && composer.parentNode ? find(composer.parentNode, 'chat-dream-link') : [])[0] || null,
        // what the person sees as their own message: the "You" bubbles
        youSaid: () => created.filter((e) => /You<\/div>/.test(e._html) && e.isConnected).map((e) => (e.querySelector('.chat-bubble-body') || {})._html),
        press(label) {
            const b = buttonNamed(messages, label);
            if (!b) throw new Error('there is no "' + label + '" button in the conversation');
            b.fire('click');
            return b;
        },
        type(text) { els['chat-input'].value = text; els['chat-input'].fire('sl-input', { target: els['chat-input'] }); },
        attachImage() { const input = els['chat-attach-input']; input.files = [{ type: 'image/png', size: 100 }]; input.fire('change'); },
        enter(extra) {
            let prevented = false;
            els['chat-input'].fire('keydown', Object.assign({ key: 'Enter', shiftKey: false, ctrlKey: false, metaKey: false, preventDefault() { prevented = true; } }, extra || {}));
            return prevented;
        }
    };
    return page;
}

function say(ok, detail) { console.log(JSON.stringify({ ok, detail })); process.exit(0); }
function assert(cond, message) { if (!cond) throw new Error(message); }
function eq(actual, expected, what) {
    const a = JSON.stringify(actual), e = JSON.stringify(expected);
    if (a !== e) throw new Error(what + ': expected ' + e + ' but got ' + a);
}
function has(text, part, what) { if (String(text).indexOf(part) < 0) throw new Error(what + ': "' + part + '" is not in "' + String(text).slice(0, 400) + '"'); }
function clean() { eq(problems, [], 'something threw'); }
const ADDRESS = { search: '?dreamAdvice=' + GUID };
// A node answer that comes when the scenario releases it.
function later() {
    let release;
    const promise = new Promise((resolve) => { release = resolve; });
    return { promise, release: (reply) => release(reply) };
}
// The routes of an advice whose "in chat" mark waits for the scenario; the put-back answers at once.
function heldMark(held) {
    return dreamRoutes({ ['POST /advice/' + GUID + '/status']: (c) => (c.body.status === 'in_chat' ? held.promise : { id: GUID, status: c.body.status }) });
}
const closedBody = { error: 'This advice was already applied on 3 Nov 2026, 10:00.', code: 'not_applicable', status: 'applied', statusAt: '2026-11-03T10:00:00Z' };

const scenarios = {
    // ---- the draft ---------------------------------------------------------------------------------------------------------------------
    async the_task_is_shown_as_a_draft_and_nothing_is_sent() {
        const page = await openPage(ADDRESS);
        eq(page.drafts().length, 1, 'one draft in the conversation');
        const draft = page.drafts()[0];
        has(draft.textContent, 'Draft from Dream', 'it says where it is from');
        has(draft.textContent, 'not sent yet', 'and that it has not been sent');
        eq(find(draft, 'chat-dream-text')[0].textContent, TASK, 'the whole task, exactly as the node wrote it');
        eq(find(draft, 'chat-dream-title')[0].textContent, TITLE, 'with the title of the advice');
        eq(find(draft, 'chat-dream-back')[0].href, '/Dream?advice=' + GUID, 'and the way back to the advice');
        eq(buttonsOf(draft).map((b) => b.textContent.trim()), ['Run', 'Edit', 'Cancel'], 'under it: Run, Edit and Cancel');
        eq(page.sends().length, 0, 'nothing reached the model');
        eq(page.statusPosts(), [], 'and the advice is not marked');
        eq(page.dreamCalls().sort(), ['GET /advice/G', 'GET /advice/G/prompt?target=chat'], 'the node was asked for the task for the chat, and the advice');
        eq(page.youSaid(), [], 'no message of the person is in the conversation');
        eq(page.els['chat-input'].value, '', 'and the composer is empty');
        clean();
    },

    async the_parameter_leaves_the_address_at_once_and_a_reload_brings_no_draft() {
        const page = await openPage({ search: '?dreamAdvice=' + GUID + '&x=1', hash: '#here', state: { spa: true, n: 3 } });
        eq(page.replaced.map((r) => r.url), ['/AI?x=1#here'], 'the address loses the parameter, and only that');
        eq(page.replaced[0].state, { spa: true, n: 3 }, 'the history state of the page stays as it was');
        eq(page.drafts().length, 1, 'the draft is shown');
        // a reload is the address as it is now
        const again = await openPage({ search: page.location.search, hash: page.location.hash });
        eq([again.drafts().length, again.dreamCalls(), again.replaced], [0, [], []], 'a reload shows no draft and asks the node nothing');
        clean();
    },

    async a_bad_or_missing_id_does_nothing() {
        for (const search of ['', '?x=1', '?dreamAdvice=', '?dreamAdvice=not-an-id', '?dreamAdvice=' + GUID + 'x', '?dreamAdvice=../../status', '?dreamAdvice=%3Cscript%3E']) {
            const page = await openPage({ search });
            eq(page.dreamCalls(), [], 'the node is asked nothing for "' + search + '"');
            eq(page.cards().length, 0, 'and nothing is shown for "' + search + '"');
            if (/dreamAdvice/.test(search)) eq(page.replaced.map((r) => r.url), ['/AI'], 'the parameter leaves the address anyway: "' + search + '"');
            else eq(page.replaced, [], 'an address without it is not touched: "' + search + '"');
        }
        clean();
    },

    async the_rest_of_the_address_never_starts_a_task() {
        const page = await openPage({
            search: '?message=' + encodeURIComponent(TASK) + '&prompt=' + encodeURIComponent(TASK) + '&handoff=' + GUID + '&q=' + encodeURIComponent(TASK),
            hash: '#' + encodeURIComponent(TASK)
        });
        eq([page.sends().length, page.dreamCalls(), page.cards().length], [0, [], 0], 'a link with a task in it starts nothing and shows nothing');
        eq(page.els['chat-input'].value, '', 'and puts nothing in the field');
        clean();
    },

    async the_task_goes_on_the_page_as_text_never_as_markup() {
        const evil = 'Trash "<img src=x onerror=alert(1)>" and <script>alert(2)</script>';
        const page = await openPage(Object.assign({ routes: dreamRoutes({
            ['GET /advice/' + GUID + '/prompt']: () => ({ prompt: evil, target: 'chat' }),
            ['GET /advice/' + GUID]: () => ({ id: GUID, title: '<b onclick=x>Title</b>', status: 'new' })
        }) }, ADDRESS));
        const draft = page.drafts()[0];
        eq(find(draft, 'chat-dream-text')[0].textContent, evil, 'the text is there exactly');
        eq(find(draft, 'chat-dream-title')[0].textContent, '<b onclick=x>Title</b>', 'and so is the title, as text');
        const marked = page.created.filter((e) => /<img|<script|<b /.test(e._html));
        eq(marked.length, 0, 'none of it was written into the page as markup');
        clean();
    },

    async the_homepage_chat_shows_no_draft() {
        const page = await openPage(Object.assign({ page: 'tree' }, ADDRESS));
        eq([page.dreamCalls(), page.cards().length, page.sends().length], [[], 0, 0], 'the chat of /Tree asks nothing, shows nothing, sends nothing');
        clean();
    },

    async a_chat_page_without_the_draft_script_works_as_before() {
        const page = await openPage(Object.assign({ draft: false }, ADDRESS));
        eq([page.dreamCalls(), page.cards().length], [[], 0], 'nothing reads the advice');
        page.type('hello');
        page.enter();
        await flush();
        eq(page.sends().map((c) => c.body.message), ['hello'], 'the chat sends what is typed');
        clean();
    },

    // ---- the order of the real page: the page's own scripts come first, the layout's (bmb-busy.js) after them -------------------------------
    async the_draft_starts_in_the_order_of_the_real_page_where_the_busy_helper_comes_after_chat_js() {
        const order = ORDER.ai;
        const at = (src) => order.indexOf(src);
        assert(at('/js/chat.js') >= 0 && at('/js/pages/ai-dream-draft.js') >= 0 && at('/js/bmb-busy.js') >= 0, 'the page has chat.js, the draft and the busy helper: ' + order.join(' '));
        assert(at('/js/pages/ai-dream-draft.js') < at('/js/chat.js'), 'the draft comes before chat.js, which starts it');
        assert(at('/js/chat.js') < at('/js/bmb-busy.js'), 'on the real page the layout\'s busy helper comes AFTER chat.js: ' + order.join(' '));
        const page = await openPage(ADDRESS);
        eq(page.loaded(), true, 'the page has loaded');
        eq(page.drafts().length, 1, 'the draft is shown');
        eq(page.dreamCalls().sort(), ['GET /advice/G', 'GET /advice/G/prompt?target=chat'], 'it asked the node for the task and the advice');
        eq(page.replaced.map((r) => r.url), ['/AI'], 'the parameter is out of the address');
        eq([page.sends().length, page.statusPosts(), page.youSaid()], [0, [], []], 'nothing was sent, nothing marked, no message of the person');
        eq(page.els['chat-input'].value, '', 'the composer is empty');
        page.press('Run');
        await flush();
        eq([page.sends().map((c) => c.body.message), page.statusPosts()], [[TASK], ['in_chat']], 'and the buttons work: Run sends the task once and marks the advice');
        clean();
    },

    async the_draft_waits_for_the_page_to_load_and_takes_the_parameter_out_of_the_address_at_once() {
        const page = await openPage(Object.assign({ hold: true }, ADDRESS));
        eq(page.loaded(), false, 'the page is still loading');
        eq(page.hasBusyHelper(), true, 'every script of the page has run by now, the layout\'s busy helper too');
        eq(page.replaced.map((r) => r.url), ['/AI'], 'the parameter left the address while the page was loading');
        eq([page.dreamCalls(), page.cards().length], [[], 0], 'nothing is asked and nothing is shown before the page has loaded');
        await page.finishLoading();
        eq(page.drafts().length, 1, 'once it has loaded, the draft is shown');
        eq(page.dreamCalls().sort(), ['GET /advice/G', 'GET /advice/G/prompt?target=chat'], 'after asking the node for the task and the advice');
        eq(page.sends().length, 0, 'and nothing was sent');
        clean();
    },

    async a_page_swapped_in_by_the_spa_navigation_starts_the_draft_at_once() {
        const page = await openPage(Object.assign({ swapped: true }, ADDRESS));
        eq(page.drafts().length, 1, 'the layout has loaded long ago: the draft is shown without waiting for an event that is over');
        eq(page.replaced.map((r) => r.url), ['/AI'], 'the parameter is out of the address');
        eq(page.sends().length, 0, 'nothing was sent');
        clean();
    },

    async a_page_without_the_busy_helper_shows_no_draft_and_sends_nothing() {
        const page = await openPage(Object.assign({ without: ['/js/bmb-busy.js'] }, ADDRESS));
        eq(page.hasBusyHelper(), false, 'there is no busy helper on this page');
        eq([page.dreamCalls(), page.cards().length, page.sends().length], [[], 0, 0], 'the draft asks nothing, shows nothing and sends nothing');
        eq(page.replaced.map((r) => r.url), ['/AI'], 'the parameter left the address anyway');
        page.type('hello');
        page.enter();
        await flush();
        eq(page.sends().map((c) => c.body.message), ['hello'], 'the chat itself works');
        clean();
    },

    // ---- Run -----------------------------------------------------------------------------------------------------------------------------
    async run_sends_the_text_once_in_a_new_conversation_and_marks_the_advice() {
        const page = await openPage(ADDRESS);
        page.type('my own words, not sent');
        page.press('Run');
        page.press('Run');       // a second press while the first is on its way does nothing
        await flush();
        eq(page.statusPosts(), ['in_chat'], 'the advice is marked "in chat" once');
        const post = page.calls.find((c) => c.method === 'POST' && /\/status$/.test(c.path));
        eq(post.headers['RequestVerificationToken'], TOKEN, 'with the anti-forgery token of the page');
        const sends = page.sends();
        eq(sends.length, 1, 'one turn is sent');
        eq([sends[0].body.conversationId, sends[0].body.message], [null, TASK], 'in a new conversation, with exactly the task as the message');
        assert(typeof sends[0].body.systemPrompt === 'string' && sends[0].body.systemPrompt.length > 0, 'with the chat\'s own system prompt, as every turn has');
        eq(sends[0].body.attachments, undefined, 'and no attachments');
        assert(page.calls.indexOf(post) < page.calls.indexOf(sends[0]), 'the advice is marked before the task goes');
        eq(page.youSaid().length, 1, 'the person sees the task as their own message');
        has(page.youSaid()[0], 'Put the note', 'the task');
        eq(page.cards().length, 0, 'and the draft is gone');
        eq(page.els['chat-input'].value, 'my own words, not sent', 'what the person had typed is still in the composer');
        clean();
    },

    async run_after_an_ordinary_message_starts_a_new_conversation() {
        const page = await openPage(ADDRESS);
        page.type('How many notes do I have?');
        page.enter();
        await flush();
        eq(page.sends().map((c) => c.body.conversationId), [null], 'the ordinary message made a conversation');
        eq(page.drafts().length, 1, 'the draft is still there');
        page.press('Run');
        await flush();
        eq(page.sends().map((c) => [c.body.conversationId, c.body.message]), [[null, 'How many notes do I have?'], [null, TASK]], 'Run starts a new conversation');
        eq(page.statusPosts(), ['in_chat'], 'and marks the advice');
        clean();
    },

    async an_advice_that_is_already_in_chat_is_sent_again_without_a_second_mark() {
        const page = await openPage(Object.assign({ routes: dreamRoutes({
            ['GET /advice/' + GUID]: () => ({ id: GUID, title: TITLE, status: 'in_chat' }),
            ['POST /advice/' + GUID + '/status']: () => ({ status: 409, body: { error: 'An advice that is in_chat cannot be set to in_chat.', code: 'not_applicable' } })
        }) }, ADDRESS));
        page.press('Run');
        await flush();
        eq(page.dreamCalls().slice(2), ['POST /advice/G/status in_chat', 'GET /advice/G', 'GET /advice/G/prompt?target=chat'],
            'the node says it is already in chat; the advice is read, and the node checks once more that it still stands');
        eq(page.sends().map((c) => c.body.message), [TASK], 'the task is sent');
        eq(page.notices().length, 0, 'and no notice is shown');
        clean();
    },

    async a_turn_of_the_task_stops_at_the_chats_own_card_and_changes_nothing_until_allow() {
        const page = await openPage(Object.assign({
            stream: [
                'event: conversation\ndata: {"conversationId":"conv-1"}',
                'event: confirm_required\ndata: {"toolCallId":"call-1","toolName":"bee_delete_article","argsSummary":"delete the note Backup notes (copy)"}'
            ]
        }, ADDRESS));
        page.press('Run');
        await flush();
        eq(page.sends().length, 1, 'the task was sent');
        const cards = page.created.filter((e) => e.className === 'chat-confirm-card');
        eq(cards.length, 1, 'the chat shows its confirmation card');
        assert(cards[0].kids[0]._html.indexOf('delete the note Backup notes (copy)') >= 0, 'with what the AI wants to do');
        const buttons = page.created.filter((e) => e.localName === 'sl-button' && e.parentNode && e.parentNode.parentNode === cards[0]);
        eq(buttons.length, 2, 'Allow and Deny');
        eq(page.calls.filter((c) => /\/confirm$/.test(c.url)).length, 0, 'nothing is allowed until the person says so');
        buttons[1].fire('click');
        await flush();
        const confirms = page.calls.filter((c) => /\/confirm$/.test(c.url));
        eq(confirms.map((c) => [c.url, c.body.toolCallId, c.body.allow]), [['/api-proxy/chat/conv-1/confirm', 'call-1', false]], 'Deny is sent as a denial');
        eq(page.statusPosts(), ['in_chat'], 'the advice was marked once, when the task went');
        clean();
    },

    async a_turn_the_chat_refuses_puts_the_advice_back_and_the_draft_returns() {
        const page = await openPage(Object.assign({ stream: { status: 409, body: { error: 'The vault is locked. Unlock it and try again.' } } }, ADDRESS));
        page.press('Run');
        await flush();
        eq(page.sends().length, 1, 'the task was offered to the chat');
        eq(page.statusPosts(), ['in_chat', 'new'], 'it refused before making a conversation, so the advice goes back to new');
        eq(page.drafts().length, 1, 'and the draft comes back for another try');
        eq(find(page.drafts()[0], 'chat-dream-text')[0].textContent, TASK, 'the same text');
        // the second try goes through
        page.setStream(['event: conversation\ndata: {"conversationId":"conv-2"}', 'event: done\ndata: {"content":"Done.","conversationId":"conv-2"}']);
        page.press('Run');
        await flush();
        eq(page.statusPosts(), ['in_chat', 'new', 'in_chat'], 'marked again');
        eq(page.sends().length, 2, 'and sent');
        eq(page.drafts().length, 0, 'the draft is gone');
        clean();
    },

    async run_while_the_chat_is_answering_waits_and_sends_nothing() {
        const page = await openPage(Object.assign({ stream: 'hang' }, ADDRESS));
        page.type('hello');
        page.enter();
        await flush();
        eq(page.sends().length, 1, 'an ordinary message is on its way, and its answer has not ended');
        const run = page.press('Run');
        await flush();
        eq([page.sends().length, page.statusPosts()], [1, []], 'Run sends nothing and marks nothing meanwhile');
        assert(page.results.some((r) => r.anchor === run && r.ok === false && /still answering/.test(r.text)), 'and says why at the button');
        eq(page.drafts().length, 1, 'the draft stays');
        clean();
    },

    // ---- Edit ----------------------------------------------------------------------------------------------------------------------------
    async edit_puts_the_whole_text_in_the_composer_and_sends_nothing() {
        const page = await openPage(ADDRESS);
        page.press('Edit');
        await flush();
        eq(page.els['chat-input'].value, TASK, 'the whole task is in the composer');
        eq(page.cards().length, 0, 'the draft is gone');
        assert(page.els['chat-input'].focused > 0, 'the composer has the focus');
        eq(page.els['chat-input'].selection, [0, 0], 'at the start of the text');
        const link = page.link();
        assert(link, 'a line above the composer says what the text is');
        has(link.textContent, TITLE, 'for which advice');
        eq(find(link, 'chat-dream-back')[0].href, '/Dream?advice=' + GUID, 'with the way back');
        eq([page.sends().length, page.statusPosts()], [0, []], 'nothing is sent and the advice is not marked');
        clean();
    },

    async the_edited_text_is_sent_with_enter_and_marks_the_advice() {
        const page = await openPage(ADDRESS);
        page.press('Edit');
        page.type(TASK + '\nAlso tell me how many notes link to it.');
        page.enter();
        await flush();
        eq(page.statusPosts(), ['in_chat'], 'the advice is marked when the text is sent');
        eq(page.sends().map((c) => c.body.message), [TASK + '\nAlso tell me how many notes link to it.'], 'the edited text is the message');
        eq(page.els['chat-input'].value, '', 'the composer is emptied, as after any send');
        eq(page.link(), null, 'and the line above it is gone');
        // the next message is an ordinary one
        page.type('Thanks');
        page.enter();
        await flush();
        eq(page.statusPosts(), ['in_chat'], 'it marks nothing');
        eq(page.sends().length, 2, 'and is sent');
        clean();
    },

    async an_edited_task_that_is_emptied_or_unlinked_is_an_ordinary_message() {
        const page = await openPage(ADDRESS);
        page.press('Edit');
        page.type('');
        eq(page.link(), null, 'emptying the box takes the line away');
        page.type('hello');
        page.enter();
        await flush();
        eq([page.statusPosts(), page.sends().map((c) => c.body.message)], [[], ['hello']], 'what is typed then is the person\'s own message');
        // the same with the x of the line
        const second = await openPage(ADDRESS);
        second.press('Edit');
        const off = second.created.find((e) => e.localName === 'sl-icon-button' && e.parentNode === second.link());
        off.fire('click');
        eq(second.link(), null, 'the x takes the line away');
        second.enter();
        await flush();
        eq([second.statusPosts(), second.sends().map((c) => c.body.message)], [[], [TASK]], 'and the text goes as an ordinary message, without marking the advice');
        clean();
    },

    // ---- Cancel, and leaving the draft ------------------------------------------------------------------------------------------------
    async cancel_removes_the_draft_and_changes_nothing() {
        const page = await openPage(ADDRESS);
        page.press('Cancel');
        await flush();
        eq(page.cards().length, 0, 'the draft is gone');
        eq([page.sends().length, page.statusPosts(), page.els['chat-input'].value], [0, [], ''], 'nothing is sent, nothing is marked, nothing is in the composer');
        clean();
    },

    async another_conversation_or_a_new_chat_takes_the_draft_away() {
        const page = await openPage(ADDRESS);
        page.els['chat-new'].fire('click');
        await flush();
        eq([page.cards().length, page.sends().length, page.statusPosts()], [0, 0, []], 'New chat empties the view: the draft goes, nothing is sent');
        clean();
    },

    // ---- a closed advice ---------------------------------------------------------------------------------------------------------------
    async an_advice_that_is_closed_when_the_tab_loads_gives_a_notice_and_sends_nothing() {
        const cases = [
            closedBody,
            { error: 'This advice was dismissed on 2 Nov 2026, 09:12.', code: 'not_applicable', status: 'dismissed' },
            { error: 'This advice expired: the note "Backup notes" changed after the advice was made.', code: 'expired', status: 'expired' }
        ];
        for (const body of cases) {
            const page = await openPage(Object.assign({ routes: dreamRoutes({
                ['GET /advice/' + GUID + '/prompt']: () => ({ status: 409, body }),
                ['GET /advice/' + GUID]: () => ({ id: GUID, title: TITLE, status: body.status })
            }) }, ADDRESS));
            eq([page.drafts().length, page.notices().length], [0, 1], 'a notice and no draft: ' + body.status);
            const notice = page.notices()[0];
            eq(find(notice, 'chat-dream-message')[0].textContent, body.error, 'in the node\'s words');
            has(notice.textContent, 'not sent', 'it says that nothing was sent');
            eq(find(notice, 'chat-dream-back')[0].href, '/Dream?advice=' + GUID, 'with the way back');
            eq(buttonsOf(notice).map((b) => b.textContent.trim()), ['Close'], 'and nothing to run');
            eq([page.sends().length, page.statusPosts()], [0, []], 'nothing is sent or marked');
            page.press('Close');
            eq(page.cards().length, 0, 'Close takes it away');
        }
        clean();
    },

    async an_advice_closed_before_run_gives_the_notice_in_place_of_the_draft() {
        const page = await openPage(Object.assign({ routes: dreamRoutes({
            ['POST /advice/' + GUID + '/status']: () => ({ status: 409, body: closedBody })
        }) }, ADDRESS));
        page.press('Run');
        await flush();
        eq([page.drafts().length, page.notices().length], [0, 1], 'the notice takes the place of the draft');
        eq(find(page.notices()[0], 'chat-dream-message')[0].textContent, closedBody.error, 'in the node\'s words');
        eq(page.sends().length, 0, 'and nothing is sent');
        clean();
    },

    async an_advice_closed_before_the_edited_text_is_sent_gives_the_notice_and_keeps_the_text() {
        const page = await openPage(Object.assign({ routes: dreamRoutes({
            ['POST /advice/' + GUID + '/status']: () => ({ status: 409, body: closedBody })
        }) }, ADDRESS));
        page.press('Edit');
        page.type(TASK + ' (edited)');
        page.enter();
        await flush();
        eq(page.sends().length, 0, 'nothing is sent');
        eq(page.notices().length, 1, 'a notice says why');
        eq(find(page.notices()[0], 'chat-dream-message')[0].textContent, closedBody.error, 'in the node\'s words');
        eq(page.els['chat-input'].value, TASK + ' (edited)', 'the edited text stays in the composer');
        eq(page.link(), null, 'no longer tied to the advice');
        eq(page.els['chat-input'].disabled, false, 'and the composer can be used');
        clean();
    },

    async a_locked_vault_at_run_is_said_at_the_button_and_the_draft_stays() {
        const page = await openPage(Object.assign({ routes: dreamRoutes({
            ['POST /advice/' + GUID + '/status']: () => ({ status: 409, body: { error: 'The vault is locked.', code: 'locked' } })
        }) }, ADDRESS));
        const run = page.press('Run');
        await flush();
        eq([page.drafts().length, page.notices().length, page.sends().length], [1, 0, 0], 'the draft stays, no notice, nothing sent');
        assert(page.results.some((r) => r.anchor === run && r.ok === false && r.text === 'The vault is locked.'), 'the reason is shown at the Run button');
        clean();
    },

    // ---- loading -------------------------------------------------------------------------------------------------------------------------
    async a_load_that_fails_for_another_reason_says_so_and_can_be_tried_again() {
        let fail = true;
        const page = await openPage(Object.assign({ routes: dreamRoutes({
            ['GET /advice/' + GUID + '/prompt']: () => (fail ? 'network' : { prompt: TASK, target: 'chat' })
        }) }, ADDRESS));
        eq(page.notices().length, 1, 'a notice');
        has(page.notices()[0].textContent, 'did not answer', 'that says what went wrong');
        fail = false;
        page.press('Try again');
        await flush();
        eq([page.notices().length, page.drafts().length], [0, 1], 'Try again brings the draft');
        eq(page.sends().length, 0, 'and still sends nothing');
        clean();
    },

    async an_advice_the_node_does_not_know_gives_a_notice_without_try_again() {
        const page = await openPage(Object.assign({ routes: dreamRoutes({
            ['GET /advice/' + GUID + '/prompt']: () => ({ status: 404, body: { error: 'Advice not found.', code: 'not_found' } }),
            ['GET /advice/' + GUID]: () => ({ status: 404, body: { error: 'Advice not found.', code: 'not_found' } })
        }) }, ADDRESS));
        eq(page.notices().length, 1, 'a notice');
        eq(find(page.notices()[0], 'chat-dream-message')[0].textContent, 'Advice not found.', 'in the node\'s words');
        eq(buttonsOf(page.notices()[0]).map((b) => b.textContent.trim()), ['Close'], 'nothing to try again');
        clean();
    },

    async somebody_who_may_not_use_dream_sees_nothing() {
        const page = await openPage(Object.assign({ routes: dreamRoutes({
            ['GET /advice/' + GUID + '/prompt']: () => ({ status: 403, body: { error: 'Forbidden' } }),
            ['GET /advice/' + GUID]: () => ({ status: 403, body: { error: 'Forbidden' } })
        }) }, ADDRESS));
        eq([page.cards().length, page.sends().length], [0, 0], 'no draft, no notice, nothing sent');
        clean();
    },

    async a_task_that_is_empty_or_too_long_for_the_chat_is_not_offered() {
        for (const prompt of ['', '   ', 'x'.repeat(100001)]) {
            const page = await openPage(Object.assign({ routes: dreamRoutes({ ['GET /advice/' + GUID + '/prompt']: () => ({ prompt, target: 'chat' }) }) }, ADDRESS));
            eq([page.drafts().length, page.notices().length], [0, 1], 'a notice instead of a draft for a task of ' + prompt.length + ' characters');
        }
        const largest = await openPage(Object.assign({ routes: dreamRoutes({ ['GET /advice/' + GUID + '/prompt']: () => ({ prompt: 'x'.repeat(100000), target: 'chat' }) }) }, ADDRESS));
        eq(largest.drafts().length, 1, 'the largest task the chat takes is a draft');
        clean();
    },

    // ---- the review's races: Run or the Send of the edited text against Cancel, Edit and unlink ---------------------------------------
    async cancel_and_edit_wait_while_run_is_checking_the_advice() {
        const held = later();
        const page = await openPage(Object.assign({ routes: heldMark(held) }, ADDRESS));
        const draft = page.drafts()[0];
        page.press('Run');
        await flush();
        eq([buttonNamed(draft, 'Edit').disabled, buttonNamed(draft, 'Cancel').disabled], [true, true], 'Edit and Cancel wait while the node checks');
        page.press('Run');
        await flush();
        eq(page.statusPosts(), ['in_chat'], 'a second Run during the check asks nothing more');
        held.release({ id: GUID, status: 'in_chat' });
        await flush();
        eq(page.sends().map((c) => c.body.message), [TASK], 'then the task goes once');
        clean();
    },

    async cancel_during_run_s_check_sends_nothing_and_takes_the_mark_back() {
        const held = later();
        const page = await openPage(Object.assign({ routes: heldMark(held) }, ADDRESS));
        page.press('Run');
        await flush();
        page.press('Cancel');      // the button waits in a browser; the press is made anyway, to show that the attempt is void
        await flush();
        eq(page.cards().length, 0, 'the draft is gone');
        held.release({ id: GUID, status: 'in_chat' });
        await flush();
        eq(page.sends().length, 0, 'the late answer sends nothing');
        eq(page.statusPosts(), ['in_chat', 'new'], 'and the mark the check made goes back');
        eq(page.cards().length, 0, 'nothing comes back on the page');
        clean();
    },

    async a_refused_mark_after_cancel_puts_nothing_back_and_shows_nothing() {
        const held = later();
        const page = await openPage(Object.assign({ routes: heldMark(held) }, ADDRESS));
        page.press('Run');
        await flush();
        page.press('Cancel');
        held.release({ status: 409, body: closedBody });
        await flush();
        eq([page.sends().length, page.statusPosts(), page.cards().length], [0, ['in_chat'], 0], 'no send, nothing to put back, no notice for a draft that was cancelled');
        clean();
    },

    async edit_during_run_s_check_sends_nothing_and_leaves_the_text_in_the_composer() {
        const held = later();
        const page = await openPage(Object.assign({ routes: heldMark(held) }, ADDRESS));
        page.press('Run');
        await flush();
        page.press('Edit');
        await flush();
        held.release({ id: GUID, status: 'in_chat' });
        await flush();
        eq(page.sends().length, 0, 'the late answer of Run sends nothing');
        eq(page.statusPosts(), ['in_chat', 'new'], 'and its mark goes back');
        eq(page.els['chat-input'].value, TASK, 'the text is in the composer, as Edit put it');
        assert(page.link(), 'tied to the advice for the person\'s own Send');
        page.enter();
        await flush();
        eq(page.statusPosts(), ['in_chat', 'new', 'in_chat'], 'that Send marks it again');
        eq(page.sends().map((c) => c.body.message), [TASK], 'and sends it once');
        clean();
    },

    async a_new_chat_during_run_s_check_sends_nothing_and_takes_the_mark_back() {
        const held = later();
        const page = await openPage(Object.assign({ routes: heldMark(held) }, ADDRESS));
        page.press('Run');
        await flush();
        page.els['chat-new'].fire('click');
        await flush();
        held.release({ id: GUID, status: 'in_chat' });
        await flush();
        eq([page.sends().length, page.statusPosts()], [0, ['in_chat', 'new']], 'leaving the view voids the Run');
        clean();
    },

    async the_unlink_control_waits_while_the_edited_text_is_checked_and_a_late_answer_sends_nothing() {
        const held = later();
        const page = await openPage(Object.assign({ routes: heldMark(held) }, ADDRESS));
        page.press('Edit');
        page.type(TASK + ' (edited)');
        page.enter();
        await flush();
        const off = page.created.find((e) => e.localName === 'sl-icon-button' && e.parentNode === page.link());
        eq([off.disabled, page.els['chat-input'].disabled], [true, true], 'the unlink control and the composer wait while the node checks');
        off.fire('click');         // made anyway, to show that the attempt is void
        await flush();
        eq(page.link(), null, 'unlinked');
        held.release({ id: GUID, status: 'in_chat' });
        await flush();
        eq(page.sends().length, 0, 'the late answer sends nothing');
        eq(page.statusPosts(), ['in_chat', 'new'], 'and the mark goes back');
        eq([page.els['chat-input'].value, page.els['chat-input'].disabled], [TASK + ' (edited)', false], 'the text stays in the composer, which can be used');
        page.enter();
        await flush();
        eq([page.statusPosts().length, page.sends().map((c) => c.body.message)], [2, [TASK + ' (edited)']], 'its next Send is an ordinary message');
        clean();
    },

    // ---- the review's interrupted stream: only a clear refusal gives the task back --------------------------------------------------
    async a_stream_that_breaks_before_the_node_confirms_leaves_the_advice_in_chat_and_offers_no_run() {
        for (const stream of ['drop', 'empty', 'offline']) {
            const page = await openPage(Object.assign({ stream }, ADDRESS));
            page.press('Run');
            await flush();
            eq(page.sends().length, 1, 'the task was sent (' + stream + ')');
            eq(page.statusPosts(), ['in_chat'], 'the advice stays "in chat": the message may be saved (' + stream + ')');
            eq(page.drafts().length, 0, 'no draft comes back (' + stream + ')');
            const notice = page.notices()[0];
            assert(notice && notice.classList.contains('chat-dream-unconfirmed'), 'a warning instead (' + stream + ')');
            eq(find(notice, 'chat-dream-message')[0].textContent, 'The connection broke while sending. Check the conversation list before sending again.', 'in these words (' + stream + ')');
            eq(find(notice, 'chat-dream-text')[0].textContent, TASK, 'with the text, to be copied (' + stream + ')');
            eq(buttonsOf(notice).map((b) => b.textContent.trim()), ['Close'], 'and no Run (' + stream + ')');
            eq(find(notice, 'chat-dream-back')[0].href, '/Dream?advice=' + GUID, 'and the way back (' + stream + ')');
        }
        clean();
    },

    async a_send_stopped_before_the_node_confirms_is_not_given_back_either() {
        const page = await openPage(Object.assign({ stream: 'silent' }, ADDRESS));
        page.press('Run');
        await flush();
        page.els['chat-stop'].fire('click');
        await flush();
        eq([page.sends().length, page.statusPosts(), page.drafts().length], [1, ['in_chat'], 0], 'sent, still "in chat", no draft');
        eq(find(page.notices()[0], 'chat-dream-message')[0].textContent, 'Sending was stopped before the chat confirmed it. Check the conversation list before sending again.', 'the warning says so');
        clean();
    },

    async an_edited_text_whose_stream_breaks_is_kept_in_the_warning_and_not_linked_again() {
        const page = await openPage(Object.assign({ stream: 'drop' }, ADDRESS));
        page.press('Edit');
        page.type(TASK + ' (edited)');
        page.enter();
        await flush();
        eq(page.statusPosts(), ['in_chat'], 'the advice stays "in chat"');
        eq(find(page.notices()[0], 'chat-dream-text')[0].textContent, TASK + ' (edited)', 'the edited text is in the warning');
        eq([page.link(), page.els['chat-input'].value], [null, ''], 'the composer holds nothing that would send it again as the task');
        clean();
    },

    async a_clear_refusal_of_the_edited_text_gives_it_back_and_takes_the_mark_back() {
        const page = await openPage(Object.assign({ stream: { status: 409, body: { error: 'The vault is locked. Unlock it and try again.' } } }, ADDRESS));
        page.press('Edit');
        page.enter();
        await flush();
        eq(page.statusPosts(), ['in_chat', 'new'], 'the node refused the turn: the mark goes back');
        eq(page.els['chat-input'].value, TASK, 'the text is back in the composer');
        assert(page.link(), 'tied to the advice again');
        clean();
    },

    // ---- the review's closed advice in the parallel load ------------------------------------------------------------------------------
    async an_advice_closed_between_the_two_answers_of_the_load_gets_the_notice_not_a_draft() {
        const cases = [
            [{ status: 'applied', statusAt: '2026-11-03T10:00:00Z' }, /^This advice was already applied on .+\.$/, ['Close']],
            [{ status: 'dismissed', statusAt: '2026-11-02T09:12:00Z' }, /^This advice was dismissed on .+\.$/, ['Close']],
            [{ status: 'expired', expiredReason: 'The note "Backup notes" changed.' }, /^This advice expired: The note "Backup notes" changed\.$/, ['Close']],
            [{ status: 'applying' }, /being applied right now/, ['Try again', 'Close']]
        ];
        for (const [extra, words, buttons] of cases) {
            const page = await openPage(Object.assign({ routes: dreamRoutes({
                ['GET /advice/' + GUID]: () => Object.assign({ id: GUID, title: TITLE }, extra)
            }) }, ADDRESS));
            eq([page.drafts().length, page.notices().length], [0, 1], 'a notice and no draft for ' + extra.status);
            const notice = page.notices()[0];
            assert(words.test(find(notice, 'chat-dream-message')[0].textContent), 'saying what happened: ' + find(notice, 'chat-dream-message')[0].textContent);
            eq(buttonsOf(notice).map((b) => b.textContent.trim()), buttons, 'no Run for ' + extra.status);
            eq([page.sends().length, page.statusPosts()], [0, []], 'nothing sent or marked for ' + extra.status);
        }
        clean();
    },

    // ---- the review's composer height ---------------------------------------------------------------------------------------------------
    async the_composer_is_capped_only_while_it_holds_the_task() {
        const plain = await openPage({});
        plain.type('x'.repeat(5000));
        assert(!plain.els['chat-input'].classList.contains('chat-dream-task'), 'an ordinary chat never gets the cap');
        const page = await openPage(ADDRESS);
        assert(!page.els['chat-input'].classList.contains('chat-dream-task'), 'nor does a page with a draft, before Edit');
        page.press('Edit');
        assert(page.els['chat-input'].classList.contains('chat-dream-task'), 'the composer is capped while it holds the task');
        page.enter();
        await flush();
        assert(!page.els['chat-input'].classList.contains('chat-dream-task'), 'and grows as before once it is sent');
        const second = await openPage(ADDRESS);
        second.press('Edit');
        second.type('');
        assert(!second.els['chat-input'].classList.contains('chat-dream-task'), 'or once the person empties it');
        clean();
    },

    // ---- Fix-up 2: which answers that are not a stream are refusals (ChatEndpoints.Stream.cs, ChatProxyEndpoints.cs; chat.js refusedBeforeSaving) -------
    async an_error_answer_the_node_gives_before_saving_is_a_refusal() {
        const cases = [
            [400, 'Message is required'], [401, 'Unauthorized'], [403, 'Chat is not enabled for this account.'], [404, 'Conversation not found'],
            [409, 'Vault is locked'], [429, 'You are sending chat messages too quickly.'], [502, 'All keys are cooling down.'], [503, 'Maintenance.'],
            [500, 'An unexpected error occurred.']
        ];
        for (const [status, error] of cases) {
            const page = await openPage(Object.assign({ stream: { status, body: { error } } }, ADDRESS));
            page.press('Run');
            await flush();
            eq(page.statusPosts(), ['in_chat', 'new'], 'a ' + status + ' with the node\'s words came before the save: the mark goes back');
            eq([page.drafts().length, page.notices().length], [1, 0], 'and the draft comes back, without a warning (' + status + ')');
        }
        clean();
    },

    async a_non_stream_answer_with_a_success_status_is_not_a_refusal() {
        for (const body of [{}, { ok: true }, undefined]) {
            const page = await openPage(Object.assign({ stream: { status: 200, body } }, ADDRESS));
            page.press('Run');
            await flush();
            eq(page.statusPosts(), ['in_chat'], 'a 200 that is not a stream is unknown: the advice stays "in chat" (' + JSON.stringify(body) + ')');
            eq(page.drafts().length, 0, 'no draft comes back');
            eq(find(page.notices()[0], 'chat-dream-message')[0].textContent, 'The connection broke while sending. Check the conversation list before sending again.', 'the warning instead');
            eq(buttonsOf(page.notices()[0]).map((b) => b.textContent.trim()), ['Close'], 'and no Run');
        }
        clean();
    },

    async the_sign_in_redirect_is_a_refusal() {
        const page = await openPage(Object.assign({ stream: 'login' }, ADDRESS));
        page.press('Run');
        await flush();
        eq(page.statusPosts(), ['in_chat', 'new'], 'the Web asked for a sign-in before the request reached the Api: the mark goes back');
        eq([page.drafts().length, page.notices().length], [1, 0], 'and the draft comes back');
        clean();
    },

    async a_bare_502_of_the_passthrough_is_not_a_refusal() {
        const page = await openPage(Object.assign({ stream: { status: 502, body: undefined } }, ADDRESS));
        page.press('Run');
        await flush();
        eq(page.statusPosts(), ['in_chat'], 'the Web lost the Api\'s answer, which may have saved the message: the advice stays "in chat"');
        eq([page.drafts().length, page.notices().length], [0, 1], 'the warning, not the draft');
        clean();
    },

    async a_500_of_a_turn_with_images_is_not_a_refusal_and_one_without_is() {
        const page = await openPage(Object.assign({ stream: { status: 500, body: { error: 'An unexpected error occurred.' } } }, ADDRESS));
        page.press('Edit');
        page.attachImage();
        await flush();
        page.enter();
        await flush();
        eq(page.sends()[0].body.attachments, [{ mime: 'image/jpeg', dataBase64: 'QUJD' }], 'the edited text went with an image');
        eq(page.statusPosts(), ['in_chat'], 'a 500 can come after the save on the path of images: the advice stays "in chat"');
        eq(find(page.notices()[0], 'chat-dream-text')[0].textContent, TASK, 'the warning holds the text');
        const plain = await openPage(Object.assign({ stream: { status: 500, body: { error: 'An unexpected error occurred.' } } }, ADDRESS));
        plain.press('Edit');
        plain.enter();
        await flush();
        eq(plain.statusPosts(), ['in_chat', 'new'], 'without images the same 500 came before the save: the mark goes back');
        eq(plain.els['chat-input'].value, TASK, 'and the text is back in the composer');
        clean();
    },

    // ---- the chat as it was ----------------------------------------------------------------------------------------------------------
    async enter_still_sends_what_was_typed_and_an_empty_field_sends_nothing() {
        const page = await openPage({});
        page.type('   ');
        assert(page.enter(), 'Enter is taken by the chat');
        await flush();
        eq(page.sends().length, 0, 'a blank field sends nothing');
        page.type('  How many notes do I have?  ');
        page.enter();
        await flush();
        eq(page.sends().map((c) => c.body.message), ['How many notes do I have?'], 'the text, trimmed');
        eq(page.els['chat-input'].value, '', 'and the field is emptied');
        page.type('with shift');
        page.enter({ shiftKey: true });
        await flush();
        eq(page.sends().length, 1, 'Shift+Enter is a new line, not a send');
        eq(page.dreamCalls(), [], 'and Dream is never asked');
        clean();
    }
};

(async () => {
    try {
        if (!scenarios[scenario]) say(false, 'no such scenario: ' + scenario);
        await scenarios[scenario]();
        say(true, 'ok');
    } catch (e) {
        say(false, (e && e.message ? e.message : String(e)) + (problems.length ? ' [problems: ' + problems.join('; ') + ']' : ''));
    }
})();
