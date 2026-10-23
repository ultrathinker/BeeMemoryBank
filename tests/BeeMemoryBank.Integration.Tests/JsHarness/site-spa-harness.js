'use strict';
// Runs site-spa.js (the single-page navigation) in a small fake browser, with no packages: node site-spa-harness.js <site-spa.js> <scenario>.
// The browser is a fake in the one way that matters here: what the script DECIDES. Which links it takes over, what it fetches, what it executes after the swap,
// what it cleans up when the page is left. The pages are named by path in `PAGES`; a "fetch" answers with a token and the "DOMParser" turns the token back into the
// page, so no HTML parser is needed. The real admin.js is run when a swapped page asks for it, to see that its timers and listeners die with the page; the real
// hard-delete.js is run on /Admin/HardDelete, against fake elements and requests the scenario answers by hand, to see that a request that ends after the page was left
// writes nothing to the page that replaced it; admin.js is run on /Admin/AiChat the same way, to see the rows it builds for the two tables of that page.
// Prints one JSON line: { ok, detail }.

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const [, , spaPath, scenario] = process.argv;
const spaSource = fs.readFileSync(spaPath, 'utf8');
const adminPath = path.join(path.dirname(spaPath), 'pages', 'admin.js');
const hardDeletePath = path.join(path.dirname(spaPath), 'pages', 'hard-delete.js');

const ORIGIN = 'http://localhost';

// What each page of the stand has: its title, the scripts the layout puts after site.js (section Scripts), and the elements its own script looks for.
const PAGES = {
    '/Admin': { title: 'Admin - Bee', scripts: ['/js/pages/admin-index.js?v=I'], selectors: {} },
    '/Admin/Snapshots': { title: 'Snapshots - Bee', scripts: ['/js/pages/admin.js?v=A'], selectors: {} },
    '/Admin/TrustedNodes': { title: 'Trusted Nodes - Bee', scripts: ['/js/pages/admin.js?v=A'], selectors: {} },
    '/Admin/DekRotation': { title: 'DEK Rotation - Bee', scripts: ['/js/pages/admin.js?v=A'], selectors: { '[data-dek-active="true"]': {} } },
    '/Admin/Agents': { title: 'Agents - Bee', scripts: ['/js/pages/admin.js?v=A'], selectors: {} },
    '/Admin/HardDelete': { title: 'Hard Delete - Bee', scripts: ['/js/pages/hard-delete.js?v=H'], selectors: {} },
    '/Admin/AiChat': { title: 'AI / Chat - Bee', scripts: ['/js/pages/admin.js?v=A'], selectors: {} },
    '/Search': { title: 'Search - Bee', scripts: [], selectors: {} },
    '/Tree': { title: 'Tree - Bee', scripts: [], selectors: {} },
};

function build(startPath) {
    const log = { fetched: [], navigations: [], reloads: 0, pushed: [], replaced: [], executed: [], events: [], timers: [], prevented: 0, titles: [],
        api: [], busy: [], appended: [], pageWrites: [], rows: [], notices: [] };

    // The elements of /Admin/HardDelete for its script: made on the first ask, and replaced when the page is swapped (a new page has new elements). A write to an element of
    // an older generation is a write to a page that is gone, and is the thing the scenarios look for.
    let generation = 0;
    let pageEls = {};
    class FakeEl {
        constructor(id) {
            this.id = id; this.gen = generation; this._listeners = {}; this._kids = {}; this.dataset = {}; this.disabled = false; this.value = ''; this.checked = false;
            const note = what => log.pageWrites.push({ el: id, what, stale: this.gen !== generation });
            this._note = note;
            this.style = new Proxy({}, { set(o, k, v) { o[k] = v; note('style.' + String(k)); return true; } });
        }
        set innerHTML(v) { this._html = v; this._note('innerHTML'); }
        get innerHTML() { return this._html || ''; }
        set textContent(v) { this._text = v; this._note('textContent'); }
        get textContent() { return this._text || ''; }
        addEventListener(type, fn) { (this._listeners[type] = this._listeners[type] || []).push(fn); }
        appendChild() { this._note('appendChild'); }
        querySelector(sel) { return this._kids[sel] || (this._kids[sel] = new FakeEl(this.id + ' ' + sel)); }
        querySelectorAll() { return []; }
        show() { this._note('show'); }
        hide() { this._note('hide'); }
        fire(type, ev) { return Promise.all((this._listeners[type] || []).map(f => f(ev || {}))); }
    }
    const pageEl = id => {
        if (!pageEls[id]) { pageEls[id] = new FakeEl(id); if (id === 'pagesize-select') pageEls[id].value = '100'; if (id === 'status-select') pageEls[id].value = 'All'; }
        return pageEls[id];
    };
    // A request the scenario answers by hand: pending until it is answered, rejected by an abort of its signal as a browser does.
    function deferred(url, init) {
        const d = { url: String(url), init: init || {}, aborted: false, settled: false };
        d.promise = new Promise((resolve, reject) => { d.resolve = v => { if (d.settled) return; d.settled = true; resolve(v); }; d.reject = e => { if (d.settled) return; d.settled = true; reject(e); }; });
        const signal = d.init.signal;
        if (signal) signal.addEventListener('abort', () => { d.aborted = true; const e = new Error('The operation was aborted.'); e.name = 'AbortError'; d.reject(e); });
        d.answer = body => d.resolve({ ok: true, status: 200, json: () => Promise.resolve(body), text: () => Promise.resolve(JSON.stringify(body)) });
        d.fail = () => d.reject(new TypeError('Failed to fetch'));
        return d;
    }
    const listeners = {};
    const docListeners = {};
    const winListeners = {};

    const main = { scrollTop: 0, style: {}, dataset: {} };
    let currentPath = startPath;
    const content = {
        _html: '<<' + startPath + '>>',
        get innerHTML() { return this._html; },
        set innerHTML(v) { this._html = v; const m = /<<(.*)>>/.exec(v); if (m) { currentPath = m[1]; generation++; pageEls = {}; } },
        querySelector: sel => (sel === '.main-content' ? main : null),
        querySelectorAll: () => [],
        appendChild(script) {
            const src = script.attrs.src || null;
            log.executed.push(src);
            // the real script, when the layout of the stand loads it
            if (src && /\/admin\.js\b/.test(src.split('?')[0])) vm.runInContext(fs.readFileSync(adminPath, 'utf8'), sandbox, { filename: adminPath });
            if (src && /\/hard-delete\.js\b/.test(src.split('?')[0])) vm.runInContext(fs.readFileSync(hardDeletePath, 'utf8'), sandbox, { filename: hardDeletePath });
            setImmediate(() => script.onload && script.onload());
        },
    };

    function addListener(map, type, fn, opts) {
        (map[type] = map[type] || []).push(fn);
        if (opts && opts.signal) opts.signal.addEventListener('abort', () => { map[type] = map[type].filter(f => f !== fn); });
    }

    const location = {
        origin: ORIGIN,
        get pathname() { return new URL(this._href).pathname; },
        get search() { return new URL(this._href).search; },
        get hash() { return new URL(this._href).hash; },
        _href: ORIGIN + startPath,
        get href() { return this._href; },
        set href(v) { log.navigations.push(String(v)); },
        reload() { log.reloads++; },
    };

    const document = {
        title: '',
        body: { classList: { remove() {} }, append(el) { log.appended.push(el); } },
        getElementById: id => (id === 'page-content' ? content : currentPath === '/Admin/HardDelete' ? pageEl(id)
            : currentPath === '/Admin/AiChat' && /^chat-(keys|models)-body$/.test(id) ? pageEl(id) : null),
        querySelector: sel => {
            if (sel === '.main-content' || sel === '#page-content .main-content') return main;
            return PAGES[currentPath] && PAGES[currentPath].selectors[sel] || null;
        },
        querySelectorAll: () => [],
        addEventListener(type, fn, opts) { addListener(docListeners, type, fn, opts); },
        dispatchEvent(ev) { log.events.push(ev.type); (docListeners[ev.type] || []).slice().forEach(f => f(ev)); },
        createElement(tag) {
            if (tag === 'tr') { const row = new FakeEl('tr'); log.rows.push(row); return row; }
            if (tag === 'sl-alert') return { toast() { return Promise.resolve(); } };
            const el = { attrs: {}, setAttribute(k, v) { this.attrs[k] = v; }, set textContent(v) {} };
            return el;
        },
    };

    const sandbox = {
        document, location, console,
        history: {
            pushState(state, t, url) { log.pushed.push(String(url)); location._href = String(url); },
            replaceState(state, t, url) { log.replaced.push(String(url)); },
        },
        setTimeout(fn, ms) { const t = { id: log.timers.length + 1, fn, ms, cleared: false }; log.timers.push(t); return t.id; },
        clearTimeout(id) { const t = log.timers.find(x => x.id === id); if (t) t.cleared = true; },
        fetch(url, init) {
            if (/^\/api-proxy\/(hard-delete|chat)\//.test(String(url))) { const d = deferred(url, init); log.api.push(d); return d.promise; }
            const p = Object.keys(PAGES).find(k => k.toLowerCase() === new URL(url).pathname.toLowerCase());
            log.fetched.push(String(url));
            const page = PAGES[p];
            if (!page) return Promise.resolve({ ok: false, status: 404, redirected: false, url: String(url), text: () => Promise.resolve('') });
            return Promise.resolve({ ok: true, status: 200, redirected: false, url: String(url), text: () => Promise.resolve('PAGE:' + p) });
        },
        DOMParser: class {
            parseFromString(html) {
                const p = html.slice(5);
                const page = PAGES[p];
                return {
                    getElementById: id => (id === 'page-content' ? { innerHTML: '<<' + p + '>>', querySelectorAll: () => [] } : null),
                    querySelector: sel => (sel === 'title' ? { textContent: page.title } : null),
                    body: {
                        querySelectorAll: sel => (sel !== 'script' ? [] : [{ src: '/js/vendor.min.js?v=V' }, { src: '/js/site.js?v=S' }, ...page.scripts.map(s => ({ src: s }))].map(fakeScript)),
                    },
                };
            }
        },
        CustomEvent: class { constructor(type) { this.type = type; } },
        AbortController, URL, Promise, Array, Object, String, Number, JSON, Date, Node: { TEXT_NODE: 3 },
        window: null,
    };
    sandbox.window = sandbox;
    sandbox.addEventListener = (type, fn, opts) => addListener(winListeners, type, fn, opts);
    // bmbBusy as far as hard-delete.js uses it: run() does the work and, when the work ends well and the call is not quiet, asks for the text of the result (what shows it
    // next to the button, or says it once when the page was left); request() is a request the scenario answers by hand.
    sandbox.bmbBusy = {
        run(target, work, options) {
            options = options || {};
            const signal = sandbox.bmbGetPageSignal ? sandbox.bmbGetPageSignal() : null;
            return Promise.resolve().then(work).then(result => {
                if (!options.quiet) {
                    const text = typeof options.success === 'function' ? options.success(result) : options.success;
                    if (text) log.notices.push({ late: !!(signal && signal.aborted), text });
                }
                return true;
            }, () => false);
        },
        request(url, init) { const d = deferred(url, init); log.busy.push(d); return d.promise.then(response => ({ status: 200, body: response.body })); },
    };
    sandbox.customElements = { whenDefined: () => Promise.resolve() };
    sandbox.confirm = () => true;
    sandbox.alert = () => {};
    sandbox.fetchLog = log.fetched;
    vm.createContext(sandbox);
    Object.defineProperty(document, 'title', { get() { return this._t || ''; }, set(v) { this._t = v; log.titles.push(v); } });

    function fakeScript(s) {
        const attrs = [{ name: 'src', value: s.src }];
        return { getAttribute: n => (n === 'src' ? s.src : null), attributes: attrs, textContent: '', remove() {} };
    }

    vm.runInContext(spaSource, sandbox, { filename: spaPath });

    /** A link (a, or sl-button) with this href, clicked; returns whether the script took the click over. */
    function click(href, opts) {
        opts = opts || {};
        const tag = opts.tag || 'SL-BUTTON';
        const attrs = Object.assign({ href }, opts.attrs || {});
        const el = {
            tagName: tag, hasAttribute: n => n in attrs, getAttribute: n => (n in attrs ? attrs[n] : null),
        };
        const target = { nodeType: 1, closest: sel => (sel === 'a[href]' && tag === 'A') || (sel === 'sl-button[href]' && tag === 'SL-BUTTON') ? el : null };
        let prevented = false;
        const ev = Object.assign({
            ctrlKey: false, metaKey: false, shiftKey: false, altKey: false, button: 0, target,
            composedPath: () => [el], preventDefault() { prevented = true; }, stopPropagation() {},
        }, opts.event || {});
        (docListeners.click || []).slice().forEach(f => f(ev));
        return prevented;
    }
    function popstate(toPath) {
        location._href = ORIGIN + toPath;
        (winListeners.popstate || []).slice().forEach(f => f({}));
    }
    async function settle() { for (let i = 0; i < 20; i++) await new Promise(r => setImmediate(r)); }

    return {
        sandbox, log, click, popstate, settle, content, get path() { return currentPath; }, signal: () => sandbox.bmbGetPageSignal(),
        listeners: type => (docListeners[type] || []).length,
        el: id => pageEls[id],
    };
}

const ok = detail => ({ ok: true, detail });
const fail = detail => ({ ok: false, detail });

const scenarios = {
    // the list to a page, and Back
    async the_list_to_a_page_is_swapped_and_the_script_of_the_page_runs() {
        const w = build('/Admin');
        const first = w.signal();
        const taken = w.click('/Admin/TrustedNodes');
        await w.settle();
        if (!taken) return fail('the click was not taken over (the browser would load the page)');
        if (w.log.navigations.length) return fail('it navigated: ' + w.log.navigations.join(', '));
        if (JSON.stringify(w.log.fetched) !== JSON.stringify([ORIGIN + '/Admin/TrustedNodes'])) return fail('fetched ' + JSON.stringify(w.log.fetched));
        if (w.path !== '/Admin/TrustedNodes') return fail('the content is that of ' + w.path);
        if (JSON.stringify(w.log.pushed) !== JSON.stringify([ORIGIN + '/Admin/TrustedNodes'])) return fail('pushed ' + JSON.stringify(w.log.pushed));
        if (w.log.titles[w.log.titles.length - 1] !== 'Trusted Nodes - Bee') return fail('title ' + JSON.stringify(w.log.titles));
        if (JSON.stringify(w.log.executed) !== JSON.stringify(['/js/pages/admin.js?v=A'])) return fail('scripts run: ' + JSON.stringify(w.log.executed));
        if (!first.aborted) return fail('the page that was left was not cleaned up (its listeners would stay)');
        if (w.signal() === first) return fail('the new page has the signal of the old one');
        const events = w.log.events.join(',');
        if (!/bmb:page-cleanup.*bmb:page-swapped.*bmb:page-loaded/.test(events)) return fail('events ' + events);
        return ok('fetched, swapped, cleaned up, ran admin.js, titled, pushed');
    },
    async the_back_link_of_the_template_goes_back_to_the_list() {
        const w = build('/Admin/Snapshots');
        const taken = w.click('/Admin');
        await w.settle();
        if (!taken) return fail('Back was not taken over');
        if (w.log.navigations.length) return fail('it navigated: ' + w.log.navigations.join(', '));
        if (w.path !== '/Admin') return fail('the content is that of ' + w.path);
        if (JSON.stringify(w.log.executed) !== JSON.stringify(['/js/pages/admin-index.js?v=I'])) return fail('scripts run: ' + JSON.stringify(w.log.executed));
        return ok('/Admin/Snapshots -> /Admin, the script of the index ran');
    },
    async a_page_to_a_page_and_to_another_area_of_the_site_are_swapped() {
        const w = build('/Admin/Snapshots');
        w.click('/Admin/Agents');
        await w.settle();
        const a = w.path;
        w.click('/search?q=x', { tag: 'A' });
        await w.settle();
        if (a !== '/Admin/Agents') return fail('page to page ended on ' + a);
        if (w.path !== '/Search') return fail('to search ended on ' + w.path);
        if (w.log.navigations.length) return fail('it navigated: ' + w.log.navigations.join(', '));
        return ok('/Admin/Snapshots -> /Admin/Agents -> /Search');
    },
    async the_case_of_the_address_and_a_trailing_slash_do_not_matter() {
        const w = build('/Admin');
        const taken = ['/admin/agents', '/ADMIN/AGENTS', '/Admin/Agents/'].map(h => {
            const t = w.click(h);
            return t;
        });
        await w.settle();
        return taken.every(Boolean) ? ok('all three are taken over') : fail('taken: ' + JSON.stringify(taken));
    },

    // what is not swapped
    async a_handler_link_of_the_area_is_left_to_the_browser() {
        const w = build('/Admin/Snapshots');
        // the server reads a query name in any case, so "Handler" and "HANDLER" are the same handler
        for (const h of ['/Admin/Snapshots?handler=DownloadSnapshot&fileName=a.tar.gz', '/admin/snapshots?x=1&Handler=DownloadSnapshot', '/Admin?HANDLER=Anything']) {
            if (w.click(h, { tag: 'A' })) return fail('the link ' + h + ' was taken over; a download would be fetched as a page');
        }
        await w.settle();
        if (w.log.fetched.length) return fail('it fetched ' + w.log.fetched.join(', '));
        return ok('a link with ?handler= is the browser\'s');
    },
    async an_unrelated_or_malformed_path_is_not_swapped() {
        const w = build('/Admin');
        const bad = [
            '/adminx', '/admin2/agents', '/Admin/Snap-shots', '/Admin/snapshots.cshtml', '/Admin/Snapshots/extra', '/Admin//Snapshots', '/Admin/%53napshots',
            '/Admin/../Login', '/Admin/Snapshots%2F..%2FLogin', '/Admin/Snap_shots', '/Admin/1', '/Administrator',
            '/Login', '/Connect', '/BlindNodes', '/HardDelete', '/Network', '/VaultEncryption', '/Lock', '/Setup',
            '/Tree?next=/Admin/Snapshots', '/Tree?x=/admin', '/Login?ReturnUrl=%2FAdmin%2FAgents',
            'https://evil.example/Admin/Agents', '//evil.example/Admin/Agents', 'javascript:alert(1)', 'mailto:a@b.c',
        ];
        const taken = bad.filter(h => w.click(h, { tag: 'A' }));
        await w.settle();
        if (taken.length) return fail('taken over: ' + taken.join(', '));
        if (w.log.fetched.length) return fail('fetched ' + w.log.fetched.join(', '));
        return ok(bad.length + ' paths left to the browser');
    },
    async the_existing_exclusions_still_hold() {
        const w = build('/Admin');
        const cases = [
            ['a modifier key', '/Admin/Agents', { event: { ctrlKey: true } }],
            ['the meta key', '/Admin/Agents', { event: { metaKey: true } }],
            ['the shift key', '/Admin/Agents', { event: { shiftKey: true } }],
            ['the middle button', '/Admin/Agents', { event: { button: 1 } }],
            ['a target', '/Admin/Agents', { attrs: { target: '_blank' } }],
            ['a fragment only', '#section', {}],
        ];
        const taken = cases.filter(([, h, o]) => w.click(h, Object.assign({ tag: 'A' }, o))).map(c => c[0]);
        await w.settle();
        return taken.length === 0 && w.log.fetched.length === 0 ? ok('modifier keys, other buttons, a target and a fragment stay the browser\'s') : fail('taken over: ' + taken.join(', '));
    },
    async a_page_the_node_does_not_have_falls_back_to_a_full_navigation() {
        const w = build('/Admin');
        const taken = w.click('/Admin/Nothing');
        await w.settle();
        if (!taken) return fail('a letters-only name is a page of the area to the script');
        if (JSON.stringify(w.log.navigations) !== JSON.stringify([ORIGIN + '/Admin/Nothing'])) return fail('navigations ' + JSON.stringify(w.log.navigations));
        return ok('a 404 is shown by the browser, as a page of its own');
    },
    async the_back_and_forward_buttons_swap_the_pages_of_the_area_and_reload_for_the_others() {
        const w = build('/Admin/Agents');
        w.popstate('/Admin');
        await w.settle();
        if (w.path !== '/Admin') return fail('popstate to /Admin ended on ' + w.path);
        if (w.log.pushed.length) return fail('a popstate must not push a new entry: ' + w.log.pushed.join(', '));
        w.popstate('/Login');
        if (w.log.reloads !== 1) return fail('popstate to a page that is not swapped must reload (reloads: ' + w.log.reloads + ')');
        w.popstate('/Admin/Snapshots?handler=DownloadSnapshot');
        if (w.log.reloads !== 2) return fail('a handler address is not a page either (reloads: ' + w.log.reloads + ')');
        return ok('/Admin by the swap, the others by a reload');
    },
    async the_entry_of_a_page_of_the_area_is_marked_as_a_swapped_page() {
        const a = build('/Admin/Snapshots');
        const b = build('/Login');
        return a.log.replaced.length === 1 && b.log.replaced.length === 0 ? ok('so that the browser\'s Back button finds it') : fail('replaced: ' + JSON.stringify([a.log.replaced, b.log.replaced]));
    },

    // what the page that is left takes with it
    async the_timers_of_a_page_that_is_left_die_with_it() {
        const w = build('/Admin');
        w.click('/Admin/DekRotation');
        await w.settle();
        const reloaders = () => w.log.timers.filter(t => t.ms === 3000);
        if (reloaders().length !== 1 || reloaders()[0].cleared) return fail('an active rotation must refresh its page every 3 seconds (timers: ' + JSON.stringify(reloaders()) + ')');
        w.click('/Admin');
        await w.settle();
        if (!reloaders()[0].cleared) return fail('the refresh of the rotation page is still pending on the page that replaced it (it would reload the list)');
        if (w.sandbox._dekRotationRefreshing) return fail('the flag that keeps a second timer away is still set');
        w.click('/Admin/DekRotation');
        await w.settle();
        if (reloaders().length !== 2 || reloaders()[1].cleared) return fail('a return to the rotation page does not refresh it again');
        return ok('cleared on leaving, started again on return');
    },
    async the_script_of_the_page_hears_its_own_clicks_only_while_the_page_is_there() {
        const w = build('/Admin');
        w.click('/Admin/Snapshots');
        await w.settle();
        const after1 = [w.listeners('click'), w.listeners('sl-change')];
        w.click('/Admin/Agents');
        await w.settle();
        const after2 = [w.listeners('click'), w.listeners('sl-change')];
        if (JSON.stringify(after1) !== JSON.stringify(after2)) return fail('listeners (click, sl-change) after the first page ' + after1 + ', after the second ' + after2 + ': the first page left its listeners behind');
        if (after1[1] < 1) return fail('admin.js did not run on the swapped page');
        if (w.signal().aborted) return fail('the signal of the page that is shown is aborted');
        return ok('the listeners of a page leave with it: ' + after2 + ' after two pages, as after one');
    },

    // what a page whose script has requests of its own leaves running when it is left (Hard Delete)
    async a_hard_delete_list_that_fails_after_the_page_was_left_writes_nothing_to_the_new_page() {
        const w = build('/Admin');
        w.click('/Admin/HardDelete');
        await w.settle();
        if (w.log.api.length !== 1 || !/hard-delete\/list/.test(w.log.api[0].url)) return fail('the page did not ask for its list: ' + JSON.stringify(w.log.api.map(a => a.url)));
        w.click('/Admin/TrustedNodes');
        await w.settle();
        w.log.api[0].fail();
        await w.settle();
        if (w.log.appended.length) return fail('a toast was put on the page that replaced Hard Delete (' + w.log.appended.length + ')');
        const stale = w.log.pageWrites.filter(x => x.stale);
        if (stale.length) return fail('the page that was left was written to after the swap: ' + JSON.stringify(stale));
        if (!w.log.api[0].aborted) return fail('the request of the list was not given the signal of the page: it runs on after the page is gone');
        return ok('the late failure of the list is not told to anybody, and the request ended with the page');
    },
    async a_hard_delete_list_that_arrives_after_the_page_was_left_writes_nothing() {
        const w = build('/Admin');
        w.click('/Admin/HardDelete');
        await w.settle();
        w.click('/Admin/TrustedNodes');
        await w.settle();
        w.log.api[0].answer({ totalCount: 1, items: [{ type: 'article', id: 'a1', title: 'T', path: '/P', status: 'A', createdAt: '2026-01-01T00:00:00Z', size: 10 }] });
        await w.settle();
        const stale = w.log.pageWrites.filter(x => x.stale);
        if (stale.length || w.log.rows.length || w.log.appended.length) return fail('a late list was drawn: ' + JSON.stringify({ stale, rows: w.log.rows.length, toasts: w.log.appended.length }));
        return ok('a late list is not drawn');
    },
    async the_hard_delete_page_still_loads_draws_and_reports_while_it_is_there() {
        const w = build('/Admin');
        w.click('/Admin/HardDelete');
        await w.settle();
        w.log.api[0].answer({ totalCount: 1, items: [{ type: 'article', id: 'a1', title: 'T', path: '/P', status: 'A', createdAt: '2026-01-01T00:00:00Z', size: 10 }] });
        await w.settle();
        if (w.log.rows.length !== 1) return fail('the list was not drawn (rows: ' + w.log.rows.length + ')');
        if (w.el('pagination-info').textContent !== '1\u20131 of 1') return fail('the paging says ' + JSON.stringify(w.el('pagination-info').textContent));
        if (w.log.appended.length) return fail('a toast on a good list');
        const v = build('/Admin');
        v.click('/Admin/HardDelete');
        await v.settle();
        v.log.api[0].fail();
        await v.settle();
        if (v.log.appended.length !== 1) return fail('a failed list is not told on the page that is shown (toasts: ' + v.log.appended.length + ')');
        return ok('drawn when it arrives, told when it fails, while the page is there');
    },
    async an_audit_log_that_arrives_after_the_page_was_left_writes_nothing() {
        const w = build('/Admin');
        w.click('/Admin/HardDelete');
        await w.settle();
        await w.el('dlg-audit').fire('sl-show');
        await w.settle();
        const audit = w.log.api.find(a => /hard-delete\/audit/.test(a.url));
        if (!audit) return fail('opening the log did not ask for it');
        w.click('/Admin/TrustedNodes');
        await w.settle();
        audit.answer({ totalCount: 1, items: [{ occurredAt: '2026-01-01T00:00:00Z', entityType: 'article', entityTitle: 'T', entityIdentifier: 'a1', deletedArticles: 1, deletedFolders: 0, deletedMedia: 0, userId: 1 }] });
        await w.settle();
        const stale = w.log.pageWrites.filter(x => x.stale);
        if (stale.length) return fail('the log was drawn into the page that was left: ' + JSON.stringify(stale));
        if (!audit.aborted) return fail('the request of the log was not given the signal of the page');
        return ok('a late audit log is not drawn');
    },
    async the_typing_delay_of_the_filter_dies_with_the_page() {
        const w = build('/Admin');
        w.click('/Admin/HardDelete');
        await w.settle();
        await w.el('filter-input').fire('sl-input');
        const delay = () => w.log.timers.filter(t => t.ms === 300);
        if (delay().length !== 1 || delay()[0].cleared) return fail('typing in the filter must wait 300 ms before it asks (timers: ' + JSON.stringify(delay()) + ')');
        w.click('/Admin/TrustedNodes');
        await w.settle();
        if (!delay()[0].cleared) return fail('the pending ask of the filter is still there on the page that replaced Hard Delete');
        return ok('cleared on leaving');
    },
    async a_hard_delete_that_ends_after_the_page_was_left_is_not_taken_back_and_says_so_once() {
        // The deletion was started on purpose: it is not aborted with the page (a request that was sent cannot be taken back, and a half-cancelled deletion helps nobody);
        // what ends is the writing into the page that is gone.
        async function start(w) {
            w.click('/Admin/HardDelete');
            await w.settle();
            w.log.api[0].answer({ totalCount: 1, items: [{ type: 'article', id: 'a1', title: 'T', path: '/P', status: 'A', createdAt: '2026-01-01T00:00:00Z', size: 10 }] });
            await w.settle();
            await w.log.rows[0].querySelector('.btn-delete-row').fire('click');
            await w.settle();
            w.el('btn-final-delete').fire('click');
            await w.settle();
            return w.log.busy[0];
        }
        const body = { body: { deletedArticles: 1, deletedFolders: 0, deletedMedia: 0 } };

        const w = build('/Admin');
        const request = await start(w);
        if (!request || !/hard-delete\/article\/a1/.test(request.url)) return fail('the deletion was not sent: ' + JSON.stringify(w.log.busy.map(b => b.url)));
        if (request.init.signal) return fail('the deletion was given the signal of the page: leaving the page would cancel it on the node');
        w.click('/Admin/TrustedNodes');
        await w.settle();
        if (request.aborted) return fail('the deletion was aborted by leaving the page');
        request.resolve(body);
        await w.settle();
        if (w.log.appended.length) return fail('"Hard delete successful" was put on the page that replaced Hard Delete');
        const stale = w.log.pageWrites.filter(x => x.stale);
        if (stale.length) return fail('the page that was left was written to after the swap: ' + JSON.stringify(stale));
        if (w.log.notices.length !== 1 || !w.log.notices[0].late) return fail('the end of the deletion is not said once as a late notice: ' + JSON.stringify(w.log.notices));

        const v = build('/Admin');
        const open = await start(v);
        open.resolve(body);
        await v.settle();
        if (v.log.appended.length !== 1) return fail('while the page is there the deletion must end with its toast (toasts: ' + v.log.appended.length + ')');
        if (v.log.notices.length) return fail('a notice besides the toast while the page is there: ' + JSON.stringify(v.log.notices));
        return ok('the toast while the page is there; once, as a notice, and never on the other page, when it was left; never aborted');
    },

    // the rows admin.js builds as strings (AI / Chat): the classes and the data-label of the stacked layout, and the sentences that stand in place of the rows
    async the_rows_of_the_ai_chat_tables_carry_the_classes_and_labels_of_the_stacked_layout() {
        const w = build('/Admin');
        w.click('/Admin/AiChat');
        await w.settle();
        const keys = w.log.api.find(a => /\/api-proxy\/chat\/keys$/.test(a.url));
        const models = w.log.api.find(a => /\/api-proxy\/chat\/models\/all$/.test(a.url));
        if (!keys || !models) return fail('the page did not ask for its keys and its models: ' + JSON.stringify(w.log.api.map(a => a.url)));
        keys.answer([{ id: 7, label: 'Main key', keyPrefix: 'sk-or-v1-abc', priority: 2, enabled: true, lastUsedAt: null }]);
        models.answer([{ id: 3, label: 'Fast', modelId: 'vendor/some-very-long-model-id:beta', isText: true, isVision: false, isImageGen: true }]);
        await w.settle();
        const k = w.el('chat-keys-body').innerHTML;
        const mo = w.el('chat-models-body').innerHTML;
        const need = (html, parts, what) => parts.filter(p => !html.includes(p)).map(p => what + ' lacks ' + p);
        const missing = [
            ...need(k, ['<td class="col-name"><strong>Main key</strong></td>', 'data-label="Prefix"', 'data-label="Priority"', 'data-label="Enabled"', 'data-label="Status"', 'data-label="Last used"',
                '<td class="col-actions"><div class="row-actions"><sl-icon-button name="trash" label="Delete"'], 'a key row'),
            ...need(mo, ['<td class="col-name"><strong>Fast</strong></td>', '<td class="wrap-anywhere" data-label="Model ID">', 'class="col-center" data-label="Text"', 'class="col-center" data-label="Vision"',
                'class="col-center" data-label="Image gen"', '<td class="col-actions"><div class="row-actions">', 'label="Edit"', 'label="Delete"'], 'a model row'),
        ];
        if (/style="text-align:(center|right);"/.test(k + mo)) missing.push('a row still aligns its cells by an inline style, which the stacked layout cannot undo');
        return missing.length ? fail(missing.join('; ')) : ok('the rows of both tables carry the classes and the labels');
    },
    async the_empty_and_the_failed_states_of_the_ai_chat_tables_are_sentences_that_wrap() {
        const w = build('/Admin');
        w.click('/Admin/AiChat');
        await w.settle();
        const keys = w.log.api.find(a => /\/api-proxy\/chat\/keys$/.test(a.url));
        const models = w.log.api.find(a => /\/api-proxy\/chat\/models\/all$/.test(a.url));
        keys.answer([]);
        models.answer([]);
        await w.settle();
        const k = w.el('chat-keys-body').innerHTML;
        const mo = w.el('chat-models-body').innerHTML;
        if (!/<td colspan="7" class="col-empty[^"]*">No keys\. Add one to enable AI chat\.<\/td>/.test(k)) return fail('the empty keys row is ' + k);
        if (!/<td colspan="6" class="col-empty[^"]*">No models\./.test(mo)) return fail('the empty models row is ' + mo);
        if (/style=/.test(k + mo)) return fail('an empty row has an inline style (the padding and the alignment are the class\'s): ' + k + mo);
        const v = build('/Admin');
        v.click('/Admin/AiChat');
        await v.settle();
        v.log.api.find(a => /\/chat\/keys$/.test(a.url)).fail();
        v.log.api.find(a => /\/chat\/models\/all$/.test(a.url)).fail();
        await v.settle();
        if (!/class="col-empty[^"]*">Failed to load keys\./.test(v.el('chat-keys-body').innerHTML) || !/class="col-empty[^"]*">Failed to load models\./.test(v.el('chat-models-body').innerHTML)) return fail('a failed load is not a col-empty sentence');
        return ok('empty and failed rows are col-empty sentences with no inline style');
    },
};

(async () => {
    if (scenario === '--list') { console.log(JSON.stringify(Object.keys(scenarios))); return; }
    const fn = scenarios[scenario];
    if (!fn) { console.log(JSON.stringify(fail('no such scenario: ' + scenario))); return; }
    try {
        console.log(JSON.stringify(await fn()));
    } catch (e) {
        console.log(JSON.stringify(fail('it threw: ' + (e && e.stack || e))));
    }
})();
