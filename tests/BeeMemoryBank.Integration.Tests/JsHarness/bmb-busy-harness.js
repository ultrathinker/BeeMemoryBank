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
