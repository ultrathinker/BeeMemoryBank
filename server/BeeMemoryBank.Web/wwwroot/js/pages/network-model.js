// ===== Network map: the model (BMB-322) =====
// Pure functions, no DOM: where the nodes stand on the circle, what each one is called and drawn as, how the calls between them are grouped into
// connections, and what the words of the hover cards say. The page (network-map.js) draws; the behavior of this file is run on its own by
// NetworkMapModelTests (node, JsHarness/network-model-harness.js).
(function (root) {
    'use strict';

    var DIRECTION = { outbound: 'we \u2192 it', inbound: 'it \u2192 us' };

    // The platforms a node can say it runs on (NodePlatform on the server: a closed set). The glyph is the drawing, the label the word under the node.
    var PLATFORMS = {
        windows: { glyph: 'windows', label: 'Windows' },
        macos: { glyph: 'mac', label: 'Mac' },
        linux: { glyph: 'linux', label: 'Linux' },
        android: { glyph: 'android', label: 'Android' },
        ios: { glyph: 'iphone', label: 'iPhone' }
    };

    // ---- where the nodes stand ------------------------------------------------------------------------------------------------------------
    // One node in the middle; two on one line; three a triangle (a corner up); four and more on a circle, the first one at the top. Angles run clockwise
    // from the right, as the screen counts them (y grows downwards), so -90 degrees is the top.
    function angles(count) {
        if (count <= 0) return [];
        if (count === 1) return [null];
        if (count === 2) return [180, 0];
        var start = -90, out = [];
        for (var i = 0; i < count; i++) out.push(start + (360 / count) * i);
        return out;
    }

    // width/height: the room of the drawing; margin: what a node needs around its centre (the ring, the badges, the two lines of words).
    function layout(count, width, height, marginX, marginY) {
        var cx = width / 2, cy = height / 2;
        var rx = Math.max(60, width / 2 - (marginX || 90));
        var ry = Math.max(60, height / 2 - (marginY || 90));
        var r = Math.min(rx, ry);
        // a circle; only where the room is a tall, narrow one (a phone held upright) it is stretched into an ellipse, or the names under the nodes would meet
        var portrait = height > width;
        return angles(count).map(function (a) {
            if (a === null) return { x: cx, y: cy, angle: null };
            // two nodes are "on one line": apart as far as the width allows (but not so far that the arrows between them are lost)
            var radiusX = count === 2 ? Math.min(rx, 300) : portrait ? rx : r;
            var radiusY = count === 2 ? radiusX : portrait ? ry : r;
            var rad = a * Math.PI / 180;
            return { x: round(cx + radiusX * Math.cos(rad)), y: round(cy + radiusY * Math.sin(rad)), angle: a };
        });
    }

    function round(n) { return Math.round(n * 10) / 10; }

    // ---- what a node is --------------------------------------------------------------------------------------------------------------------
    // glyph: the drawing; platformLabel: the word under the name; docker / blind: the two badges. A node that did not say what it runs on (a build before
    // 2.6.4, or not heard since this node started) is drawn from what is known of it: a blind copy and a node with an address as a server, one without an
    // address as a device (a phone, a node behind a NAT).
    function look(node) {
        var known = PLATFORMS[node && node.platform];
        var kind = node && node.kind;
        var blind = kind === 'blind';
        var docker = !!(node && node.inContainer);
        var glyph = known ? known.glyph : kind === 'device' ? 'device' : 'server';
        var parts = [];
        if (known) parts.push(known.label);
        else parts.push(kind === 'device' ? 'Device' : 'Server');
        if (docker) parts.push('Docker');
        if (blind) parts.push('blind node');
        return {
            glyph: glyph,
            known: !!known,
            platformLabel: known ? known.label : null,
            docker: docker,
            blind: blind,
            caption: parts.join(' \u00b7 '),
            note: known ? null : 'Platform not known yet: a node tells it when it is called or calls, after this node started, and only from version 2.6.4 on'
        };
    }

    // How the ring and the dot of a node look: self, ok (online), bad (this node calls it and gets no answer), warn (was heard, not lately), idle (never heard).
    function nodeTone(node) {
        if (node.isSelf) return 'self';
        if (node.state === 'not_answering') return 'bad';
        if (node.online) return 'ok';
        return node.lastContactAt ? 'warn' : 'idle';
    }

    // The tone of one call: ok (answering, or heard in time), bad (no answer), warn (heard once, not lately), idle (not asked yet), rule (the network's rule).
    function linkTone(link) {
        if (link.basis === 'expected') return 'rule';
        switch (link.state) {
            case 'answering': case 'heard': return 'ok';
            case 'not_answering': return 'bad';
            case 'stale': return 'warn';
            default: return 'idle';
        }
    }

    // ---- who calls whom ---------------------------------------------------------------------------------------------------------------------
    // The calls (one entry per direction) as connections: one per pair of nodes, with the call a->b and the call b->a, either of which may be missing. A
    // connection with both is two-way; with one, it is a one-way arrow.
    function connections(links) {
        var byKey = {}, out = [];
        (links || []).forEach(function (l) {
            var forward = l.from < l.to;
            var a = forward ? l.from : l.to, b = forward ? l.to : l.from;
            var key = a + '|' + b;
            var c = byKey[key];
            if (!c) { c = byKey[key] = { a: a, b: b, ab: null, ba: null, twoWay: false }; out.push(c); }
            if (forward) c.ab = l; else c.ba = l;
        });
        out.forEach(function (c) { c.twoWay = !!(c.ab && c.ba); });
        return out;
    }

    // ---- the words --------------------------------------------------------------------------------------------------------------------------
    function parseUtc(text) {
        return new Date(/(Z|[+-]\d\d:\d\d)$/.test(text) ? text : text + 'Z');
    }

    function relativeTime(text, nowMs) {
        if (!text) return 'never';
        var then = parseUtc(text);
        var diff = Math.max(0, Math.floor(((nowMs == null ? Date.now() : nowMs) - then.getTime()) / 1000));
        if (diff < 15) return 'just now';
        if (diff < 60) return diff + ' s ago';
        if (diff < 3600) return Math.floor(diff / 60) + ' min ago';
        if (diff < 86400) return Math.floor(diff / 3600) + ' h ago';
        return Math.floor(diff / 86400) + ' d ago';
    }

    // "https://host:5610", or null when the node has no address
    function addressText(thing) {
        if (!thing || !thing.host) return null;
        return (thing.scheme ? thing.scheme + '://' : '') + thing.host + (thing.port ? ':' + thing.port : '');
    }

    function plural(n, one, many) { return Number(n).toLocaleString('en-US') + ' ' + (n === 1 ? one : many); }

    // The one line that says how a node stands, from this node's point of view.
    function statusText(node, nowMs) {
        if (node.isSelf) return 'This node';
        var dir = DIRECTION[node.lastContactDirection] || '';
        var when = node.lastContactAt ? relativeTime(node.lastContactAt, nowMs) : null;
        // A blind copy whose owner removed it from the device, or whose owner's limits told it to wait: said by this node in words of its own (a closed set).
        if (node.leftAt) return 'Removed from the device by its owner — revoke it under Blind nodes';
        if (node.pausedText) return node.pausedText + (when ? ' · last call ' + when : '');
        if (node.state === 'not_answering') {
            return 'Not answering since ' + relativeTime(node.notAnsweringSince, nowMs) + (node.notAnsweringReason ? ' \u2014 ' + node.notAnsweringReason : '');
        }
        if (node.state === 'calls_us') {
            return when ? 'Calls this node \u00b7 last call ' + when : 'Has not called since this node started';
        }
        if (when) return (node.online ? 'Online' : 'Quiet') + ' \u00b7 last contact ' + when + (dir ? ' (' + dir + ')' : '');
        return 'Not heard from since this node started';
    }

    // What this node knows of the notes on a node: its own count; for another node what the delivery says (the network keeps one set of notes on every node,
    // so a node with nothing left to deliver to it holds the same notes as this one).
    function notesText(node, self) {
        if (node.isSelf) {
            if (node.notes == null) return 'Not counted now (the vault is closed)';
            return plural(node.notes, 'note', 'notes') + ' \u00b7 ' + plural(node.folders || 0, 'folder', 'folders') + ' \u00b7 ' + plural(node.changes || 0, 'change', 'changes') + ' in the log';
        }
        if (node.kind === 'blind') {
            return node.isSynced ? 'An encrypted copy of everything delivered \u2014 it cannot read the notes'
                : plural(node.unsyncedCount, 'change', 'changes') + ' still to deliver to its encrypted copy';
        }
        var of = self && self.notes != null ? ' (' + plural(self.notes, 'note', 'notes') + ' on this node)' : '';
        if (node.isSynced) return 'Holds the same notes as this node' + of;
        var waiting = node.canBeCalled === false || node.kind === 'device';
        return plural(node.unsyncedCount, 'change', 'changes') + (waiting ? ' waiting for it to call' : ' not delivered to it yet') + of;
    }

    function trustText(node) {
        if (node.tlsTrust === 'pin') return 'its certificate is pinned';
        if (node.tlsTrust === 'public-ca') return 'a public certificate authority vouches for it';
        return null;
    }

    function dateText(text) {
        if (!text) return null;
        var d = parseUtc(text);
        return isNaN(d.getTime()) ? null : d.toISOString().slice(0, 10);
    }

    // The rows of a node's card: [label, value] pairs, in the order they are shown. A row with no value is left out.
    function nodeRows(node, self, nowMs) {
        var rows = [];
        rows.push(['Status', statusText(node, nowMs)]);
        var address = addressText(node);
        if (address) rows.push(['Address', address]);
        else if (!node.isSelf) rows.push(['Address', 'none \u2014 it calls this node, this node cannot call it']);
        if (node.seenFrom) rows.push(['Last call came from', node.seenFrom]);
        if (node.appVersion || node.protocol != null) rows.push(['Version', (node.appVersion || 'not reported') + (node.protocol != null ? ' \u00b7 protocol ' + node.protocol : '')]);
        rows.push(['Notes', notesText(node, self)]);
        if (!node.isSelf && node.lastPushAt) rows.push(['Last delivery', relativeTime(node.lastPushAt, nowMs)]);
        var trust = trustText(node);
        if (trust) rows.push(['Connection', trust]);
        var flags = [];
        if (node.isSuperadmin && !node.isSelf) flags.push('can change the network');
        if (node.canGenerateEmbeddings) flags.push('builds the search index');
        if (flags.length) rows.push(['Role', flags.join(' \u00b7 ')]);
        var added = dateText(node.addedAt);
        if (added) rows.push(['In the network since', added]);
        return rows;
    }

    // What a call says: a title ("A -> B"), and the lines under it.
    function linkRows(link, names, nowMs) {
        var from = names[link.from] || 'a node', to = names[link.to] || 'a node';
        var lines = [];
        var address = addressText(link);
        lines.push(['Goes to', address || 'this node has no address']);
        if (link.seenFrom) lines.push(['Came from', link.seenFrom]);
        switch (linkTone(link)) {
            case 'rule':
                lines.push(['Status', 'Expected: every node calls every node that has an address. This node cannot see calls between other nodes.']);
                break;
            case 'ok':
                lines.push(['Status', link.state === 'heard' ? 'Heard ' + relativeTime(link.lastOkAt, nowMs) : 'Answering \u00b7 last good call ' + relativeTime(link.lastOkAt, nowMs)]);
                break;
            case 'bad':
                lines.push(['Status', 'No answer since ' + relativeTime(link.failingSince, nowMs) + (link.reason ? ' \u2014 ' + link.reason : '')]);
                break;
            case 'warn':
                lines.push(['Status', 'Not heard lately \u00b7 the last call was ' + relativeTime(link.lastOkAt, nowMs)]);
                break;
            default:
                lines.push(['Status', 'Not asked yet since this node started']);
        }
        return { title: from + ' \u2192 ' + to, rows: lines };
    }

    // Names of a connection's two ends, for the one-line summary under the map and for a screen reader.
    function connectionText(c, names) {
        var a = names[c.a] || 'a node', b = names[c.b] || 'a node';
        if (c.twoWay) return a + ' \u21c4 ' + b;
        return c.ab ? a + ' \u2192 ' + b : b + ' \u2192 ' + a;
    }

    root.bmbNetworkModel = {
        angles: angles,
        layout: layout,
        look: look,
        nodeTone: nodeTone,
        linkTone: linkTone,
        connections: connections,
        relativeTime: relativeTime,
        addressText: addressText,
        statusText: statusText,
        notesText: notesText,
        nodeRows: nodeRows,
        linkRows: linkRows,
        connectionText: connectionText
    };
})(typeof window !== 'undefined' ? window : globalThis);
