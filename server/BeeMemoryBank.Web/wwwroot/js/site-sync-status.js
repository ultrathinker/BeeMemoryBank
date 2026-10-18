// ===== Sync Status Widget & Modal =====
// One implementation for both places that show it (the header dropdown and the "Sync Delivery Status" dialog of the article page).
// BMB-313: the times are CONTACT times (any completed session, with whose call it was), not the time of the last push of events; a node that does not
// answer says since when and why; a double-click on a node asks it now. Invisible Mode is not switched here any more (Admin > Invisible Mode); while it is
// on, the sync icon carries a badge.
(function () {
    var btn = document.getElementById('syncStatusBtn');
    var dropdown = document.getElementById('syncDropdown');
    var badge = document.getElementById('syncBadge');
    var invisibleBadge = document.getElementById('syncInvisibleBadge');

    var DIRECTION = { outbound: 'we \u2192 it', inbound: 'it \u2192 us' };

    function syncEscapeHtml(str) {
        return String(str == null ? '' : str)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function parseUtc(dateStr) {
        return new Date(/(Z|[+-]\d\d:\d\d)$/.test(dateStr) ? dateStr : dateStr + 'Z');
    }

    function getRelativeTime(dateStr) {
        if (!dateStr) return 'never';
        var date = parseUtc(dateStr);
        var diff = Math.max(0, Math.floor((new Date() - date) / 1000));
        if (diff < 15) return 'just now';
        if (diff < 60) return diff + 's ago';
        if (diff < 3600) return Math.floor(diff / 60) + 'm ago';
        if (diff < 86400) return Math.floor(diff / 3600) + 'h ago';
        return date.toLocaleDateString();
    }

    function clockTime(dateStr) {
        var date = parseUtc(dateStr);
        var sameDay = date.toDateString() === new Date().toDateString();
        return sameDay ? date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : date.toLocaleString();
    }

    function getNodeIcon(n) {
        var name = (n.displayName || '').toLowerCase();
        if (name.includes('phone') || name.includes('mobile') || n.nodeType === 'private') return 'smartphone';
        if (name.includes('laptop') || name.includes('desktop')) return 'laptop';
        return 'cloud';
    }

    function setInvisible(on) {
        if (invisibleBadge) invisibleBadge.style.display = on ? 'inline-flex' : 'none';
    }

    function invisibleNote(data) {
        if (!data.isInvisible) return '';
        return '<div class="sync-invisible-note">' +
               '<sl-icon name="eye-slash"></sl-icon> Invisible Mode is on: this node calls nobody and answers nobody. ' +
               '<a href="/Admin#card-invisible-mode">Change it in Admin</a></div>';
    }

    // ---- what a node's row says -------------------------------------------------------------------------------------------------------
    var checking = {};   // node id -> true while a double-click is being answered
    var checks = {};     // node id -> { html, at }: what the last double-click found; shown in the row for a minute (the rows are drawn again every few seconds)

    function checkLine(n) {
        var c = checks[n.nodeId];
        if (!c) return '';
        if (Date.now() - c.at > 60000) { delete checks[n.nodeId]; return ''; }
        return c.html;
    }

    function nodeMeta(n) {
        if (checking[n.nodeId]) {
            return '<sl-spinner style="font-size:0.9rem;vertical-align:-2px;"></sl-spinner> Checking this node now...';
        }
        var lines = [];
        var direction = DIRECTION[n.lastContactDirection] || '';
        if (n.state === 'not_answering') {
            lines.push('<span class="sync-bad">Not answering since ' + clockTime(n.notAnsweringSince) + ' (' + getRelativeTime(n.notAnsweringSince) + ')</span>');
            if (n.notAnsweringReason) lines.push(syncEscapeHtml(n.notAnsweringReason));
            if (n.lastContactAt) lines.push('Last contact ' + getRelativeTime(n.lastContactAt) + (direction ? ' (' + direction + ')' : ''));
        } else if (n.state === 'calls_us') {
            lines.push(n.lastContactAt
                ? 'It calls this node \u00b7 last call ' + getRelativeTime(n.lastContactAt) + ' (' + direction + ')'
                : 'It calls this node \u00b7 no call since this node started');
        } else if (n.lastContactAt) {
            lines.push('Last contact ' + getRelativeTime(n.lastContactAt) + (direction ? ' (' + direction + ')' : ''));
            if (n.state === 'unknown') lines.push('Not checked since this node started \u00b7 the next check will say');
        } else {
            lines.push('No contact since this node started');
        }
        var pushed = n.lastPushAt ? 'last delivery ' + getRelativeTime(n.lastPushAt) : 'nothing delivered yet';
        lines.push(n.isSynced
            ? 'All changes delivered' + (n.lastPushAt ? ' \u00b7 ' + pushed : '')
            : n.unsyncedCount + ' change' + (n.unsyncedCount === 1 ? '' : 's') + ' not delivered \u00b7 ' + pushed);
        var found = checkLine(n);
        if (found) lines.push(found);
        return lines.join('<br>');
    }

    function nodeName(n, size) {
        return syncEscapeHtml(n.displayName) + (n.online ? ' <small style="color:var(--sl-color-success-600);font-size:' + size + ';">ONLINE</small>' : '');
    }

    function deliveryBar(n, height, margin) {
        if (n.isSynced) return '';
        var pct = n.headSeq > 0 ? Math.min(100, Math.max(0, n.lastPushedSeq / n.headSeq * 100)) : 0;
        return '<sl-progress-bar value="' + pct + '" style="--height:' + height + ';margin-top:' + margin + ';"></sl-progress-bar>';
    }

    // The action of a row, reachable without a mouse: a real button (focus, Enter, Space, a tap), with a name that says which node. The double-click on
    // the row stays as a shortcut.
    function checkButton(n) {
        if (checking[n.nodeId]) return '';
        return '<button type="button" class="sync-check-btn" data-check-node="' + syncEscapeHtml(n.nodeId) + '" aria-label="Check ' +
            syncEscapeHtml(n.displayName) + ' now">Check now</button>';
    }

    function dropdownRow(n) {
        return '<div class="sync-node-item" data-node-id="' + syncEscapeHtml(n.nodeId) + '" title="Double-click to check this node now">' +
            '<sl-icon name="' + getNodeIcon(n) + '" style="font-size:1.1rem;color:' + (n.online ? 'var(--accent)' : 'var(--text-secondary)') + ';"></sl-icon>' +
            '<div class="sync-node-info">' +
                '<div class="sync-node-name">' + nodeName(n, '0.65rem') + '</div>' +
                '<div class="sync-node-meta">' + nodeMeta(n) + '</div>' +
                checkButton(n) +
                deliveryBar(n, '4px', '4px') +
            '</div>' +
        '</div>';
    }

    function modalRow(n) {
        return '<div class="sync-modal-node" data-node-id="' + syncEscapeHtml(n.nodeId) + '" title="Double-click to check this node now">' +
            '<sl-icon name="' + getNodeIcon(n) + '" style="font-size:1.4rem;margin-top:2px;color:' + (n.online ? 'var(--accent)' : 'var(--text-secondary)') + ';"></sl-icon>' +
            '<div class="sync-modal-node-info">' +
                '<strong>' + nodeName(n, 'inherit') + '</strong><br>' +
                '<small class="sync-node-meta">' + nodeMeta(n) + '</small>' +
                checkButton(n) +
                deliveryBar(n, '6px', '8px') +
            '</div>' +
        '</div>';
    }

    // ---- a double-click on a node: ask it now ----------------------------------------------------------------------------------------
    function checkResultHtml(status, body) {
        var node = body && body.node;
        if (!body || !body.outcome) return '<span class="sync-bad">Could not check: the node did not answer (' + status + ').</span>';
        switch (body.outcome) {
            case 'answered':
                if (body.reason) return '<span class="sync-bad">Alive, but ' + syncEscapeHtml(body.reason) + '</span>';
                return '<strong>Alive \u00b7 synced just now</strong>' +
                    (body.applied > 0 ? ' (' + body.applied + ' change' + (body.applied === 1 ? '' : 's') + ' received)' : '') +
                    (node && node.unsyncedCount > 0 ? ' \u00b7 ' + node.unsyncedCount + ' change' + (node.unsyncedCount === 1 ? '' : 's') + ' still to deliver' : '');
            case 'not_answering':
                return '<span class="sync-bad"><strong>Not answering</strong>' + (body.reason ? ': ' + syncEscapeHtml(body.reason) : '') + '</span>';
            case 'calls_us':
                return 'This device has no address: it calls this node, this node cannot call it. ' +
                    (node && node.lastInboundAt ? 'Last heard from it ' + getRelativeTime(node.lastInboundAt) + '.' : 'Not heard from it since this node started.');
            default:   // busy, invisible, unknown_node
                return syncEscapeHtml(body.reason || 'Could not check this node now.');
        }
    }

    function checkNode(id, redraw, row) {
        if (checking[id]) return;
        checking[id] = true;
        delete checks[id];
        // At once, before anything is fetched: the row says it is working.
        var meta = row && row.querySelector('.sync-node-meta');
        if (meta) meta.innerHTML = nodeMeta({ nodeId: id });
        redraw();
        // The one busy helper (BMB-311): the row is marked working; the answer, or why there is none, is written on the row itself.
        bmbBusy.run(row || null, function () {
            // The check makes this node call out: it carries the anti-forgery token the page was rendered with (the one every form of the page has).
            var token = document.querySelector('input[name="__RequestVerificationToken"]');
            return bmbBusy.request('/api-proxy/sync/nodes/' + encodeURIComponent(id) + '/check',
                { method: 'POST', headers: { 'RequestVerificationToken': token ? token.value : '' } })
                .then(function (res) { checks[id] = { at: Date.now(), html: checkResultHtml(res.status, res.body) }; })
                .catch(function (err) {
                    if (!err || !err.status) throw err;   // a status: the node answered with a refusal, whose words are on the row
                    checks[id] = { at: Date.now(), html: checkResultHtml(err.status, err.body) };
                });
        }, {
            quiet: true,
            error: function () { checks[id] = { at: Date.now(), html: '<span class="sync-bad">Could not check: the node did not answer.</span>' }; }
        }).then(function () { delete checking[id]; redraw(); });
    }

    function bindChecks(container, redraw) {
        if (!container || container._bmbCheckBound) return;
        container._bmbCheckBound = true;
        container.addEventListener('dblclick', function (e) {
            var row = e.target.closest('[data-node-id]');
            if (row) checkNode(row.getAttribute('data-node-id'), redraw, row);
        });
        // The button: a click, a tap, Enter and Space all arrive as a click.
        container.addEventListener('click', function (e) {
            var button = e.target.closest('[data-check-node]');
            if (!button) return;
            e.stopPropagation();   // not a click on the row of a dropdown that closes on an outside click
            var row = button.closest('[data-node-id]');
            checkNode(button.getAttribute('data-check-node'), redraw, row);
        });
    }

    // ---- the dropdown -----------------------------------------------------------------------------------------------------------------
    function refreshSyncStatus() {
        fetch('/api-proxy/sync/delivery-status')
            .then(function (r) { return r.json(); })
            .then(function (data) {
                var list = document.getElementById('syncNodeList');
                if (!list || !data.nodes) return;
                bindChecks(list, refreshSyncStatus);

                var pending = data.nodes.filter(function (n) { return !n.isSynced; }).length;
                if (badge) {
                    badge.style.display = pending > 0 ? 'inline-flex' : 'none';
                    badge.textContent = pending;
                }
                setInvisible(data.isInvisible);

                var html = invisibleNote(data);
                if (data.nodes.length === 0) {
                    html += '<div class="sync-node-empty">No remote nodes configured</div>';
                } else {
                    html += data.nodes.map(dropdownRow).join('') +
                            '<div class="sync-node-hint">Press "Check now" (or double-click a node) to check it now.</div>';
                }
                list.innerHTML = html;
            })
            .catch(function () {
                var list = document.getElementById('syncNodeList');
                if (list) list.innerHTML = '<div class="sync-node-empty">Failed to load status</div>';
            });
    }

    function updateSyncBadge() {
        fetch('/api-proxy/sync/delivery-status')
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (!data.nodes || !badge) return;
                var pending = data.nodes.filter(function (n) { return !n.isSynced; }).length;
                badge.style.display = pending > 0 ? 'inline-flex' : 'none';
                badge.textContent = pending;
                setInvisible(data.isInvisible);

                var desiredInterval = pending > 0 ? 5000 : 30000;
                if (window._syncBadgeMs !== desiredInterval) {
                    window._syncBadgeMs = desiredInterval;
                    if (window._syncBadgeInterval) clearInterval(window._syncBadgeInterval);
                    window._syncBadgeInterval = setInterval(updateSyncBadge, desiredInterval);
                }
            })
            .catch(function () {});
    }

    if (btn && dropdown) {
        var isOpen = false;
        btn.addEventListener('click', function (e) {
            e.stopPropagation();
            isOpen = !isOpen;
            dropdown.style.display = isOpen ? 'block' : 'none';
            if (isOpen) refreshSyncStatus();
        });

        document.addEventListener('click', function (e) {
            if (isOpen && !dropdown.contains(e.target)) {
                isOpen = false;
                dropdown.style.display = 'none';
            }
        });

        updateSyncBadge();
        if (!window._syncBadgeInterval) {
            window._syncBadgeMs = 30000;
            window._syncBadgeInterval = setInterval(updateSyncBadge, 30000);
        }

        var urlParams = new URLSearchParams(window.location.search);
        if (urlParams.has('created') || urlParams.has('updated')) {
            showSyncToast();
        }
    }

    function showSyncToast() {
        var toast = document.createElement('div');
        toast.className = 'sync-toast';
        toast.innerHTML = '<div class="sync-toast-content">' +
            '<sl-spinner style="font-size: 1rem;"></sl-spinner>' +
            '<span class="sync-toast-label">Syncing to cloud...</span>' +
            '</div>';
        document.body.appendChild(toast);

        var checkCount = 0;
        var interval = setInterval(function() {
            checkCount++;
            fetch('/api-proxy/sync/delivery-status')
                .then(function(r) { return r.json(); })
                .then(function(data) {
                    var publicNodes = (data.nodes || []).filter(function(n) { return n.nodeType === 'public'; });
                    var anySynced = publicNodes.some(function(n) { return n.isSynced; });

                    if (anySynced || checkCount > 20) {
                        clearInterval(interval);
                        toast.innerHTML = '<div class="sync-toast-content">' +
                            '<sl-icon name="cloud-check" style="color:var(--sl-color-success-600);font-size:1.2rem;"></sl-icon>' +
                            '<span class="sync-toast-label">Synced to Cloud</span>' +
                            '</div>';
                        setTimeout(function() {
                            toast.style.opacity = '0';
                            setTimeout(function() { if (toast.parentNode) toast.parentNode.removeChild(toast); }, 500);
                        }, 3000);
                    }
                })
                .catch(function() { clearInterval(interval); });
        }, 2000);
    }

    // ---- the dialog of the article page: the same rows, the same checks ---------------------------------------------------------------
    window.updateSyncModal = function () {
        fetch('/api-proxy/sync/delivery-status')
            .then(function (r) {
                if (!r.ok) return null;
                return r.json();
            })
            .then(function (data) {
                if (!data) return;
                var content = document.getElementById('syncModalContent');
                if (!content || !data.nodes) return;
                bindChecks(content, window.updateSyncModal);
                setInvisible(data.isInvisible);

                content.innerHTML = invisibleNote(data) +
                    (data.nodes.length === 0 ? '<div class="sync-node-empty">No remote nodes configured</div>'
                        : data.nodes.map(modalRow).join('') + '<div class="sync-node-hint">Press "Check now" (or double-click a node) to check it now.</div>');

                var modal = document.getElementById('syncModal');
                if (modal && modal.open) {
                    clearTimeout(window._syncModalTimer);
                    window._syncModalTimer = setTimeout(window.updateSyncModal, 3000);
                }
            })
            .catch(function () {});
    };
})();
