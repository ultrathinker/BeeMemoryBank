// A small harness that runs wwwroot/js/bmb-busy.js in a fake page (no browser, no packages): a tiny DOM, timers that only move when the test says so, and a
// scripted fetch. It prints one JSON line {"ok": true|false, "detail": "..."} for the scenario named on the command line. The scenarios are the ones of
// BusyHelperBehaviorTests.cs (BMB-311 fix round): what the helper DOES, not what its source says.
//
//   node bmb-busy-harness.js <path of bmb-busy.js> <scenario>
'use strict';
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const scriptPath = process.argv[2];
const scenario = process.argv[3];

// ---------------------------------------------------------------- timers that move when told to
let now = 0;
let nextTimer = 1;
const timers = new Map();
const fakeSetTimeout = (fn, ms) => { const id = nextTimer++; timers.set(id, { id, at: now + (ms || 0), fn }); return id; };
const fakeClearTimeout = (id) => { timers.delete(id); };
// intervals never fire by themselves: a scenario calls the callback it wants to run, and sees which ones are set
const intervals = new Map();
const fakeSetInterval = (fn, ms) => { const id = nextTimer++; intervals.set(id, { id, fn, ms }); return id; };
const fakeClearInterval = (id) => { intervals.delete(id); };
function advance(ms) {
    const target = now + ms;
    for (;;) {
        let next = null;
        for (const t of timers.values()) if (t.at <= target && (!next || t.at < next.at || (t.at === next.at && t.id < next.id))) next = t;
        if (!next) break;
        timers.delete(next.id);
        now = next.at;
        next.fn();
    }
    now = target;
}
async function flush() { for (let i = 0; i < 20; i++) await Promise.resolve(); }

// ---------------------------------------------------------------- a tiny DOM
const camel = (s) => s.replace(/-([a-z])/g, (_, c) => c.toUpperCase());

function parseSelector(sel) {
    return sel.split(',').map((part) => {
        part = part.trim();
        const m = /^([a-z][a-z0-9-]*)?((?:\.[\w-]+)*)((?:\[[^\]]+\])*)$/i.exec(part);
        if (!m) throw new Error('harness: selector not supported: ' + part);
        const attrs = [];
        const re = /\[([\w-]+)(?:="([^"]*)")?\]/g;
        let a;
        while ((a = re.exec(m[3] || ''))) attrs.push({ name: a[1], value: a[2] });
        return { tag: m[1] ? m[1].toLowerCase() : null, classes: (m[2] || '').split('.').filter(Boolean), attrs };
    });
}

class Text { constructor(text) { this.nodeType = 3; this.text = String(text); } }

class El {
    constructor(tag) {
        this.localName = tag.toLowerCase();
        this.tagName = tag.toUpperCase();
        this.children = [];
        this.texts = [];
        this.parentElement = null;
        this.attrs = {};
        this.dataset = {};
        this.listeners = {};
        this.className = '';
        this.hidden = false;
        this.style = {};
        this.id = '';
        this.disabled = false;
        if (this.localName.startsWith('sl-')) this.loading = false;
    }
    get classList() { const el = this; return { contains: (c) => el.className.split(/\s+/).includes(c) }; }
    setAttribute(n, v) { this.attrs[n] = String(v); if (n === 'id') this.id = String(v); if (n === 'class') this.className = String(v); }
    getAttribute(n) { return Object.prototype.hasOwnProperty.call(this.attrs, n) ? this.attrs[n] : null; }
    hasAttribute(n) { return Object.prototype.hasOwnProperty.call(this.attrs, n); }
    removeAttribute(n) { delete this.attrs[n]; }
    appendChild(child) {
        if (child instanceof Text) { this.texts.push(child); return child; }
        child.parentElement = this;
        this.children.push(child);
        return child;
    }
    remove() { if (this.parentElement) this.parentElement.children = this.parentElement.children.filter((c) => c !== this); this.parentElement = null; }
    set innerHTML(html) {
        this.children = [];
        this.texts = [];
        const re = /id="([^"]+)"/g;
        let m;
        while ((m = re.exec(html || ''))) { const c = new El('div'); c.id = m[1]; this.appendChild(c); }
    }
    get textContent() { return this.texts.map((t) => t.text).join('') + this.children.map((c) => c.textContent).join(''); }
    set textContent(v) { this.children = []; this.texts = [new Text(v)]; }
    addEventListener(type, fn) { (this.listeners[type] = this.listeners[type] || []).push(fn); }
    focus() { document.activeElement = this; }
    scrollIntoView() { }
    querySelector(sel) { return this.querySelectorAll(sel)[0] || null; }
    matchesOne(s) {
        if (s.tag && s.tag !== this.localName) return false;
        for (const c of s.classes) if (!this.classList.contains(c)) return false;
        for (const a of s.attrs) {
            if (!this.hasAttribute(a.name)) return false;
            if (a.value !== undefined && this.getAttribute(a.name) !== a.value) return false;
        }
        return true;
    }
    matches(sel) { return parseSelector(sel).some((s) => this.matchesOne(s)); }
    closest(sel) { for (let e = this; e; e = e.parentElement) if (e.matches(sel)) return e; return null; }
    querySelectorAll(sel) {
        const list = parseSelector(sel);
        const out = [];
        const walk = (el) => { for (const c of el.children) { if (list.some((s) => c.matchesOne(s))) out.push(c); walk(c); } };
        walk(this);
        return out;
    }
    contains(node) { for (let e = node; e; e = e.parentElement) if (e === this) return true; return false; }
}

class FormEl extends El {
    constructor() { super('form'); this.submitted = 0; }
    submit() { this.submitted++; }
}

// events: the capture listeners of the document first, then the listeners of the element itself, as a browser runs them
function makeEvent(type, target) {
    return { type, target, defaultPrevented: false, stopped: false, persisted: false, preventDefault() { this.defaultPrevented = true; }, stopImmediatePropagation() { this.stopped = true; } };
}
function dispatchOn(listeners, e) { for (const fn of listeners || []) { if (e.stopped) return; fn(e); } }

const body = new El('body');
const document = {
    body,
    activeElement: null,
    listeners: {},
    createElement: (tag) => (tag === 'form' ? new FormEl() : new El(tag)),
    createTextNode: (text) => new Text(text),
    getElementById(id) { const walk = (el) => { for (const c of el.children) { if (c.id === id) return c; const f = walk(c); if (f) return f; } return null; }; return walk(body); },
    contains: (node) => body.contains(node),
    addEventListener(type, fn) { (this.listeners[type] = this.listeners[type] || []).push(fn); },
    dispatch(e) { dispatchOn(this.listeners[e.type], e); },
    querySelectorAll: (sel) => body.querySelectorAll(sel),
    querySelector: (sel) => body.querySelectorAll(sel)[0] || null,
};
const windowListeners = {};
const sandbox = {
    document,
    setTimeout: fakeSetTimeout,
    clearTimeout: fakeClearTimeout,
    setInterval: fakeSetInterval,
    clearInterval: fakeClearInterval,
    URLSearchParams,
    location: { search: '' },
    HTMLFormElement: FormEl,
    fetch: () => { throw new Error('fetch is not scripted'); },
};
sandbox.window = sandbox;
sandbox.addEventListener = (type, fn) => { (windowListeners[type] = windowListeners[type] || []).push(fn); };
vm.createContext(sandbox);
let bmbBusy;
// The script under test is loaded when a scenario asks (a scenario that needs a page built first loads it itself).
function load() {
    vm.runInContext(fs.readFileSync(scriptPath, 'utf8'), sandbox, { filename: scriptPath });
    bmbBusy = sandbox.window.bmbBusy;
}

// site-sync-status.js (the header's sync badge, the dropdown, the check of a node) is a neighbour of bmb-busy.js and uses it
function loadSyncStatus() {
    const file = path.join(path.dirname(scriptPath), 'site-sync-status.js');
    vm.runInContext(fs.readFileSync(file, 'utf8'), sandbox, { filename: file });
}
// pages/protected-save-dialog.js (BMB-316: the dialog that asks for the password when the save of a protected article finds its unlock gone)
function loadProtectedSaveDialog() {
    const file = path.join(path.dirname(scriptPath), 'pages', 'protected-save-dialog.js');
    vm.runInContext(fs.readFileSync(file, 'utf8'), sandbox, { filename: file });
    return sandbox.window.bmbProtectedSave;
}
function jsonResponse(data) { return { ok: true, status: 200, json: () => Promise.resolve(data), text: () => Promise.resolve(JSON.stringify(data)) }; }

function submit(form, own) {
    const e = makeEvent('submit', form);
    document.dispatch(e);
    if (!e.stopped) dispatchOn(own || form.listeners.submit, e);
    return e;
}
function fakeResponse(status, text) { return { ok: status >= 200 && status < 300, status, text: () => Promise.resolve(text) }; }
function assert(cond, detail) { if (!cond) throw new Error(detail); }
function deferred() { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }
const modal = () => document.getElementById('bmb-busy-modal');
const modalOpen = () => !!modal() && modal().hasAttribute('open');
const modalText = () => { const m = document.getElementById('bmb-busy-msg'); return m ? m.textContent : ''; };
function fresh(tag, parent) { const el = new El(tag); (parent || body).appendChild(el); return el; }function resultText(scopeEl) { return scopeEl.children.filter((c) => c.className === 'bmb-busy-result-host').map((c) => c.textContent).join('|'); }

const scenarios = {
    // F3: the time limit says "still running"; it never gives the control back
    async long_work_keeps_the_lock_after_the_notice_time() {
        const button = fresh('sl-button');
        const work = deferred();
        let started = 0;
        const run = bmbBusy.run(button, () => { started++; return work.promise; }, { long: true, title: 'Restoring' });
        advance(31 * 60 * 1000);
        assert(button.loading === true && button.disabled === true, 'the button was given back after 31 minutes');
        assert(modalOpen(), 'the pop-up closed after 31 minutes');
        assert(/do not start it again/.test(modalText()), 'the pop-up does not say that it is still running: ' + modalText());
        const again = await bmbBusy.run(button, () => { started++; return Promise.resolve(); }, { long: true });
        assert(again === false && started === 1, 'a second start was allowed while the first was still running');
        work.resolve('done');
        assert((await run) === true, 'the work did not end well');
        assert(button.disabled === false && !modalOpen(), 'the control was not given back when the work ended');
        const third = await bmbBusy.run(button, () => Promise.resolve(), { long: false });
        assert(third === true, 'the next run was refused after the first ended');
    },
    async short_work_says_it_is_still_running_and_keeps_the_lock() {
        const card = fresh('div');
        card.className = 'card';
        const button = fresh('sl-button', card);
        const work = deferred();
        const run = bmbBusy.run(button, () => work.promise, {});
        advance(121 * 1000);
        assert(button.disabled === true, 'the button was given back after two minutes');
        assert(/still be running/.test(resultText(card)), 'no notice next to the button: ' + resultText(card));
        work.reject(new Error('The node said no.'));
        assert((await run) === false, 'the failure was not reported');
        assert(button.disabled === false, 'the button stayed locked after the failure');
        assert(/The node said no\./.test(resultText(card)), 'the failure is not next to the button');
    },
    // F2 + F3 for forms: the form is not given back after two minutes
    async a_form_stays_locked_after_two_minutes() {
        const form = document.createElement('form');
        form.setAttribute('data-busy', 'short');
        body.appendChild(form);
        const submitButton = new El('sl-button');
        submitButton.setAttribute('type', 'submit');
        form.appendChild(submitButton);
        const e = submit(form);
        advance(0);
        assert(!e.defaultPrevented && form.dataset.busyNow === '1' && submitButton.loading === true, 'the form was not marked busy');
        advance(121 * 1000);
        assert(form.dataset.busyNow === '1' && submitButton.loading === true, 'the form was given back after two minutes');
        assert(submit(form).defaultPrevented, 'a second send was allowed after two minutes');
    },
    // F6: the reservation is made in the task of the first submit
    async two_submits_in_one_task_send_once() {
        const form = document.createElement('form');
        form.setAttribute('data-busy', 'long');
        body.appendChild(form);
        const first = submit(form);
        const second = submit(form);   // a script, a confirmation callback: the timers have not run yet
        assert(!first.defaultPrevented, 'the first send was refused');
        assert(second.defaultPrevented, 'the second send in the same task was not refused');
        advance(0);
        assert(form.dataset.busyNow === '1', 'the form is not busy');
    },
    async a_declined_confirmation_ends_the_reservation() {
        const form = document.createElement('form');
        form.setAttribute('data-busy', 'short');
        body.appendChild(form);
        form.addEventListener('submit', (e) => e.preventDefault());   // confirm() said no
        const declined = submit(form);
        advance(0);
        assert(declined.defaultPrevented && !form.dataset.busyNow, 'the form stayed reserved after a declined confirmation');
        form.listeners.submit = [];   // the person confirms the second time
        const accepted = submit(form);
        advance(0);
        assert(!accepted.defaultPrevented && form.dataset.busyNow === '1', 'the form could not be sent after it was declined once');
    },
    // F5: what the node said
    async errors_carry_what_the_node_said() {
        const cases = [
            [503, JSON.stringify({ title: 'Service Unavailable', detail: 'The vault is locked: unlock it first.' }), 'The vault is locked: unlock it first.'],
            [400, JSON.stringify('The snapshot mode is not served here.'), 'The snapshot mode is not served here.'],
            [409, JSON.stringify({ error: 'A restore is already running.', code: 'already_running' }), 'A restore is already running.'],
            [400, JSON.stringify({ message: 'No such folder.' }), 'No such folder.'],
            [400, JSON.stringify({ title: 'One or more validation errors occurred.' }), 'One or more validation errors occurred.'],
            [500, JSON.stringify({ error: { message: 'inner message' } }), 'inner message'],
            [502, 'upstream said no', 'upstream said no'],
            [500, '<html><body>Bad gateway</body></html>', 'The node answered 500.'],
            [404, '', 'The node does not know this (it may have been removed). Reload the page.'],
        ];
        for (const [status, text, expected] of cases) {
            sandbox.fetch = () => Promise.resolve(fakeResponse(status, text));
            let message = null;
            try { await bmbBusy.request('/x', {}); } catch (e) { message = e.message; }
            assert(message === expected, status + ' ' + text + ' -> "' + message + '" (expected "' + expected + '")');
        }
        sandbox.fetch = () => Promise.resolve(fakeResponse(500, 'x'.repeat(5000)));
        let long = '';
        try { await bmbBusy.request('/x', {}); } catch (e) { long = e.message; }
        assert(long.length === 5000, 'a long answer is cut (the card says the whole error text): ' + long.length);
        sandbox.fetch = () => Promise.resolve(fakeResponse(200, '{"ok":true}'));
        const ok = await bmbBusy.request('/x', {});
        assert(ok.status === 200 && ok.body.ok === true, 'a good answer is not returned');
    },
    // BMB-311 fix2 (R6): a long message is a preview in the line and ALL of it in an area that opens; a short one is plain text
    async a_long_error_keeps_its_whole_text_in_an_area_that_opens() {
        const card = fresh('div');
        card.className = 'card';
        const button = fresh('sl-button', card);
        const huge = 'line of what the node said '.repeat(300).trim();
        sandbox.fetch = () => Promise.resolve(fakeResponse(500, huge));
        assert((await bmbBusy.run(button, () => bmbBusy.request('/x', {}), {})) === false, 'the failure was not reported');
        const alertOf = () => card.children.find((c) => c.className === 'bmb-busy-result-host').children[0];
        const details = alertOf().children.find((c) => c.localName === 'details');
        assert(details, 'a long message has no area for its whole text');
        const full = details.children.find((c) => c.localName === 'pre');
        assert(full && full.textContent === huge, 'the whole text is not in the area: ' + (full ? full.textContent.length : 'none') + ' of ' + huge.length);
        const preview = alertOf().texts.map((t) => t.text).join('');
        assert(preview.length === 1001 && preview.endsWith('…') && huge.startsWith(preview.slice(0, 1000)), 'the line itself is not the beginning of the text: ' + preview.length);
        sandbox.fetch = () => Promise.resolve(fakeResponse(500, 'It did not work.'));
        await bmbBusy.run(button, () => bmbBusy.request('/x', {}), {});
        assert(!alertOf().children.some((c) => c.localName === 'details') && alertOf().textContent === 'It did not work.', 'a short message got an area of its own');
    },

    // BMB-311 fix2 (R7): the first submit event is SEEN as busy (the buttons spin, the note and the pop-up are up) and a declined confirmation undoes all of it;
    // the submit buttons are only disabled after the event, when the data of the form has been taken
    async the_first_submit_event_is_seen_busy_at_once_and_a_declined_one_is_undone() {
        function page(declines) {
            const form = document.createElement('form');
            form.setAttribute('data-busy', 'long');
            form.setAttribute('data-busy-title', 'Encrypting this node');
            body.appendChild(form);
            const submitButton = new El('sl-button');
            submitButton.setAttribute('type', 'submit');
            form.appendChild(submitButton);
            const note = new El('div');
            note.setAttribute('data-busy-note', '');
            note.hidden = true;
            form.appendChild(note);
            if (declines) form.addEventListener('submit', (e) => e.preventDefault());   // confirm() said no
            return { form, submitButton, note };
        }
        const sent = page(false);
        const first = submit(sent.form);   // no timer has run: this is the first turn
        assert(!first.defaultPrevented, 'the first send was refused');
        assert(sent.submitButton.loading === true && sent.submitButton.getAttribute('aria-busy') === 'true', 'the button does not spin at the first event');
        assert(sent.note.hidden === false, 'the note does not say "working" at the first event');
        assert(modalOpen(), 'the pop-up is not up at the first event');
        assert(sent.submitButton.disabled === false, 'a submit button was disabled before the data of the form was taken');
        advance(0);
        assert(sent.submitButton.disabled === true && sent.submitButton.loading === true && sent.form.dataset.busyNow === '1', 'the send is not locked after the event');
        assert(submit(sent.form).defaultPrevented, 'a second send was allowed');

        sandbox.window.bmbSetFormBusy(sent.form, false);   // the first form is let go (its pop-up with it): the second half starts from a quiet page
        assert(!modalOpen() && sent.submitButton.loading === false && sent.submitButton.disabled === false, 'letting a form go did not undo what was shown');
        const declined = page(true);
        const second = submit(declined.form);
        assert(second.defaultPrevented, 'the declined send was not prevented');
        assert(declined.submitButton.loading === true && modalOpen(), 'the declined send is not seen busy until the answer to the question is known');
        advance(0);
        assert(declined.submitButton.loading === false && declined.submitButton.disabled === false && !declined.submitButton.hasAttribute('aria-busy'), 'the button kept spinning after a declined confirmation');
        assert(declined.note.hidden === true, 'the note stayed after a declined confirmation');
        assert(!modalOpen(), 'the pop-up stayed over the page after a declined confirmation');
        assert(!declined.form.dataset.busyNow, 'the form stayed reserved after a declined confirmation');
    },

    // BMB-313 Q3: the header badge of the sync status follows the node's answer when the page loads, and keeps asking at the pace the answer asks for
    async the_header_badge_follows_the_status_when_the_page_loads() {
        const button = fresh('button'); button.id = 'syncStatusBtn';
        const dropdown = fresh('div'); dropdown.id = 'syncDropdown';
        const badge = fresh('span'); badge.id = 'syncBadge';
        const invisible = fresh('span'); invisible.id = 'syncInvisibleBadge';
        const nodes = [{ nodeId: 'a', displayName: 'A', isSynced: false }, { nodeId: 'b', displayName: 'B', isSynced: true }, { nodeId: 'c', displayName: 'C', isSynced: false }];
        let asked = 0;
        sandbox.fetch = () => { asked++; return Promise.resolve(jsonResponse({ nodes, isInvisible: true })); };

        loadSyncStatus();   // the page was (re)loaded
        await flush();

        assert(asked === 1, 'the page did not ask for the status when it loaded (a reload would leave the badge stale): ' + asked);
        assert(badge.textContent === '2' && badge.style.display === 'inline-flex', 'the badge does not say how many nodes are behind: "' + badge.textContent + '" ' + badge.style.display);
        assert(invisible.style.display === 'inline-flex', 'the Invisible Mode badge is not shown');
        assert(sandbox.window._syncBadgeMs === 5000, 'with changes waiting the page does not ask every five seconds: ' + sandbox.window._syncBadgeMs);
        assert([...intervals.values()].length === 1 && [...intervals.values()][0].ms === 5000, 'there is not exactly one timer, at five seconds');

        nodes.forEach((n) => { n.isSynced = true; });   // everything delivered, and the node says so the next time it is asked
        [...intervals.values()][0].fn();
        await flush();
        assert(badge.style.display === 'none' && badge.textContent === '0', 'the badge stayed after everything was delivered');
        assert(sandbox.window._syncBadgeMs === 30000 && [...intervals.values()].length === 1 && [...intervals.values()][0].ms === 30000, 'the page keeps asking every five seconds with nothing to wait for');
    },

    // BMB-313 Q3: a check of a node whose request is REJECTED (the node is gone, the network is down) gives the row back and can be asked again
    async a_check_that_is_rejected_gives_the_node_row_back() {
        const button = fresh('button'); button.id = 'syncStatusBtn';
        const dropdown = fresh('div'); dropdown.id = 'syncDropdown';
        const badge = fresh('span'); badge.id = 'syncBadge';
        const list = fresh('div'); list.id = 'syncNodeList';
        const nodes = [{ nodeId: 'a', displayName: 'A', isSynced: true }];
        let posts = 0;
        let mode = 'rejected';
        sandbox.fetch = (url, init) => {
            if (init && init.method === 'POST') {
                posts++;
                if (mode === 'rejected') return Promise.reject(new TypeError('Failed to fetch'));
                if (mode === 'refused') return Promise.resolve(fakeResponse(403, JSON.stringify({ error: 'no' })));
                return Promise.resolve(fakeResponse(200, JSON.stringify({ outcome: 'answered', node: { unsyncedCount: 0 } })));
            }
            return Promise.resolve(jsonResponse({ nodes, isInvisible: false }));
        };
        loadSyncStatus();
        await flush();
        button.listeners.click[0]({ stopPropagation() { } });   // the dropdown opens: the list is drawn, the checks are bound
        await flush();
        assert(list.listeners.dblclick && list.listeners.click, 'the checks are not bound to the list');

        const row = new El('div');
        row.setAttribute('data-node-id', 'a');
        list.appendChild(row);
        const meta = new El('div');
        meta.className = 'sync-node-meta';
        row.appendChild(meta);
        const check = new El('button');
        check.setAttribute('data-check-node', 'a');
        row.appendChild(check);
        const doubleClick = () => list.listeners.dblclick[0]({ target: row });
        const press = () => list.listeners.click[0]({ target: check, stopPropagation() { } });

        doubleClick();
        assert(row.disabled === true && row.getAttribute('aria-busy') === 'true', 'the row is not marked working while the request is out');
        await flush();
        assert(posts === 1, 'the request was not sent');
        assert(row.disabled === false && !row.getAttribute('aria-busy') && !row.dataset.busyNow, 'the row was left busy after the request was rejected');

        press();   // the same node can be asked again, by the button this time
        await flush();
        assert(posts === 2 && row.disabled === false, 'the node could not be checked again after a rejection');
        mode = 'refused';
        doubleClick();
        await flush();
        assert(posts === 3 && row.disabled === false, 'a refusal left the row busy');
        mode = 'answered';
        doubleClick();
        await flush();
        assert(posts === 4 && row.disabled === false, 'an answer left the row busy');
    },

    // BMB-317: a node of the dropdown is two lines (the name, when it was last heard from); the rest of what the row says is under a chevron, closed
    async the_sync_dropdown_is_two_lines_with_the_rest_under_a_chevron() {
        const button = fresh('button'); button.id = 'syncStatusBtn';
        const dropdown = fresh('div'); dropdown.id = 'syncDropdown';
        const badge = fresh('span'); badge.id = 'syncBadge';
        const list = fresh('div'); list.id = 'syncNodeList';
        let html = '';
        Object.defineProperty(list, 'innerHTML', { configurable: true, get: () => html, set: (v) => { html = v; } });
        const ago = (s) => new Date(Date.now() - s * 1000).toISOString();
        const nodes = [
            { nodeId: 'a', displayName: 'Hetzner', online: true, state: 'answering', canBeCalled: true, isSynced: true, lastContactAt: ago(57), lastContactDirection: 'outbound' },
            { nodeId: 'b', displayName: 'Blind', online: false, state: 'not_answering', canBeCalled: true, isSynced: false, unsyncedCount: 3, headSeq: 10, lastPushedSeq: 7,
              notAnsweringSince: ago(600), notAnsweringReason: 'no answer: it did not answer in time', lastContactAt: ago(700) },
            { nodeId: 'c', displayName: 'OnePlus 12', online: false, state: 'calls_us', canBeCalled: false, isSynced: false, unsyncedCount: 5, headSeq: 10, lastPushedSeq: 5 },
        ];
        sandbox.fetch = () => Promise.resolve(jsonResponse({ nodes, isInvisible: false }));
        loadSyncStatus();
        await flush();
        button.listeners.click[0]({ stopPropagation() { } });
        await flush();

        assert(!/Press "Check now"/.test(html) && !/sync-node-hint/.test(html), 'the hint about Check now is still in the dropdown');
        const rows = html.split('class="sync-node-item').slice(1);
        assert(rows.length === 3, 'three nodes were not drawn as three rows: ' + rows.length);
        const summary = (row) => (/class="sync-node-summary">([^<]*)</.exec(row) || [])[1];
        assert(summary(rows[0]) === 'Last contact 57s ago', 'the second line of the first node is "' + summary(rows[0]) + '"');
        assert(/>Hetzner <small[^>]*>ONLINE</.test(rows[0]), 'the name line does not say ONLINE');
        assert(/class="sync-node-name">Blind <small class="sync-bad"[^>]*>NOT ANSWERING</.test(rows[1]), 'a node that does not answer does not say so on its name line (it must not wait under the chevron)');
        assert(summary(rows[1]) === 'Last contact 11m ago', 'the second line of the node that does not answer is "' + summary(rows[1]) + '"');
        assert(summary(rows[2]) === 'No call since this node started', 'the second line of a node that only calls in is "' + summary(rows[2]) + '"');
        rows.forEach((row, i) => {
            assert(/class="sync-node-details" hidden>/.test(row), 'the details of node ' + i + ' are not closed');
            assert(/data-toggle-node="/.test(row) && /aria-expanded="false"/.test(row), 'node ' + i + ' has no closed chevron');
            assert(/data-check-node="/.test(row) && /<sl-progress-bar/.test(row) === (i > 0), 'Check now and the delivery bar are not both under the chevron for node ' + i);
        });
        assert(/waiting for it to call/.test(rows[2]) && !/not delivered/.test(rows[2]), 'a node that calls in is said to be "not delivered" to');
        assert(/3 changes not delivered/.test(rows[1]), 'the node that is called does not say how many changes are not delivered');
    },
    // the chevron opens and closes the details in place, two quick presses of it are no check, and a check opens the row it asks about
    async the_chevron_opens_the_details_and_a_check_opens_a_closed_row() {
        const button = fresh('button'); button.id = 'syncStatusBtn';
        const dropdown = fresh('div'); dropdown.id = 'syncDropdown';
        const badge = fresh('span'); badge.id = 'syncBadge';
        const list = fresh('div'); list.id = 'syncNodeList';
        let html = '';
        Object.defineProperty(list, 'innerHTML', { configurable: true, get: () => html, set: (v) => { html = v; } });
        const nodes = [{ nodeId: 'a', displayName: 'A', state: 'answering', canBeCalled: true, isSynced: true, lastContactAt: new Date().toISOString() }];
        let posts = 0;
        sandbox.fetch = (url, init) => {
            if (init && init.method === 'POST') { posts++; return Promise.resolve(fakeResponse(200, JSON.stringify({ outcome: 'answered', node: { unsyncedCount: 0 } }))); }
            return Promise.resolve(jsonResponse({ nodes, isInvisible: false }));
        };
        loadSyncStatus();
        await flush();
        button.listeners.click[0]({ stopPropagation() { } });
        await flush();

        const row = new El('div'); row.setAttribute('data-node-id', 'a'); row.className = 'sync-node-item'; list.appendChild(row);
        const summary = new El('div'); summary.className = 'sync-node-summary'; row.appendChild(summary);
        const details = new El('div'); details.className = 'sync-node-details'; details.hidden = true; row.appendChild(details);
        const chevron = new El('button'); chevron.setAttribute('data-toggle-node', 'a'); chevron.setAttribute('aria-expanded', 'false');
        chevron.setAttribute('aria-label', 'Show the details of A'); row.appendChild(chevron);
        const press = () => list.listeners.click[0]({ target: chevron, stopPropagation() { this.stopped = true; } });

        press();
        assert(details.hidden === false && chevron.getAttribute('aria-expanded') === 'true' && row.classList.contains('is-open'), 'the chevron did not open the details');
        assert(chevron.getAttribute('aria-label') === 'Hide the details of A', 'the chevron does not say that it hides: ' + chevron.getAttribute('aria-label'));
        press();
        assert(details.hidden === true && chevron.getAttribute('aria-expanded') === 'false' && !row.classList.contains('is-open'), 'the chevron did not close the details');

        list.listeners.dblclick[0]({ target: chevron });
        await flush();
        assert(posts === 0, 'two quick presses of the chevron made a check');

        list.listeners.dblclick[0]({ target: row });   // the double-click shortcut on a closed row
        assert(details.hidden === false, 'a check did not open the row that it asks about');
        await flush();
        assert(posts === 1, 'the check was not sent');
        assert(/class="sync-node-item is-open"/.test(html), 'the row drawn again after the check is closed: ' + html.slice(0, 120));
        assert(/class="sync-node-details">/.test(html) && !/class="sync-node-details" hidden/.test(html), 'the details drawn again are closed');
    },

    // BMB-316: which failures of the save of a protected article are "the password is needed", and which are not
    async the_save_of_a_protected_article_tells_a_missing_password_from_other_failures() {
        const api = loadProtectedSaveDialog();
        const err = (status, body, message) => Object.assign(new Error(message || ''), { status, body });
        assert(api.needsPassword(err(400, { error: 'This article is protected — a passphrase is required to edit its content.', code: 'PassphraseRequired' })), 'the code is not the sign');
        assert(api.needsPassword(err(400, { error: 'This article is protected — a passphrase is required to edit its content.' }, 'This article is protected — a passphrase is required to edit its content.')),
            'a node or proxy that does not send the code is not understood by its words: the person would get a toast instead of the dialog');
        assert(api.needsPassword(err(400, null, 'A passphrase is required.')), 'the words alone are not enough when there is no body');
        assert(!api.needsPassword(err(400, { error: 'Title cannot be empty.' }, 'Title cannot be empty.')), 'a blank title is taken for a missing password');
        assert(!api.needsPassword(err(403, { error: 'This article is in a read-only folder for your user.' }, 'This article is in a read-only folder for your user.')), 'a refusal of the folder is taken for a missing password');
        assert(!api.needsPassword(err(0, null, 'The node did not answer.')) && !api.needsPassword(null), 'a failure without a status is taken for a missing password');
        assert(api.wrongPassword(err(401, { error: 'Wrong passphrase.' }, 'Wrong passphrase.')), 'a wrong password is not told');
        assert(!api.wrongPassword(err(401, null, 'Your session has ended. Sign in again.')), 'the end of the web session is taken for a wrong password');
        assert(!api.wrongPassword(err(400, { error: 'Wrong passphrase.' })), 'only a 401 is a wrong password');
    },
    // the dialog asks, takes the password, saves with it, and says why it failed without closing or touching anything else
    async the_password_dialog_saves_with_what_is_typed_and_stays_open_when_it_fails() {
        const api = loadProtectedSaveDialog();
        const dialog = new El('sl-dialog'); dialog.open = false; let shown = 0, hidden = 0;
        dialog.show = () => { dialog.open = true; shown++; };
        dialog.hide = () => { dialog.open = false; hidden++; };
        const input = new El('sl-input'); let focused = 0;
        input.focus = () => { focused++; };
        const error = new El('div'); error.style.display = 'none';
        const saveButton = new El('sl-button');
        const calls = [];
        let outcome = null;   // what the next save does: a function of fail
        const reauth = api.attach({ dialog, input, error, saveButton, save: (button, password, fail) => { calls.push({ password, button }); if (outcome) outcome(fail); } });
        const err = (status, message, body) => Object.assign(new Error(message), { status, body: body || null });

        assert(!reauth.isOpen(), 'the dialog is open before anything asked');
        input.value = 'left over';
        reauth.ask('');
        assert(shown === 1 && reauth.isOpen() && input.value === '' && error.style.display === 'none', 'ask() did not show a clean dialog');

        saveButton.listeners.click[0]();   // nothing typed
        assert(calls.length === 0 && focused >= 1, 'an empty password was sent');

        input.value = 'wrong';
        outcome = (fail) => fail(err(401, 'Wrong passphrase.', { error: 'Wrong passphrase.' }));
        saveButton.listeners.click[0]();
        assert(calls.length === 1 && calls[0].password === 'wrong' && calls[0].button === saveButton, 'the save was not asked with what was typed and the busy button');
        assert(/Wrong password/.test(error.textContent) && error.style.display === '', 'a wrong password is not said in the dialog: "' + error.textContent + '"');
        assert(dialog.open === true && hidden === 0, 'the dialog closed after a wrong password');

        input.value = 'again';
        outcome = (fail) => fail(err(403, 'This article is in a read-only folder for your user.'));
        let enter = false;
        input.listeners.keydown[0]({ key: 'Enter', preventDefault() { enter = true; } });   // Enter saves, as the button does
        assert(enter && calls.length === 2 && calls[1].password === 'again', 'Enter did not save');
        assert(error.textContent === 'This article is in a read-only folder for your user.' && dialog.open === true, 'another refusal is not said in the dialog, or closes it');

        input.listeners.keydown[0]({ key: 'a', preventDefault() { throw new Error('a letter was taken'); } });
        assert(calls.length === 2, 'a letter saved');

        // what is inside the dialog does not count as the dialog being shown or hidden; its own hiding wipes the password and the words
        input.value = 'secret';
        dialog.listeners['sl-after-hide'][0]({ target: new El('sl-tooltip') });
        assert(input.value === 'secret', 'the hiding of something inside the dialog wiped the password');
        dialog.listeners['sl-after-hide'][0]({ target: dialog });
        assert(input.value === '' && error.style.display === 'none', 'the password was left in the page after the dialog was closed');
    },

    // BMB-318: the strip over the editor counts the remembered password down, says when the time is out, and does not keep a timer after that
    async the_unlock_strip_over_the_editor_counts_down_and_says_when_the_time_is_out() {
        const api = loadProtectedSaveDialog();
        const banner = new El('div'); banner.style.display = 'none';
        const counting = new El('span'); const over = new El('span'); over.style.display = 'none';
        const note = new El('small'); const remaining = new El('strong');
        let t = 1000000;
        const strip = api.unlockBanner({ banner, counting, remaining, note, over, now: () => t });
        const shown = (el) => el.style.display !== 'none';

        strip.start(null);
        assert(!shown(banner), 'a strip was shown for an article that is not unlocked');

        strip.start(125);
        assert(shown(banner) && shown(counting) && shown(note) && !shown(over), 'the counting strip is not shown');
        assert(remaining.textContent === '3 min', 'two minutes and five seconds do not read "3 min": "' + remaining.textContent + '"');
        assert(intervals.size === 1, 'there is not exactly one timer: ' + intervals.size);
        t += 70000;
        [...intervals.values()][0].fn();
        assert(remaining.textContent === '55 s', 'the rest does not read "55 s": "' + remaining.textContent + '"');
        t += 55000;
        [...intervals.values()][0].fn();
        assert(shown(banner) && !shown(counting) && !shown(note) && shown(over), 'the strip does not say that the time is out');
        assert(intervals.size === 0, 'a timer is left running after the time is out');

        strip.start(120);   // unlocked again
        assert(shown(counting) && !shown(over) && remaining.textContent === '2 min' && intervals.size === 1, 'a new unlock does not start the count again');
        strip.expire();     // the node says the password is gone (a save was refused for it)
        assert(shown(over) && !shown(counting) && intervals.size === 0, 'expire() does not stop the count and say so');
        strip.start(0);
        assert(shown(over) && intervals.size === 0, 'an unlock with no time left starts a timer');
    },
    // BMB-319: the strip says hours when the time is long ("8 h", "2 h 15 min"), minutes round up, and a strip that runs out closes the slider
    async the_strip_says_hours_for_a_long_time_and_closes_the_slider_when_the_time_is_out() {
        const api = loadProtectedSaveDialog();
        const f = api.formatSpan;
        const said = [[45, '45 s'], [60, '1 min'], [61, '2 min'], [3540, '59 min'], [3599, '1 h'], [3600, '1 h'], [3601, '1 h 1 min'], [8100, '2 h 15 min'], [28800, '8 h'], [28740, '7 h 59 min']];
        for (const [sec, text] of said) assert(f(sec) === text, sec + ' s reads "' + f(sec) + '", not "' + text + '"');

        const banner = new El('div'); const counting = new El('span'); const over = new El('span'); over.style.display = 'none';
        const note = new El('small'); const remaining = new El('strong');
        let t = 5000000, closed = 0;
        const strip = api.unlockBanner({ banner, counting, remaining, note, over, now: () => t, onExpire: () => { closed++; } });
        strip.start(28800);
        assert(remaining.textContent === '8 h', 'eight hours read "' + remaining.textContent + '"');
        t += 28800 * 1000;
        [...intervals.values()][0].fn();
        assert(closed === 1 && over.style.display !== 'none', 'the time that ran out did not close the slider');
        strip.start(600); strip.expire();
        assert(closed === 2, 'expire() did not close the slider');
    },
    // BMB-319: the slider moves in steps of 15 min between 15 min and 8 h, says what it is set to while it moves, and applies the time when it is let go
    async the_unlock_time_of_one_article_is_chosen_on_a_slider_that_applies_when_it_is_let_go() {
        const api = loadProtectedSaveDialog();
        const link = new El('a'); link.textContent = 'change';
        const panel = new El('span'); panel.style.display = 'none';
        const range = new El('sl-range'); const picked = new El('strong');
        const requests = [], applied = [], failed = [];
        let answer = (seconds) => Promise.resolve({ unlockExpiresInSeconds: seconds - 3, unlockDurationSeconds: seconds });
        const picker = api.durationPicker({
            link, panel, range, picked,
            request: (s) => { requests.push(s); return answer(s); },
            applied: (b) => applied.push(b), failed: (e) => failed.push(e)
        });
        const shown = () => panel.style.display !== 'none';
        const press = () => link.listeners.click[0]({ preventDefault() { } });
        const letGo = async () => { range.listeners['sl-change'][0](); await flush(); };

        assert(!shown() && link.textContent === 'change' && !picker.isOpen(), 'the slider is open before "change" was pressed');
        press();
        assert(shown() && link.textContent === 'close' && picker.isOpen(), '"change" did not open the slider');
        press();
        assert(!shown() && link.textContent === 'change', 'the link did not close the slider again');

        picker.setSeconds(900);
        assert(range.value === 15 && picked.textContent === '15 min', 'the standard time is not on the slider: ' + range.value + ' ' + picked.textContent);
        picker.setSeconds(130 * 60);
        assert(range.value === 135 && picked.textContent === '2 h 15 min', 'a time between two steps is not rounded to a step: ' + range.value);
        picker.setSeconds(100 * 3600);
        assert(range.value === 480 && picked.textContent === '8 h', 'a time over 8 h is not held at 8 h: ' + range.value);
        picker.setSeconds(120);   // the node-wide time can be set shorter by BMB_PROTECTED_UNLOCK_SECONDS; the slider never goes under 15 min
        assert(range.value === 15, 'a time under 15 min is not held at 15 min: ' + range.value);
        picker.setSeconds(null);
        assert(range.value === 15, 'a missing time is not the standard time');

        // moving the handle says the time and sends nothing
        press();
        range.value = 135; range.listeners['sl-input'][0]();
        assert(picked.textContent === '2 h 15 min' && requests.length === 0 && shown(), 'moving the handle sent or closed something');

        // letting go sends it, the slider goes, and what the node answers goes to the page
        range.listeners['sl-change'][0]();
        assert(range.disabled === true, 'the slider can be moved while the time is being sent');
        await flush();
        assert(requests.length === 1 && requests[0] === 135 * 60, 'the time was not sent in seconds: ' + requests);
        assert(applied.length === 1 && applied[0].unlockExpiresInSeconds === 8097 && failed.length === 0, 'the answer of the node did not come to the page');
        assert(!shown() && range.disabled === false && link.textContent === 'change', 'the slider did not go away after the time was applied');
        assert(range.value === 135, 'the slider does not stay on the time the node says it keeps');

        // the node says it is locked already (409), or fails: the slider goes, the page is told, nothing is applied
        answer = () => Promise.reject(Object.assign(new Error('This article is locked. Enter its password again.'), { status: 409 }));
        press(); range.value = 30; await letGo();
        assert(failed.length === 1 && failed[0].status === 409 && applied.length === 1 && !shown() && range.disabled === false, 'a refusal was not told or left the slider open');
        answer = () => { throw new Error('the request could not even start'); };
        press(); range.value = 45; await letGo();
        assert(failed.length === 2 && applied.length === 1 && !shown() && range.disabled === false, 'a request that throws was not turned into a failure');

        picker.close();
        assert(!shown(), 'close() left the slider open');
    },
    // F13: the single-page navigation replaces the content while an operation runs
    async an_operation_of_a_replaced_page_closes_its_popup_and_says_its_result_as_a_notice() {
        const content = fresh('div');
        const button = fresh('sl-button', content);
        const work = deferred();
        const run = bmbBusy.run(button, () => work.promise, { long: true, title: 'Importing', success: 'Imported.' });
        assert(modalOpen(), 'the pop-up did not open');
        document.dispatch(makeEvent('bmb:page-cleanup', document));   // site-spa.js ...
        content.remove();                                             // ... and then the swap
        advance(0);
        assert(!modalOpen(), 'the pop-up of a page that is gone stayed over the new page');
        work.resolve('ok');
        await run;
        assert(!body.querySelectorAll('.bmb-busy-result-host').length, 'a late result was drawn into the new page');
        assert(body.children.some((c) => c.localName === 'sl-alert' && /has finished/.test(c.textContent)), 'the late result was not said anywhere');
    },
    async an_operation_of_a_control_that_stays_keeps_its_popup_through_a_navigation() {
        const header = fresh('div');
        const button = fresh('sl-button', header);
        const work = deferred();
        const run = bmbBusy.run(button, () => work.promise, { long: true, title: 'Checking', success: 'Checked.' });
        document.dispatch(makeEvent('bmb:page-cleanup', document));
        advance(0);   // the content was swapped; the header was not
        assert(modalOpen(), 'the pop-up of a control that is still on the page was closed');
        work.resolve('ok');
        await run;
        assert(/Checked\./.test(resultText(header)), 'the result did not come to the control that stayed');
    },
    // F14: the dialog has a name and a description, and the focus comes back
    async the_dialog_is_named_described_and_gives_the_focus_back() {
        const button = fresh('sl-button');
        button.focus();
        const work = deferred();
        const run = bmbBusy.run(button, () => work.promise, { long: true, title: 'Encrypting this node', text: 'It takes a minute.' });
        const m = modal();
        assert(m.getAttribute('role') === 'alertdialog', 'role');
        assert(m.getAttribute('aria-modal') === 'true', 'aria-modal');
        assert(m.getAttribute('aria-labelledby') === 'bmb-busy-title' && document.getElementById('bmb-busy-title').textContent === 'Encrypting this node', 'aria-labelledby');
        assert(m.getAttribute('aria-describedby') === 'bmb-busy-msg' && modalText() === 'It takes a minute.', 'aria-describedby');
        assert(document.activeElement === m, 'the focus did not move into the dialog');
        work.resolve();
        await run;
        assert(!modalOpen() && document.activeElement === button, 'the focus did not go back to the button');
    },
    // the same operation in two controls: the pop-up of one does not close under the other
    async two_long_operations_share_one_popup() {
        const a = fresh('sl-button');
        const b = fresh('sl-button');
        const wa = deferred();
        const wb = deferred();
        const ra = bmbBusy.run(a, () => wa.promise, { long: true });
        const rb = bmbBusy.run(b, () => wb.promise, { long: true });
        wa.resolve();
        await ra;
        assert(modalOpen(), 'the pop-up closed while the second operation still ran');
        wb.resolve();
        await rb;
        assert(!modalOpen(), 'the pop-up stayed after both ended');
    },

    // pages/vault-encryption.js (B2): the form of "Encrypt this node" asks first (site.js, in the capture phase); a declined question prevents the send, and the
    // panel that says "Starting..." must not come up for a send that did not happen.
    async seal_form_shows_starting_only_when_it_is_sent() {
        const panel = fresh('div');
        panel.id = 'seal-progress';
        panel.style.display = 'none';
        const text = fresh('div');
        text.id = 'seal-progress-text';
        const form = document.createElement('form');
        form.id = 'seal-form';
        body.appendChild(form);
        load();

        const declined = makeEvent('submit', form);
        declined.preventDefault();   // confirm() said no
        dispatchOn(form.listeners.submit, declined);
        assert(panel.style.display === 'none' && text.textContent === '', 'the panel came up for a send that was declined');

        const sent = makeEvent('submit', form);
        dispatchOn(form.listeners.submit, sent);
        assert(panel.style.display === '' && text.textContent === 'Starting...', 'the panel did not come up for a send that happened');
    },
};
const manualLoad = new Set(['seal_form_shows_starting_only_when_it_is_sent']);

(async () => {
    try {
        if (!scenarios[scenario]) throw new Error('unknown scenario ' + scenario);
        if (!manualLoad.has(scenario)) load();
        await scenarios[scenario]();
        await flush();
        console.log(JSON.stringify({ ok: true, detail: '' }));
    } catch (e) {
        console.log(JSON.stringify({ ok: false, detail: String(e && e.message || e) }));
    }
})();
