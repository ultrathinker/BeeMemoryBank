// Shared folder access dialog logic, used by Users and Roles pages.

function escapeHtml(text) {
    return String(text == null ? '' : text)
        .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

function showDlgError(alertId, message) {
    var el = document.getElementById(alertId);
    if (!el) return;
    var msg = el.querySelector('.msg');
    if (msg) msg.textContent = message;
    el.style.display = '';
    el.open = true;
}

function hideDlgError(alertId) {
    var el = document.getElementById(alertId);
    if (!el) return;
    el.open = false;
    el.style.display = 'none';
}

window.folderAccess = (function () {
    var subjectType = 'user';     // 'user' | 'role'
    var subjectId = null;         // numeric user id, or role name
    var inheritedRoleName = null; // role whose rules also apply, when the subject is a user
    var selectedFolderId = null;
    var searchTimer = null;

    function getSearchInput() { return document.getElementById('restriction-folder-search'); }
    function getDropdown() { return document.getElementById('restriction-folder-dropdown'); }
    function getAddBtn() { return document.getElementById('btn-add-restriction'); }

    function basePath() {
        return '/api-proxy/restrictions/' + subjectType + '/' + encodeURIComponent(subjectId);
    }

    function open(type, id, label, options) {
        subjectType = type;
        subjectId = id;
        inheritedRoleName = (options && options.inheritedRole) || null;
        var nameEl = document.getElementById('restriction-user-name');
        if (nameEl) nameEl.textContent = label;

        var addRow = document.getElementById('restriction-add-row');
        var readOnlySubject = !!(options && options.readOnly);
        if (addRow) addRow.style.display = readOnlySubject ? 'none' : '';
        var managedNote = document.getElementById('restriction-managed-note');
        if (managedNote) managedNote.style.display = readOnlySubject ? '' : 'none';

        selectedFolderId = null;
        var searchInput = getSearchInput();
        if (searchInput) searchInput.value = '';
        var dropdown = getDropdown();
        if (dropdown) dropdown.style.display = 'none';
        var effectEl = document.getElementById('restriction-effect');
        if (effectEl) effectEl.value = 'deny';
        var roRow = document.getElementById('restriction-readonly-row');
        if (roRow) roRow.style.display = 'none';
        var roEl = document.getElementById('restriction-readonly');
        if (roEl) roEl.checked = false;
        hideDlgError('restriction-error');
        load();
        var dlg = document.getElementById('dlg-restrictions');
        if (dlg) dlg.show();
    }

    function renderRow(r, editable) {
        var effectLabel = r.effect === 'allow' ? 'Allow' : 'Deny';
        var roBadge = (r.effect === 'allow' && r.isReadOnly)
            ? ' <sl-badge variant="warning" pill style="margin-left:4px;" title="Read-only"><sl-icon slot="prefix" name="lock-fill"></sl-icon>RO</sl-badge>'
            : '';
        var actions = '';
        if (editable) {
            if (r.effect === 'allow') {
                actions += '<sl-icon-button name="lock' + (r.isReadOnly ? '-fill' : '') + '" label="Toggle read-only"' +
                    ' data-folder="' + escapeHtml(r.folderId) + '" data-ro="' + (r.isReadOnly ? 'false' : 'true') +
                    '" class="acl-toggle-ro"></sl-icon-button>';
            }
            actions += '<sl-icon-button name="trash" label="Remove" data-folder="' + escapeHtml(r.folderId) + '" class="acl-remove"></sl-icon-button>';
        } else {
            actions = '<sl-badge variant="neutral" pill title="Comes from the role — edit it on the Roles page">inherited</sl-badge>';
        }
        return '<div class="' + (editable ? '' : 'acl-inherited') + '" style="display:flex;align-items:center;justify-content:space-between;padding:6px 0;border-bottom:1px solid var(--sl-color-neutral-200);">' +
            '<span><sl-icon name="folder2"></sl-icon> ' + escapeHtml(r.folderPath) +
            ' <sl-badge variant="' + (r.effect === 'allow' ? 'success' : 'danger') + '" pill style="margin-left:8px;">' + effectLabel + '</sl-badge>' + roBadge + '</span>' +
            '<span>' + actions + '</span></div>';
    }

    async function load() {
        var container = document.getElementById('restriction-list');
        if (!container) return;
        try {
            var own = await fetch(basePath()).then(function (r) { return r.ok ? r.json() : []; });
            var inherited = [];
            if (inheritedRoleName) {
                inherited = await fetch('/api-proxy/restrictions/role/' + encodeURIComponent(inheritedRoleName))
                    .then(function (r) { return r.ok ? r.json() : []; })
                    .catch(function () { return []; });
            }

            if ((!own || own.length === 0) && inherited.length === 0) {
                container.innerHTML = '<div style="color:var(--sl-color-neutral-500);font-size:0.9rem;">No access rules — sees everything</div>';
                return;
            }

            var html = '';
            if (inherited.length > 0) {
                html += '<div style="font-size:0.78rem;text-transform:uppercase;letter-spacing:0.04em;color:var(--sl-color-neutral-500);padding:6px 0 2px;">From role ' +
                    escapeHtml(inheritedRoleName) + '</div>';
                html += inherited.map(function (r) { return renderRow(r, false); }).join('');
            }
            if (own && own.length > 0) {
                if (inherited.length > 0) {
                    html += '<div style="font-size:0.78rem;text-transform:uppercase;letter-spacing:0.04em;color:var(--sl-color-neutral-500);padding:10px 0 2px;">This user only</div>';
                }
                html += own.map(function (r) { return renderRow(r, true); }).join('');
            }
            container.innerHTML = html;
        } catch (e) {
            container.innerHTML = '<div style="color:var(--sl-color-danger);font-size:0.9rem;">Failed to load rules</div>';
        }
    }

    async function searchFolders(q) {
        var dropdown = getDropdown();
        if (!dropdown) return;
        try {
            var resp = await fetch('/api-proxy/folders/search?q=' + encodeURIComponent(q) + '&limit=50');
            var data = await resp.json();
            var current = await fetch(basePath()).then(function (r) { return r.ok ? r.json() : []; });
            var takenIds = new Set(current.map(function (r) { return r.folderId; }));
            var folders = (data.folders || []).filter(function (f) { return !takenIds.has(f.id); });
            folders.sort(function (a, b) {
                return a.path.length - b.path.length || a.path.localeCompare(b.path);
            });

            if (folders.length === 0) {
                dropdown.innerHTML = '<div style="padding:8px 12px;color:var(--sl-color-neutral-500);font-size:0.9rem;">No matching folders</div>';
            } else {
                dropdown.innerHTML = folders.map(function (f) {
                    return '<div class="folder-search-item" data-id="' + escapeHtml(f.id) + '" data-path="' + escapeHtml(f.path) + '" style="padding:8px 12px;cursor:pointer;border-bottom:1px solid var(--sl-color-neutral-100);">' +
                        '<sl-icon name="folder2" style="margin-right:6px;"></sl-icon>' + escapeHtml(f.path) + '</div>';
                }).join('');
                if (data.hasMore) {
                    dropdown.innerHTML += '<div style="padding:6px 12px;color:var(--sl-color-neutral-400);font-size:0.8rem;text-align:center;">Type more to narrow results...</div>';
                }
            }
            dropdown.style.display = 'block';
        } catch (e) {
            dropdown.innerHTML = '<div style="padding:8px 12px;color:var(--sl-color-danger-600);">Search failed</div>';
            dropdown.style.display = 'block';
        }
    }

    async function resolveFolderByPath(path) {
        var resp = await fetch('/api-proxy/folders/search?q=' + encodeURIComponent(path) + '&limit=100');
        if (!resp.ok) return null;
        var data = await resp.json();
        var folders = data.folders || [];
        if (folders.length === 0) return null;

        var needle = path.replace(/\/+$/, '').toLowerCase();
        if (!needle.startsWith('/')) needle = '/' + needle;

        for (var i = 0; i < folders.length; i++) {
            var p = folders[i].path.replace(/\/+$/, '').toLowerCase();
            if (p === needle) return folders[i];
        }
        var segment = needle.split('/').pop();
        if (segment) {
            for (var j = 0; j < folders.length; j++) {
                var lastSeg = folders[j].path.replace(/\/+$/, '').split('/').pop().toLowerCase();
                if (lastSeg === segment) return folders[j];
            }
        }
        return null;
    }

    async function errorTextOf(resp, fallback) {
        try {
            var data = await resp.json();
            if (data && data.error) return data.error;
        } catch (e) { }
        return fallback;
    }

    async function add() {
        hideDlgError('restriction-error');
        var searchInput = getSearchInput();
        var addBtn = getAddBtn();
        var typed = searchInput ? searchInput.value.trim() : '';
        if (!typed) {
            showDlgError('restriction-error', 'Enter a folder path or pick one from the list.');
            return;
        }

        var folderId = selectedFolderId;
        if (!folderId) {
            if (addBtn) addBtn.loading = true;
            var match = await resolveFolderByPath(typed);
            if (addBtn) addBtn.loading = false;
            if (!match) {
                showDlgError('restriction-error', "Folder '" + typed + "' not found.");
                return;
            }
            folderId = match.id;
        }

        if (addBtn) addBtn.loading = true;
        var effect = (document.getElementById('restriction-effect') || {}).value || 'deny';
        var isReadOnly = effect === 'allow' && (document.getElementById('restriction-readonly') || {}).checked;
        var resp = await fetch(basePath(), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ folderId: folderId, effect: effect, isReadOnly: isReadOnly })
        });
        if (addBtn) addBtn.loading = false;
        if (resp.ok) {
            selectedFolderId = null;
            if (searchInput) searchInput.value = '';
            var dropdown = getDropdown();
            if (dropdown) dropdown.style.display = 'none';
            load();
        } else {
            showDlgError('restriction-error', await errorTextOf(resp, 'Failed to add rule'));
        }
    }

    function initListeners() {
        var searchInput = getSearchInput();
        var dropdown = getDropdown();
        var addBtn = getAddBtn();

        if (searchInput && !searchInput._faBound) {
            searchInput._faBound = true;
            searchInput.addEventListener('sl-input', function () {
                selectedFolderId = null;
                hideDlgError('restriction-error');
                clearTimeout(searchTimer);
                var q = searchInput.value.trim();
                if (q.length < 1) { if (dropdown) dropdown.style.display = 'none'; return; }
                searchTimer = setTimeout(function () { searchFolders(q); }, 250);
            });
        }

        if (dropdown && !dropdown._faBound) {
            dropdown._faBound = true;
            dropdown.addEventListener('click', function (e) {
                var item = e.target.closest('.folder-search-item');
                if (!item) return;
                selectedFolderId = item.dataset.id;
                if (searchInput) searchInput.value = item.dataset.path;
                dropdown.style.display = 'none';
                hideDlgError('restriction-error');
            });
            dropdown.addEventListener('mouseover', function (e) {
                var item = e.target.closest('.folder-search-item');
                if (item) item.style.background = 'var(--sl-color-primary-50)';
            });
            dropdown.addEventListener('mouseout', function (e) {
                var item = e.target.closest('.folder-search-item');
                if (item) item.style.background = '';
            });
        }

        if (!window._faDocClickBound) {
            window._faDocClickBound = true;
            document.addEventListener('click', function (e) {
                var dd = document.getElementById('restriction-folder-dropdown');
                if (!dd) return;
                if (!e.target.closest('#restriction-folder-search') && !e.target.closest('#restriction-folder-dropdown')) {
                    dd.style.display = 'none';
                }
            });
        }

        var list = document.getElementById('restriction-list');
        if (list && !list._faBound) {
            list._faBound = true;
            list.addEventListener('click', async function (e) {
                var removeBtn = e.target.closest('.acl-remove');
                if (removeBtn) {
                    var resp = await fetch(basePath() + '/' + removeBtn.dataset.folder, { method: 'DELETE' });
                    if (resp.ok) load();
                    else showDlgError('restriction-error', await errorTextOf(resp, 'Failed to remove rule'));
                    return;
                }
                var roBtn = e.target.closest('.acl-toggle-ro');
                if (roBtn) {
                    var r2 = await fetch(basePath() + '/' + roBtn.dataset.folder, {
                        method: 'PATCH',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ isReadOnly: roBtn.dataset.ro === 'true' })
                    });
                    if (r2.ok) load();
                    else showDlgError('restriction-error', await errorTextOf(r2, 'Failed to toggle read-only'));
                }
            });
        }

        var effectEl = document.getElementById('restriction-effect');
        if (effectEl && !effectEl._faBound) {
            effectEl._faBound = true;
            effectEl.addEventListener('sl-change', function () {
                var sel = effectEl.value;
                var roRow = document.getElementById('restriction-readonly-row');
                if (roRow) roRow.style.display = sel === 'allow' ? '' : 'none';
                if (sel !== 'allow') {
                    var roEl = document.getElementById('restriction-readonly');
                    if (roEl) roEl.checked = false;
                }
            });
        }

        if (addBtn && !addBtn._faBound) {
            addBtn._faBound = true;
            addBtn.addEventListener('click', add);
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initListeners);
    } else {
        initListeners();
    }

    return { open: open, reload: load, init: initListeners };
})();
