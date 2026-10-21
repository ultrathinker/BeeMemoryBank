// Runs wwwroot/js/pages/network-model.js (the model of the network map: where the nodes stand, what each is drawn as, how the calls group into connections,
// what the cards say) in a bare context, and prints one JSON line {"ok": true|false, "detail": "..."} for the scenario named on the command line. The scenarios
// are the ones of NetworkMapModelTests.cs (BMB-322).
//
//   node network-model-harness.js <path of network-model.js> <scenario>
'use strict';
const fs = require('fs');
const vm = require('vm');

const scriptPath = process.argv[2];
const scenario = process.argv[3];

const sandbox = { window: {} };
vm.createContext(sandbox);
vm.runInContext(fs.readFileSync(scriptPath, 'utf8'), sandbox, { filename: scriptPath });
const M = sandbox.window.bmbNetworkModel;

function say(ok, detail) { console.log(JSON.stringify({ ok, detail })); process.exit(0); }
function assert(cond, message) { if (!cond) throw new Error(message); }
function eq(actual, expected, what) {
    const a = JSON.stringify(actual), e = JSON.stringify(expected);
    if (a !== e) throw new Error(what + ': expected ' + e + ' but got ' + a);
}
function near(a, b, tolerance, what) { if (Math.abs(a - b) > tolerance) throw new Error(what + ': ' + a + ' is not within ' + tolerance + ' of ' + b); }
function dist(p, q) { return Math.hypot(p.x - q.x, p.y - q.y); }

const NOW = Date.parse('2026-10-20T12:00:00Z');
function ago(seconds) { return new Date(NOW - seconds * 1000).toISOString(); }

const scenarios = {
    // ---- where the nodes stand ----
    one_node_is_in_the_middle() {
        const spots = M.layout(1, 960, 600, 90, 90);
        eq(spots.length, 1, 'one spot');
        eq([spots[0].x, spots[0].y], [480, 300], 'the middle');
    },
    two_nodes_are_on_one_line() {
        const spots = M.layout(2, 1000, 400, 90, 90);
        eq(spots.length, 2, 'two spots');
        near(spots[0].y, spots[1].y, 0.5, 'the same height');
        assert(spots[0].x < spots[1].x, 'this node (the first) is on the left');
        near(spots[0].x + spots[1].x, 1000, 0.5, 'symmetrical about the middle');
        assert(spots[1].x - spots[0].x <= 600.5, 'not so far apart that the arrows are lost (' + (spots[1].x - spots[0].x) + ')');
    },
    three_nodes_are_a_triangle_with_a_corner_up() {
        const s = M.layout(3, 960, 600, 90, 90);
        near(s[0].x, 480, 0.5, 'the first corner is above the middle');
        assert(s[0].y < 300, 'the first corner is at the top');
        near(s[1].y, s[2].y, 0.5, 'the other two corners are level');
        assert(s[1].y > 300, 'and below the middle');
        const a = dist(s[0], s[1]), b = dist(s[1], s[2]), c = dist(s[2], s[0]);
        near(a, b, 1, 'sides'); near(b, c, 1, 'sides');
    },
    four_and_more_stand_on_a_circle() {
        for (let n = 4; n <= 9; n++) {
            const s = M.layout(n, 960, 600, 90, 90);
            eq(s.length, n, 'spots for ' + n);
            const r = dist(s[0], { x: 480, y: 300 });
            s.forEach((p, i) => near(dist(p, { x: 480, y: 300 }), r, 0.8, n + ' nodes, node ' + i + ' on the circle'));
            near(s[0].x, 480, 0.5, 'the first is at the top (' + n + ')');
            assert(s[0].y < 300, 'the first is above the middle (' + n + ')');
            const step = dist(s[0], s[1]);
            for (let i = 0; i < n; i++) near(dist(s[i], s[(i + 1) % n]), step, 1, n + ' nodes evenly spaced');
        }
    },
    a_tall_narrow_room_makes_an_ellipse_that_still_fits() {
        const s = M.layout(5, 360, 620, 62, 98);
        s.forEach((p, i) => {
            assert(p.x >= 62 - 0.5 && p.x <= 360 - 62 + 0.5, 'node ' + i + ' is inside the margins in x: ' + p.x);
            assert(p.y >= 98 - 0.5 && p.y <= 620 - 98 + 0.5, 'node ' + i + ' is inside the margins in y: ' + p.y);
        });
        const ys = s.map((p) => p.y);
        assert(Math.max.apply(null, ys) - Math.min.apply(null, ys) > 300, 'the height of the room is used');
    },

    // ---- what a node is drawn as ----
    every_platform_has_its_drawing_and_word() {
        const cases = [['windows', 'windows', 'Windows'], ['macos', 'mac', 'Mac'], ['linux', 'linux', 'Linux'], ['android', 'android', 'Android'], ['ios', 'iphone', 'iPhone']];
        cases.forEach(([platform, glyph, label]) => {
            const look = M.look({ platform, kind: 'full' });
            eq([look.glyph, look.platformLabel, look.known, look.caption], [glyph, label, true, label], platform);
        });
    },
    a_node_that_said_nothing_is_drawn_from_what_is_known() {
        const server = M.look({ platform: null, kind: 'full' });
        eq([server.glyph, server.caption, server.known], ['server', 'Server', false], 'a node with an address');
        assert(/2\.6\.4/.test(server.note), 'it says why there is no platform');
        const device = M.look({ platform: null, kind: 'device' });
        eq([device.glyph, device.caption], ['device', 'Device'], 'a node without an address');
        const blind = M.look({ platform: null, kind: 'blind' });
        eq([blind.glyph, blind.caption, blind.blind], ['server', 'Server · blind node', true], 'a blind copy');
    },
    docker_and_blind_are_badges_and_in_the_caption() {
        const look = M.look({ platform: 'linux', kind: 'blind', inContainer: true });
        eq([look.docker, look.blind, look.caption], [true, true, 'Linux · Docker · blind node'], 'both badges');
        const plain = M.look({ platform: 'macos', kind: 'full', inContainer: false });
        eq([plain.docker, plain.blind, plain.caption], [false, false, 'Mac'], 'neither badge');
    },
    a_platform_word_that_is_not_known_never_becomes_a_label() {
        const look = M.look({ platform: '<b>plan9</b>', kind: 'full' });
        eq([look.known, look.platformLabel, look.caption], [false, null, 'Server'], 'unknown word');
    },
    the_ring_of_a_node_says_how_it_stands() {
        eq(M.nodeTone({ isSelf: true }), 'self', 'this node');
        eq(M.nodeTone({ state: 'not_answering', online: false, lastContactAt: ago(100) }), 'bad', 'no answer');
        eq(M.nodeTone({ state: 'answering', online: true, lastContactAt: ago(5) }), 'ok', 'online');
        eq(M.nodeTone({ state: 'calls_us', online: false, lastContactAt: ago(4000) }), 'warn', 'heard once, not lately');
        eq(M.nodeTone({ state: 'unknown', online: false, lastContactAt: null }), 'idle', 'never heard');
    },

    // ---- who calls whom ----
    two_calls_between_a_pair_are_one_two_way_connection() {
        const links = [
            { from: 'a', to: 'b', basis: 'observed', state: 'answering' },
            { from: 'b', to: 'a', basis: 'observed', state: 'heard' },
            { from: 'c', to: 'a', basis: 'observed', state: 'heard' }
        ];
        const c = M.connections(links);
        eq(c.length, 2, 'two connections');
        const ab = c.filter((x) => x.a === 'a' && x.b === 'b')[0];
        assert(ab && ab.twoWay && ab.ab && ab.ba, 'a and b call each other');
        const ac = c.filter((x) => (x.a === 'a' && x.b === 'c'))[0];
        assert(ac && !ac.twoWay, 'only c calls a');
        assert(ac.ba && !ac.ab, 'the call is c -> a, which is the second of the pair (a, c)');
        eq(M.connections([]), [], 'no calls');
        eq(M.connections(null), [], 'nothing');
    },
    the_colour_of_a_call_follows_what_is_known_of_it() {
        const tone = (basis, state) => M.linkTone({ basis, state });
        eq([tone('observed', 'answering'), tone('observed', 'heard')], ['ok', 'ok'], 'good');
        eq(tone('observed', 'not_answering'), 'bad', 'no answer');
        eq(tone('observed', 'stale'), 'warn', 'not heard lately');
        eq(tone('observed', 'unknown'), 'idle', 'not asked yet');
        eq(tone('expected', 'expected'), 'rule', 'the network\'s rule');
        eq(tone('expected', 'answering'), 'rule', 'a rule is never drawn as measured');
    },

    // ---- the words ----
    times_are_told_as_a_person_says_them() {
        eq(M.relativeTime(ago(5), NOW), 'just now', 'seconds');
        eq(M.relativeTime(ago(40), NOW), '40 s ago', 'a minute');
        eq(M.relativeTime(ago(300), NOW), '5 min ago', 'minutes');
        eq(M.relativeTime(ago(3 * 3600), NOW), '3 h ago', 'hours');
        eq(M.relativeTime(ago(2 * 86400), NOW), '2 d ago', 'days');
        eq(M.relativeTime(null, NOW), 'never', 'no time');
        eq(M.relativeTime('2026-10-20T11:59:30', NOW), '30 s ago', 'a time without a zone is UTC');
    },
    an_address_is_told_as_scheme_host_and_port() {
        eq(M.addressText({ scheme: 'https', host: 'bmb.example.net', port: 443 }), 'https://bmb.example.net:443', 'full');
        eq(M.addressText({ host: '10.0.0.8', port: 5610 }), '10.0.0.8:5610', 'without a scheme');
        eq(M.addressText({ host: null }), null, 'no address');
        eq(M.addressText(null), null, 'nothing');
    },
    the_line_of_a_node_says_how_it_stands() {
        eq(M.statusText({ isSelf: true }, NOW), 'This node', 'this node');
        eq(M.statusText({ state: 'answering', online: true, lastContactAt: ago(60), lastContactDirection: 'outbound' }, NOW), 'Online · last contact 1 min ago (we → it)', 'online');
        eq(M.statusText({ state: 'answering', online: false, lastContactAt: ago(7200), lastContactDirection: 'inbound' }, NOW), 'Quiet · last contact 2 h ago (it → us)', 'quiet');
        eq(M.statusText({ state: 'not_answering', notAnsweringSince: ago(1500), notAnsweringReason: 'no answer: connection refused' }, NOW), 'Not answering since 25 min ago — no answer: connection refused', 'no answer');
        eq(M.statusText({ state: 'calls_us', lastContactAt: ago(120) }, NOW), 'Calls this node · last call 2 min ago', 'a device that calls in');
        eq(M.statusText({ state: 'calls_us', lastContactAt: null }, NOW), 'Has not called since this node started', 'a device not heard');
        eq(M.statusText({ state: 'unknown', lastContactAt: null }, NOW), 'Not heard from since this node started', 'unknown');
    },
    a_paused_or_removed_copy_says_so() {
        const paused = "Paused by the device's owner: daily traffic limit (until 00:00 device time)";
        eq(M.statusText({ state: 'calls_us', lastContactAt: ago(120), pausedText: paused }, NOW), paused + ' · last call 2 min ago', 'paused, with its last call');
        eq(M.statusText({ state: 'calls_us', lastContactAt: null, pausedText: paused }, NOW), paused, 'paused, never heard');
        eq(M.statusText({ state: 'calls_us', lastContactAt: ago(120), leftAt: ago(60), pausedText: paused }, NOW),
            'Removed from the device by its owner — revoke it under Blind nodes', 'removed wins over paused');
        eq(M.statusText({ state: 'calls_us', lastContactAt: ago(120), pausedText: null, leftAt: null }, NOW), 'Calls this node · last call 2 min ago', 'neither: as before');
        const rows = M.nodeRows({ kind: 'blind', state: 'calls_us', lastContactAt: ago(60), pausedText: paused, isSynced: true, unsyncedCount: 0 }, { isSelf: true }, NOW);
        eq(rows[0][1], paused + ' · last call 1 min ago', 'the Status row of the card');
    },
    what_this_node_knows_of_the_notes_on_a_node() {
        const self = { isSelf: true, notes: 482, folders: 37, changes: 1204 };
        eq(M.notesText(self, self), '482 notes · 37 folders · 1,204 changes in the log', 'this node');
        eq(M.notesText({ isSelf: true, notes: null }, self), 'Not counted now (the vault is closed)', 'a closed vault');
        eq(M.notesText({ kind: 'full', isSynced: true, unsyncedCount: 0 }, self), 'Holds the same notes as this node (482 notes on this node)', 'level');
        eq(M.notesText({ kind: 'full', isSynced: false, unsyncedCount: 7, canBeCalled: true }, self), '7 changes not delivered to it yet (482 notes on this node)', 'behind');
        eq(M.notesText({ kind: 'device', isSynced: false, unsyncedCount: 1, canBeCalled: false }, self), '1 change waiting for it to call (482 notes on this node)', 'a device that has to call');
        assert(/cannot read the notes/.test(M.notesText({ kind: 'blind', isSynced: true, unsyncedCount: 0 }, self)), 'a blind copy cannot read');
        assert(/still to deliver/.test(M.notesText({ kind: 'blind', isSynced: false, unsyncedCount: 3 }, self)), 'a blind copy that is behind');
    },
    the_card_of_a_node_has_its_address_version_and_where_it_called_from() {
        const self = { isSelf: true, notes: 5, folders: 1, changes: 9 };
        const node = { kind: 'full', platform: 'linux', state: 'answering', online: true, lastContactAt: ago(30), lastContactDirection: 'outbound', scheme: 'https', host: 'h.example', port: 443,
            seenFrom: '203.0.113.7', appVersion: '2.6.4', protocol: 4, isSynced: true, unsyncedCount: 0, isSuperadmin: true, canGenerateEmbeddings: true, tlsTrust: 'pin', addedAt: '2026-09-12T10:00:00Z' };
        const rows = M.nodeRows(node, self, NOW);
        const get = (label) => (rows.filter((r) => r[0] === label)[0] || [])[1];
        eq(get('Address'), 'https://h.example:443', 'address');
        eq(get('Last call came from'), '203.0.113.7', 'where it called from');
        eq(get('Version'), '2.6.4 · protocol 4', 'version');
        eq(get('Connection'), 'its certificate is pinned', 'trust');
        eq(get('Role'), 'can change the network · builds the search index', 'role');
        eq(get('In the network since'), '2026-09-12', 'since');
        assert(get('Notes'), 'notes');
    },
    a_node_without_an_address_says_it_calls_in() {
        const rows = M.nodeRows({ kind: 'device', state: 'calls_us', isSynced: true, unsyncedCount: 0 }, null, NOW);
        const address = rows.filter((r) => r[0] === 'Address')[0];
        assert(address && /calls this node/.test(address[1]), 'the card says so: ' + JSON.stringify(address));
        eq(rows.filter((r) => r[0] === 'Version').length, 0, 'no version to show');
    },
    the_card_of_a_call_says_where_it_goes_and_what_is_known() {
        const names = { a: 'ROG', b: 'hetzner' };
        const ok = M.linkRows({ from: 'a', to: 'b', basis: 'observed', state: 'answering', scheme: 'https', host: 'h.example', port: 443, lastOkAt: ago(20) }, names, NOW);
        eq(ok.title, 'ROG → hetzner', 'title');
        eq(ok.rows[0], ['Goes to', 'https://h.example:443'], 'where it goes');
        eq(ok.rows[ok.rows.length - 1], ['Status', 'Answering · last good call 20 s ago'], 'status');
        const heard = M.linkRows({ from: 'b', to: 'a', basis: 'observed', state: 'heard', host: 'self.example', port: 5610, seenFrom: '1.2.3.4', lastOkAt: ago(90) }, names, NOW);
        assert(heard.rows.some((r) => r[0] === 'Came from' && r[1] === '1.2.3.4'), 'where it came from');
        assert(heard.rows.some((r) => r[0] === 'Status' && r[1] === 'Heard 1 min ago'), 'heard');
        const bad = M.linkRows({ from: 'a', to: 'b', basis: 'observed', state: 'not_answering', host: 'h', port: 1, failingSince: ago(600), reason: 'connection refused' }, names, NOW);
        assert(bad.rows.some((r) => r[0] === 'Status' && r[1] === 'No answer since 10 min ago — connection refused'), 'no answer');
        const rule = M.linkRows({ from: 'a', to: 'b', basis: 'expected', state: 'expected', host: 'h', port: 1 }, names, NOW);
        assert(rule.rows.some((r) => r[0] === 'Status' && /^Expected:/.test(r[1]) && /cannot see/.test(r[1])), 'a rule says it is not seen');
        const none = M.linkRows({ from: 'a', to: 'b', basis: 'observed', state: 'unknown' }, names, NOW);
        eq(none.rows[0], ['Goes to', 'this node has no address'], 'no address');
    },
    a_connection_is_told_in_one_line() {
        const names = { a: 'ROG', b: 'hetzner', c: 'phone' };
        eq(M.connectionText({ a: 'a', b: 'b', ab: {}, ba: {}, twoWay: true }, names), 'ROG ⇄ hetzner', 'two-way');
        eq(M.connectionText({ a: 'a', b: 'c', ab: null, ba: {}, twoWay: false }, names), 'phone → ROG', 'one way, from the second of the pair');
        eq(M.connectionText({ a: 'a', b: 'b', ab: {}, ba: null, twoWay: false }, names), 'ROG → hetzner', 'one way, from the first');
    }
};

if (!M) say(false, 'the script did not define window.bmbNetworkModel');
const run = scenarios[scenario];
if (!run) say(false, 'no such scenario: ' + scenario);
try { run(); say(true, 'ok'); } catch (e) { say(false, e && e.message ? e.message : String(e)); }
