'use strict';
// Runs the script of the Users page (wwwroot/js/pages/users.js) in a small fake page, with no packages: node users-page-harness.js <users.js> <scenario>.
// What matters here is what the dialogs DO: the Edit dialog never moves a person across the superadmin line, "Make superadmin..." asks first, and "Remove superadmin
// rights..." cannot be confirmed before the answer to "what may they see afterwards?" is complete. Prints one JSON line: { ok, detail }.

const fs = require('fs');
const vm = require('vm');

const [, , scriptPath, scenario] = process.argv;
const source = fs.readFileSync(scriptPath, 'utf8');

/** A fake element: it records its listeners and what is set on it; innerHTML is read back for the children a script builds from it. */
function element(props) {
    const el = Object.assign({
        value: '', checked: false, disabled: false, textContent: '', innerHTML: '', style: {}, dataset: {}, listeners: [], shown: 0,
        addEventListener(type, fn) { this.listeners.push({ type, fn }); },
        fire(type) { for (const l of this.listeners.filter(x => x.type === type)) l.fn({ target: this, preventDefault() {} }); },
        show() { this.shown++; }, hide() { this.shown = 0; }, focus() {},
        querySelector(sel) { return sel === '.msg' ? (this._msg || (this._msg = element())) : null; },
        // The children a script puts into innerHTML as <tag class="x" data-a="b">: found again by their class, with their data- attributes and listeners.
        querySelectorAll(sel) {
            if (!sel.startsWith('.')) return [];
            const cls = sel.slice(1);
            this._found = this._found || {};
            const re = new RegExp('<[a-z-]+ ([^>]*class="[^"]*\\b' + cls + '\\b[^"]*"[^>]*)>', 'g');
            const found = [];
            let m;
            while ((m = re.exec(this.innerHTML)) !== null) {
                const attrs = m[1];
                const key = attrs;
                if (!this._found[key]) {
                    const dataset = {};
                    for (const d of attrs.matchAll(/data-([a-z-]+)="([^"]*)"/g)) dataset[d[1].replace(/-([a-z])/g, (_, c) => c.toUpperCase())] = d[2].replace(/&quot;/g, '"').replace(/&amp;/g, '&');
                    this._found[key] = element({ dataset, checked: /\bchecked\b/.test(attrs) });
                }
                found.push(this._found[key]);
            }
            return found;
        },
    }, props || {});
    return el;
}

/** The page: the elements of users.js by id, the buttons of the table by class, what was fetched, sent and navigated to. */
function usersPage(options) {
    options = options || {};
    const log = { fetched: [], requests: [], navigated: [], timers: [] };
    const ids = [
        'btn-open-add-user', 'dlg-add-user', 'add-username', 'add-display-name', 'add-password', 'add-password-hint', 'add-role', 'add-user-error', 'btn-add-user-submit', 'add-superadmin-warning',
        'dlg-change-password', 'cp-user-id', 'cp-user-name', 'cp-new-password', 'cp-password-hint', 'change-password-error', 'btn-change-password-submit',
        'dlg-edit-user', 'edit-user-id', 'edit-display-name', 'edit-role', 'edit-role-fixed', 'edit-role-choice', 'edit-role-hint', 'edit-chat-access', 'edit-chat-access-hint', 'edit-user-error', 'btn-edit-user-submit',
        'dlg-make-superadmin', 'make-superadmin-error', 'make-sa-user-id', 'make-sa-display', 'make-sa-name', 'btn-make-sa-confirm',
        'dlg-remove-superadmin', 'remove-superadmin-error', 'remove-sa-user-id', 'remove-sa-display', 'remove-sa-name', 'remove-sa-choice', 'remove-sa-panel-folders', 'remove-sa-panel-role',
        'remove-sa-panel-open', 'remove-sa-folder-search', 'remove-sa-folder-dropdown', 'remove-sa-folder-list', 'remove-sa-role', 'remove-sa-role-empty', 'remove-sa-open-ack', 'btn-remove-sa-confirm',
        'remove-sa-end', 'remove-sa-panel-access', 'remove-sa-password',
        'form-add-user', 'form-change-password', 'form-edit-user',
    ];
    const elements = {};
    for (const id of ids) elements[id] = element();
    elements['btn-remove-sa-confirm'].disabled = true;   // as the markup has it

    const buttons = {
        '.btn-user-edit': options.editButtons || [],
        '.btn-user-make-superadmin': options.makeButtons || [],
        '.btn-user-remove-superadmin': options.removeButtons || [],
    };
    const doc = {
        getElementById: id => elements[id] || null,
        querySelectorAll: sel => buttons[sel] || [],
        querySelector: () => null,
        addEventListener() {},
    };
    const location = { href: 'https://node.test/Users', pathname: '/Users', search: '', hash: '' };
    let navigated = null;
    Object.defineProperty(location, 'assign', { value: u => log.navigated.push(u) });
    const sandbox = {
        document: doc, console, URL, history: { state: null, replaceState() {} }, customElements: { whenDefined: () => Promise.resolve() },
        setTimeout(fn, ms) { log.timers.push(ms); return 1; }, clearTimeout() {},
        fetch(url) {
            log.fetched.push(String(url));
            const answer = (options.fetch || {})[String(url).split('?')[0]];
            return Promise.resolve({ ok: answer !== undefined, json: () => Promise.resolve(answer === undefined ? {} : answer) });
        },
        confirm: () => true,
        bmbBusy: {
            // The shape of the real helper: run() resolves true when the work went through, false when it threw (after handing the error to opts.error).
            async run(btn, fn, opts) {
                try { await fn(); return true; } catch (e) { if (opts && opts.error) opts.error(e); return false; }
            },
            async request(url, opts) {
                log.requests.push({ method: (opts && opts.method) || 'GET', url, body: opts && opts.body ? JSON.parse(opts.body) : null });
                if (options.refuse) throw new Error(options.refuse);
                return { response: { ok: true }, body: {} };
            },
            submit() {},
        },
        Date, JSON, Promise, Array, Object, String, Number, parseInt, isNaN, encodeURIComponent, decodeURIComponent, Set,
    };
    sandbox.window = sandbox;
    Object.defineProperty(sandbox, 'location', { get: () => location, set: v => log.navigated.push(String(v)) });
    Object.defineProperty(location, 'href', { get: () => 'https://node.test/Users', set: v => log.navigated.push(String(v)) });
    return { sandbox, log, elements, location };
}

function button(dataset) { return element({ dataset }); }

function run(p) {
    vm.createContext(p.sandbox);
    vm.runInContext(source, p.sandbox, { filename: scriptPath });
}

async function flush() { for (let i = 0; i < 6; i++) await new Promise(r => setImmediate(r)); }

const ROLES = [
    { name: 'superadmin', displayName: 'Superadmin', isSystem: true, basePolicy: 'open', ruleCount: 0, userCount: 1 },
    { name: 'user', displayName: 'User', isSystem: true, basePolicy: 'open', ruleCount: 0, userCount: 2 },
    { name: 'user-closed', displayName: 'Closed', isSystem: false, basePolicy: 'closed', ruleCount: 1, userCount: 0 },
    { name: 'user-open', displayName: 'Open', isSystem: false, basePolicy: 'open', ruleCount: 2, userCount: 0 },
];

const scenarios = {
    // ---- the Edit dialog
    async the_edit_dialog_of_a_superadmin_shows_the_role_and_sends_no_role() {
        const edit = button({ uid: '3', display: 'Carol', role: 'superadmin', chatAccess: 'true' });
        const p = usersPage({ editButtons: [edit], fetch: { '/api-proxy/roles': ROLES } });
        run(p);
        await flush();
        edit.fire('click');
        const e = p.elements;
        if (e['edit-role-fixed'].style.display !== '') return fail('the role of a superadmin is not shown');
        if (e['edit-role-choice'].style.display !== 'none') return fail('a role choice is offered for a superadmin');
        e['edit-display-name'].value = 'Carol B';
        e['edit-chat-access'].checked = true;
        e['btn-edit-user-submit'].fire('click');
        await flush();
        const put = p.log.requests.find(r => r.method === 'PUT');
        if (!put) return fail('nothing was saved');
        if ('role' in put.body) return fail('the save sent a role: ' + JSON.stringify(put.body));
        return put.url === '/api-proxy/users/3' && put.body.displayName === 'Carol B' ? ok('the role is shown, not chosen, and not sent') : fail('unexpected request ' + JSON.stringify(put));
    },
    async the_edit_dialog_of_an_ordinary_user_offers_no_superadmin() {
        const edit = button({ uid: '5', display: 'Bob', role: 'user', chatAccess: 'true' });
        const p = usersPage({ editButtons: [edit], fetch: { '/api-proxy/roles': ROLES } });
        run(p);
        await flush();
        const e = p.elements;
        if (/value="superadmin"/.test(e['edit-role'].innerHTML)) return fail('the Edit dialog offers superadmin: ' + e['edit-role'].innerHTML);
        if (!/value="user-closed"/.test(e['edit-role'].innerHTML)) return fail('the other roles are gone');
        if (!/value="superadmin"/.test(e['add-role'].innerHTML)) return fail('the Add dialog lost superadmin (a new person may be created as one, with the warning)');
        edit.fire('click');
        if (e['edit-role-fixed'].style.display !== 'none' || e['edit-role-choice'].style.display !== '') return fail('the choice is not shown for an ordinary user');
        e['edit-role'].value = 'user-closed';
        e['edit-display-name'].value = 'Bob';
        e['btn-edit-user-submit'].fire('click');
        await flush();
        const put = p.log.requests.find(r => r.method === 'PUT');
        return put && put.body.role === 'user-closed' ? ok('the choice of an ordinary user holds no superadmin') : fail('unexpected ' + JSON.stringify(put));
    },
    async a_superadmin_picked_in_the_edit_dialog_is_not_sent() {
        // Whatever reaches the select, the save of an ordinary user never carries 'superadmin': the way across is the other dialog.
        const edit = button({ uid: '5', display: 'Bob', role: 'user', chatAccess: 'true' });
        const p = usersPage({ editButtons: [edit], fetch: { '/api-proxy/roles': ROLES } });
        run(p);
        await flush();
        edit.fire('click');
        p.elements['edit-role'].value = 'superadmin';
        p.elements['edit-display-name'].value = 'Bob';
        p.elements['btn-edit-user-submit'].fire('click');
        await flush();
        if (p.log.requests.length) return fail('it sent ' + JSON.stringify(p.log.requests));
        return /Make superadmin/.test(p.elements['edit-user-error']._msg.textContent) ? ok('refused, and says where to go') : fail('no explanation');
    },

    // ---- the Add dialog
    async choosing_superadmin_in_the_add_dialog_shows_what_it_is() {
        const p = usersPage({ fetch: { '/api-proxy/roles': ROLES } });
        run(p);
        await flush();
        const warn = p.elements['add-superadmin-warning'];
        p.elements['add-role'].value = 'superadmin';
        p.elements['add-role'].fire('sl-change');
        if (warn.style.display !== '') return fail('no warning for a new superadmin');
        p.elements['add-role'].value = 'user';
        p.elements['add-role'].fire('sl-change');
        return warn.style.display === 'none' ? ok('shown for superadmin only') : fail('the warning stays for another role');
    },

    // ---- Make superadmin...
    async make_superadmin_asks_first_and_then_puts_the_role() {
        const make = button({ uid: '5', name: 'bob', display: 'Bob B' });
        const p = usersPage({ makeButtons: [make], fetch: { '/api-proxy/roles': ROLES } });
        run(p);
        await flush();
        make.fire('click');
        const e = p.elements;
        if (!e['dlg-make-superadmin'].shown) return fail('the confirmation did not open');
        if (e['make-sa-name'].textContent !== 'bob') return fail('the confirmation does not name the person');
        if (p.log.requests.length) return fail('it sent something before the confirmation: ' + JSON.stringify(p.log.requests));
        e['btn-make-sa-confirm'].fire('click');
        await flush();
        const put = p.log.requests.find(r => r.method === 'PUT');
        if (!put || put.url !== '/api-proxy/users/5' || put.body.role !== 'superadmin' || put.body.displayName !== 'Bob B') return fail('unexpected ' + JSON.stringify(put));
        return p.log.navigated.some(u => u.startsWith('?msg=')) ? ok('confirmed, sent, the page says it happened') : fail('the page was not told');
    },
    async make_superadmin_that_is_refused_says_so_in_the_dialog_and_does_not_reload() {
        const make = button({ uid: '5', name: 'bob', display: 'Bob' });
        const p = usersPage({ makeButtons: [make], refuse: 'The node said no.' });
        run(p);
        await flush();
        make.fire('click');
        p.elements['btn-make-sa-confirm'].fire('click');
        await flush();
        if (p.log.navigated.length) return fail('it navigated after a refusal');
        const err = p.elements['make-superadmin-error'];
        return err._msg && err._msg.textContent === 'The node said no.' && err.style.display === 'block' ? ok('the refusal is where the button is') : fail('the refusal was not shown in the dialog');
    },

    // ---- Remove superadmin rights...: a removal process (2.12) - what happens at the end, the access choice of a demotion, and the administrator's password
    async remove_cannot_be_confirmed_before_a_choice_is_made() {
        const remove = button({ uid: '3', name: 'carol', display: 'Carol' });
        const p = usersPage({ removeButtons: [remove], fetch: { '/api-proxy/roles': ROLES } });
        run(p);
        await flush();
        remove.fire('click');
        const e = p.elements;
        if (!e['dlg-remove-superadmin'].shown) return fail('the dialog did not open');
        if (e['remove-sa-name'].textContent !== 'carol') return fail('the dialog does not name the person');
        if (e['remove-sa-choice'].value !== '') return fail('a choice was made for the administrator: ' + e['remove-sa-choice'].value);
        if (e['remove-sa-end'].value !== '') return fail('the end of the removal was chosen for the administrator: ' + e['remove-sa-end'].value);
        if (!e['btn-remove-sa-confirm'].disabled) return fail('the button is on with no choice');
        // A complete access choice without the password: still off.
        chooseDemote(e, '');
        e['remove-sa-choice'].value = 'open';
        e['remove-sa-open-ack'].checked = true;
        e['remove-sa-choice'].fire('sl-change');
        if (!e['btn-remove-sa-confirm'].disabled) return fail('the button is on without the administrator\'s password');
        // Enter or a script reaches the handler without the button: still nothing is sent.
        e['btn-remove-sa-confirm'].fire('click');
        await flush();
        return p.log.requests.length === 0 ? ok('no choice, no request') : fail('it sent ' + JSON.stringify(p.log.requests));
    },
    async remove_with_folders_needs_a_folder_and_sends_the_rules() {
        const remove = button({ uid: '3', name: 'carol', display: 'Carol' });
        const p = usersPage({ removeButtons: [remove], fetch: { '/api-proxy/roles': ROLES, '/api-proxy/folders/search': { folders: [{ id: 'f-1', path: '/Public' }, { id: 'f-2', path: '/Public/Notes' }], hasMore: false } } });
        run(p);
        await flush();
        remove.fire('click');
        const e = p.elements;
        chooseDemote(e, 'Admin-Pass-1');
        e['remove-sa-choice'].value = 'folders';
        e['remove-sa-choice'].fire('sl-change');
        if (!e['btn-remove-sa-confirm'].disabled) return fail('the button is on with folders chosen but none picked');
        if (e['remove-sa-panel-folders'].style.display !== '' || e['remove-sa-panel-role'].style.display !== 'none') return fail('the wrong panel is open');

        e['remove-sa-folder-search'].value = 'pub';
        e['remove-sa-folder-search'].fire('sl-input');
        const search = p.log.timers.length;   // the search waits a moment after typing
        if (!search) return fail('the search did not start');
        // The timer of the harness does not run; call what the script asked for through a direct fetch of the same query: the dropdown fills when the answer arrives.
        const items = await pickFolder(p, 'pub');
        if (!items) return fail('the folders found were not offered');
        e['btn-remove-sa-confirm'].fire('click');
        await flush();
        const put = p.log.requests.find(r => r.method === 'PUT');
        if (!put) return fail('nothing was sent');
        const body = put.body;
        if (body.role !== 'user' || 'allowOpenRole' in body) return fail('role/open flag wrong: ' + JSON.stringify(body));
        if (body.adminPassword !== 'Admin-Pass-1') return fail('the administrator\'s password was not sent: ' + JSON.stringify(body));
        if (!body.folderRules || body.folderRules.length !== 1 || body.folderRules[0].folderId !== 'f-1' || body.folderRules[0].effect !== 'allow') return fail('rules wrong: ' + JSON.stringify(body));
        return ok('a folder picked, the rules sent as allow rules of the user role');
    },
    async remove_with_a_closed_role_sends_that_role_and_offers_only_closed_roles() {
        const remove = button({ uid: '3', name: 'carol', display: 'Carol' });
        const p = usersPage({ removeButtons: [remove], fetch: { '/api-proxy/roles': ROLES } });
        run(p);
        await flush();
        remove.fire('click');
        const e = p.elements;
        const options = e['remove-sa-role'].innerHTML;
        if (!/value="user-closed"/.test(options)) return fail('the closed role is not offered');
        if (/value="user-open"|value="user"|value="superadmin"/.test(options)) return fail('a role that is open (or superadmin) is offered as an access choice: ' + options);
        chooseDemote(e, 'Admin-Pass-1');
        e['remove-sa-choice'].value = 'role';
        e['remove-sa-choice'].fire('sl-change');
        if (!e['btn-remove-sa-confirm'].disabled) return fail('the button is on with no role picked');
        e['remove-sa-role'].value = 'user-closed';
        e['remove-sa-role'].fire('sl-change');
        if (e['btn-remove-sa-confirm'].disabled) return fail('the button stays off with a role picked');
        e['btn-remove-sa-confirm'].fire('click');
        await flush();
        const put = p.log.requests.find(r => r.method === 'PUT');
        return put && put.body.role === 'user-closed' && !('folderRules' in put.body) && !('allowOpenRole' in put.body) ? ok('the role is the choice') : fail('unexpected ' + JSON.stringify(put));
    },
    async remove_with_the_open_role_needs_the_tick_and_says_so_to_the_api() {
        const remove = button({ uid: '3', name: 'carol', display: 'Carol' });
        const p = usersPage({ removeButtons: [remove], fetch: { '/api-proxy/roles': ROLES } });
        run(p);
        await flush();
        remove.fire('click');
        const e = p.elements;
        chooseDemote(e, 'Admin-Pass-1');
        e['remove-sa-choice'].value = 'open';
        e['remove-sa-choice'].fire('sl-change');
        if (!e['btn-remove-sa-confirm'].disabled) return fail('the open role can be confirmed without the tick');
        e['remove-sa-open-ack'].checked = true;
        e['remove-sa-open-ack'].fire('sl-change');
        if (e['btn-remove-sa-confirm'].disabled) return fail('the tick did not enable the button');
        e['btn-remove-sa-confirm'].fire('click');
        await flush();
        const put = p.log.requests.find(r => r.method === 'PUT');
        return put && put.body.role === 'user' && put.body.allowOpenRole === true ? ok('allowOpenRole is sent, and only with the tick') : fail('unexpected ' + JSON.stringify(put));
    },
    async opening_the_dialog_again_forgets_the_last_choice() {
        const remove = button({ uid: '3', name: 'carol', display: 'Carol' });
        const p = usersPage({ removeButtons: [remove], fetch: { '/api-proxy/roles': ROLES } });
        run(p);
        await flush();
        remove.fire('click');
        const e = p.elements;
        e['remove-sa-choice'].value = 'open';
        e['remove-sa-open-ack'].checked = true;
        e['remove-sa-choice'].fire('sl-change');
        remove.fire('click');
        return e['remove-sa-choice'].value === '' && e['remove-sa-open-ack'].checked === false && e['btn-remove-sa-confirm'].disabled
            ? ok('nothing carries over from the last time') : fail('a choice carried over');
    },
    async a_removal_that_is_refused_says_so_in_the_dialog_and_does_not_reload() {
        const remove = button({ uid: '3', name: 'carol', display: 'Carol' });
        const p = usersPage({ removeButtons: [remove], fetch: { '/api-proxy/roles': ROLES }, refuse: 'Cannot demote the last superadmin' });
        run(p);
        await flush();
        remove.fire('click');
        const e = p.elements;
        chooseDemote(e, 'Admin-Pass-1');
        e['remove-sa-choice'].value = 'open';
        e['remove-sa-open-ack'].checked = true;
        e['remove-sa-choice'].fire('sl-change');
        e['btn-remove-sa-confirm'].fire('click');
        await flush();
        if (p.log.navigated.length) return fail('it navigated after a refusal');
        const err = e['remove-superadmin-error'];
        return err._msg && err._msg.textContent === 'Cannot demote the last superadmin' && err.style.display === 'block' ? ok('the refusal is in the dialog') : fail('the refusal was not shown');
    },
    async a_removal_that_goes_through_tells_the_page() {
        const remove = button({ uid: '3', name: 'carol', display: 'Carol' });
        const p = usersPage({ removeButtons: [remove], fetch: { '/api-proxy/roles': ROLES } });
        run(p);
        await flush();
        remove.fire('click');
        const e = p.elements;
        chooseDemote(e, 'Admin-Pass-1');
        e['remove-sa-choice'].value = 'open';
        e['remove-sa-open-ack'].checked = true;
        e['remove-sa-choice'].fire('sl-change');
        e['btn-remove-sa-confirm'].fire('click');
        await flush();
        const msg = p.log.navigated.find(u => u.startsWith('?msg='));
        if (e['remove-sa-password'].value !== '') return fail('the password stays in the dialog');
        return msg && decodeURIComponent(msg).includes('The removal has started') && decodeURIComponent(msg).includes('People being removed') ? ok('the page names where it goes on') : fail('navigated to ' + JSON.stringify(p.log.navigated));
    },
    async a_deletion_needs_no_access_choice_and_sends_a_delete_with_the_password() {
        const remove = button({ uid: '3', name: 'carol', display: 'Carol', end: 'delete' });
        const p = usersPage({ removeButtons: [remove], fetch: { '/api-proxy/roles': ROLES } });
        run(p);
        await flush();
        remove.fire('click');
        const e = p.elements;
        if (e['remove-sa-end'].value !== 'delete') return fail('the trash button did not ask for a deletion');
        if (e['remove-sa-panel-access'].style.display !== 'none') return fail('a deletion asks what the person may see');
        if (!e['btn-remove-sa-confirm'].disabled) return fail('a deletion can start without the password');
        e['remove-sa-password'].value = 'Admin-Pass-1';
        e['remove-sa-password'].fire('sl-input');
        if (e['btn-remove-sa-confirm'].disabled) return fail('the password did not enable the button');
        e['btn-remove-sa-confirm'].fire('click');
        await flush();
        const del = p.log.requests.find(r => r.method === 'DELETE');
        return del && del.body.adminPassword === 'Admin-Pass-1' && !('role' in del.body) ? ok('a DELETE with the password, nothing else') : fail('unexpected ' + JSON.stringify(p.log.requests));
    },
};

/** "They stay as an ordinary user", and the administrator's password (empty: not typed). */
function chooseDemote(e, password) {
    e['remove-sa-end'].value = 'demote';
    e['remove-sa-end'].fire('sl-change');
    e['remove-sa-password'].value = password;
    e['remove-sa-password'].fire('sl-input');
}

/** Types into the folder search and lets the script's debounce run: the harness has no clock, so the script's own search function is reached through the timer it set. */
async function pickFolder(p, query) {
    const e = p.elements;
    // The script asks for the debounce with setTimeout(fn, 200); take that function.
    const original = p.sandbox.setTimeout;
    let pending = null;
    p.sandbox.setTimeout = (fn, ms) => { pending = fn; return 1; };
    e['remove-sa-folder-search'].value = query;
    e['remove-sa-folder-search'].fire('sl-input');
    p.sandbox.setTimeout = original;
    if (!pending) return false;
    pending();
    await flush();
    const items = e['remove-sa-folder-dropdown'].querySelectorAll('.remove-sa-folder-item');
    if (!items.length) return false;
    items[0].fire('click');
    await flush();
    return true;
}

function ok(detail) { return { ok: true, detail }; }
function fail(detail) { return { ok: false, detail }; }

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
