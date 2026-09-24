(function () {
    var dataEl = document.getElementById('article-view-data');
    var pageData = dataEl ? JSON.parse(dataEl.textContent || '{}') : {};
    var articleId = pageData.articleId || '';
    var content = pageData.content || '';
    var treePath = pageData.treePath || '/';
    var isProtected = !!pageData.isProtected;
    var isUnlockedFromCache = !!pageData.isUnlockedFromCache;
    var unlockExpiresInSeconds = pageData.unlockExpiresInSeconds != null ? Number(pageData.unlockExpiresInSeconds) : null;
    var articleTitle = pageData.articleTitle || '';
    var articleTreePath = pageData.articleTreePath || '/';

    // Dialog opening buttons
    document.getElementById('btn-open-protect')?.addEventListener('click', function () {
        document.getElementById('dlg-protect')?.show();
    });
    document.getElementById('btn-open-copy-article')?.addEventListener('click', function () {
        document.getElementById('dlg-copy-article')?.show();
    });
    document.getElementById('btn-open-move-article')?.addEventListener('click', function () {
        document.getElementById('dlg-move-article')?.show();
    });
    document.getElementById('btn-open-delete-article')?.addEventListener('click', function () {
        document.getElementById('dlg-delete-article')?.show();
    });
    document.getElementById('btn-export-article')?.addEventListener('click', function () {
        if (typeof window.openDownloadDialog === 'function') {
            window.openDownloadDialog({ kind: 'article', id: articleId });
        }
    });
    document.querySelectorAll('.btn-open-change-pass').forEach(function (b) {
        b.addEventListener('click', function () { document.getElementById('dlg-change-pass')?.show(); });
    });
    document.querySelectorAll('.btn-open-unprotect').forEach(function (b) {
        b.addEventListener('click', function () { document.getElementById('dlg-unprotect')?.show(); });
    });
    document.querySelectorAll('[data-dlg-cancel]').forEach(function (b) {
        b.addEventListener('click', function () {
            var dlgId = b.getAttribute('data-dlg-cancel');
            document.getElementById(dlgId)?.hide();
        });
    });

    // Delegated actions for comments & attachments
    document.addEventListener('click', function (e) {
        var commentBtn = e.target.closest('.btn-delete-comment');
        if (commentBtn) {
            e.preventDefault();
            var cId = commentBtn.getAttribute('data-comment-id');
            if (cId) deleteComment(cId);
            return;
        }
        var attBtn = e.target.closest('.btn-delete-attachment');
        if (attBtn) {
            e.preventDefault();
            var aId = attBtn.getAttribute('data-attachment-id');
            if (aId) deleteAttachment(aId);
            return;
        }
    });

    function escHtml(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function renderMarkdown(md) {
        var body = document.getElementById('article-body');
        if (!body) return;
        var renderer = new marked.Renderer();
        var origImage = renderer.image.bind(renderer);
        renderer.image = function (token) {
            if (token && token.href && token.href.startsWith('/api/media/')) {
                token.href = '/api-proxy/media/' + token.href.substring('/api/media/'.length);
            }
            return origImage(token);
        };
        if (typeof window.bmbRenderMarkdown === 'function') {
            body.innerHTML = window.bmbRenderMarkdown(md, { renderer: renderer });
        } else {
            body.innerHTML = DOMPurify.sanitize(marked.parse(md, { breaks: true, renderer: renderer }));
        }
    }

    if (content && (!isProtected || isUnlockedFromCache)) {
        renderMarkdown(content);
    }

    // ── Protected article: unlock / re-lock ──
    if (isProtected) {
        var lockCard = document.getElementById('lock-card');
        var unlockedCard = document.getElementById('unlocked-card');
        var unlockPass = document.getElementById('unlock-pass');
        var unlockError = document.getElementById('unlock-error');
        var btnUnlock = document.getElementById('btn-unlock');
        var btnRelock = document.getElementById('btn-relock');
        var unlockBanner = document.getElementById('unlock-banner');
        var unlockRemaining = document.getElementById('unlock-remaining');
        var lnkLockNow = document.getElementById('lnk-lock-now');
        var unlockTimer = null;

        function formatRemaining(sec) {
            return sec >= 60 ? Math.ceil(sec / 60) + ' min' : sec + ' s';
        }

        function startUnlockCountdown(seconds) {
            if (unlockTimer) { clearInterval(unlockTimer); unlockTimer = null; }
            if (seconds == null) { if (unlockBanner) unlockBanner.style.display = 'none'; return; }
            var deadline = Date.now() + seconds * 1000;
            if (unlockBanner) unlockBanner.style.display = '';
            function tick() {
                var left = Math.max(0, Math.round((deadline - Date.now()) / 1000));
                if (unlockRemaining) unlockRemaining.textContent = formatRemaining(left);
                if (left <= 0) relock();
            }
            tick();
            unlockTimer = setInterval(tick, 1000);
        }

        function relock() {
            if (unlockTimer) { clearInterval(unlockTimer); unlockTimer = null; }
            fetch('/api-proxy/article/' + articleId + '/relock', { method: 'POST' });
            var body = document.getElementById('article-body');
            if (body) body.innerHTML = '';
            if (unlockBanner) unlockBanner.style.display = 'none';
            if (unlockedCard) unlockedCard.style.display = 'none';
            if (lockCard) lockCard.style.display = '';
            if (unlockPass) unlockPass.focus();
        }

        function doUnlock() {
            var pass = unlockPass ? (unlockPass.value || '') : '';
            if (!pass) { unlockPass?.focus(); return; }
            if (unlockError) unlockError.style.display = 'none';
            if (btnUnlock) btnUnlock.loading = true;
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
                renderMarkdown(data.content || '');
                if (unlockPass) unlockPass.value = '';
                if (lockCard) lockCard.style.display = 'none';
                if (unlockedCard) unlockedCard.style.display = '';
                startUnlockCountdown(data.unlockExpiresInSeconds);
            }).catch(function (err) {
                if (unlockError) {
                    unlockError.textContent = err.message;
                    unlockError.style.display = '';
                }
                unlockPass?.focus();
            }).finally(function () {
                if (btnUnlock) btnUnlock.loading = false;
            });
        }

        if (btnUnlock) btnUnlock.addEventListener('click', doUnlock);
        if (unlockPass) unlockPass.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { e.preventDefault(); doUnlock(); }
        });
        if (btnRelock) btnRelock.addEventListener('click', relock);
        if (lnkLockNow) lnkLockNow.addEventListener('click', function (e) { e.preventDefault(); relock(); });
        if (isUnlockedFromCache) startUnlockCountdown(unlockExpiresInSeconds);
    }

    // ── Protect / change password / remove protection ──
    function postProtection(url, payload, errEl, btn) {
        if (errEl) errEl.style.display = 'none';
        btn.loading = true;
        return fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        }).then(function (r) {
            if (r.ok) { location.reload(); return; }
            return r.json().catch(function () { return {}; }).then(function (j) {
                throw new Error(r.status === 401 ? 'Wrong password.' : (j.error || 'Operation failed.'));
            });
        }).catch(function (err) {
            if (errEl) {
                errEl.textContent = err.message;
                errEl.style.display = '';
            }
        }).finally(function () {
            btn.loading = false;
        });
    }

    var btnProtect = document.getElementById('btn-protect');
    if (btnProtect) {
        btnProtect.addEventListener('click', function () {
            var p1 = document.getElementById('protect-pass').value || '';
            var p2 = document.getElementById('protect-pass2').value || '';
            var hint = document.getElementById('protect-hint').value || '';
            var err = document.getElementById('protect-error');
            if (p1.length < 4) { if (err) { err.textContent = 'Password must be at least 4 characters.'; err.style.display = ''; } return; }
            if (p1 !== p2) { if (err) { err.textContent = 'Passwords do not match.'; err.style.display = ''; } return; }
            postProtection('/api-proxy/article/' + articleId + '/protect', { passphrase: p1, hint: hint }, err, btnProtect);
        });
    }

    var btnChangePass = document.getElementById('btn-change-pass');
    if (btnChangePass) {
        btnChangePass.addEventListener('click', function () {
            var oldp = document.getElementById('cp-old').value || '';
            var n1 = document.getElementById('cp-new').value || '';
            var n2 = document.getElementById('cp-new2').value || '';
            var hint = document.getElementById('cp-hint').value || '';
            var err = document.getElementById('cp-error');
            if (n1.length < 4) { if (err) { err.textContent = 'New password must be at least 4 characters.'; err.style.display = ''; } return; }
            if (n1 !== n2) { if (err) { err.textContent = 'New passwords do not match.'; err.style.display = ''; } return; }
            postProtection('/api-proxy/article/' + articleId + '/change-passphrase', { oldPassphrase: oldp, newPassphrase: n1, hint: hint }, err, btnChangePass);
        });
    }

    var btnUnprotect = document.getElementById('btn-unprotect');
    if (btnUnprotect) {
        btnUnprotect.addEventListener('click', function () {
            var pass = document.getElementById('up-pass').value || '';
            var err = document.getElementById('up-error');
            postProtection('/api-proxy/article/' + articleId + '/unprotect', { passphrase: pass }, err, btnUnprotect);
        });
    }

    // Move article
    var moveArticleSelectedPath = null;
    var moveArticleSearchTimer = null;
    var moveArticleSearch = document.getElementById('move-article-search');
    var moveArticleDropdown = document.getElementById('move-article-dropdown');

    function escapeHtmlMove(text) {
        return String(text == null ? '' : text)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    if (moveArticleSearch) {
        moveArticleSearch.addEventListener('sl-input', function () {
            moveArticleSelectedPath = null;
            clearTimeout(moveArticleSearchTimer);
            var q = moveArticleSearch.value.trim();
            if (q.length < 1) { moveArticleDropdown.style.display = 'none'; return; }
            moveArticleSearchTimer = setTimeout(function () {
                fetch('/api-proxy/folders/search?q=' + encodeURIComponent(q) + '&limit=12')
                    .then(function (r) { return r.json(); })
                    .then(function (data) {
                        var folders = data.folders || [];
                        if (folders.length === 0) {
                            moveArticleDropdown.innerHTML = '<div style="padding:8px 12px;color:var(--sl-color-neutral-500);font-size:0.9rem;">No matching folders</div>';
                        } else {
                            moveArticleDropdown.innerHTML = folders.map(function (f) {
                                return '<div class="move-article-item" data-path="' + escapeHtmlMove(f.path) + '" style="padding:8px 12px;cursor:pointer;border-bottom:1px solid var(--sl-color-neutral-100);">' +
                                    '<sl-icon name="folder2" style="margin-right:6px;"></sl-icon>' + escapeHtmlMove(f.path) + '</div>';
                            }).join('');
                            if (data.hasMore) {
                                moveArticleDropdown.innerHTML += '<div style="padding:6px 12px;color:var(--sl-color-neutral-400);font-size:0.8rem;text-align:center;">Type more to narrow results...</div>';
                            }
                        }
                        moveArticleDropdown.style.display = 'block';
                    });
            }, 250);
        });

        moveArticleDropdown.addEventListener('click', function (e) {
            var item = e.target.closest('.move-article-item');
            if (!item) return;
            moveArticleSelectedPath = item.dataset.path;
            moveArticleSearch.value = moveArticleSelectedPath;
            moveArticleDropdown.style.display = 'none';
        });

        moveArticleDropdown.addEventListener('mouseover', function (e) {
            var item = e.target.closest('.move-article-item');
            if (item) item.style.background = 'var(--sl-color-primary-50)';
        });
        moveArticleDropdown.addEventListener('mouseout', function (e) {
            var item = e.target.closest('.move-article-item');
            if (item) item.style.background = '';
        });

        document.addEventListener('click', function (e) {
            if (!e.target.closest('#move-article-search') && !e.target.closest('#move-article-dropdown')) {
                moveArticleDropdown.style.display = 'none';
            }
        });
    }

    var moveBtn = document.getElementById('btn-move-article');
    if (moveBtn) {
        moveBtn.addEventListener('click', function () {
            var inputVal = moveArticleSearch ? moveArticleSearch.value.trim() : '';
            if (inputVal && !moveArticleSelectedPath) {
                alert('Please select a folder from the search results.');
                return;
            }
            var newPath = moveArticleSelectedPath || '/';
            if (newPath === treePath) { alert('Article is already in that folder'); return; }
            moveBtn.loading = true;
            fetch('/api-proxy/article/' + articleId + '/move', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ newPath: newPath })
            }).then(function (r) {
                if (r.ok) window.location.reload();
                else r.json().then(function (e) { alert(e.error || 'Move failed'); }).catch(function () { alert('Move failed'); });
            }).catch(function () { alert('Error'); }).finally(function () { moveBtn.loading = false; });
        });
    }

    // Delete
    var deleteBtn = document.getElementById('btn-confirm-delete-article');
    if (deleteBtn) {
        deleteBtn.addEventListener('click', function () {
            deleteBtn.loading = true;
            fetch('/api-proxy/article/' + articleId, {
                method: 'DELETE'
            }).then(function (r) {
                if (r.ok) window.location.href = '/Folder?path=' + encodeURIComponent(treePath);
                else r.json().then(function (d) { alert(d.error || 'Delete failed'); }).catch(function () { alert('Delete failed'); });
            }).catch(function () { alert('Error'); }).finally(function () { deleteBtn.loading = false; });
        });
    }

    // Comments
    var addCommentBtn = document.getElementById('btn-add-comment');
    if (addCommentBtn) {
        addCommentBtn.addEventListener('click', function () {
            var textarea = document.getElementById('new-comment-text');
            var text = textarea.value.trim();
            if (!text) return;
            addCommentBtn.loading = true;
            fetch('/api-proxy/comments', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ articleId: articleId, text: text })
            }).then(function (r) {
                if (r.ok) return r.json();
                throw new Error('Failed');
            }).then(function (comment) {
                textarea.value = '';
                var list = document.getElementById('comments-list');
                var div = document.createElement('div');
                div.className = 'comment-item';
                div.id = 'comment-' + comment.id;
                var d = new Date(comment.createdAt);
                div.innerHTML =
                    '<div style="display:flex;align-items:center;justify-content:space-between;">' +
                    '<div><span class="comment-author">owner</span>' +
                    '<span class="comment-date">' + d.toLocaleString() + '</span></div>' +
                    '<sl-icon-button name="trash" label="Delete" class="btn-delete-comment" data-comment-id="' + comment.id + '" style="font-size:0.9rem;"></sl-icon-button>' +
                    '</div>' +
                    '<p class="comment-text">' + escHtml(comment.text) + '</p>';
                list.appendChild(div);
                var h4 = list.closest('.card')?.querySelector('h4');
                if (h4) {
                    var cnt = list.querySelectorAll('.comment-item').length;
                    h4.innerHTML = '<sl-icon name="chat-dots"></sl-icon> Comments (' + cnt + ')';
                }
            }).catch(function () { alert('Error adding comment'); })
            .finally(function () { addCommentBtn.loading = false; });
        });
    }

    function deleteComment(id) {
        if (!confirm('Delete this comment?')) return;
        fetch('/api-proxy/comments/' + id, { method: 'DELETE' })
            .then(function (r) {
                if (r.ok || r.status === 204) {
                    var el = document.getElementById('comment-' + id);
                    if (el) el.remove();
                    var list = document.getElementById('comments-list');
                    var h4 = list?.closest('.card')?.querySelector('h4');
                    if (h4 && list) {
                        var cnt = list.querySelectorAll('.comment-item').length;
                        h4.innerHTML = '<sl-icon name="chat-dots"></sl-icon> Comments (' + cnt + ')';
                    }
                } else { alert('Delete failed'); }
            });
    }
    window.deleteComment = deleteComment;

    // Attachments
    function formatFileSize(bytes) {
        var units = ['B', 'KB', 'MB', 'GB'];
        var size = bytes, i = 0;
        while (size >= 1024 && i < units.length - 1) { size /= 1024; i++; }
        return (i === 0 ? size.toFixed(0) : size.toFixed(1)) + ' ' + units[i];
    }

    function attachmentsListEl() { return document.getElementById('attachments-list'); }

    function updateAttachmentsCount() {
        var list = attachmentsListEl();
        if (!list) return;
        var countEl = document.getElementById('attachments-count');
        if (countEl) countEl.textContent = list.querySelectorAll('.attachment-item').length;
    }

    function appendAttachmentRow(att) {
        var list = attachmentsListEl();
        if (!list) return;
        var div = document.createElement('div');
        div.className = 'attachment-item';
        div.id = 'attachment-' + att.id;
        div.style.cssText = 'display:flex;align-items:center;justify-content:space-between;padding:8px 0;border-bottom:1px solid var(--sl-color-neutral-200);';
        div.innerHTML =
            '<a href="/api-proxy/media/' + att.id + '" download="' + escHtml(att.fileName) + '" style="display:flex;align-items:center;gap:8px;flex:1;min-width:0;">' +
            '<sl-icon name="file-earmark"></sl-icon>' +
            '<span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">' + escHtml(att.fileName) + '</span>' +
            '<span style="font-size:0.78rem;color:var(--sl-color-neutral-500);flex-shrink:0;">(' + formatFileSize(att.fileSize) + ')</span>' +
            '</a>' +
            '<sl-icon-button name="trash" label="Delete" class="btn-delete-attachment" data-attachment-id="' + escHtml(att.id) + '" style="font-size:0.9rem;flex-shrink:0;"></sl-icon-button>';
        list.appendChild(div);
        updateAttachmentsCount();
    }

    var attachmentFileInput = document.getElementById('attachment-file-input');
    var attachmentDropzone = document.getElementById('attachment-dropzone');
    if (attachmentDropzone && attachmentFileInput) {
        var dropzoneText = document.getElementById('attachment-dropzone-text');
        var dropzoneIdleText = dropzoneText ? dropzoneText.textContent : '';
        var uploadQueue = Promise.resolve();
        var queued = 0;
        var failures = [];

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
                .then(function (media) { appendAttachmentRow(media); });
        }

        function setBusyText() {
            if (dropzoneText) dropzoneText.textContent = 'Uploading ' + queued + (queued === 1 ? ' file…' : ' files…');
        }

        if (typeof window.bmbAttachDropZone === 'function') {
            window.bmbAttachDropZone(attachmentDropzone, attachmentFileInput, function (files) {
                files.forEach(function (f) {
                    queued++;
                    attachmentDropzone.classList.add('is-busy');
                    setBusyText();
                    uploadQueue = uploadQueue.then(function () {
                        return uploadAttachment(f).catch(function (e) { failures.push(e.message || 'Upload failed'); });
                    }).then(function () {
                        queued--;
                        if (queued > 0) { setBusyText(); return; }
                        attachmentDropzone.classList.remove('is-busy');
                        if (dropzoneText) dropzoneText.textContent = dropzoneIdleText;
                        if (failures.length) { alert(failures.join('\n')); failures = []; }
                    });
                });
            });
        }
    }

    function deleteAttachment(id) {
        if (!confirm('Delete this attachment?')) return;
        fetch('/api-proxy/media/' + id, { method: 'DELETE' })
            .then(function (r) {
                if (r.ok || r.status === 204) {
                    var el = document.getElementById('attachment-' + id);
                    if (el) el.remove();
                    updateAttachmentsCount();
                } else { alert('Delete failed'); }
            });
    }
    window.deleteAttachment = deleteAttachment;

    // ─── Print preview ───
    var printBtn = document.getElementById('btn-print-article');
    var printDlg = document.getElementById('dlg-print-article');
    if (printBtn && printDlg) {
        var printIframe = document.getElementById('print-iframe');
        var pageStatus = document.getElementById('print-page-status');
        var zoomLabel = document.getElementById('print-zoom-label');
        var zoomIn = document.getElementById('print-zoom-in');
        var zoomOut = document.getElementById('print-zoom-out');
        var doPrintBtn = document.getElementById('btn-do-print');
        var fontSelect = document.getElementById('print-font');
        var marginsSelect = document.getElementById('print-margins');
        var sizeUp = document.getElementById('print-size-up');
        var sizeDown = document.getElementById('print-size-down');
        var sizeLabel = document.getElementById('print-size-label');
        var pageNumbersSwitch = document.getElementById('print-page-numbers');

        var settings = {
            zoom: 1.0,
            fontScale: 1.0,
            font: 'serif',
            margins: 'normal',
            pageNumbers: false
        };
        var rendered = false;
        var rebuildTimer = null;

        var FONT_STACKS = {
            serif:   'Georgia, "Times New Roman", serif',
            sans:    'Helvetica, Arial, sans-serif',
            times:   '"Times New Roman", Times, serif',
            verdana: 'Verdana, Geneva, sans-serif',
            mono:    '"Courier New", Courier, monospace'
        };
        var MARGIN_VALUES = {
            narrow: '12mm 12mm',
            normal: '20mm 18mm',
            wide:   '28mm 25mm'
        };

        function escAttr(s) {
            return String(s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
        }

        function printCss() {
            var fontFamily = FONT_STACKS[settings.font] || FONT_STACKS.serif;
            var margins = MARGIN_VALUES[settings.margins] || MARGIN_VALUES.normal;
            var scale = settings.fontScale;
            var s = function (pt) { return 'calc(' + pt + 'pt * ' + scale + ')'; };
            var pageNumbersRule = settings.pageNumbers
                ? '@page { @bottom-center { content: counter(page) " / " counter(pages); font-family: ' + fontFamily + '; font-size: 9pt; color: #666; } }'
                : '';
            return [
                '@page { size: A4; margin: ' + margins + '; }',
                pageNumbersRule,
                'html, body { margin: 0; padding: 0; }',
                'html { background: #e5e7eb; }',
                'body { font-family: ' + fontFamily + '; color: #111; font-size: ' + s(11) + '; line-height: 1.4; background: transparent; }',
                '.print-header { margin-bottom: 10pt; padding-bottom: 5pt; border-bottom: 1pt solid #888; }',
                '.print-header h1 { margin: 0 0 3pt 0; font-size: ' + s(18) + '; }',
                '.print-meta { color: #555; font-size: ' + s(9) + '; }',
                'h1, h2, h3, h4 { color: #000; page-break-after: avoid; margin: 12pt 0 4pt 0; font-family: ' + fontFamily + '; }',
                'h1 { font-size: ' + s(16) + '; } h2 { font-size: ' + s(14) + '; } h3 { font-size: ' + s(12) + '; }',
                'p { margin: 0 0 4pt 0; orphans: 3; widows: 3; }',
                'p:empty { display: none; }',
                'br + br { display: none; }',
                'pre, code { font-family: "Courier New", monospace; font-size: ' + s(9.5) + '; }',
                'pre { background: #f4f4f4; padding: 6pt 8pt; border: 1pt solid #ddd; border-radius: 3pt; white-space: pre-wrap; word-wrap: break-word; page-break-inside: avoid; margin: 4pt 0; }',
                'code { background: #f4f4f4; padding: 0 3pt; border-radius: 2pt; }',
                'pre code { background: none; padding: 0; }',
                'blockquote { border-left: 2pt solid #888; margin: 4pt 0; padding-left: 10pt; color: #444; }',
                'ul, ol { padding-left: 22pt; margin: 2pt 0 4pt 0; }',
                'li { margin: 0; }',
                'li > p { margin: 0 0 2pt 0; }',
                'li > p:only-child { margin: 0; }',
                'img { max-width: 100%; }',
                'table { border-collapse: collapse; width: 100%; margin: 8pt 0; font-size: ' + s(10) + '; page-break-inside: avoid; }',
                'th, td { border: 1pt solid #ccc; padding: 4pt 8pt; text-align: left; }',
                'th { background: #f0f0f0; }',
                'a { color: inherit; text-decoration: underline; }',
                '.pagedjs_pages { display: block !important; padding: 24px 0 !important; background: #e5e7eb !important; min-height: 100vh !important; }',
                '.pagedjs_page { display: block !important; margin: 0 auto 24px auto !important; background: white !important; box-shadow: 0 4px 14px rgba(0,0,0,0.25) !important; border: 1px solid rgba(0,0,0,0.08) !important; }',
                '.pagedjs_page:last-child { margin-bottom: 24px !important; }',
                'html.bmb-printing, html.bmb-printing body { background: white !important; }',
                'html.bmb-printing .pagedjs_pages { padding: 0 !important; background: white !important; min-height: 0 !important; }',
                'html.bmb-printing .pagedjs_page { margin: 0 !important; box-shadow: none !important; border: 0 !important; page-break-after: always; break-after: page; page-break-inside: avoid; break-inside: avoid; }',
                'html.bmb-printing .pagedjs_page:last-child { page-break-after: auto; break-after: auto; }'
            ].join('\n');
        }

        function buildPrintDoc(title, bodyHtml, footerMeta) {
            return '<!doctype html><html><head><meta charset="utf-8">' +
                '<base href="/">' +
                '<title>' + escAttr(title) + '</title>' +
                '<style>' + printCss() + '</style>' +
                '<script src="/js/pages/print-bridge.js"><\/script>' +
                '<script src="/lib/pagedjs/paged.polyfill.js"><\/script>' +
                '</head><body>' +
                '<article class="print-article">' +
                '<header class="print-header"><h1>' + escAttr(title) + '</h1>' +
                '<div class="print-meta">' + escAttr(footerMeta) + '</div></header>' +
                bodyHtml +
                '</article>' +
                '</body></html>';
        }

        function applyZoom() {
            try {
                var doc = printIframe.contentDocument;
                if (!doc) return;
                var pages = doc.querySelector('.pagedjs_pages');
                if (pages) {
                    pages.style.transform = 'scale(' + settings.zoom + ')';
                    pages.style.transformOrigin = 'top center';
                }
                if (zoomLabel) zoomLabel.textContent = Math.round(settings.zoom * 100) + '%';
            } catch (e) { }
        }

        function rebuildIframe() {
            var body = document.getElementById('article-body');
            if (!body) return;
            rendered = false;
            if (pageStatus) pageStatus.textContent = 'Rendering…';
            var meta = articleTreePath + '   ·   ' + new Date().toLocaleString();
            printIframe.srcdoc = buildPrintDoc(articleTitle, body.innerHTML, meta);
        }

        function rebuildDebounced() {
            clearTimeout(rebuildTimer);
            rebuildTimer = setTimeout(rebuildIframe, 250);
        }

        window.addEventListener('message', function (e) {
            if (!e.data || e.data.type !== 'bmb-print-rendered') return;
            rendered = true;
            if (pageStatus) pageStatus.textContent = e.data.pages + (e.data.pages === 1 ? ' page' : ' pages');
            applyZoom();
        });

        printBtn.addEventListener('click', function () {
            settings.zoom = 1.0;
            if (zoomLabel) zoomLabel.textContent = '100%';
            rebuildIframe();
            printDlg.show();
        });

        zoomIn?.addEventListener('click', function () {
            if (settings.zoom < 2.0) { settings.zoom = Math.round((settings.zoom + 0.1) * 10) / 10; applyZoom(); }
        });
        zoomOut?.addEventListener('click', function () {
            if (settings.zoom > 0.4) { settings.zoom = Math.round((settings.zoom - 0.1) * 10) / 10; applyZoom(); }
        });

        function updateSizeLabel() { if (sizeLabel) sizeLabel.textContent = Math.round(settings.fontScale * 100) + '%'; }
        sizeUp?.addEventListener('click', function () {
            if (settings.fontScale < 1.6) {
                settings.fontScale = Math.round((settings.fontScale + 0.1) * 10) / 10;
                updateSizeLabel(); rebuildDebounced();
            }
        });
        sizeDown?.addEventListener('click', function () {
            if (settings.fontScale > 0.6) {
                settings.fontScale = Math.round((settings.fontScale - 0.1) * 10) / 10;
                updateSizeLabel(); rebuildDebounced();
            }
        });

        fontSelect?.addEventListener('sl-change', function () {
            settings.font = fontSelect.value;
            rebuildDebounced();
        });
        marginsSelect?.addEventListener('sl-change', function () {
            settings.margins = marginsSelect.value;
            rebuildDebounced();
        });
        pageNumbersSwitch?.addEventListener('sl-change', function () {
            settings.pageNumbers = pageNumbersSwitch.checked;
            rebuildDebounced();
        });

        function doPrint() {
            if (!rendered || !printIframe) return;
            try {
                printIframe.contentWindow.focus();
                printIframe.contentWindow.print();
            } catch (e) { alert('Print failed: ' + e.message); }
        }
        doPrintBtn?.addEventListener('click', doPrint);
    }

    // Sync delivery status toast
    (function () {
        var params = new URLSearchParams(window.location.search);
        if (!params.has('justSaved')) return;

        var url = new URL(window.location);
        url.searchParams.delete('justSaved');
        history.replaceState(null, '', url);

        var toast = document.getElementById('syncToast');
        if (!toast) return;

        toast.style.display = 'flex';
        var polling = true;
        var fadeTimer = setTimeout(function () {
            if (!toast.classList.contains('sync-toast-pinned')) {
                toast.style.display = 'none';
                polling = false;
            }
        }, 5000);

        toast.addEventListener('click', function () {
            toast.classList.add('sync-toast-pinned');
            clearTimeout(fadeTimer);
            toast.style.display = 'none';
            polling = false;
            var modal = document.getElementById('syncModal');
            if (modal) {
                modal.show();
                if (typeof updateSyncModal === 'function') updateSyncModal();
            }
        });

        function updateSyncToast() {
            if (!polling) return;
            fetch('/api-proxy/sync/delivery-status')
                .then(function (r) {
                    if (!r.ok) { polling = false; return null; }
                    return r.json();
                })
                .then(function (data) {
                    if (!data) return;
                    var circles = document.getElementById('syncToastCircles');
                    if (!circles || !data.nodes) return;
                    circles.innerHTML = data.nodes.map(function (n) {
                        var color = n.isSynced ? 'var(--sl-color-success-600)' : 'var(--sl-color-warning-500)';
                        var title = escHtml(n.displayName) + (n.isSynced ? ' ✓' : ' pending');
                        return '<span class="sync-circle" style="background:' + color + ';" title="' + title + '"></span>';
                    }).join('');
                    if (polling) setTimeout(updateSyncToast, 3000);
                })
                .catch(function () { if (polling) setTimeout(updateSyncToast, 5000); });
        }
        updateSyncToast();
    })();

    // Concept tags
    (function () {
        if (!articleId) return;

        var chipsEl = document.getElementById('concept-tags-chips');
        var btnAdd = document.getElementById('btn-add-concept');
        var inputWrap = document.getElementById('concept-tag-input-wrap');
        var searchInput = document.getElementById('concept-tag-search');
        var dropdown = document.getElementById('concept-tag-dropdown');
        if (!chipsEl || !btnAdd || !searchInput || !dropdown) return;

        var currentTags = Array.from(chipsEl.querySelectorAll('sl-tag[data-concept]'))
            .map(function (el) { return el.getAttribute('data-concept'); });

        var searchTimer = null;

        function putTags(tags) {
            return fetch('/api-proxy/article/' + articleId + '/concept-tags', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ conceptTags: tags })
            });
        }

        function escConceptHtml(s) {
            return String(s == null ? '' : s)
                .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
        }

        function renderChips() {
            chipsEl.innerHTML = currentTags.map(function (t) {
                return '<sl-tag size="small" variant="warning" pill removable data-concept="' + escConceptHtml(t) + '" style="margin-right:4px;">' + escConceptHtml(t) + '</sl-tag>';
            }).join('');
            attachRemoveHandlers();
        }

        function attachRemoveHandlers() {
            chipsEl.querySelectorAll('sl-tag[data-concept]').forEach(function (el) {
                el.addEventListener('sl-remove', function () {
                    var name = el.getAttribute('data-concept');
                    var idx = currentTags.indexOf(name);
                    if (idx === -1) return;
                    var prev = currentTags.slice();
                    currentTags.splice(idx, 1);
                    renderChips();
                    putTags(currentTags).then(function (r) {
                        if (!r.ok) { currentTags = prev; renderChips(); alert('Failed to remove tag'); }
                    });
                });
            });
        }

        attachRemoveHandlers();

        function addTag(name) {
            name = (name || '').trim();
            if (!name) return;
            if (currentTags.some(function (t) { return t.toLowerCase() === name.toLowerCase(); })) {
                searchInput.value = '';
                dropdown.style.display = 'none';
                return;
            }
            var prev = currentTags.slice();
            currentTags.push(name);
            renderChips();
            putTags(currentTags).then(function (r) {
                if (!r.ok) { currentTags = prev; renderChips(); alert('Failed to add tag'); }
            });
            searchInput.value = '';
            dropdown.style.display = 'none';
        }

        btnAdd.addEventListener('click', function () {
            inputWrap.style.display = 'block';
            btnAdd.style.display = 'none';
            setTimeout(function () { searchInput.focus(); }, 50);
        });

        searchInput.addEventListener('sl-input', function () {
            clearTimeout(searchTimer);
            var q = searchInput.value.trim();
            if (q.length < 1) { dropdown.style.display = 'none'; return; }
            searchTimer = setTimeout(function () {
                fetch('/api-proxy/concept-tags?q=' + encodeURIComponent(q) + '&limit=20')
                    .then(function (r) { return r.ok ? r.json() : []; })
                    .then(function (data) {
                        var tags = (data || []).filter(function (t) {
                            return !currentTags.some(function (c) { return c.toLowerCase() === t.name.toLowerCase(); });
                        });
                        var items = tags.map(function (t) {
                            return '<div class="concept-tag-item" data-name="' + escConceptHtml(t.name) + '" style="padding:8px 12px;cursor:pointer;border-bottom:1px solid var(--sl-color-neutral-100);">' +
                                '<sl-icon name="diagram-3" style="margin-right:6px;"></sl-icon>' +
                                escConceptHtml(t.name) +
                                '<span style="float:right;color:var(--sl-color-neutral-500);font-size:0.8rem;">' + t.articleCount + '</span>' +
                                '</div>';
                        });
                        var exactMatch = tags.some(function (t) { return t.name.toLowerCase() === q.toLowerCase(); });
                        var alreadyOnArticle = currentTags.some(function (c) { return c.toLowerCase() === q.toLowerCase(); });
                        if (!exactMatch && !alreadyOnArticle) {
                            items.unshift('<div class="concept-tag-item concept-tag-new" data-name="' + escConceptHtml(q) + '" style="padding:8px 12px;cursor:pointer;border-bottom:1px solid var(--sl-color-neutral-100);background:var(--sl-color-primary-50);">' +
                                '<sl-icon name="plus-lg" style="margin-right:6px;"></sl-icon>Create <strong>' + escConceptHtml(q) + '</strong>' +
                                '</div>');
                        }
                        dropdown.innerHTML = items.length === 0
                            ? '<div style="padding:8px 12px;color:var(--sl-color-neutral-500);font-size:0.9rem;">No matches.</div>'
                            : items.join('');
                        dropdown.style.display = 'block';
                    });
            }, 200);
        });

        searchInput.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                e.preventDefault();
                var q = searchInput.value.trim();
                if (q) addTag(q);
            } else if (e.key === 'Escape') {
                dropdown.style.display = 'none';
                inputWrap.style.display = 'none';
                btnAdd.style.display = '';
                searchInput.value = '';
            }
        });

        var suppressBlurHide = false;
        dropdown.addEventListener('mousedown', function (e) {
            if (e.target.closest('.concept-tag-item')) suppressBlurHide = true;
        });

        dropdown.addEventListener('click', function (e) {
            var item = e.target.closest('.concept-tag-item');
            if (!item) return;
            addTag(item.getAttribute('data-name'));
            setTimeout(function () { searchInput.focus(); }, 0);
        });

        dropdown.addEventListener('mouseover', function (e) {
            var item = e.target.closest('.concept-tag-item');
            if (item) item.style.background = 'var(--sl-color-primary-50)';
        });
        dropdown.addEventListener('mouseout', function (e) {
            var item = e.target.closest('.concept-tag-item');
            if (item) item.style.background = item.classList.contains('concept-tag-new') ? 'var(--sl-color-primary-50)' : '';
        });

        document.addEventListener('click', function (e) {
            if (!e.target.closest('#concept-tag-input-wrap')) {
                dropdown.style.display = 'none';
            }
        });

        searchInput.addEventListener('sl-blur', function () {
            setTimeout(function () {
                if (suppressBlurHide) {
                    suppressBlurHide = false;
                    return;
                }
                if (searchInput.value.trim() === '' && inputWrap.style.display !== 'none') {
                    dropdown.style.display = 'none';
                    inputWrap.style.display = 'none';
                    btnAdd.style.display = '';
                }
            }, 150);
        });
    })();

    // Related articles
    (function () {
        var prev = document.getElementById('related-prev');
        var next = document.getElementById('related-next');
        if (!prev || !next) return;
        var list = document.getElementById('related-list');
        var info = document.getElementById('related-pager-info');
        if (!articleId) return;
        var page = 1, pageSize = 5, totalPages = 1;

        function escRel(s) {
            return String(s == null ? '' : s)
                .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
        }
        function load(p) {
            prev.loading = next.loading = true;
            fetch('/api-proxy/article/' + articleId + '/related?page=' + p + '&pageSize=' + pageSize)
                .then(function (r) { return r.json(); })
                .then(function (d) {
                    page = d.page; totalPages = d.totalPages;
                    list.innerHTML = (d.items || []).map(function (r) {
                        return '<div class="related-item" style="display:flex;align-items:center;justify-content:space-between;padding:8px 0;border-bottom:1px solid var(--sl-color-neutral-200);">'
                            + '<div><a href="/Article/View?id=' + r.id + '" style="font-weight:500;">' + escRel(r.title) + '</a>'
                            + '<div style="font-size:0.78rem;color:var(--sl-color-neutral-500);">' + escRel((r.sharedConcepts || []).join(', ')) + '</div></div>'
                            + '<sl-badge variant="neutral" pill>' + r.strength + '</sl-badge>'
                            + '</div>';
                    }).join('');
                    info.textContent = 'Page ' + page + ' of ' + totalPages + ' · ' + d.total + ' items';
                    prev.disabled = page <= 1;
                    next.disabled = page >= totalPages;
                })
                .finally(function () { prev.loading = next.loading = false; });
        }
        prev.addEventListener('click', function () { if (page > 1) load(page - 1); });
        next.addEventListener('click', function () { load(page + 1); });
    })();

    // Copy article
    (function () {
        var btn = document.getElementById('btn-copy-article');
        if (!btn) return;
        var dlg = document.getElementById('dlg-copy-article');
        var input = document.getElementById('copy-article-target');
        btn.addEventListener('click', async function () {
            var target = (input ? input.value : '').trim() || '/';
            try {
                var resp = await fetch('/api-proxy/article/' + articleId + '/copy', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ targetFolderPath: target })
                });
                if (resp.ok) {
                    dlg?.hide();
                    window.location.href = '/Folder?path=' + encodeURIComponent(target);
                } else {
                    var data = {}; try { data = await resp.json(); } catch (_) { }
                    var box = document.getElementById('copy-article-error');
                    var errEl = document.getElementById('copy-article-error-msg');
                    if (errEl) errEl.textContent = data.error || ('Copy failed (HTTP ' + resp.status + ')');
                    if (box) { box.style.display = ''; box.open = true; }
                }
            } catch (e) {
                var box = document.getElementById('copy-article-error');
                var errEl = document.getElementById('copy-article-error-msg');
                if (errEl) errEl.textContent = String(e);
                if (box) { box.style.display = ''; box.open = true; }
            }
        });
    })();
})();
