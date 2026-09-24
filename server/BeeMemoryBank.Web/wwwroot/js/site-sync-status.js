// ===== Sync Status Widget & Modal =====
(function () {
    var btn = document.getElementById('syncStatusBtn');
    var dropdown = document.getElementById('syncDropdown');
    var badge = document.getElementById('syncBadge');

    function syncEscapeHtml(str) {
        var d = document.createElement('div');
        d.textContent = str;
        return d.innerHTML;
    }

    function getRelativeTime(dateStr) {
        if (!dateStr) return 'Never';
        var date = new Date(dateStr.endsWith('Z') ? dateStr : dateStr + 'Z');
        var now = new Date();
        var diff = Math.floor((now - date) / 1000);
        if (diff < 0) diff = 0;
        if (diff < 15) return 'Just now';
        if (diff < 60) return diff + 's ago';
        if (diff < 3600) return Math.floor(diff / 60) + 'm ago';
        if (diff < 86400) return Math.floor(diff / 3600) + 'h ago';
        return date.toLocaleDateString();
    }

    function getNodeIcon(n) {
        var name = (n.displayName || '').toLowerCase();
        if (name.includes('phone') || name.includes('mobile') || n.nodeType === 'private') return 'smartphone';
        if (name.includes('laptop') || name.includes('desktop')) return 'laptop';
        return 'cloud';
    }

    function refreshSyncStatus() {
        fetch('/api-proxy/sync/delivery-status')
            .then(function (r) { return r.json(); })
            .then(function (data) {
                var list = document.getElementById('syncNodeList');
                if (!list || !data.nodes) return;

                var pending = data.nodes.filter(function (n) { return !n.isSynced; }).length;
                if (badge) {
                    badge.style.display = pending > 0 ? 'inline-flex' : 'none';
                    badge.textContent = pending;
                }

                var listHtml = '<div style="margin: 0 10px 10px; padding: 10px; background: var(--bg-input); border: 1px solid var(--border-color); border-radius: 6px; display: flex; align-items: center; justify-content: space-between;">' +
                               '  <div>' +
                               '    <strong style="color: var(--sl-color-danger-500); font-size: 0.9rem;">Invisible Mode</strong><br>' +
                               '    <small style="color: var(--text-secondary); font-size: 0.75rem;">Other nodes won\'t see you</small>' +
                               '  </div>' +
                               '  <sl-tooltip content="When enabled, your node rejects incoming requests and stops background synchronization." placement="left" hoist style="--max-width: 200px;">' +
                               '    <sl-switch id="dropdownInvisibleModeSwitch" ' + (data.isInvisible ? 'checked' : '') + ' style="--sl-color-primary-600: var(--sl-color-danger-500);"></sl-switch>' +
                               '  </sl-tooltip>' +
                               '</div>';

                if (data.nodes.length === 0) {
                    listHtml += '<div class="sync-node-empty">No remote nodes configured</div>';
                    list.innerHTML = listHtml;
                } else {
                    listHtml += data.nodes.map(function (n) {
                        var isOnline = n.lastContactAt && (new Date() - new Date(n.lastContactAt) < 300000);
                        var icon = getNodeIcon(n);
                        var metaText;
                        if (n.isSynced) {
                            metaText = 'Up to date · ' + getRelativeTime(n.lastContactAt);
                        } else {
                            var label = n.unsyncedCount + ' change' + (n.unsyncedCount === 1 ? '' : 's') + ' not synced';
                            metaText = label + ' · ' + (n.lastContactAt ? 'last sync ' + getRelativeTime(n.lastContactAt) : 'never synced');
                        }
                        var pct = n.headSeq > 0 ? Math.min(100, Math.max(0, n.lastPushedSeq / n.headSeq * 100)) : 0;

                        return '<div class="sync-node-item">' +
                            '<sl-icon name="' + icon + '" style="font-size:1.1rem;color:' + (isOnline ? 'var(--accent)' : 'var(--text-secondary)') + ';"></sl-icon>' +
                            '<div class="sync-node-info">' +
                                '<div class="sync-node-name">' + syncEscapeHtml(n.displayName) +
                                (isOnline ? ' <small style="color:var(--sl-color-success-600);font-size:0.65rem;">ONLINE</small>' : '') + '</div>' +
                                '<div class="sync-node-meta">' + metaText + '</div>' +
                                (n.isSynced ? '' : '<sl-progress-bar value="' + pct + '" style="--height:4px;margin-top:4px;"></sl-progress-bar>') +
                            '</div>' +
                        '</div>';
                    }).join('');
                    list.innerHTML = listHtml;
                }

                var invSwitch = document.getElementById('dropdownInvisibleModeSwitch');
                if (invSwitch) {
                    invSwitch.addEventListener('sl-change', function (e) {
                        fetch('/api-proxy/sync/invisible', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify(e.target.checked)
                        }).catch(function() {
                            e.target.checked = !e.target.checked;
                        });
                    });
                }
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

                function escForModal(str) {
                    return String(str == null ? '' : str)
                        .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
                        .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
                }

                function getRelativeTimeModal(dateStr) {
                    if (!dateStr) return 'Never';
                    var date = new Date(dateStr.endsWith('Z') ? dateStr : dateStr + 'Z');
                    var now = new Date();
                    var diff = Math.max(0, Math.floor((now - date) / 1000));
                    if (diff < 15) return 'Just now';
                    if (diff < 60) return diff + 's ago';
                    if (diff < 3600) return Math.floor(diff / 60) + 'm ago';
                    if (diff < 86400) return Math.floor(diff / 3600) + 'h ago';
                    return date.toLocaleDateString();
                }

                var html = '<div style="margin-bottom: 20px; padding: 12px; background: var(--bg-input); border: 1px solid var(--border-color); border-radius: 6px; display: flex; align-items: center; justify-content: space-between;">' +
                           '  <div>' +
                           '    <strong style="color: var(--sl-color-danger-500);">Invisible Mode</strong><br>' +
                           '    <small style="color: var(--text-secondary);">Other nodes won\'t see you and you won\'t sync</small>' +
                           '  </div>' +
                           '  <sl-tooltip content="When enabled, your node rejects incoming requests and stops background synchronization." placement="left" hoist style="--max-width: 200px;">' +
                           '    <sl-switch id="invisibleModeSwitch" ' + (data.isInvisible ? 'checked' : '') + ' style="--sl-color-primary-600: var(--sl-color-danger-500);"></sl-switch>' +
                           '  </sl-tooltip>' +
                           '</div>';

                html += data.nodes.map(function (n) {
                    var isOnline = n.lastContactAt && (new Date() - new Date(n.lastContactAt) < 300000);
                    var icon = getNodeIcon(n);
                    var metaText;
                    if (n.isSynced) {
                        metaText = 'Up to date · ' + getRelativeTimeModal(n.lastContactAt);
                    } else {
                        var label = n.unsyncedCount + ' change' + (n.unsyncedCount === 1 ? '' : 's') + ' not synced';
                        metaText = label + ' · ' + (n.lastContactAt ? 'last sync ' + getRelativeTimeModal(n.lastContactAt) : 'never synced');
                    }
                    var pct = n.headSeq > 0 ? Math.min(100, Math.max(0, n.lastPushedSeq / n.headSeq * 100)) : 0;

                    return '<div class="sync-modal-node">' +
                        '<sl-icon name="' + icon + '" style="font-size:1.4rem;margin-top:2px;color:' + (isOnline ? 'var(--accent)' : 'var(--text-secondary)') + ';"></sl-icon>' +
                        '<div class="sync-modal-node-info">' +
                            '<strong>' + escForModal(n.displayName) + (isOnline ? ' <small style="color:var(--sl-color-success-600);">ONLINE</small>' : '') + '</strong><br>' +
                            '<small>' + metaText + '</small>' +
                            (n.isSynced ? '' : '<sl-progress-bar value="' + pct + '" style="--height:6px;margin-top:8px;"></sl-progress-bar>') +
                        '</div>' +
                    '</div>';
                }).join('');

                content.innerHTML = html;

                var invSwitch = document.getElementById('invisibleModeSwitch');
                if (invSwitch) {
                    invSwitch.addEventListener('sl-change', function (e) {
                        fetch('/api-proxy/sync/invisible', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify(e.target.checked)
                        }).catch(function() {
                            e.target.checked = !e.target.checked;
                        });
                    });
                }

                var modal = document.getElementById('syncModal');
                if (modal && modal.open) {
                    setTimeout(window.updateSyncModal, 3000);
                }
            })
            .catch(function () {});
    };
})();
