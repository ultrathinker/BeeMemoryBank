(function () {
    var dataEl = document.getElementById('article-edit-data');
    var pageData = dataEl ? JSON.parse(dataEl.textContent || '{}') : {};
    var isProtected = !!pageData.isProtected;
    var isLocked = !!pageData.isLocked;
    var isNew = !!pageData.isNew;
    var pageTreePath = pageData.treePath || '';
    var originalTitle = pageData.originalTitle || '';
    var originalContent = pageData.originalContent || '';

    var pageSignal = window.bmbGetPageSignal ? window.bmbGetPageSignal() : null;
    var pageOpts = pageSignal ? { signal: pageSignal } : false;

    // Dialog cancel handler
    document.querySelectorAll('[data-dlg-cancel]').forEach(function (btn) {
        btn.addEventListener('click', function () {
            var dlgId = btn.getAttribute('data-dlg-cancel');
            document.getElementById(dlgId)?.hide();
        });
    });

    // Draft recovery button
    var btnDraftRecovery = document.getElementById('btn-draft-recovery');
    if (btnDraftRecovery) {
        btnDraftRecovery.addEventListener('click', function () {
            document.getElementById('dlg-draft-compare')?.show();
        });
    }

    var protPass = null; // held in memory only after a manual gate unlock; never persisted

    // True while the user is filling the "protect on create" section of a NEW article.
    function isCreateProtected() {
        var cp = document.getElementById('create-pass');
        var on = !!(cp && cp.value);
        // The user may have typed plaintext (auto-drafted to localStorage) BEFORE deciding to protect.
        // The moment a password appears, scrub any existing plaintext draft so it can't linger on disk.
        if (on) { try { localStorage.removeItem(draftKey); } catch (e) {} }
        return on;
    }
    var imageCounter = 0;
    // Count existing images in content to continue numbering
    var existingImages = (document.getElementById('md-editor')?.value || '').match(/!\[image-\d+\]/g);
    if (existingImages) {
        existingImages.forEach(function (m) {
            var n = parseInt(m.match(/\d+/)[0], 10);
            if (n >= imageCounter) imageCounter = n + 1;
        });
    }

    function uploadImage(file) {
        var formData = new FormData();
        formData.append('file', file);
        var artId = document.querySelector('input[name="id"]');
        if (artId && artId.value) formData.append('articleId', artId.value);

        return fetch('/api-proxy/media/upload', {
            method: 'POST',
            body: formData
        })
        .then(function (r) {
            if (!r.ok) return r.json().then(function (e) { throw new Error(e.error || 'Upload failed'); });
            return r.json();
        });
    }

    function insertImageAtCursor(cm, file) {
        var name = file.name || 'image';
        // Validate size
        if (file.size > 5 * 1024 * 1024) {
            showToast('File size exceeds 5 MB limit.'); return;
        }
        var allowed = ['image/png', 'image/jpeg', 'image/gif', 'image/webp', 'image/svg+xml'];
        if (allowed.indexOf(file.type) < 0) {
            showToast('File type ' + file.type + ' is not allowed.'); return;
        }

        var placeholder = '![Uploading ' + name + '...]()';
        var cursor = cm.getCursor();
        cm.replaceRange(placeholder + '\n', cursor);

        uploadImage(file).then(function (data) {
            imageCounter++;
            var altText = 'image-' + String(imageCounter).padStart(3, '0');
            var md = '![' + altText + '](/api/media/' + data.id + ')';
            // Find and replace placeholder
            var content = cm.getValue();
            var idx = content.indexOf(placeholder);
            if (idx >= 0) {
                var from = cm.posFromIndex(idx);
                var to = cm.posFromIndex(idx + placeholder.length);
                cm.replaceRange(md, from, to);
            }
        }).catch(function (err) {
            // Remove placeholder on error
            var content = cm.getValue();
            var idx = content.indexOf(placeholder);
            if (idx >= 0) {
                var from = cm.posFromIndex(idx);
                var to = cm.posFromIndex(idx + placeholder.length + 1);
                cm.replaceRange('', from, to);
            }
            showToast(err.message || 'Upload failed');
        });
    }

    function showToast(msg) {
        var alert = Object.assign(document.createElement('sl-alert'), {
            variant: 'danger', closable: true, duration: 5000
        });
        alert.innerHTML = '<sl-icon name="exclamation-triangle" slot="icon"></sl-icon>';
        var span = document.createElement('span');
        span.textContent = msg;
        alert.appendChild(span);
        document.body.append(alert);
        alert.toast();
    }

    var mdEditorEl = document.getElementById('md-editor');
    if (!mdEditorEl) return;

    var easyMDE = new EasyMDE({
        element: mdEditorEl,
        spellChecker: false,
        autoDownloadFontAwesome: false,
        autofocus: false,
        status: false,
        minHeight: '350px',
        viewportMargin: Infinity,
        toolbar: [
            'bold', 'italic', 'heading', '|',
            'code', 'quote', 'unordered-list', 'ordered-list', '|',
            'link', {
                name: 'upload-image',
                action: function (editor) {
                    var input = document.createElement('input');
                    input.type = 'file';
                    input.accept = 'image/png,image/jpeg,image/gif,image/webp,image/svg+xml';
                    input.onchange = function () {
                        if (input.files[0]) insertImageAtCursor(editor.codemirror, input.files[0]);
                    };
                    input.click();
                },
                className: 'fa fa-image',
                title: 'Upload Image'
            }, 'table', '|',
            'preview', 'side-by-side', 'fullscreen', '|',
            'guide'
        ],
        previewRender: function (text) {
            var html = window.bmbRenderMarkdown ? window.bmbRenderMarkdown(text) : text;
            return html.replace(/src="\/api\/media\//g, 'src="/api-proxy/media/');
        }
    });

    requestAnimationFrame(function () { easyMDE.codemirror.refresh(); });
    if (document.fonts && document.fonts.ready) {
        document.fonts.ready.then(function () { easyMDE.codemirror.refresh(); });
    }
    if (typeof ResizeObserver !== 'undefined') {
        var mdeWrapper = easyMDE.codemirror.getWrapperElement().closest('.EasyMDEContainer') || easyMDE.codemirror.getWrapperElement();
        new ResizeObserver(function () { easyMDE.codemirror.refresh(); }).observe(mdeWrapper);
    }

    // Swap every FontAwesome <i> EasyMDE built in the toolbar for a local <sl-icon>
    (function replaceToolbarIcons() {
        var faToBi = {
            'fa-bold': 'type-bold',
            'fa-italic': 'type-italic',
            'fa-header': 'type-h1',
            'fa-code': 'code-slash',
            'fa-quote-left': 'blockquote-left',
            'fa-list-ul': 'list-ul',
            'fa-list-ol': 'list-ol',
            'fa-link': 'link-45deg',
            'fa-image': 'image',
            'fa-table': 'table',
            'fa-eye': 'eye',
            'fa-columns': 'layout-split',
            'fa-arrows-alt': 'arrows-fullscreen',
            'fa-question-circle': 'question-circle'
        };
        var toolbar = document.querySelector('.editor-toolbar');
        if (!toolbar) return;
        toolbar.querySelectorAll('i').forEach(function (iEl) {
            var mapped = null;
            iEl.classList.forEach(function (cls) {
                if (faToBi.hasOwnProperty(cls)) mapped = faToBi[cls];
            });
            if (!mapped) return;
            var icon = document.createElement('sl-icon');
            icon.setAttribute('name', mapped);
            icon.setAttribute('class', 'bmb-mde-icon');
            iEl.replaceWith(icon);
        });
    })();

    // Handle paste
    easyMDE.codemirror.on('paste', function (cm, e) {
        var items = e.clipboardData && e.clipboardData.items;
        if (!items) return;
        for (var i = 0; i < items.length; i++) {
            if (items[i].type.indexOf('image/') === 0) {
                e.preventDefault();
                var file = items[i].getAsFile();
                if (file) insertImageAtCursor(cm, file);
                return;
            }
        }
    });

    var attachDroppedFiles = null;
    easyMDE.codemirror.on('drop', function (cm, e) {
        var files = e.dataTransfer && e.dataTransfer.files;
        if (!files || !files.length) return;
        var others = [];
        for (var i = 0; i < files.length; i++) {
            if (files[i].type.indexOf('image/') === 0) {
                e.preventDefault();
                insertImageAtCursor(cm, files[i]);
            } else {
                others.push(files[i]);
            }
        }
        if (others.length && attachDroppedFiles) {
            e.preventDefault();
            attachDroppedFiles(others);
        }
    });

    var conceptTagsInput = document.querySelector('#concept-tags-input');
    var conceptTagify = null;
    if (conceptTagsInput && typeof Tagify !== 'undefined') {
        conceptTagify = new Tagify(conceptTagsInput, {
            originalInputValueFormat: function (valuesArr) { return valuesArr.map(function (item) { return item.value; }).join(', '); }
        });
    }

    var form = document.getElementById('edit-form');
    var titleInput = form.querySelector('sl-input[name="title"]');
    var articleId = form.querySelector('input[name="id"]').value;
    var draftKey = articleId ? 'draft-article-' + articleId : 'draft-article-new-' + pageTreePath;

    // Clickable breadcrumb under the title
    (function initBreadcrumb(attemptsLeft) {
        if (typeof window.bmbFolderPicker !== 'function') {
            if (attemptsLeft <= 0) return;
            setTimeout(function () { initBreadcrumb(attemptsLeft - 1); }, 50);
            return;
        }
        var display = document.getElementById('article-treepath-display');
        var pathText = document.getElementById('article-treepath-text');
        var hiddenInput = form.querySelector('input[name="treePath"]');
        var dlg = document.getElementById('dlg-edit-treepath');
        var pickerRoot = document.getElementById('edit-treepath-picker');
        var applyBtn = document.getElementById('btn-confirm-edit-treepath');
        if (!display || !dlg) return;

        var pickerApi = window.bmbFolderPicker({
            rootEl: pickerRoot,
            initialPath: hiddenInput.value || '/'
        });

        function openDlg() {
            pickerApi.setPath(hiddenInput.value || '/');
            dlg.show();
        }

        dlg.addEventListener('sl-after-show', function () { pickerApi.focus(); });
        display.addEventListener('click', openDlg);
        display.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openDlg(); }
        });

        applyBtn.addEventListener('click', function () {
            var p = pickerApi.getPath() || '/';
            hiddenInput.value = p;
            pathText.textContent = p;
            dlg.hide();
        });
    })(40);

    var isDirty = false;

    function saveDraft() {
        if (isProtected || isCreateProtected()) return;
        var currentTitle = titleInput ? titleInput.value : '';
        var currentContent = easyMDE.value();
        var currentConceptTags = conceptTagify ? conceptTagify.value.map(function (t) { return t.value; }).join(',') : '';
        if (currentTitle === originalTitle && currentContent === originalContent) {
            localStorage.removeItem(draftKey);
            isDirty = false;
            return;
        }
        localStorage.setItem(draftKey, JSON.stringify({
            title: currentTitle, content: currentContent, conceptTags: currentConceptTags,
            savedAt: new Date().toISOString()
        }));
        isDirty = false;
    }

    function checkForDraft() {
        if (isProtected) return;
        var savedDraft = localStorage.getItem(draftKey);
        if (!savedDraft) return;
        var draft = JSON.parse(savedDraft);
        var btn = document.getElementById('btn-draft-recovery');
        if (btn) btn.style.display = 'flex';

        btn?.addEventListener('click', function () {
            var curContent = originalContent;
            var curPanel = document.getElementById('draft-current-panel');
            var savedPanel = document.getElementById('draft-saved-panel');
            var savedLabel = document.getElementById('draft-saved-label');
            if (curPanel && typeof window.bmbRenderMarkdown === 'function') {
                curPanel.innerHTML = window.bmbRenderMarkdown(curContent);
            }
            if (savedPanel && typeof window.bmbRenderMarkdown === 'function') {
                savedPanel.innerHTML = window.bmbRenderMarkdown(draft.content || '');
            }
            if (savedLabel) {
                savedLabel.textContent = 'Draft from ' + new Date(draft.savedAt).toLocaleString();
            }
        });

        document.getElementById('btn-draft-pick-current')?.addEventListener('click', function () {
            localStorage.removeItem(draftKey);
            if (btn) btn.style.display = 'none';
            document.getElementById('dlg-draft-compare')?.hide();
        });
        document.getElementById('btn-draft-cancel')?.addEventListener('click', function () {
            document.getElementById('dlg-draft-compare')?.hide();
        });
        document.getElementById('btn-draft-pick-draft')?.addEventListener('click', function () {
            if (titleInput) titleInput.value = draft.title;
            easyMDE.value(draft.content);
            var draftConcepts = draft.conceptTags || '';
            if (conceptTagify && draftConcepts) conceptTagify.loadOriginalValues(draftConcepts);
            localStorage.removeItem(draftKey);
            if (btn) btn.style.display = 'none';
            document.getElementById('dlg-draft-compare')?.hide();
        });
    }

    easyMDE.codemirror.on('change', function () { isDirty = true; });
    titleInput?.addEventListener('sl-input', function () { isDirty = true; });
    if (conceptTagify) conceptTagify.on('change', function () { isDirty = true; });

    setInterval(function () { if (isDirty) saveDraft(); }, 3000);

    form.addEventListener('submit', function () {
        isDirty = false;
        localStorage.removeItem(draftKey);
    });

    form.querySelectorAll('sl-button[href]').forEach(function (b) {
        b.addEventListener('click', function () {
            isDirty = false;
            localStorage.removeItem(draftKey);
        });
    });

    window.addEventListener('beforeunload', function (e) {
        if (isDirty) { e.preventDefault(); e.returnValue = ''; }
    }, pageOpts);

    document.addEventListener('keydown', function (e) {
        if ((e.ctrlKey || e.metaKey) && e.key === 's') {
            e.preventDefault();
            form.requestSubmit();
        }
    }, pageOpts);

    // Existing protected article
    if (isProtected) {
        var protGate = document.getElementById('prot-gate');
        var protPassInput = document.getElementById('prot-pass');
        var protUnlockBtn = document.getElementById('prot-unlock');
        var protError = document.getElementById('prot-error');
        var contentBlock = document.getElementById('content-block');
        var reauthPending = false;

        if (isLocked) easyMDE.codemirror.refresh();

        function protUnlock() {
            var pass = protPassInput.value || '';
            if (!pass) { protPassInput.focus(); return; }
            protError.style.display = 'none';
            protUnlockBtn.loading = true;
            fetch('/api-proxy/article/' + articleId + '/unlock', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ passphrase: pass })
            }).then(function (r) {
                if (r.ok) return r.json();
                return r.json().catch(function () { return {}; }).then(function (j) {
                    throw new Error(r.status === 401 ? 'Wrong password.' : (j.error || 'Unlock failed.'));
                });
            }).then(function (data) {
                protPass = pass;
                if (!reauthPending) {
                    easyMDE.value(data.content || '');
                    originalContent = data.content || '';
                }
                protPassInput.value = '';
                if (protGate) protGate.hidden = true;
                if (contentBlock) contentBlock.hidden = false;
                easyMDE.codemirror.refresh();
                if (reauthPending) { reauthPending = false; doProtectedSave(); }
            }).catch(function (err) {
                protError.textContent = err.message;
                protError.style.display = '';
                protPassInput.focus();
            }).finally(function () {
                protUnlockBtn.loading = false;
            });
        }
        if (protUnlockBtn) protUnlockBtn.addEventListener('click', protUnlock);
        if (protPassInput) protPassInput.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { e.preventDefault(); protUnlock(); }
        });

        function doProtectedSave() {
            var payload = {
                title: titleInput.value,
                treePath: form.querySelector('input[name="treePath"]').value,
                content: easyMDE.value()
            };
            if (protPass) payload.passphrase = protPass;
            fetch('/api-proxy/article/' + articleId, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            }).then(function (r) {
                if (!r.ok) {
                    if (r.status === 401) protPass = null;
                    return r.json().catch(function () { return {}; }).then(function (j) {
                        // Only this code means the server-side unlock expired; any other 400 is a
                        // validation error (e.g. a blank title) and is shown as is.
                        if (r.status === 400 && j.code === 'PassphraseRequired') {
                            reauthPending = true;
                            if (protGate) protGate.hidden = false;
                            protError.textContent = 'Your unlock expired — re-enter the password to save.';
                            protError.style.display = '';
                            protPassInput.focus();
                            return 'reauth';
                        }
                        throw new Error(r.status === 401 ? 'Wrong password — re-enter to save.' : (j.error || 'Save failed.'));
                    });
                }
                var tags = conceptTagify ? conceptTagify.value.map(function (t) { return t.value; }) : [];
                return fetch('/api-proxy/article/' + articleId + '/concept-tags', {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ conceptTags: tags })
                }).catch(function () { }).then(function () {
                    window.location.href = '/Article/View?id=' + articleId;
                });
            }).catch(function (err) {
                showToast(err.message);
            });
        }

        form.addEventListener('submit', function (e) {
            e.preventDefault();
            if (protGate && !protGate.hidden && !protPass) {
                protError.textContent = 'Unlock the article first.';
                protError.style.display = '';
                protPassInput.focus();
                return;
            }
            doProtectedSave();
        });
    }

    // Attachments
    var attachmentsList = document.getElementById('attachments-list');
    var pendingInput = document.getElementById('pending-attachments');
    var pendingAttachments = [];
    if (pendingInput) {
        try { pendingAttachments = JSON.parse(pendingInput.value || '[]') || []; } catch (e) { pendingAttachments = []; }
    }

    function escHtml(s) {
        return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }
    function formatFileSize(bytes) {
        var units = ['B', 'KB', 'MB', 'GB'];
        var size = bytes, i = 0;
        while (size >= 1024 && i < units.length - 1) { size /= 1024; i++; }
        return (i === 0 ? size.toFixed(0) : size.toFixed(1)) + ' ' + units[i];
    }
    function updateAttachmentsCount() {
        var countEl = document.getElementById('attachments-count');
        if (countEl && attachmentsList) countEl.textContent = attachmentsList.querySelectorAll('.attachment-item').length;
    }
    function syncPending() {
        if (pendingInput) pendingInput.value = JSON.stringify(pendingAttachments);
    }
    function appendAttachmentRow(att) {
        var div = document.createElement('div');
        div.className = 'attachment-item';
        div.id = 'attachment-' + att.id;
        div.style.cssText = 'display:flex;align-items:center;justify-content:space-between;padding:8px 0;border-bottom:1px solid var(--sl-color-neutral-200);';
        div.innerHTML =
            '<a href="/api-proxy/media/' + escHtml(att.id) + '" download="' + escHtml(att.fileName) + '" style="display:flex;align-items:center;gap:8px;flex:1;min-width:0;">' +
            '<sl-icon name="file-earmark"></sl-icon>' +
            '<span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">' + escHtml(att.fileName) + '</span>' +
            '<span style="font-size:0.78rem;color:var(--sl-color-neutral-500);flex-shrink:0;">(' + formatFileSize(att.fileSize) + ')</span>' +
            '</a>' +
            '<sl-icon-button name="trash" label="Delete" style="font-size:0.9rem;flex-shrink:0;" data-delete-attachment="' + escHtml(att.id) + '"></sl-icon-button>';
        attachmentsList.appendChild(div);
        updateAttachmentsCount();
    }

    if (attachmentsList) {
        pendingAttachments.forEach(appendAttachmentRow);

        var attachmentFileInput = document.getElementById('attachment-file-input');
        var dropzone = document.getElementById('attachment-dropzone');
        var dropzoneText = document.getElementById('attachment-dropzone-text');
        var dropzoneIdleText = dropzoneText ? dropzoneText.textContent : '';

        function uploadAttachment(file) {
            var formData = new FormData();
            formData.append('file', file);
            formData.append('attachment', 'true');
            if (articleId) formData.append('articleId', articleId);
            return fetch('/api-proxy/media/upload', { method: 'POST', body: formData })
                .then(function (r) {
                    if (!r.ok) return r.json().catch(function () { return {}; }).then(function (e) {
                        throw new Error((e.error || 'Upload failed') + ' (' + file.name + ')');
                    });
                    return r.json();
                })
                .then(function (media) {
                    appendAttachmentRow(media);
                    if (pendingInput) {
                        pendingAttachments.push({ id: media.id, fileName: media.fileName, fileSize: media.fileSize });
                        syncPending();
                    }
                });
        }

        var uploadQueue = Promise.resolve();
        var queued = 0;
        function setBusyText() {
            if (dropzoneText) dropzoneText.textContent = 'Uploading ' + queued + (queued === 1 ? ' file…' : ' files…');
        }

        function queueAttachmentUploads(files) {
            if (dropzone && dropzone.getAttribute('aria-disabled') === 'true') {
                showToast('A password-protected article cannot have attachments.');
                return;
            }
            files.forEach(function (f) {
                queued++;
                if (dropzone) dropzone.classList.add('is-busy');
                setBusyText();
                uploadQueue = uploadQueue.then(function () {
                    return uploadAttachment(f).catch(function (err) { showToast(err.message || 'Upload failed'); });
                }).then(function () {
                    queued--;
                    if (queued > 0) { setBusyText(); return; }
                    if (dropzone) dropzone.classList.remove('is-busy');
                    if (dropzoneText) dropzoneText.textContent = dropzoneIdleText;
                });
            });
        }
        if (typeof window.bmbAttachDropZone === 'function' && dropzone && attachmentFileInput) {
            window.bmbAttachDropZone(dropzone, attachmentFileInput, queueAttachmentUploads);
        }
        attachDroppedFiles = queueAttachmentUploads;

        form.addEventListener('submit', function (e) {
            if (queued === 0) return;
            e.preventDefault();
            e.stopImmediatePropagation();
            showToast('Files are still uploading — save again once they finish.');
        }, true);

        attachmentsList.addEventListener('click', function (e) {
            var btn = e.target.closest('[data-delete-attachment]');
            if (!btn) return;
            e.preventDefault();
            var id = btn.getAttribute('data-delete-attachment');
            if (!confirm('Delete this attachment?')) return;
            fetch('/api-proxy/media/' + id, { method: 'DELETE' }).then(function (r) {
                if (!(r.ok || r.status === 204)) { showToast('Delete failed'); return; }
                var row = document.getElementById('attachment-' + id);
                if (row) row.remove();
                pendingAttachments = pendingAttachments.filter(function (p) { return p.id !== id; });
                syncPending();
                updateAttachmentsCount();
            });
        });
    }

    if (isNew) {
        var createPassEl = document.getElementById('create-pass');
        if (createPassEl) {
            createPassEl.addEventListener('sl-input', function () {
                if (createPassEl.value) { try { localStorage.removeItem(draftKey); } catch (e) {} }
                var note = document.getElementById('attachments-protect-note');
                var zone = document.getElementById('attachment-dropzone');
                if (note) note.style.display = createPassEl.value ? '' : 'none';
                if (zone) zone.setAttribute('aria-disabled', createPassEl.value ? 'true' : 'false');
            });
        }
        form.addEventListener('submit', function (e) {
            var cp = document.getElementById('create-pass');
            if (!cp || !cp.value) return;
            var cp2 = document.getElementById('create-pass2');
            var err = document.getElementById('create-prot-error');
            var details = document.getElementById('protect-on-create');
            function fail(msg) {
                e.preventDefault();
                if (err) { err.textContent = msg; err.style.display = ''; }
                if (details) details.open = true;
            }
            if (pendingAttachments.length > 0)
                return fail('A password-protected article can\'t have attachments. Remove the files or clear the password.');
            if (cp.value.length < 4) return fail('Password must be at least 4 characters.');
            if (cp.value !== (cp2 ? cp2.value : '')) return fail('Passwords do not match.');
        });
    }

    checkForDraft();
})();
