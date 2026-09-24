(function () {
    var dataEl = document.getElementById('article-history-data');
    var pageData = dataEl ? JSON.parse(dataEl.textContent || '{}') : {};
    var articleId = pageData.articleId || '';

    var dialog = document.getElementById('versionDialog');
    var dialogTitle = document.getElementById('versionDialogTitle');
    var dialogContent = document.getElementById('versionDialogContent');
    var dialogLoading = document.getElementById('versionDialogLoading');
    var tabRendered = document.getElementById('tabRendered');
    var tabRaw = document.getElementById('tabRaw');
    var tabDiff = document.getElementById('tabDiff');

    var currentContent = null;
    var activeVersionData = null;
    var activeTab = 'rendered';
    var requestId = 0;

    function setActiveTab(tab) {
        activeTab = tab;
        if (tabRendered) tabRendered.variant = tab === 'rendered' ? 'primary' : 'default';
        if (tabRaw) tabRaw.variant = tab === 'raw' ? 'primary' : 'default';
        if (tabDiff) tabDiff.variant = tab === 'diff' ? 'primary' : 'default';
        renderActiveTab();
    }

    if (tabRendered) tabRendered.addEventListener('click', function () { setActiveTab('rendered'); });
    if (tabRaw) tabRaw.addEventListener('click', function () { setActiveTab('raw'); });
    if (tabDiff) tabDiff.addEventListener('click', function () { setActiveTab('diff'); });

    function renderActiveTab() {
        if (!activeVersionData || !dialogContent) return;
        if (activeTab === 'rendered') {
            dialogContent.innerHTML = '<div class="dialog-single-pane article-content">' + renderMarkdown(activeVersionData.content || '') + '</div>';
        } else if (activeTab === 'raw') {
            dialogContent.innerHTML = '<div class="dialog-single-pane"><div class="raw-content">' + escHtml(activeVersionData.content || '') + '</div></div>';
        } else if (activeTab === 'diff') {
            if (currentContent === null) {
                dialogContent.innerHTML = '';
                if (dialogLoading) dialogLoading.style.display = 'block';
                loadCurrentContent(function (curr) {
                    if (dialogLoading) dialogLoading.style.display = 'none';
                    renderDiffSplit(activeVersionData.content || '', curr);
                }, function () {
                    if (dialogLoading) dialogLoading.style.display = 'none';
                    dialogContent.innerHTML = '<p class="text-muted" style="padding:24px;text-align:center;">Failed to load current content for diff.</p>';
                });
            } else {
                renderDiffSplit(activeVersionData.content || '', currentContent);
            }
        }
    }

    function renderDiffSplit(oldText, newText) {
        // Current (newText) on LEFT, version (oldText) on RIGHT
        var changes = Diff.diffLines(newText, oldText);
        var leftLines = [], rightLines = [];
        var leftNum = 0, rightNum = 0;

        changes.forEach(function (part) {
            var lines = part.value.replace(/\n$/, '').split('\n');
            if (part.removed) {
                lines.forEach(function (line) {
                    leftNum++;
                    leftLines.push({ num: leftNum, text: line, type: 'added' });
                    rightLines.push({ num: '', text: '', type: 'pad' });
                });
            } else if (part.added) {
                lines.forEach(function (line) {
                    rightNum++;
                    leftLines.push({ num: '', text: '', type: 'pad' });
                    rightLines.push({ num: rightNum, text: line, type: 'removed' });
                });
            } else {
                lines.forEach(function (line) {
                    leftNum++;
                    rightNum++;
                    leftLines.push({ num: leftNum, text: line, type: '' });
                    rightLines.push({ num: rightNum, text: line, type: '' });
                });
            }
        });

        var html = '<div class="diff-split">';
        html += '<div class="diff-split-header">Current</div>';
        html += '<div class="diff-split-header">Version ' + activeVersionData.versionNumber + '</div>';
        html += '<div class="diff-split-col">';
        leftLines.forEach(function (l) {
            var cls = l.type === 'added' ? ' diff-line-added' : '';
            html += '<div class="diff-line' + cls + '">';
            html += '<span class="diff-line-num">' + l.num + '</span>';
            html += escHtml(l.text);
            html += '</div>';
        });
        html += '</div>';
        html += '<div class="diff-split-col">';
        rightLines.forEach(function (l) {
            var cls = l.type === 'removed' ? ' diff-line-removed' : '';
            html += '<div class="diff-line' + cls + '">';
            html += '<span class="diff-line-num">' + l.num + '</span>';
            html += escHtml(l.text);
            html += '</div>';
        });
        html += '</div>';
        html += '</div>';
        dialogContent.innerHTML = html;
    }

    function openDialog(versionNum, startTab) {
        var myId = ++requestId;
        activeVersionData = null;
        dialogContent.innerHTML = '';
        if (dialogLoading) dialogLoading.style.display = 'block';
        if (dialogTitle) dialogTitle.textContent = 'Version ' + versionNum;
        if (dialog) dialog.show();
        setActiveTab(startTab);

        loadVersionContent(versionNum, function (data) {
            if (requestId !== myId) return;
            activeVersionData = { content: data.content || '', title: data.title, versionNumber: versionNum };
            if (dialogTitle) dialogTitle.textContent = 'Version ' + versionNum + ' \u2014 ' + data.title;
            if (dialogLoading) dialogLoading.style.display = 'none';

            if (startTab === 'diff') {
                loadCurrentContent(function () { renderActiveTab(); }, function () {
                    dialogContent.innerHTML = '<p class="text-muted" style="padding:24px;text-align:center;">Failed to load current content.</p>';
                });
            } else {
                renderActiveTab();
            }
        }, function () {
            if (requestId !== myId) return;
            if (dialogLoading) dialogLoading.style.display = 'none';
            dialogContent.innerHTML = '<p class="text-muted" style="padding:24px;text-align:center;">Failed to load version content. Is the session unlocked?</p>';
        });
    }

    document.querySelectorAll('.btn-view-version').forEach(function (btn) {
        btn.addEventListener('click', function () {
            openDialog(parseInt(this.getAttribute('data-version'), 10), 'rendered');
        });
    });

    document.querySelectorAll('.btn-diff-version').forEach(function (btn) {
        btn.addEventListener('click', function () {
            openDialog(parseInt(this.getAttribute('data-version'), 10), 'diff');
        });
    });

    document.querySelectorAll('.btn-download-version').forEach(function (btn) {
        btn.addEventListener('click', function () {
            var ver = parseInt(this.getAttribute('data-version'), 10);
            this.loading = true;
            var self = this;
            var onErr = function () { self.loading = false; alert('Failed to load content for download.'); };
            loadVersionContent(ver, function (data) {
                loadCurrentContent(function (curr) {
                    var zip = new JSZip();
                    zip.file('version_' + ver + '.md', data.content || '');
                    zip.file('current.md', curr || '');
                    zip.generateAsync({ type: 'blob' }).then(function (blob) {
                        saveAs(blob, 'article_compare_v' + ver + '_vs_current.zip');
                        self.loading = false;
                    });
                }, onErr);
            }, onErr);
        });
    });

    function loadCurrentContent(cb, errCb) {
        if (currentContent !== null) { cb(currentContent); return; }
        fetch('/api-proxy/article/' + encodeURIComponent(articleId))
            .then(function (r) { if (!r.ok) throw new Error(r.status); return r.json(); })
            .then(function (data) { currentContent = data.content || ''; cb(currentContent); })
            .catch(function () { if (errCb) errCb(); else alert('Failed to load article content'); });
    }

    function loadVersionContent(versionNum, cb, errCb) {
        fetch('/api-proxy/articles/' + encodeURIComponent(articleId) + '/versions/' + encodeURIComponent(versionNum))
            .then(function (r) { if (!r.ok) throw new Error(r.status); return r.json(); })
            .then(function (data) { cb(data); })
            .catch(function () { if (errCb) errCb(); else alert('Failed to load version content'); });
    }

    function renderMarkdown(md) {
        if (typeof marked !== 'undefined' && marked.parse) {
            var renderer = new marked.Renderer();
            var origImage = renderer.image.bind(renderer);
            renderer.image = function (token) {
                if (token && token.href && token.href.startsWith('/api/media/')) {
                    token.href = '/api-proxy/media/' + token.href.substring('/api/media/'.length);
                }
                return origImage(token);
            };
            if (typeof window.bmbRenderMarkdown === 'function') {
                return window.bmbRenderMarkdown(md, { renderer: renderer });
            }
            return DOMPurify.sanitize(marked.parse(md, { breaks: true, renderer: renderer }));
        }
        return '<pre>' + escHtml(md) + '</pre>';
    }

    function escHtml(s) {
        return String(s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }
})();
