(function () {
    var pageRoot = document.querySelector('#page-content .main-content') || document.getElementById('page-content');
    if (pageRoot) {
        if (pageRoot.dataset.treeInit) return;
        pageRoot.dataset.treeInit = '1';
    }

    document.body.classList.add('home-chat-page', 'chat-ui');

    // Header buttons
    var btnAddFolderHome = document.getElementById('btn-open-add-folder-home');
    if (btnAddFolderHome) {
        btnAddFolderHome.addEventListener('click', function () {
            document.getElementById('dlg-add-folder-home')?.show();
        });
    }

    var btnExportAll = document.getElementById('btn-export-all-home');
    if (btnExportAll) {
        btnExportAll.addEventListener('click', function () {
            if (typeof window.openDownloadDialog === 'function') {
                window.openDownloadDialog({ kind: 'all' });
            }
        });
    }

    var btnOpenObsidian = document.getElementById('btn-open-import-obsidian');
    if (btnOpenObsidian) {
        btnOpenObsidian.addEventListener('click', function () {
            document.getElementById('dlg-import-obsidian')?.show();
        });
    }

    var btnOpenBee = document.getElementById('btn-open-import-bee');
    if (btnOpenBee) {
        btnOpenBee.addEventListener('click', function () {
            document.getElementById('dlg-import-bee')?.show();
        });
    }

    document.querySelectorAll('[data-dlg-cancel]').forEach(function (btn) {
        btn.addEventListener('click', function () {
            var dlgId = btn.getAttribute('data-dlg-cancel');
            document.getElementById(dlgId)?.hide();
        });
    });

    // UX-only chat gating
    fetch('/api-proxy/chat/access').then(function (r) { return r.ok ? r.json() : { allowed: true }; })
        .then(function (data) {
            if (data.allowed) return;
            var selectors = ['.welcome-chat-hint', '#home-welcome-collapse-tab', '#chat-messages', '.chat-composer-outer', '#home-chat-actions'];
            selectors.forEach(function (sel) {
                document.querySelectorAll(sel).forEach(function (el) { el.style.display = 'none'; });
            });
        }).catch(function () {});

    // Add folder dialog
    (function () {
        var btn = document.getElementById('btn-add-folder-home');
        var dlg = document.getElementById('dlg-add-folder-home');
        var errBox = document.getElementById('add-folder-home-error');
        var errMsg = document.getElementById('add-folder-home-error-msg');

        function showError(msg) {
            if (errMsg) errMsg.textContent = msg || 'Failed to create folder';
            if (errBox) {
                errBox.style.display = 'block';
                errBox.open = true;
            }
        }
        function clearError() {
            if (errBox) errBox.style.display = 'none';
            if (errMsg) errMsg.textContent = '';
        }
        if (dlg) dlg.addEventListener('sl-show', clearError);

        if (btn) {
            btn.addEventListener('click', async function () {
                clearError();
                var nameInput = document.getElementById('add-folder-home-name');
                var name = (nameInput ? nameInput.value || '' : '').trim();
                if (!name) {
                    showError('Folder name is required.');
                    return;
                }
                btn.loading = true;
                var path = '/' + name.replace(/^\/+/, '');
                try {
                    var r = await fetch('/api-proxy/folders', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ path: path })
                    });
                    if (r.ok) {
                        dlg?.hide();
                        window.location.href = '/Folder?path=' + encodeURIComponent(path);
                        return;
                    }
                    var d = null;
                    try { d = await r.json(); } catch (_) {}
                    showError((d && d.error) || 'Failed to create folder');
                } catch (_) {
                    showError('Network error. Please try again.');
                } finally {
                    btn.loading = false;
                }
            });
        }
    })();

    // Import Obsidian
    (function () {
        var btn = document.getElementById('btn-import-obsidian');
        var dlg = document.getElementById('dlg-import-obsidian');
        var errBox = document.getElementById('import-obsidian-error');
        var errMsg = document.getElementById('import-obsidian-error-msg');
        var fileInput = document.getElementById('import-obsidian-file');

        function showError(msg) {
            if (errMsg) errMsg.textContent = msg || 'Import failed';
            if (errBox) {
                errBox.style.display = 'block';
                errBox.open = true;
            }
        }
        function clearError() {
            if (errBox) errBox.style.display = 'none';
            if (errMsg) errMsg.textContent = '';
        }
        if (dlg && fileInput) {
            dlg.addEventListener('sl-show', function () {
                clearError();
                fileInput.value = '';
            });
        }

        if (btn) {
            btn.addEventListener('click', async function () {
                clearError();
                var file = fileInput && fileInput.files ? fileInput.files[0] : null;
                if (!file) {
                    showError('Please select a ZIP file.');
                    return;
                }
                btn.loading = true;
                dlg?.hide();
                if (window.bmbShowBusyModal) {
                    window.bmbShowBusyModal('Importing…', "This can take a while for large vaults — please don't close this window.");
                }
                try {
                    var formData = new FormData();
                    formData.append('file', file);
                    var r = await fetch('/api-proxy/import/obsidian', {
                        method: 'POST',
                        body: formData
                    });
                    var d = null;
                    try { d = await r.json(); } catch (_) {}
                    if (window.bmbHideBusyModal) window.bmbHideBusyModal();
                    if (r.ok && d) {
                        var msg = 'Imported ' + (d.articlesCreated || 0) + ' articles, '
                            + (d.imagesImported || 0) + ' images, '
                            + (d.filesSkipped || 0) + ' files skipped.';
                        alert(msg);
                        window.location.reload();
                        return;
                    }
                    dlg?.show();
                    showError((d && d.error) || 'Import failed');
                } catch (_) {
                    if (window.bmbHideBusyModal) window.bmbHideBusyModal();
                    dlg?.show();
                    showError('Network error. Please try again.');
                } finally {
                    btn.loading = false;
                }
            });
        }
    })();

    // Import Bee
    (function () {
        var btn = document.getElementById('btn-import-bee');
        var dlg = document.getElementById('dlg-import-bee');
        var errBox = document.getElementById('import-bee-error');
        var errMsg = document.getElementById('import-bee-error-msg');
        var fileInput = document.getElementById('import-bee-file');
        var pathSelectorRoot = document.getElementById('import-bee-path-selector');
        var pathSelector = null;

        function showError(msg) {
            if (errMsg) errMsg.textContent = msg || 'Import failed';
            if (errBox) {
                errBox.style.display = 'block';
                errBox.open = true;
            }
        }
        function clearError() {
            if (errBox) errBox.style.display = 'none';
            if (errMsg) errMsg.textContent = '';
        }
        if (dlg) {
            dlg.addEventListener('sl-show', function () {
                clearError();
                if (fileInput) fileInput.value = '';
                if (window.bmbInitPathSelector && pathSelectorRoot) {
                    pathSelector = window.bmbInitPathSelector({ rootEl: pathSelectorRoot, basePath: '/' });
                }
            });
        }

        if (btn) {
            btn.addEventListener('click', async function () {
                clearError();
                var file = fileInput && fileInput.files ? fileInput.files[0] : null;
                if (!file) {
                    showError('Please select a ZIP file.');
                    return;
                }
                btn.loading = true;
                dlg?.hide();
                if (window.bmbShowBusyModal) {
                    window.bmbShowBusyModal('Importing…', "This can take a while for large vaults — please don't close this window.");
                }
                try {
                    var formData = new FormData();
                    formData.append('file', file);
                    formData.append('destinationPath', pathSelector ? pathSelector.getEffectivePath() : '/');
                    var r = await fetch('/api-proxy/import/bee', {
                        method: 'POST',
                        body: formData
                    });
                    var d = null;
                    try { d = await r.json(); } catch (_) {}
                    if (window.bmbHideBusyModal) window.bmbHideBusyModal();
                    if (r.ok && d) {
                        var msg = 'Imported into ' + (d.rootFolderPath || '/') + ': '
                            + (d.foldersCreated || 0) + ' folders, '
                            + (d.articlesCreated || 0) + ' articles, '
                            + (d.imagesImported || 0) + ' images.'
                            + (d.articlesSkippedProtected ? ' ' + d.articlesSkippedProtected + ' password-protected article(s) skipped.' : '');
                        alert(msg);
                        window.location.reload();
                        return;
                    }
                    dlg?.show();
                    showError((d && d.error) || 'Import failed');
                } catch (_) {
                    if (window.bmbHideBusyModal) window.bmbHideBusyModal();
                    dlg?.show();
                    showError('Network error. Please try again.');
                } finally {
                    btn.loading = false;
                }
            });
        }
    })();
})();
