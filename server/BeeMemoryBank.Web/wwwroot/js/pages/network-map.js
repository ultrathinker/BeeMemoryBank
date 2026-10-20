// ===== Network map (BMB-322) =====
// The page the header's "Sync Status" opens: this node and the nodes it knows on a circle (two on a line, three a triangle), an arrow for every call (one-way or
// two-way) with the address it goes to, a drawing for what each node runs on, and a card with what this node knows about a node when it is pointed at.
// Where the nodes stand and what the words say is network-model.js (run on its own by NetworkMapModelTests); this file draws.
(function () {
    'use strict';

    var M = window.bmbNetworkModel;
    var stage = document.getElementById('net-stage');
    if (!stage || !M) return;

    var NS = 'http://www.w3.org/2000/svg';
    var RING = 40;            // the radius of a node's ring
    var REFRESH_MS = 5000;

    var data = null;          // the last answer
    var lastJson = '';
    var svg = null, pop = null;
    var pinned = null;        // the key of the node or call whose card stays open ("n:<id>" / "l:<from>><to>")
    var shown = null;         // what the card shows now: { key, kind, id, x, y }
    var loadedOnce = false;
    var nameMax = 22;         // how many letters of a name fit under a node (fewer on a phone)

    function esc(text) {
        return String(text == null ? '' : text).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function el(tag, attrs, parent) {
        var node = document.createElementNS(NS, tag);
        if (attrs) for (var k in attrs) if (Object.prototype.hasOwnProperty.call(attrs, k)) node.setAttribute(k, attrs[k]);
        if (parent) parent.appendChild(node);
        return node;
    }

    function shorten(text, max) {
        text = String(text || '');
        return text.length > max ? text.slice(0, max - 1) + '…' : text;
    }

    // ---- the drawings ------------------------------------------------------------------------------------------------------------------------
    // Line drawings in a 52 x 52 box around (0, 0), drawn with the colour of the platform (--g). Not the makers' logos: what the thing is.
    function rack(y) {
        return '<rect class="g-tint" x="-21" y="' + y + '" width="42" height="14" rx="3"/>' +
            '<circle class="g-fill" cx="-13" cy="' + (y + 7) + '" r="1.9"/><circle class="g-fill" cx="-7" cy="' + (y + 7) + '" r="1.9"/>' +
            '<path d="M3 ' + (y + 7) + 'h13"/>';
    }
    var SERVER = rack(-25) + rack(-7) + rack(11);
    var GLYPHS = {
        linux: SERVER,
        server: SERVER,
        windows:
            '<rect class="g-tint" x="-25" y="-23" width="50" height="33" rx="3"/>' +
            '<rect class="g-fill" x="-14" y="-17" width="12" height="9" rx="1"/><rect class="g-fill" x="2" y="-17" width="12" height="9" rx="1"/>' +
            '<rect class="g-fill" x="-14" y="-6" width="12" height="9" rx="1"/><rect class="g-fill" x="2" y="-6" width="12" height="9" rx="1"/>' +
            '<path d="M-5 10 L-7 21 H7 L5 10"/><path d="M-13 23.5 H13"/>',
        mac:
            '<rect class="g-tint" x="-20" y="-23" width="40" height="30" rx="3"/>' +
            '<circle class="g-fill" cx="0" cy="-19" r="1.1"/>' +
            '<path class="g-tint" d="M-27 7 H27 L23 17 H-23 Z"/><path d="M-6 7 H6"/>',
        iphone:
            '<rect class="g-tint" x="-13" y="-25" width="26" height="50" rx="7"/>' +
            '<rect class="g-fill" x="-5" y="-21" width="10" height="3.6" rx="1.8"/><path d="M-5 20 H5"/>',
        android:
            '<rect class="g-tint" x="-13" y="-25" width="26" height="50" rx="4"/>' +
            '<circle class="g-fill" cx="0" cy="-20" r="1.9"/><path d="M-6 19.5 h0.01 M0 19.5 h0.01 M6 19.5 h0.01"/>',
        device:
            '<rect class="g-tint" x="-15" y="-15" width="30" height="30" rx="6"/><rect x="-6" y="-6" width="12" height="12" rx="2"/>' +
            '<path d="M-8 -23 V-15 M0 -23 V-15 M8 -23 V-15 M-8 15 V23 M0 15 V23 M8 15 V23 M-23 -8 H-15 M-23 0 H-15 M-23 8 H-15 M15 -8 H23 M15 0 H23 M15 8 H23"/>'
    };

    // The two badges on a node's ring: a container (Docker), and a blind copy (an eye that sees nothing).
    var BADGE_DOCKER = '<g class="net-badge-glyph is-docker"><rect class="g-fill" x="-8" y="-1.5" width="4.4" height="4.4"/><rect class="g-fill" x="-3" y="-1.5" width="4.4" height="4.4"/>' +
        '<rect class="g-fill" x="2" y="-1.5" width="4.4" height="4.4"/><rect class="g-fill" x="-5.5" y="-6.3" width="4.4" height="4.4"/><rect class="g-fill" x="-0.5" y="-6.3" width="4.4" height="4.4"/>' +
        '<path d="M-9.5 5.2 Q-5 9 0 5.2 T10 4.6"/></g>';
    var BADGE_BLIND = '<g class="net-badge-glyph is-blind"><path d="M-8.5 0 Q0 -7.5 8.5 0 Q0 7.5 -8.5 0 Z"/><circle class="g-fill" cx="0" cy="0" r="2.2"/><path d="M-7 7 L7 -7"/></g>';

    // ---- the tones --------------------------------------------------------------------------------------------------------------------------
    var TONES = ['ok', 'bad', 'warn', 'idle', 'rule'];

    function defs(root) {
        var d = el('defs', null, root);
        TONES.forEach(function (tone) {
            var m = el('marker', { id: 'net-arrow-' + tone, viewBox: '0 0 10 10', refX: '8.5', refY: '5', markerWidth: '10', markerHeight: '10', orient: 'auto', markerUnits: 'userSpaceOnUse' }, d);
            el('path', { d: 'M0 1.2 L9 5 L0 8.8 L2.4 5 Z', 'class': 'net-arrow tone-' + tone }, m);
        });
    }

    // ---- the picture ------------------------------------------------------------------------------------------------------------------------
    function render() {
        if (!data) return;
        var width = Math.max(300, Math.round(stage.clientWidth || 800));
        var count = data.peers.length + 1;
        var narrow = width < 560;
        // a line of two nodes needs little height; a circle of more needs its room (and on a phone, the height the narrow width leaves out)
        var height = count <= 2 ? 380 : narrow ? Math.round(width * 1.7) : Math.max(480, Math.min(720, Math.round(width * 0.6)));
        stage.style.height = height + 'px';
        stage.style.minHeight = '0';
        nameMax = narrow ? 12 : 22;
        closeCard();

        svg = el('svg', { 'class': 'net-map', viewBox: '0 0 ' + width + ' ' + height, width: width, height: height, role: 'group', 'aria-label': 'The network: ' + (data.peers.length + 1) + ' nodes' });
        defs(svg);

        var nodes = [data.self].concat(sortPeers(data.peers));
        var spots = M.layout(nodes.length, width, height, narrow ? 62 : 96, 98);
        var at = {}, byId = {}, names = {};
        nodes.forEach(function (n, i) { at[n.nodeId] = spots[i]; byId[n.nodeId] = n; names[n.nodeId] = n.displayName; });

        var conns = M.connections(data.links);
        var linkLayer = el('g', { 'class': 'net-links' }, svg);
        var nodeLayer = el('g', { 'class': 'net-nodes' }, svg);
        var tagsOn = data.links.filter(function (l) { return l.basis === 'observed'; }).length <= 8;
        conns.forEach(function (c) {
            if (c.ab) drawLink(linkLayer, c.ab, at[c.a], at[c.b], c.twoWay ? 1 : 0, tagsOn);
            if (c.ba) drawLink(linkLayer, c.ba, at[c.b], at[c.a], c.twoWay ? 1 : 0, tagsOn);
        });
        nodes.forEach(function (n, i) { drawNode(nodeLayer, n, spots[i], i); });

        svg.addEventListener('click', function (e) {
            if (e.target === svg) { pinned = null; closeCard(); unfocus(); }   // the empty space puts the card away
        });
        stage.querySelectorAll('svg.net-map').forEach(function (old) { old.remove(); });
        var loading = stage.querySelector('.net-loading');
        if (loading) loading.remove();
        stage.insertBefore(svg, stage.firstChild);

        var empty = document.getElementById('net-empty');
        if (empty) empty.hidden = data.peers.length > 0;
        fillTable(names, byId);
        // the card that was pinned stays where it is, with fresh words
        if (pinned) reopenPinned();
    }

    function sortPeers(peers) {
        var rank = { full: 0, blind: 1, device: 2 };
        return peers.slice().sort(function (a, b) {
            var d = (rank[a.kind] || 0) - (rank[b.kind] || 0);
            if (d) return d;
            return String(a.displayName).localeCompare(String(b.displayName)) || (a.nodeId < b.nodeId ? -1 : 1);
        });
    }

    function drawNode(layer, n, spot, index) {
        var look = M.look(n);
        var tone = M.nodeTone(n);
        var g = el('g', {
            'class': 'net-node tone-' + tone + (n.isSelf ? ' is-self' : ''), transform: 'translate(' + spot.x + ' ' + spot.y + ')', tabindex: '0', role: 'button',
            'data-id': n.nodeId, 'aria-label': n.displayName + ', ' + look.caption + '. ' + M.statusText(n)
        }, layer);
        g.style.setProperty('--delay', (index * 70) + 'ms');
        el('circle', { 'class': 'net-halo', r: RING + 12 }, g);
        el('circle', { 'class': 'net-ring', r: RING }, g);
        var glyph = el('g', { 'class': 'net-glyph is-' + look.glyph }, g);
        glyph.innerHTML = GLYPHS[look.glyph] || GLYPHS.server;
        // the dot: how the node stands
        el('circle', { 'class': 'net-dot', cx: 29, cy: -29, r: 7 }, g);
        if (look.docker) badge(g, -29, 29, BADGE_DOCKER);
        if (look.blind) badge(g, 29, 29, BADGE_BLIND);
        var name = el('text', { 'class': 'net-name', y: RING + 24, 'text-anchor': 'middle' }, g);
        name.textContent = shorten(n.displayName, nameMax);
        var cap = el('text', { 'class': 'net-caption', y: RING + 42, 'text-anchor': 'middle' }, g);
        cap.textContent = look.caption;
        if (n.isSelf) {
            var pill = el('g', { 'class': 'net-self-pill', transform: 'translate(0 ' + (-RING - 18) + ')' }, g);
            el('rect', { x: -33, y: -10, width: 66, height: 20, rx: 10 }, pill);
            var t = el('text', { y: 4, 'text-anchor': 'middle' }, pill);
            t.textContent = 'THIS NODE';
        }

        // while a card is kept open, pointing at other things changes nothing
        function enter() { if (pinned) return; openNodeCard(n.nodeId, spot); focus(n.nodeId); }
        function leave() { if (pinned) return; closeCard(); unfocus(); }
        g.addEventListener('mouseenter', enter);
        g.addEventListener('mouseleave', leave);
        g.addEventListener('focus', enter);
        g.addEventListener('blur', leave);
        g.addEventListener('click', function (e) {
            e.stopPropagation();
            var key = 'n:' + n.nodeId;
            if (pinned === key) { pinned = null; closeCard(); } else { pinned = key; openNodeCard(n.nodeId, spot); focus(n.nodeId); }
        });
        g.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); g.dispatchEvent(new MouseEvent('click', { bubbles: true })); }
        });
    }

    function badge(parent, x, y, glyphMarkup) {
        var b = el('g', { 'class': 'net-badge', transform: 'translate(' + x + ' ' + y + ')' }, parent);
        el('circle', { r: 12.5 }, b);
        var inner = el('g', { 'class': 'net-badge-art' }, b);
        inner.innerHTML = glyphMarkup;
    }

    // One call as an arrow. lane: 0 when it is the only call between the two nodes (a straight arrow), 1 when there is a call the other way too (the two arrows bow
    // apart and make a lens). The arrow keeps off the rings so that its head is seen.
    function drawLink(layer, link, from, to, lane, tagsOn) {
        if (!from || !to) return;
        var tone = M.linkTone(link);
        var dx = to.x - from.x, dy = to.y - from.y, len = Math.sqrt(dx * dx + dy * dy) || 1;
        var ux = dx / len, uy = dy / len, nx = -uy, ny = ux;
        // each arrow keeps to the left of its own way (as traffic does): the call a->b and the call b->a are then on opposite sides of the pair
        var side = lane ? 1 : 0;
        var gap = RING + 7;
        var off = side * 7, bow = side * (8 + Math.min(40, len * 0.07));
        var x0 = from.x + ux * gap + nx * off, y0 = from.y + uy * gap + ny * off;
        var x1 = to.x - ux * (gap + 3) + nx * off, y1 = to.y - uy * (gap + 3) + ny * off;
        var cx = (x0 + x1) / 2 + nx * bow, cy = (y0 + y1) / 2 + ny * bow;
        var d = 'M' + r1(x0) + ' ' + r1(y0) + ' Q' + r1(cx) + ' ' + r1(cy) + ' ' + r1(x1) + ' ' + r1(y1);
        var key = 'l:' + link.from + '>' + link.to;
        var g = el('g', { 'class': 'net-link tone-' + tone, 'data-key': key, 'data-from': link.from, 'data-to': link.to }, layer);
        el('path', { 'class': 'net-link-line', d: d, 'marker-end': 'url(#net-arrow-' + tone + ')' }, g);
        var hit = el('path', { 'class': 'net-link-hit', d: d }, g);
        var midX = 0.25 * x0 + 0.5 * cx + 0.25 * x1, midY = 0.25 * y0 + 0.5 * cy + 0.25 * y1;
        // the port is written on the arrow of a call this node measured (the expected ones would only be noise)
        if (tagsOn && link.port && link.basis === 'observed') {
            var tag = el('text', { 'class': 'net-link-tag', x: r1(midX), y: r1(midY - 6), 'text-anchor': 'middle' }, g);
            tag.textContent = ':' + link.port;
        }
        hit.addEventListener('mouseenter', function (e) { if (pinned) return; openLinkCard(link, e); linkFocus(link); });
        hit.addEventListener('mousemove', function (e) { if (!pinned && shown && shown.key === key) place(pointFrom(e), 14); });
        hit.addEventListener('mouseleave', function () { if (pinned) return; closeCard(); unfocus(); });
        hit.addEventListener('click', function (e) {
            e.stopPropagation();
            if (pinned === key) { pinned = null; closeCard(); unfocus(); return; }
            pinned = key;
            openLinkCard(link, e);
            linkFocus(link);
        });
    }

    function r1(n) { return Math.round(n * 10) / 10; }

    // The node and its calls stand out, the rest steps back.
    function focus(id) {
        if (!svg) return;
        svg.classList.add('has-focus');
        var neighbours = {};
        svg.querySelectorAll('.net-link').forEach(function (l) {
            var related = l.getAttribute('data-from') === id || l.getAttribute('data-to') === id;
            l.classList.toggle('is-related', related);
            if (related) { neighbours[l.getAttribute('data-from')] = true; neighbours[l.getAttribute('data-to')] = true; }
        });
        svg.querySelectorAll('.net-node').forEach(function (n) {
            var nid = n.getAttribute('data-id');
            n.classList.toggle('is-focus', nid === id);
            n.classList.toggle('is-neighbour', nid !== id && !!neighbours[nid]);
        });
    }

    function linkFocus(link) {
        if (!svg) return;
        svg.classList.add('has-focus');
        svg.querySelectorAll('.net-link').forEach(function (l) {
            l.classList.toggle('is-related', l.getAttribute('data-from') === link.from && l.getAttribute('data-to') === link.to);
        });
        svg.querySelectorAll('.net-node').forEach(function (n) {
            var nid = n.getAttribute('data-id');
            n.classList.remove('is-focus');
            n.classList.toggle('is-neighbour', nid === link.from || nid === link.to);
        });
    }

    function unfocus() {
        if (!svg || pinned) return;
        svg.classList.remove('has-focus');
        svg.querySelectorAll('.is-related, .is-focus, .is-neighbour').forEach(function (e) { e.classList.remove('is-related', 'is-focus', 'is-neighbour'); });
    }

    // ---- the cards --------------------------------------------------------------------------------------------------------------------------
    function ensurePop() {
        if (pop) return pop;
        pop = document.createElement('div');
        pop.className = 'net-pop';
        pop.setAttribute('role', 'status');
        pop.hidden = true;
        stage.appendChild(pop);
        return pop;
    }

    function rowsHtml(rows) {
        return '<dl class="net-pop-rows">' + rows.map(function (r) { return '<dt>' + esc(r[0]) + '</dt><dd>' + esc(r[1]) + '</dd>'; }).join('') + '</dl>';
    }

    function miniGlyph(look) {
        return '<svg class="net-pop-glyph net-glyph is-' + look.glyph + '" viewBox="-30 -30 60 60" aria-hidden="true">' + (GLYPHS[look.glyph] || GLYPHS.server) + '</svg>';
    }

    function nodeById(id) { return id === data.self.nodeId ? data.self : data.peers.filter(function (p) { return p.nodeId === id; })[0]; }

    function openNodeCard(id, spot) {
        if (!data) return;
        var n = nodeById(id);
        if (!n) return;
        var look = M.look(n);
        var chips = '';
        if (look.docker) chips += '<span class="net-chip is-docker">Docker</span>';
        if (look.blind) chips += '<span class="net-chip is-blind">Blind node</span>';
        var tone = M.nodeTone(n);
        var html = '<div class="net-pop-head">' + miniGlyph(look) + '<div class="net-pop-title"><strong>' + esc(n.displayName) + '</strong><small>' + esc(look.caption) + '</small></div>' +
            '<span class="net-pop-dot tone-' + tone + '"></span></div>' + (chips ? '<div class="net-chips">' + chips + '</div>' : '') +
            rowsHtml(M.nodeRows(n, data.self)) + (look.note ? '<p class="net-pop-note">' + esc(look.note) + '</p>' : '') +
            '<p class="net-pop-hint">' + (pinned === 'n:' + id ? 'Click the node again to close this card.' : 'Click the node to keep this card open.') + '</p>';
        var p = ensurePop();
        p.innerHTML = html;
        p.className = 'net-pop' + (pinned === 'n:' + id ? ' is-pinned' : '');
        p.hidden = false;
        shown = { key: 'n:' + id, kind: 'node', id: id, spot: spot };
        place({ x: spot.x, y: spot.y }, RING + 16);
    }

    function openLinkCard(link, e) {
        if (!data) return;
        var names = {};
        names[data.self.nodeId] = data.self.displayName;
        data.peers.forEach(function (n) { names[n.nodeId] = n.displayName; });
        var info = M.linkRows(link, names);
        var tone = M.linkTone(link);
        var kind = link.basis === 'expected' ? 'By the network’s rule' : 'Measured by this node';
        var key = 'l:' + link.from + '>' + link.to;
        var html = '<div class="net-pop-head"><span class="net-pop-arrow tone-' + tone + '">→</span><div class="net-pop-title"><strong>' + esc(info.title) + '</strong><small>' + esc(kind) + '</small></div></div>' +
            rowsHtml(info.rows) + '<p class="net-pop-hint">' + (pinned === key ? 'Click the arrow again to close this card.' : 'Click the arrow to keep this card open.') + '</p>';
        var p = ensurePop();
        p.innerHTML = html;
        p.className = 'net-pop' + (pinned === key ? ' is-pinned' : '');
        p.hidden = false;
        shown = { key: key, kind: 'link', link: link };
        place(pointFrom(e), 14);
    }

    function pointFrom(e) {
        var box = stage.getBoundingClientRect();
        return { x: e.clientX - box.left, y: e.clientY - box.top };
    }

    // Beside the node (or the pointer), on the side that has the room; on a narrow screen a sheet at the bottom.
    function place(point, gap) {
        var p = ensurePop();
        var W = stage.clientWidth, H = stage.clientHeight;
        p.classList.toggle('is-sheet', W < 560);
        if (W < 560) { p.style.left = ''; p.style.top = ''; return; }
        var w = p.offsetWidth || 300, h = p.offsetHeight || 200;
        var right = point.x + gap + w + 8 <= W;
        var x = right ? point.x + gap : Math.max(8, point.x - gap - w);
        var y = Math.min(Math.max(8, point.y - h / 2), Math.max(8, H - h - 8));
        p.style.left = Math.round(x) + 'px';
        p.style.top = Math.round(y) + 'px';
    }

    // Puts the card away (the caller decides whether it stays pinned: a redraw closes it and opens it again).
    function closeCard() {
        if (pop) pop.hidden = true;
        shown = null;
    }

    function reopenPinned() {
        if (!pinned || !data) return;
        if (pinned.indexOf('n:') === 0) {
            var id = pinned.slice(2);
            var spot = svg && svg.querySelector('.net-node[data-id="' + id + '"]');
            if (!nodeById(id) || !spot) { pinned = null; return; }
            var m = /translate\(([-\d.]+) ([-\d.]+)\)/.exec(spot.getAttribute('transform') || '');
            openNodeCard(id, { x: m ? parseFloat(m[1]) : 0, y: m ? parseFloat(m[2]) : 0 });
            focus(id);
        } else {
            var parts = pinned.slice(2).split('>');
            var link = data.links.filter(function (l) { return l.from === parts[0] && l.to === parts[1]; })[0];
            if (!link) { pinned = null; return; }
            var old = pop && !pop.hidden ? { x: parseFloat(pop.style.left) || 0, y: parseFloat(pop.style.top) || 0 } : null;
            openLinkCard(link, { clientX: stage.getBoundingClientRect().left + (old ? old.x : 0), clientY: stage.getBoundingClientRect().top + (old ? old.y : 0) });
            linkFocus(link);
        }
    }

    // ---- the table under the map: the same calls in words, for a screen reader and for copying an address ---------------------------------
    function fillTable(names, byId) {
        var body = document.getElementById('net-links-body');
        if (!body) return;
        var rows = data.links.slice().sort(function (a, b) { return (a.basis === b.basis ? 0 : a.basis === 'observed' ? -1 : 1); });
        if (rows.length === 0) { body.innerHTML = '<tr><td colspan="4" class="net-table-empty">No calls to show yet.</td></tr>'; return; }
        var now = Date.now();
        body.innerHTML = rows.map(function (l) {
            var info = M.linkRows(l, names, now);
            var status = info.rows.filter(function (r) { return r[0] === 'Status'; })[0];
            var tone = M.linkTone(l);
            return '<tr><td data-label="Call">' + esc(names[l.from]) + ' → ' + esc(names[l.to]) + '</td><td data-label="Goes to" class="net-mono">' + esc(M.addressText(l) || '—') + '</td>' +
                '<td data-label="State"><span class="net-pill tone-' + tone + '">' + (l.basis === 'expected' ? 'expected' : l.state.replace('_', ' ')) + '</span> ' + esc(status ? status[1] : '') + '</td>' +
                '<td data-label="Source">' + (l.basis === 'expected' ? 'by the network’s rule' : 'measured here') + '</td></tr>';
        }).join('');
    }

    // ---- loading ----------------------------------------------------------------------------------------------------------------------------
    function setNote(text, bad) {
        var note = document.getElementById('net-note');
        if (!note) return;
        note.textContent = text;
        note.classList.toggle('is-bad', !!bad);
    }

    function setInvisible(on) {
        var box = document.getElementById('net-invisible');
        if (box) box.hidden = !on;
    }

    function load() {
        return fetch('/api-proxy/sync/network', { credentials: 'same-origin', cache: 'no-store', headers: { 'Accept': 'application/json' } })
            .then(function (r) {
                if (r.status === 401 || r.status === 403) throw new Error('Only a superadmin can see the network.');
                if (!r.ok) throw new Error('This node answered ' + r.status + '.');
                return r.text();
            })
            .then(function (text) {
                var next = JSON.parse(text);
                if (!next || !next.self || !next.peers) throw new Error('This node sent an answer the map cannot read.');
                var changed = text !== lastJson;
                lastJson = text;
                data = next;
                loadedOnce = true;
                setInvisible(!!next.isInvisible);
                if (changed || !svg) render();
                else refreshWords();
                setNote('Updated ' + new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }), false);
            })
            .catch(function (err) {
                setNote((loadedOnce ? 'Could not refresh: ' : 'Could not load the network: ') + (err && err.message ? err.message : 'no answer') + (loadedOnce ? ' The picture is the last one that loaded.' : ''), true);
                var loading = stage.querySelector('.net-loading');
                if (loading && !loadedOnce) loading.textContent = 'The network could not be loaded.';
            });
    }

    // Nothing changed since the last answer: only the relative times of an open card and of the table move.
    function refreshWords() {
        if (shown && shown.kind === 'node') openNodeCard(shown.id, shown.spot);
        else if (shown && shown.kind === 'link') { /* a card that follows the pointer is rewritten at its next move */ }
        var names = {}, byId = {};
        [data.self].concat(data.peers).forEach(function (n) { names[n.nodeId] = n.displayName; byId[n.nodeId] = n; });
        fillTable(names, byId);
    }

    var timer = null;
    function schedule() {
        if (timer) clearInterval(timer);
        timer = setInterval(function () { if (!document.hidden) load(); }, REFRESH_MS);
    }

    var resizeTimer = null;
    if (typeof ResizeObserver !== 'undefined') {
        var lastWidth = 0;
        new ResizeObserver(function () {
            var w = stage.clientWidth;
            if (w === lastWidth) return;
            lastWidth = w;
            clearTimeout(resizeTimer);
            resizeTimer = setTimeout(function () { if (data) render(); }, 120);
        }).observe(stage);
    }

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && (pinned || shown)) { pinned = null; closeCard(); unfocus(); }
    });
    var refresh = document.getElementById('net-refresh');
    if (refresh) refresh.addEventListener('click', function () { load(); });
    document.addEventListener('visibilitychange', function () { if (!document.hidden) load(); });

    load();
    schedule();
})();
