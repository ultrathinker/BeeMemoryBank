// Admin Pages Module. One script for the pages of the Admin area (/Admin/<Name>): every part below looks for its own elements and does nothing when they
// are not on the page, so the pages that have none of them (and the index, which does not load this file) raise no errors.
(function () {
    'use strict';

    var pageRoot = document.querySelector('#page-content .main-content') || document.getElementById('page-content');
    if (pageRoot) {
        if (pageRoot.dataset.adminInit) return;
        pageRoot.dataset.adminInit = '1';
    }

    // Per-page AbortSignal: every document-level listener below uses pageOpts so that
    // spaCleanup()'s abort on the next SPA navigation tears down this run's listeners
    // together with their closures.
    var pageSignal = window.bmbGetPageSignal ? window.bmbGetPageSignal() : null;
    var pageOpts = pageSignal ? { signal: pageSignal } : false;
    var pageCaptureOpts = pageSignal ? { signal: pageSignal, capture: true } : true;

    // Auto-refresh for an in-progress software update and an active DEK rotation: the page reloads itself until the work is done. The pages of the area are swapped in
    // by the single-page navigation (site-spa.js), so the timer must die with the page that started it: otherwise it reloads the NEXT page, and the flag that keeps
    // two runs of this script from starting two timers stays set for good, so that a return to the page never refreshes again.
    function reloadWhileBusy(selector, flag, ms) {
        if (!document.querySelector(selector) || window[flag]) return;
        window[flag] = true;
        var timer = setTimeout(function () { location.reload(); }, ms);
        if (pageSignal) pageSignal.addEventListener('abort', function () { clearTimeout(timer); window[flag] = false; });
    }
    reloadWhileBusy('[data-is-updating="true"]', '_updateRefreshing', 2000);
    reloadWhileBusy('[data-dek-active="true"]', '_dekRotationRefreshing', 3000);

    // Check for updates dialog
    var btnOpenCheckUpdate = document.getElementById('btn-open-check-update');
    if (btnOpenCheckUpdate) {
        btnOpenCheckUpdate.addEventListener('click', function () {
            var dlg = document.getElementById('dlg-check-update');
            if (dlg) dlg.show();
        });
    }

    // Local sync events counter
    var localCounter = document.getElementById('local-events-counter');
    if (localCounter) {
        fetch('/api-proxy/sync/status')
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (localCounter && data && data.totalLocalEvents != null) {
                    localCounter.textContent = 'Local events: ' + data.totalLocalEvents;
                }
            })
            .catch(function () {});
    }

    // AI / Chat (keys, models, providers, defaults, the chat switch) has its own script: ai-providers.js (the page script of Admin -> AI / Chat).

    // Recovery Key
    var issueRecoveryBtn = document.getElementById('issue-recovery-key-btn');
    if (issueRecoveryBtn) {
        issueRecoveryBtn.addEventListener('click', function () {
            if (!confirm('Issue a new recovery key?\n\nIt will be shown once and cannot be retrieved afterwards. Existing passwords keep working.')) return;
            // A key change (BMB-311): the button is off and spins; a refusal is written next to it, in full.
            bmbBusy.run(issueRecoveryBtn, function () {
                return bmbBusy.request('/api-proxy/keys/add-recovery', { method: 'POST', headers: { 'Accept': 'application/json' } })
                    .then(function (res) {
                        if (!(res.body && res.body.recoveryKey)) throw new Error('The node did not return a recovery key (is the vault unlocked?).');
                        document.getElementById('recovery-key-value').value = res.body.recoveryKey;
                        document.getElementById('recovery-key-dialog').show();
                    });
            }, { quiet: true });
        });

        var recoveryCopyBtn = document.getElementById('recovery-key-copy');
        if (recoveryCopyBtn) {
            recoveryCopyBtn.addEventListener('click', function () {
                var input = document.getElementById('recovery-key-value');
                if (input) navigator.clipboard.writeText(input.value).catch(function () {});
            });
        }

        var recoveryCloseBtn = document.getElementById('recovery-key-close');
        if (recoveryCloseBtn) {
            recoveryCloseBtn.addEventListener('click', function () {
                var input = document.getElementById('recovery-key-value');
                if (input) input.value = '';
                var dlg = document.getElementById('recovery-key-dialog');
                if (dlg) dlg.hide();
            });
        }
    }

    // Embeddings & Search
    var embeddingsToggle = document.getElementById('embeddings-enabled-toggle');
    function loadEmbeddingsEnabled() {
        if (!embeddingsToggle) return;
        fetch('/api-proxy/admin/search/embeddings-enabled', { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok ? r.json() : { enabled: false }; })
            .then(function (data) { embeddingsToggle.checked = !!data.enabled; })
            .catch(function () {});
    }
    if (embeddingsToggle) {
        embeddingsToggle.addEventListener('sl-change', function () {
            var enabled = embeddingsToggle.checked;
            bmbBusy.run(embeddingsToggle, function () {
                return bmbBusy.request('/api-proxy/admin/search/embeddings-enabled', {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ enabled: enabled })
                });
            }, {
                quiet: true,
                error: function (err) { embeddingsToggle.checked = !enabled; bmbBusy.showResult(embeddingsToggle, false, err.message); }
            });
        });
    }

    var embeddingsBackfillBtn = document.getElementById('embeddings-backfill-btn');
    var embeddingsBackfillStatus = document.getElementById('embeddings-backfill-status');
    if (embeddingsBackfillBtn) {
        embeddingsBackfillBtn.addEventListener('click', function () {
            if (embeddingsBackfillStatus) embeddingsBackfillStatus.textContent = '';
            // The node answers at once and works on in the background; a second press while it works is refused (409) and the reason is said here.
            bmbBusy.run(embeddingsBackfillBtn, function () {
                return bmbBusy.request('/api-proxy/admin/search/embeddings/backfill', { method: 'POST' });
            }, { success: 'Started — check the server logs or the metrics below in a bit.' });
        });
    }

    // Web Session Settings
    var sessionHoursInput = document.getElementById('session-expire-hours');
    var sessionSlidingToggle = document.getElementById('session-sliding-toggle');
    var sessionSaveBtn = document.getElementById('btn-save-session-settings');
    var sessionSavedMsg = document.getElementById('session-settings-saved-msg');
    function loadSessionSettings() {
        if (!sessionHoursInput || !sessionSlidingToggle) return;
        fetch('/api-proxy/session/settings', { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok ? r.json() : { expireHours: 48, slidingExpiration: true }; })
            .then(function (data) {
                sessionHoursInput.value = data.expireHours;
                sessionSlidingToggle.checked = !!data.slidingExpiration;
            })
            .catch(function () {});
    }
    if (sessionSaveBtn) {
        sessionSaveBtn.addEventListener('click', function () {
            if (sessionSavedMsg) sessionSavedMsg.style.display = 'none';
            var hours = parseInt(sessionHoursInput.value, 10);
            if (!hours || hours < 1 || hours > 720) {
                alert('Session duration must be between 1 and 720 hours.');
                return;
            }
            bmbBusy.run(sessionSaveBtn, function () {
                return bmbBusy.request('/api-proxy/session/settings', {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ expireHours: hours, slidingExpiration: sessionSlidingToggle.checked })
                }).then(function () {
                    if (sessionSavedMsg) {
                        sessionSavedMsg.style.display = 'inline';
                        setTimeout(function () { sessionSavedMsg.style.display = 'none'; }, 3000);
                    }
                });
            }, { quiet: true });
        });
    }

    // Branding
    var brandInput = document.getElementById('brand-name-input');
    var brandPreview = document.getElementById('brand-preview');
    var brandSaveBtn = document.getElementById('btn-save-branding');
    var brandResetBtn = document.getElementById('btn-reset-branding');
    var brandSavedMsg = document.getElementById('branding-saved-msg');
    var brandDefault = 'Bee Memory Bank';

    function renderBrandPreview() {
        if (!brandPreview) return;
        var typed = (brandInput && brandInput.value || '').trim();
        brandPreview.textContent = typed || brandDefault;
    }
    function loadBranding() {
        if (!brandInput) return;
        fetch('/api-proxy/branding', { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (!data) return;
                brandDefault = data.defaultName || brandDefault;
                brandInput.placeholder = brandDefault;
                brandInput.value = data.isCustom ? data.name : '';
                renderBrandPreview();
            })
            .catch(function () {});
    }
    function saveBranding(name, btn) {
        if (brandSavedMsg) brandSavedMsg.style.display = 'none';
        bmbBusy.run(btn, function () {
            return bmbBusy.request('/api-proxy/branding', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name: name })
            }).then(function (res) {
                var data = res.body || {};
                if (brandInput) brandInput.value = data.isCustom ? data.name : '';
                renderBrandPreview();
                var headerSpan = document.querySelector('.app-logo-name');
                if (headerSpan && data.name) headerSpan.textContent = data.name;
                if (brandSavedMsg) {
                    brandSavedMsg.style.display = 'inline';
                    setTimeout(function () { brandSavedMsg.style.display = 'none'; }, 3000);
                }
            });
        }, { quiet: true });
    }
    if (brandInput) brandInput.addEventListener('sl-input', renderBrandPreview);
    if (brandSaveBtn) brandSaveBtn.addEventListener('click', function () { saveBranding((brandInput.value || '').trim(), brandSaveBtn); });
    if (brandResetBtn) brandResetBtn.addEventListener('click', function () { if (brandInput) brandInput.value = ''; saveBranding('', brandResetBtn); });

    // Snapshots: Upload dropzone and progress
    var dropZone = document.getElementById('snapshot-upload-zone');
    var fileInput = document.getElementById('snapshot-file-input');
    if (dropZone && fileInput) {
        var pickFileBtn = document.getElementById('btn-pick-file');
        if (pickFileBtn) pickFileBtn.addEventListener('click', function () { fileInput.click(); });

        dropZone.addEventListener('dragover', function (e) { e.preventDefault(); dropZone.style.background = 'rgba(0,0,0,0.05)'; });
        dropZone.addEventListener('dragleave', function () { dropZone.style.background = ''; });
        dropZone.addEventListener('drop', function (e) {
            e.preventDefault();
            dropZone.style.background = '';
            if (e.dataTransfer.files.length) uploadSnapshotFile(e.dataTransfer.files[0]);
        });
        fileInput.addEventListener('change', function (e) {
            if (e.target.files.length) uploadSnapshotFile(e.target.files[0]);
        });
    }

    function uploadSnapshotFile(file) {
        var fd = new FormData();
        fd.append('file', file);
        var prog = document.getElementById('upload-progress');
        var status = document.getElementById('upload-status');
        var bar = document.getElementById('upload-progress-bar');
        if (prog) prog.style.display = 'block';
        if (status) status.textContent = 'Uploading ' + file.name + '...';

        // A long operation (BMB-311): the pop-up says how far the upload is; a failure is written in the upload area, in full.
        bmbBusy.run(document.getElementById('btn-pick-file'), function () {
            return new Promise(function (resolve, reject) {
                var xhr = new XMLHttpRequest();
                xhr.upload.addEventListener('progress', function (e) {
                    if (!e.lengthComputable) return;
                    var pct = Math.round((e.loaded / e.total) * 100);
                    if (bar) bar.value = pct;
                    bmbBusy.setModalMessage('Uploading ' + file.name + ': ' + pct + '%. Do not close this page.');
                });
                xhr.onload = function () {
                    if (xhr.status === 200) {
                        if (status) status.textContent = 'Uploaded successfully';
                        resolve();
                        return;
                    }
                    var said = '';
                    try { said = (JSON.parse(xhr.responseText) || {}).error || ''; } catch (parseError) { said = ''; }
                    reject(new Error(said || ('Upload failed: ' + (xhr.statusText || xhr.status) + '.')));
                };
                xhr.onerror = function () { reject(new Error('The node did not answer. Check that it is running and try again.')); };
                xhr.open('POST', '/Admin/Snapshots?handler=UploadSnapshot');
                var tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');
                if (tokenInput) {
                    xhr.setRequestHeader('RequestVerificationToken', tokenInput.value);
                }
                xhr.send(fd);
            }).then(function () {
                setTimeout(function () { location.reload(); }, 1500);
            });
        }, {
            long: true,
            title: 'Uploading the snapshot',
            text: 'Uploading ' + file.name + '. Do not close this page.',
            quiet: true,
            scope: dropZone,
            error: function (err) {
                if (status) status.textContent = 'Upload failed.';
                bmbBusy.showResult(dropZone, false, err.message);
            }
        });
    }

    // Restore snapshot: the choice between "this node only" and "the whole network"
    var restoreModeEl = document.getElementById('restore-mode');
    function currentRestoreMode() {
        // "standalone" (this node only, the safe default) unless the network option is chosen
        return restoreModeEl && restoreModeEl.value === 'network' ? 'network' : 'standalone';
    }
    function showRestoreMode() {
        var mode = currentRestoreMode();
        var standaloneInfo = document.getElementById('restore-mode-standalone');
        var networkInfo = document.getElementById('restore-mode-network');
        var backupRow = document.getElementById('restore-backup-row');
        if (standaloneInfo) standaloneInfo.hidden = mode !== 'standalone';
        if (networkInfo) networkInfo.hidden = mode !== 'network';
        // A network restore always takes its own safety backup, so the checkbox does not apply to it
        if (backupRow) backupRow.hidden = mode === 'network';
    }
    if (restoreModeEl) restoreModeEl.addEventListener('sl-change', showRestoreMode);

    // Restore snapshot confirmation
    var restoreBtn = document.getElementById('btn-restore-confirm');
    if (restoreBtn) {
        restoreBtn.addEventListener('click', function () {
            var fileName = document.getElementById('restore-file-name') ? document.getElementById('restore-file-name').value : '';
            var passwordInput = document.getElementById('restore-master-password');
            var password = passwordInput ? passwordInput.value : '';
            var backupCheckbox = document.getElementById('restore-backup-checkbox');
            var createBackup = backupCheckbox ? backupCheckbox.checked : true;
            var mode = currentRestoreMode();
            var token = document.querySelector('input[name="__RequestVerificationToken"]') ? document.querySelector('input[name="__RequestVerificationToken"]').value : '';

            if (!password) {
                alert('Master Password is required to restore a snapshot.');
                return;
            }

            if (mode === 'network') {
                if (!confirm('Restore the whole network from this snapshot?\n\nEvery node that accepts it will replace its articles, folders, tags and media with the snapshot content.')) return;

                var fd = new FormData();
                fd.append('fileName', fileName);
                fd.append('masterPassword', password);
                // A long operation (BMB-311): the pop-up covers the page; when the node refuses, the dialog is still here and says why next to the button.
                bmbBusy.run(restoreBtn, function () {
                    return bmbBusy.request('/Admin/Snapshots?handler=InitiateNetworkRestore', {
                        method: 'POST',
                        headers: { 'RequestVerificationToken': token },
                        body: fd
                    }).then(function (res) {
                        if (!(res.body && res.body.eventId)) throw new Error('The node did not start the network restore.');
                        var networkDlg = document.getElementById('dlg-restore-snapshot');
                        if (networkDlg) networkDlg.hide();
                        window.location.href = '/Login?restore=true';
                    });
                }, { quiet: true, long: true, title: 'Starting the network restore', text: 'Please wait. Do not close this page.' });
                return;
            }

            if (!confirm('Restore this node only?\n\nIt becomes a new node: new identity, and it forgets its trusted nodes, blind copies and sync history. Your other devices must be joined to it again.')) return;

            var detail = createBackup
                ? 'Creating a safety backup first, then restoring. Do not close this page.'
                : 'Restoring the snapshot without a backup. Do not close this page.';

            // A long operation (BMB-311): the pop-up covers the page; when the node refuses, the dialog is still here and says why next to the button.
            bmbBusy.run(restoreBtn, function () {
                return bmbBusy.request('/Admin/Snapshots?handler=RestoreSnapshot', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/x-www-form-urlencoded',
                        'RequestVerificationToken': token
                    },
                    body: 'fileName=' + encodeURIComponent(fileName) +
                          '&masterPassword=' + encodeURIComponent(password) +
                          '&createBackupFirst=' + (createBackup ? 'true' : 'false') +
                          '&mode=' + encodeURIComponent(mode)
                }).then(function (res) {
                    var restoreDlg = document.getElementById('dlg-restore-snapshot');
                    if (restoreDlg) restoreDlg.hide();
                    if (res.response.redirected) {
                        // The page answers with a redirect: to the sign-in page, which says what happened, or back here with the error
                        window.location.href = res.response.url;
                    } else {
                        window.location.href = '/Login?restored=standalone';
                    }
                });
            }, { quiet: true, long: true, title: 'Restoring the snapshot', text: detail });
        });
    }

    // Delegated listeners (registered per page run, aborted by spaCleanup via pageSignal)

    // Confirm before executing destructive admin actions (.btn-confirm-click) in capture phase
    document.addEventListener('click', function (e) {
        var btn = e.target.closest('.btn-confirm-click');
        if (!btn || btn._bmbConfirmed) return;
        var msg = btn.dataset.confirm || 'Are you sure?';
        if (!confirm(msg)) {
            e.preventDefault();
            e.stopImmediatePropagation();
            return;
        }
        btn._bmbConfirmed = true;
        setTimeout(function () { delete btn._bmbConfirmed; }, 500);
    }, pageCaptureOpts);

    // Document-level clicks
    document.addEventListener('click', function (e) {
        // Upload dropzone toggle
        var uploadToggle = e.target.closest('#btn-show-upload');
        if (uploadToggle) {
            var zone = document.getElementById('snapshot-upload-zone');
            if (zone) zone.style.display = (zone.style.display === 'none' || !zone.style.display) ? 'block' : 'none';
            return;
        }

        // Create snapshot button
        var createBtn = e.target.closest('#btn-create-snapshot');
        if (createBtn) {
            // A long operation (BMB-311): a second press is refused by the page and by the node (409); a refusal is written next to the button.
            bmbBusy.run(createBtn, function () {
                return bmbBusy.request('/api-proxy/snapshots', { method: 'POST' }).then(function () { window.location.reload(); });
            }, { quiet: true, long: true, title: 'Creating the snapshot', text: 'This may take a while for large databases. Do not close this page.' });
            return;
        }

        // Open restore snapshot dialog
        var restoreTrigger = e.target.closest('.btn-open-restore-snapshot');
        if (restoreTrigger) {
            var fname = restoreTrigger.dataset.filename;
            var inputFn = document.getElementById('restore-file-name');
            var disp = document.getElementById('restore-snapshot-name-display');
            var dlg = document.getElementById('dlg-restore-snapshot');
            if (inputFn) inputFn.value = fname;
            if (disp) disp.textContent = fname;
            // Every opening starts from the default: this node only, backup first, nothing typed
            if (restoreModeEl) restoreModeEl.value = 'standalone';
            var backupDefault = document.getElementById('restore-backup-checkbox');
            if (backupDefault) backupDefault.checked = true;
            var passwordDefault = document.getElementById('restore-master-password');
            if (passwordDefault) passwordDefault.value = '';
            showRestoreMode();
            if (dlg) dlg.show();
            return;
        }

        // Change node URL
        var changeUrlBtn = e.target.closest('.btn-open-change-url');
        if (changeUrlBtn) {
            var nid = changeUrlBtn.dataset.nodeId;
            var nname = changeUrlBtn.dataset.nodeName;
            var curl = changeUrlBtn.dataset.currentUrl;
            var nidInput = document.getElementById('change-url-node-id');
            var nnameEl = document.getElementById('change-url-node-name');
            var urlInput = document.getElementById('change-url-input');
            var dlgUrl = document.getElementById('dlg-change-url');
            if (nidInput) nidInput.value = nid;
            if (nnameEl) nnameEl.textContent = 'Node: ' + nname;
            if (urlInput) {
                urlInput.value = curl;
                setTimeout(function () { urlInput.value = curl; }, 50);
            }
            if (dlgUrl) dlgUrl.show();
            return;
        }

        // Let blind copies call this node (ADR 0007)
        var hubBtn = e.target.closest('.btn-open-hub');
        if (hubBtn) {
            var hubIdInput = document.getElementById('hub-node-id');
            var hubNameEl = document.getElementById('hub-node-name');
            var hubAddr = document.getElementById('hub-address');
            var hubTrust = document.getElementById('hub-trust');
            var hubDlg = document.getElementById('dlg-hub');
            var hubUrl = hubBtn.dataset.currentUrl || '';
            if (hubIdInput) hubIdInput.value = hubBtn.dataset.nodeId;
            if (hubNameEl) hubNameEl.textContent = 'Node: ' + hubBtn.dataset.nodeName;
            if (hubAddr) {
                hubAddr.value = /^https:\/\//i.test(hubUrl) ? hubUrl : '';
                setTimeout(function () { if (!hubAddr.value && /^https:\/\//i.test(hubUrl)) hubAddr.value = hubUrl; }, 50);
            }
            if (hubTrust) {
                hubTrust.value = hubBtn.dataset.trust || 'public-ca';
                setHubMode(hubTrust.value);
            }
            if (hubDlg) hubDlg.show();
            return;
        }

        // Open DEK rotation dialog (initial propose + retry after a failed rotation)
        var openDekBtn = e.target.closest('.btn-open-dek-rotation');
        if (openDekBtn) {
            var dlgDek = document.getElementById('dlg-dek-rotation');
            if (dlgDek) dlgDek.show();
            return;
        }

        // Promote/Demote superadmin
        var superadminBtn = e.target.closest('.btn-confirm-superadmin');
        if (superadminBtn) {
            var sname = superadminBtn.dataset.nodeName;
            var demoting = superadminBtn.dataset.demote === 'true';
            var msg = demoting
                ? 'Demote "' + sname + '"?\n\nIt will no longer be able to revoke peers, hard-delete content across the network, or restore every node from its own snapshot. It keeps syncing content as usual.'
                : 'Promote "' + sname + '" to superadmin?\n\nIt will be able to revoke any peer, hard-delete content on every node, and restore the whole network from its own snapshot.';
            if (!confirm(msg)) {
                e.preventDefault();
                return;
            }
            var form = superadminBtn.closest('form');
            if (form) bmbBusy.submit(form);
            return;
        }

        // Generic confirm button in form
        var confirmBtn = e.target.closest('.btn-confirm-submit');
        if (confirmBtn) {
            var pat = confirmBtn.dataset.confirmPattern || 'Are you sure?';
            var nameVal = confirmBtn.dataset.name || '';
            var text = pat.replace('{name}', nameVal);
            if (!confirm(text)) {
                e.preventDefault();
                return;
            }
            var cform = confirmBtn.closest('form');
            if (cform) bmbBusy.submit(cform);
            return;
        }

        // Open dialog button
        var openDlgBtn = e.target.closest('.btn-open-dialog');
        if (openDlgBtn) {
            var targetDlgId = openDlgBtn.dataset.dialogId;
            if (targetDlgId) {
                var targetDlg = document.getElementById(targetDlgId);
                if (targetDlg) targetDlg.show();
            }
            return;
        }

        // Destructive admin actions with confirmation (.btn-confirm-click)
        var confirmClickBtn = e.target.closest('.btn-confirm-click');
        if (confirmClickBtn) {
            var href = confirmClickBtn.getAttribute('href');
            if (href) {
                e.preventDefault();
                window.location.href = href;
                return;
            }
            // Form submit actions are handled natively by Shoelace once confirmed in capture phase.
        }

        // Dialog cancel buttons (admin fallback)
        var cancelDlgBtn = e.target.closest('[data-dlg-cancel]');
        if (cancelDlgBtn) {
            var cDlgId = cancelDlgBtn.getAttribute('data-dlg-cancel');
            if (cDlgId) {
                var cDlg = document.getElementById(cDlgId);
                if (cDlg && typeof cDlg.hide === 'function') cDlg.hide();
            }
            return;
        }

        // Copy public key
        var copyPubBtn = e.target.closest('.btn-copy-pubkey, #btn-copy-pubkey');
        if (copyPubBtn) {
            var tgtId = copyPubBtn.dataset.target || 'pubkey-val';
            var el = document.getElementById(tgtId);
            if (el) {
                var val = el.textContent.trim();
                if (window.bmbCopyToClipboard) {
                    window.bmbCopyToClipboard(val, copyPubBtn);
                } else if (navigator.clipboard) {
                    navigator.clipboard.writeText(val).catch(function () {});
                }
            }
            return;
        }
    }, pageOpts);

    // The question of a form that asks first is asked by site.js, on every page.

    // Which explanation and fields the "Let blind copies call this node" dialog shows for the mode chosen.
    function setHubMode(mode) {
        var show = function (id, on) {
            var el = document.getElementById(id);
            if (el) el.style.display = on ? '' : 'none';
        };
        show('hub-help-public-ca', mode === 'public-ca');
        show('hub-help-pin', mode === 'pin');
        show('hub-help-off', mode === 'off');
        show('hub-address-row', mode !== 'off');
        show('hub-pin-row', mode === 'pin');
    }

    // Delegated Shoelace switch changes
    document.addEventListener('sl-change', function (e) {
        var hubSelect = e.target.closest ? e.target.closest('#hub-trust') : null;
        if (hubSelect) {
            setHubMode(hubSelect.value);
            return;
        }

        // Auto accept restore toggle
        var autoAcceptSwitch = e.target.closest('.switch-auto-accept');
        if (autoAcceptSwitch) {
            var nodeId = autoAcceptSwitch.dataset.nodeId;
            var nodeName = autoAcceptSwitch.dataset.nodeName;
            var newVal = autoAcceptSwitch.checked;
            var proceed = true;
            if (newVal) {
                proceed = confirm(
                    "Enable AUTO-RESTORE for peer '" + nodeName + "'?\n\n" +
                    "When this peer initiates a snapshot restore, it will apply on this node WITHOUT confirmation. " +
                    "Your articles, folders, and tags from after the restore point will be replaced.\n\n" +
                    "Only enable for peers you fully trust (your own devices, your team's nodes). " +
                    "For peers belonging to other people, keep this OFF.\n\n" +
                    "Continue?"
                );
            }
            if (!proceed) {
                autoAcceptSwitch.checked = !newVal;
                return;
            }
            var valInput = document.getElementById("autoaccept-value-" + nodeId);
            var form = document.getElementById("autoaccept-form-" + nodeId);
            if (valInput && form) {
                valInput.value = newVal ? "true" : "false";
                bmbBusy.submit(form);
            }
            return;
        }

        // Auto accept DEK toggle
        var autoAcceptDekSwitch = e.target.closest('.switch-auto-accept-dek');
        if (autoAcceptDekSwitch) {
            var dekNodeId = autoAcceptDekSwitch.dataset.nodeId;
            var dekNodeName = autoAcceptDekSwitch.dataset.nodeName;
            var dekNewVal = autoAcceptDekSwitch.checked;
            var dekProceed = true;
            if (dekNewVal) {
                dekProceed = confirm(
                    "Enable AUTO-ACCEPT DEK ROTATION for peer '" + dekNodeName + "'?\n\n" +
                    "When this peer initiates a DEK rotation, it will apply on this node WITHOUT confirmation. " +
                    "ALL article bodies, versions, and media will be re-encrypted with a new key. " +
                    "This is more destructive than snapshot restore.\n\n" +
                    "Only enable for peers you fully trust (your own devices).\n\n" +
                    "Continue?"
                );
            }
            if (!dekProceed) {
                autoAcceptDekSwitch.checked = !dekNewVal;
                return;
            }
            var dekValInput = document.getElementById("autoaccept-dek-value-" + dekNodeId);
            var dekForm = document.getElementById("autoaccept-dek-form-" + dekNodeId);
            if (dekValInput && dekForm) {
                dekValInput.value = dekNewVal ? "true" : "false";
                bmbBusy.submit(dekForm);
            }
            return;
        }
    }, pageOpts);

    // Initial data loads
    loadEmbeddingsEnabled();
    loadSessionSettings();
    loadBranding();
})();
