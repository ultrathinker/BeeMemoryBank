'use strict';
// Runs a script of the Admin area (admin.js, admin-index.js) in a small fake page, with no packages: node admin-pages-harness.js <script> <scenario>.
// The Admin area is a page per block and one script serves them all, so what matters is what it does on a page that has only some of its elements, and on a page
// that has none. Prints one JSON line: { ok, detail }.

const fs = require('fs');
const vm = require('vm');

const [, , scriptPath, scenario] = process.argv;
const source = fs.readFileSync(scriptPath, 'utf8');

/** A fake element: it records what is listened to and what is set, and does nothing else. */
function element(props) {
    const el = Object.assign({
        value: '', checked: false, textContent: '', innerHTML: '', style: {}, dataset: {}, hidden: false,
        listeners: [],
        addEventListener(type, fn) { this.listeners.push({ type, fn }); },
        removeEventListener() {},
        getAttribute(name) { return (this.attrs || {})[name] ?? null; },
        show() {}, hide() {}, click() {}, focus() {},
        appendChild() {}, closest() { return null; },
    }, props || {});
    return el;
}

/** A page with exactly these elements (by id); every other lookup finds nothing. Returns the sandbox and what happened in it. */
function page(elements, extra) {
    const log = { fetched: [], timers: 0, requests: [], replaced: [], listeners: [], alerts: [], hrefs: [] };
    const doc = {
        getElementById: id => elements[id] || null,
        querySelector: sel => ((extra && extra.selectors) || {})[sel] || null,
        querySelectorAll: () => [],
        addEventListener(type, fn) { log.listeners.push({ type, fn }); },
        createElement: () => element(),
        readyState: 'complete',
    };
    const location = {
        hash: (extra && extra.hash) || '', href: '', reload() { log.reloaded = true; },
        replace(url) { log.replaced.push(url); },
    };
    const sandbox = {
        document: doc, location, console,
        setTimeout() { log.timers++; return 1; }, clearTimeout() {},
        fetch(url) { log.fetched.push(String(url)); return Promise.resolve({ ok: true, json: () => Promise.resolve({}) }); },
        confirm: () => true, alert(m) { log.alerts.push(String(m)); },
        navigator: { clipboard: { writeText: () => Promise.resolve() } },
        FormData: class { constructor() { this.parts = []; } append(k, v) { this.parts.push([k, v]); } },
        XMLHttpRequest: class { constructor() { this.upload = { addEventListener() {} }; } open(m, u) { log.requests.push(m + ' ' + u); } setRequestHeader() {} send() {} },
        bmbBusy: {
            run(el, fn) { return Promise.resolve().then(fn); },
            request(url, opts) { log.requests.push(((opts && opts.method) || 'GET') + ' ' + url); return Promise.resolve({ response: { redirected: false, url: '' }, body: { eventId: 'e1' } }); },
            submit() {}, setModalMessage() {}, showResult() {},
        },
        Date, JSON, Promise, Array, Object, String, Number, parseInt, isNaN, encodeURIComponent, decodeURIComponent,
    };
    sandbox.window = sandbox;
    sandbox.location = location;
    return { sandbox, log, doc };
}

function run(p) {
    vm.createContext(p.sandbox);
    vm.runInContext(source, p.sandbox, { filename: scriptPath });
}

/** The event a click or a switch change would make, aimed at something that is none of the controls of the script. */
function stray() {
    return { target: { closest: () => null, nodeType: 1 }, preventDefault() {}, stopImmediatePropagation() {}, stopPropagation() {} };
}

async function flush() { for (let i = 0; i < 5; i++) await new Promise(r => setImmediate(r)); }

const scenarios = {
    // admin.js
    async a_page_without_any_of_its_elements_does_nothing() {
        const p = page({});
        run(p);
        for (const l of p.log.listeners) l.fn(stray());
        await flush();
        if (p.log.fetched.length) return fail('it asked the server for ' + p.log.fetched.join(', '));
        if (p.log.timers) return fail('it set a timer');
        if (p.log.requests.length) return fail('it sent ' + p.log.requests.join(', '));
        return ok('no call, no timer, ' + p.log.listeners.length + ' delegated listeners that ignore a stray event');
    },
    async the_branding_page_asks_for_the_branding_and_nothing_else() {
        const p = page({
            'brand-name-input': element(), 'brand-preview': element(), 'btn-save-branding': element(), 'btn-reset-branding': element(), 'branding-saved-msg': element(),
        });
        run(p);
        await flush();
        return same(p.log.fetched, ['/api-proxy/branding']);
    },
    async the_web_session_page_asks_for_the_session_settings_and_nothing_else() {
        const p = page({
            'session-expire-hours': element(), 'session-sliding-toggle': element(), 'btn-save-session-settings': element(), 'session-settings-saved-msg': element(),
        });
        run(p);
        await flush();
        return same(p.log.fetched, ['/api-proxy/session/settings']);
    },
    async the_search_page_asks_for_the_embeddings_switch_and_nothing_else() {
        const p = page({ 'embeddings-enabled-toggle': element(), 'embeddings-backfill-btn': element(), 'embeddings-backfill-status': element() });
        run(p);
        await flush();
        return same(p.log.fetched, ['/api-proxy/admin/search/embeddings-enabled']);
    },
    async the_ai_page_asks_for_its_keys_models_defaults_and_switch() {
        const p = page({
            'chat-keys-body': element(), 'chat-models-body': element(), 'chat-globally-enabled-toggle': element(),
            'default-text-model': element(), 'default-vision-model': element(), 'default-imagegen-model': element(),
        });
        run(p);
        await flush();
        const want = ['/api-proxy/chat/keys', '/api-proxy/chat/models/all', '/api-proxy/chat/settings/chat-enabled'];
        const missing = want.filter(u => !p.log.fetched.includes(u));
        if (missing.length) return fail('it did not ask for ' + missing.join(', ') + '; it asked for ' + p.log.fetched.join(', '));
        const foreign = p.log.fetched.filter(u => /branding|session\/settings|embeddings/.test(u));
        return foreign.length ? fail('it asked for ' + foreign.join(', ')) : ok('keys, models, defaults and the switch');
    },
    async the_snapshots_page_posts_to_its_own_handlers() {
        const mode = element({ value: 'standalone' });
        const restore = element();
        const p = page({
            'restore-mode': mode, 'btn-restore-confirm': restore, 'restore-file-name': element({ value: 'a.tar.gz' }),
            'restore-master-password': element({ value: 'pw' }), 'restore-backup-checkbox': element({ checked: true }), 'dlg-restore-snapshot': element(),
        }, { selectors: { 'input[name="__RequestVerificationToken"]': element({ value: 'tok' }) } });
        run(p);
        const click = restore.listeners.find(l => l.type === 'click');
        if (!click) return fail('the Restore button of the dialog has no click handler');
        click.fn(stray());
        await flush();
        mode.value = 'network';
        click.fn(stray());
        await flush();
        const posts = p.log.requests.filter(r => r.startsWith('POST '));
        return same(posts, ['POST /Admin/Snapshots?handler=RestoreSnapshot', 'POST /Admin/Snapshots?handler=InitiateNetworkRestore']);
    },
    async the_upload_goes_to_the_snapshots_page() {
        const zone = element(), input = element(), pick = element();
        const p = page({ 'snapshot-upload-zone': zone, 'snapshot-file-input': input, 'btn-pick-file': pick, 'upload-progress': element(), 'upload-status': element(), 'upload-progress-bar': element() });
        run(p);
        const change = input.listeners.find(l => l.type === 'change');
        if (!change) return fail('the file input has no change handler');
        change.fn({ target: { files: [{ name: 'x.tar.gz' }] } });
        await flush();
        return same(p.log.requests, ['POST /Admin/Snapshots?handler=UploadSnapshot']);
    },

    // admin-index.js
    async the_index_moves_a_known_fragment_to_its_page() {
        const map = JSON.stringify({ 'card-snapshots': '/Admin/Snapshots', 'card-metadata': '/Admin/MetadataEncryption' });
        const holder = element({ attrs: { 'data-map': map } });
        const p = page({ 'admin-legacy-anchors': holder }, { hash: '#card-metadata' });
        run(p);
        return same(p.log.replaced, ['/Admin/MetadataEncryption']);
    },
    async the_index_ignores_what_it_does_not_know() {
        const map = JSON.stringify({ 'card-snapshots': '/Admin/Snapshots' });
        for (const hash of ['', '#', '#nothing', '#constructor', '#__proto__', '#toString', '#hasOwnProperty', '#%E0%A4%A']) {
            const p = page({ 'admin-legacy-anchors': element({ attrs: { 'data-map': map } }) }, { hash });
            run(p);
            if (p.log.replaced.length) return fail('it moved ' + JSON.stringify(hash) + ' to ' + p.log.replaced[0]);
        }
        return ok('an empty, unknown, inherited or damaged fragment stays where it is');
    },
    async the_index_follows_nothing_but_a_page_of_the_area() {
        for (const target of ['https://evil.example/x', '//evil.example/Admin/X', '/Admin/../Login', '/Login', 'javascript:alert(1)', '/Admin/', '/Admin/X/Y', '/admin/Snapshots/']) {
            const p = page({ 'admin-legacy-anchors': element({ attrs: { 'data-map': JSON.stringify({ 'card-x': target }) } }) }, { hash: '#card-x' });
            run(p);
            if (p.log.replaced.length) return fail('it followed ' + target);
        }
        return ok('only /Admin/<Name> is followed');
    },
    async the_index_without_its_table_or_with_a_damaged_one_does_nothing() {
        const none = page({}, { hash: '#card-snapshots' });
        run(none);
        const damaged = page({ 'admin-legacy-anchors': element({ attrs: { 'data-map': '{not json' } }) }, { hash: '#card-snapshots' });
        run(damaged);
        const missing = page({ 'admin-legacy-anchors': element() }, { hash: '#card-snapshots' });
        run(missing);
        return none.log.replaced.length + damaged.log.replaced.length + missing.log.replaced.length === 0 ? ok('nothing happens') : fail('it moved');
    },
};

function ok(detail) { return { ok: true, detail }; }
function fail(detail) { return { ok: false, detail }; }
function same(actual, expected) {
    return JSON.stringify(actual) === JSON.stringify(expected) ? ok(actual.join(', ')) : fail('expected ' + JSON.stringify(expected) + ' but it was ' + JSON.stringify(actual));
}

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
