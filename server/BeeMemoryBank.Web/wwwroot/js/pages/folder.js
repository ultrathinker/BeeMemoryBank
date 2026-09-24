(function () {
    var dataEl = document.getElementById('folder-page-data');
    var pageData = dataEl ? JSON.parse(dataEl.textContent || '{}') : {};
    var currentPath = pageData.currentPath || '';

    function escapeHtml(text) {
        return String(text == null ? '' : text)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    // Open dialog buttons
    var btnOpenRename = document.getElementById('btn-open-rename-folder');
    if (btnOpenRename) {
        btnOpenRename.addEventListener('click', function () {
            document.getElementById('dlg-rename-folder')?.show();
        });
    }

    var btnOpenMove = document.getElementById('btn-open-move-folder');
    if (btnOpenMove) {
        btnOpenMove.addEventListener('click', function () {
            document.getElementById('dlg-move-folder')?.show();
        });
    }

    var btnOpenCopy = document.getElementById('btn-open-copy-folder');
    if (btnOpenCopy) {
        btnOpenCopy.addEventListener('click', function () {
            document.getElementById('dlg-copy-folder')?.show();
        });
    }

    var btnOpenDelete = document.getElementById('btn-open-delete-folder');
    if (btnOpenDelete) {
        btnOpenDelete.addEventListener('click', function () {
            document.getElementById('dlg-delete-folder')?.show();
        });
    }

    document.querySelectorAll('[data-dlg-cancel]').forEach(function (btn) {
        btn.addEventListener('click', function () {
            var dlgId = btn.getAttribute('data-dlg-cancel');
            document.getElementById(dlgId)?.hide();
        });
    });

    // Copy folder
    (function () {
        var dlg = document.getElementById('dlg-copy-folder');
        var btn = document.getElementById('btn-copy-folder');
        var input = document.getElementById('copy-folder-target');
        if (!dlg || !btn) return;

        function showCopyErr(msg) {
            var box = document.getElementById('copy-folder-error');
            var errMsg = document.getElementById('copy-folder-error-msg');
            if (errMsg) errMsg.textContent = msg;
            if (box) {
                box.style.display = '';
                box.open = true;
            }
        }

        btn.addEventListener('click', async function () {
            var target = (input.value || '').trim() || '/';
            try {
                var lookupResp = await fetch('/api-proxy/folders/search?q=' + encodeURIComponent(currentPath) + '&limit=20');
                var lookup = await lookupResp.json();
                var match = (lookup.folders || []).find(function (f) { return f.path === currentPath; });
                if (!match) { showCopyErr('Could not locate source folder'); return; }
                var resp = await fetch('/api-proxy/folder/' + match.id + '/copy', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ targetParentPath: target })
                });
                if (resp.ok) {
                    dlg.hide();
                    window.location.href = '/Folder?path=' + encodeURIComponent(target);
                } else {
                    var data = {}; try { data = await resp.json(); } catch (_) {}
                    showCopyErr(data.error || ('Copy failed (HTTP ' + resp.status + ')'));
                }
            } catch (e) { showCopyErr(String(e)); }
        });
    })();

    // Export folder
    var exportFolderBtn = document.getElementById('btn-export-folder');
    if (exportFolderBtn) {
        exportFolderBtn.addEventListener('click', function () {
            if (typeof window.openDownloadDialog === 'function') {
                window.openDownloadDialog({ kind: 'folder', path: currentPath });
            }
        });
    }

    // New-article toolbar button
    var openNewArticleBtn = document.getElementById('btn-open-new-article-folder');
    if (openNewArticleBtn) {
        openNewArticleBtn.addEventListener('click', function () {
            window.location.href = '/Article/Edit?treePath=' + encodeURIComponent(currentPath || '/');
        });
    }

    var addSubfolderSelector = null;
    var openAddSubBtn = document.getElementById('btn-open-add-subfolder');
    if (openAddSubBtn) {
        openAddSubBtn.addEventListener('click', function () {
            if (window.bmbInitPathSelector) {
                addSubfolderSelector = window.bmbInitPathSelector({
                    rootEl: document.getElementById('folder-add-subfolder-path-selector'),
                    basePath: currentPath
                });
            }
            document.getElementById('dlg-add-subfolder')?.show();
        });
    }

    // Move-folder picker
    var moveFolderPicker = null;
    function initMovePicker() {
        if (moveFolderPicker || typeof window.bmbFolderPicker !== 'function') return;
        var pickerRoot = document.getElementById('move-folder-picker');
        if (!pickerRoot) return;
        moveFolderPicker = window.bmbFolderPicker({
            rootEl: pickerRoot,
            initialPath: '/'
        });
    }
    var moveDlg = document.getElementById('dlg-move-folder');
    if (moveDlg) moveDlg.addEventListener('sl-show', initMovePicker);

    var moveBtn = document.getElementById('btn-move-folder');
    if (moveBtn) {
        moveBtn.addEventListener('click', function () {
            initMovePicker();
            var newParentPath = (moveFolderPicker ? moveFolderPicker.getPath() : null) || '/';
            var folderName = currentPath.replace(/\/$/, '').split('/').pop();
            var newPath = newParentPath.replace(/\/$/, '') + '/' + folderName;
            if (newPath === currentPath) { alert('Already in that folder'); return; }
            moveBtn.loading = true;
            fetch('/api-proxy/folders/move?path=' + encodeURIComponent(currentPath), {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ newParentPath: newParentPath })
            }).then(function (r) {
                if (r.ok) window.location.href = '/Folder?path=' + encodeURIComponent(newPath);
                else r.json().then(function (d) { alert(d.error || 'Move failed'); }).catch(function () { alert('Move failed'); });
            }).catch(function () { alert('Error'); }).finally(function () { moveBtn.loading = false; });
        });
    }

    var renameBtn = document.getElementById('btn-rename-folder');
    if (renameBtn) {
        renameBtn.addEventListener('click', function () {
            var newName = document.getElementById('rename-folder-name').value.trim();
            if (!newName) return;
            var parentPath = currentPath.substring(0, currentPath.replace(/\/$/, '').lastIndexOf('/'));
            var newPath = parentPath + '/' + newName;
            if (newPath === currentPath) return;
            renameBtn.loading = true;
            fetch('/api-proxy/folders?path=' + encodeURIComponent(currentPath), {
                method: 'PATCH',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ newPath: newPath })
            }).then(function (r) {
                if (r.ok) window.location.href = '/Folder?path=' + encodeURIComponent(newPath);
                else r.json().then(function (d) { alert(d.error || 'Rename failed'); }).catch(function () { alert('Rename failed'); });
            }).catch(function () { alert('Error'); }).finally(function () { renameBtn.loading = false; });
        });
    }

    var addSubBtn = document.getElementById('btn-add-subfolder');
    if (addSubBtn) {
        var addSubDlg = document.getElementById('dlg-add-subfolder');
        var addSubErrBox = document.getElementById('add-subfolder-error');
        var addSubErrMsg = document.getElementById('add-subfolder-error-msg');
        function showAddSubError(msg) {
            if (addSubErrMsg) addSubErrMsg.textContent = msg || 'Failed to create folder';
            if (addSubErrBox) {
                addSubErrBox.style.display = 'block';
                addSubErrBox.open = true;
            }
        }
        function clearAddSubError() {
            if (addSubErrBox) addSubErrBox.style.display = 'none';
            if (addSubErrMsg) addSubErrMsg.textContent = '';
        }
        if (addSubDlg) addSubDlg.addEventListener('sl-show', clearAddSubError);

        addSubBtn.addEventListener('click', async function () {
            clearAddSubError();
            var nameInput = document.getElementById('add-subfolder-name');
            var name = (nameInput.value || '').trim();
            if (!name) {
                showAddSubError('Folder name is required.');
                return;
            }
            addSubBtn.loading = true;
            var basePath = addSubfolderSelector ? addSubfolderSelector.getEffectivePath() : currentPath;
            var newPath = basePath.replace(/\/$/, '') + '/' + name;
            try {
                var r = await fetch('/api-proxy/folders', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ path: newPath })
                });
                if (r.ok) {
                    addSubDlg.hide();
                    window.location.href = '/Folder?path=' + encodeURIComponent(newPath);
                    return;
                }
                var d = null;
                try { d = await r.json(); } catch (_) {}
                showAddSubError((d && d.error) || 'Failed to create folder');
            } catch (_) {
                showAddSubError('Network error. Please try again.');
            } finally {
                addSubBtn.loading = false;
            }
        });
    }

    var deleteBtn = document.getElementById('btn-confirm-delete-folder');
    if (deleteBtn) {
        deleteBtn.addEventListener('click', function () {
            deleteBtn.loading = true;
            fetch('/api-proxy/folders?path=' + encodeURIComponent(currentPath), {
                method: 'DELETE'
            }).then(function (r) {
                if (r.ok) window.location.href = '/Tree';
                else r.json().then(function (d) { alert(d.error || 'Delete failed'); }).catch(function () { alert('Delete failed'); });
            }).catch(function () { alert('Error'); }).finally(function () { deleteBtn.loading = false; });
        });
    }

    var collator = new Intl.Collator(undefined, { sensitivity: 'base' });

    function initSortableTable(tableId, columns) {
        var table = document.getElementById(tableId);
        if (!table) return;
        var headers = table.querySelectorAll('.bmb-sort-th');
        var tbody = table.querySelector('tbody');
        var currentSort = null;
        var currentDir = null;

        headers.forEach(function (th) {
            th.addEventListener('click', function () {
                var sortKey = th.dataset.sort;
                var defaultDir = th.dataset.default || 'asc';
                if (currentSort === sortKey) {
                    currentDir = currentDir === 'asc' ? 'desc' : 'asc';
                } else {
                    currentSort = sortKey;
                    currentDir = defaultDir;
                }
                var col = columns[sortKey];
                var rows = Array.from(tbody.querySelectorAll('.bmb-sort-row'));
                rows.sort(function (a, b) {
                    var va = col.extract(a);
                    var vb = col.extract(b);
                    var cmp = col.compare(va, vb);
                    return currentDir === 'asc' ? cmp : -cmp;
                });
                rows.forEach(function (r) { tbody.appendChild(r); });
                headers.forEach(function (h) {
                    var arrow = h.querySelector('.bmb-sort-arrow');
                    if (arrow) arrow.textContent = '';
                    h.style.opacity = '0.7';
                });
                var activeArrow = th.querySelector('.bmb-sort-arrow');
                if (activeArrow) activeArrow.textContent = currentDir === 'asc' ? ' \u25B2' : ' \u25BC';
                th.style.opacity = '1';
            });
        });
    }

    initSortableTable('folders-table', {
        name: {
            extract: function (r) { return r.dataset.name; },
            compare: function (a, b) { return collator.compare(a, b); }
        },
        count: {
            extract: function (r) { return parseInt(r.dataset.count, 10) || 0; },
            compare: function (a, b) { return a - b; }
        },
        updated: {
            extract: function (r) { return new Date(r.dataset.updatedTs).getTime(); },
            compare: function (a, b) { return a - b; }
        }
    });

    initSortableTable('articles-table', {
        title: {
            extract: function (r) { return r.dataset.title; },
            compare: function (a, b) { return collator.compare(a, b); }
        },
        created: {
            extract: function (r) { return new Date(r.dataset.createdTs).getTime(); },
            compare: function (a, b) { return a - b; }
        },
        updated: {
            extract: function (r) { return new Date(r.dataset.updatedTs).getTime(); },
            compare: function (a, b) { return a - b; }
        }
    });
})();
