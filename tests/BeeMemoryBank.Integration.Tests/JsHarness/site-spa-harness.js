'use strict';
// Runs site-spa.js (the single-page navigation) in a small fake browser, with no packages: node site-spa-harness.js <site-spa.js> <scenario>.
// The browser is a fake in the one way that matters here: what the script DECIDES. Which links it takes over, what it fetches, what it executes after the swap,
// what it cleans up when the page is left. The pages are named by path in `PAGES`; a "fetch" answers with a token and the "DOMParser" turns the token back into the
// page, so no HTML parser is needed. The real admin.js is run when a swapped page asks for it, to see that its timers and listeners die with the page.
// Prints one JSON line: { ok, detail }.

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const [, , spaPath, scenario] = process.argv;
const spaSource = fs.readFileSync(spaPath, 'utf8');
const adminPath = path.join(path.dirname(spaPath), 'pages', 'admin.js');

const ORIGIN = 'http://localhost';

// What each page of the stand has: its title, the scripts the layout puts after site.js (section Scripts), and the elements its own script looks for.
const PAGES = {
    '/Admin': { title: 'Admin - Bee', scripts: ['/js/pages/admin-index.js?v=I'], selectors: {} },
    '/Admin/Snapshots': { title: 'Snapshots - Bee', scripts: ['/js/pages/admin.js?v=A'], selectors: {} },
    '/Admin/TrustedNodes': { title: 'Trusted Nodes - Bee', scripts: ['/js/pages/admin.js?v=A'], selectors: {} },
    '/Admin/DekRotation': { title: 'DEK Rotation - Bee', scripts: ['/js/pages/admin.js?v=A'], selectors: { '[data-dek-active="true"]': {} } },
    '/Admin/Agents': { title: 'Agents - Bee', scripts: ['/js/pages/admin.js?v=A'], selectors: {} },
    '/Search': { title: 'Search - Bee', scripts: [], selectors: {} },
    '/Tree': { title: 'Tree - Bee', scripts: [], selectors: {} },
};

function build(startPath) {
    const log = { fetched: [], navigations: [], reloads: 0, pushed: [], replaced: [], executed: [], events: [], timers: [], prevented: 0, titles: [] };
    const listeners = {};
    const docListeners = {};
    const winListeners = {};

    const main = { scrollTop: 0, style: {}, dataset: {} };
    let currentPath = startPath;
    const content = {
        _html: '<<' + startPath + '>>',
        get innerHTML() { return this._html; },
        set innerHTML(v) { this._html = v; const m = /<<(.*)>>/.exec(v); if (m) currentPath = m[1]; },
        querySelector: sel => (sel === '.main-content' ? main : null),
        querySelectorAll: () => [],
        appendChild(script) {
            const src = script.attrs.src || null;
            log.executed.push(src);
            // the real script, when the layout of the stand loads it
            if (src && /\/admin\.js\b/.test(src.split('?')[0])) vm.runInContext(fs.readFileSync(adminPath, 'utf8'), sandbox, { filename: adminPath });
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
        body: { classList: { remove() {} } },
        getElementById: id => (id === 'page-content' ? content : null),
        querySelector: sel => {
            if (sel === '.main-content' || sel === '#page-content .main-content') return main;
            return PAGES[currentPath] && PAGES[currentPath].selectors[sel] || null;
        },
        querySelectorAll: () => [],
        addEventListener(type, fn, opts) { addListener(docListeners, type, fn, opts); },
        dispatchEvent(ev) { log.events.push(ev.type); (docListeners[ev.type] || []).slice().forEach(f => f(ev)); },
        createElement() {
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
        fetch(url) {
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
    sandbox.bmbBusy = { run() { return Promise.resolve(); }, request() { return Promise.resolve({}); } };
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
