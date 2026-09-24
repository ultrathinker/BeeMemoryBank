// ===== AJAX Tree (Lazy Loading by path, keyboard navigation, sidebar create buttons) =====
(function () {
    var expandedPaths = new Set();
    var saveExpandedSet = function () {};
    var highlightActiveItem = function () {};
    var loadTree = function () { return Promise.resolve(); };
    var treeContainer = document.getElementById('tree-container');

    if (treeContainer) {
        var EXPAND_KEY = 'bee-tree-expanded-v2';

        function getExpandedSet() {
            try { return new Set(JSON.parse(localStorage.getItem(EXPAND_KEY) || '[]')); }
            catch (e) { return new Set(); }
        }
        saveExpandedSet = function (set) {
            localStorage.setItem(EXPAND_KEY, JSON.stringify(Array.from(set)));
        };
        expandedPaths = getExpandedSet();

        function escapeHtml(str) {
            return String(str == null ? '' : str)
                .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
                .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
        }

        function getLastSegment(path) {
            if (!path || path === '/') return '/';
            var trimmed = path.replace(/\/$/, '');
            var idx = trimmed.lastIndexOf('/');
            return idx < 0 ? trimmed : trimmed.substring(idx + 1);
        }

        function renderFolderNode(folder, depth) {
            var isExpanded = expandedPaths.has(folder.path);
            var pl = 8 + depth * 20;
            var name = getLastSegment(folder.path);
            var encodedPath = encodeURIComponent(folder.path);

            var html = '<div class="tree-node" data-path="' + escapeHtml(folder.path) + '" data-depth="' + depth + '">';
            html += '<div class="tree-node-row" style="padding-left:' + pl + 'px;">';
            html += '<sl-icon class="tree-chevron' + (isExpanded ? ' expanded' : '') + '" name="chevron-right" data-toggle-path="' + escapeHtml(folder.path) + '"></sl-icon>';
            html += '<a href="/Folder?path=' + encodedPath + '" class="tree-node-link tree-node-folder">';
            html += '<sl-icon name="folder2" style="margin-right:4px;"></sl-icon>' + escapeHtml(name) + '</a>';
            html += '<sl-icon-button class="tree-refresh-node-btn" name="arrow-clockwise" label="Refresh" data-path="' + escapeHtml(folder.path) + '" style="font-size:0.8rem;"></sl-icon-button>';
            html += '</div>';
            html += '<div class="tree-children' + (isExpanded ? '' : ' hidden') + '" data-owner-path="' + escapeHtml(folder.path) + '"></div>';
            html += '</div>';
            return html;
        }

        function renderArticleRow(article, depth) {
            var pl = 8 + (depth + 1) * 20;
            var html = '<div class="tree-node-row tree-article-row" style="padding-left:' + pl + 'px;">';
            html += '<span class="tree-article-dash">\u2013</span>';
            html += '<a href="/Article/View?id=' + article.id + '" class="tree-node-link tree-article-link">' + escapeHtml(article.title) + '</a>';
            if (article.protected) {
                html += ' <sl-icon name="shield-lock" title="Password-protected" style="color:var(--sl-color-warning-600);font-size:0.85em;vertical-align:-1px;"></sl-icon>';
            }
            html += '</div>';
            return html;
        }

        function fillChildren(path, data) {
            var container = treeContainer.querySelector('.tree-children[data-owner-path="' + path + '"]');
            if (!container) return;
            var parentNode = treeContainer.querySelector('.tree-node[data-path="' + path + '"]');
            var depth = parentNode ? parseInt(parentNode.getAttribute('data-depth') || '0') : 0;

            var html = '';
            if (data.folders && data.folders.length > 0) {
                data.folders.forEach(function (f) { html += renderFolderNode(f, depth + 1); });
            }
            if (data.articles && data.articles.length > 0) {
                data.articles.forEach(function (a) { html += renderArticleRow(a, depth); });
            }
            if (!html) html = '<div class="tree-empty-folder" style="padding-left:' + (8 + (depth + 1) * 20) + 'px;color:var(--sl-color-neutral-400);font-size:0.8rem;">empty</div>';
            container.innerHTML = html;
            container.setAttribute('data-loaded', '1');
        }

        function isPathLoaded(path) {
            var c = treeContainer.querySelector('.tree-children[data-owner-path="' + path + '"]');
            return c && c.getAttribute('data-loaded') === '1';
        }

        function loadPathChildren(path) {
            if (isPathLoaded(path)) return Promise.resolve();
            var chevron = treeContainer.querySelector('.tree-chevron[data-toggle-path="' + path + '"]');
            if (chevron) chevron.setAttribute('name', 'arrow-repeat');

            return fetch('/api-proxy/tree/children?path=' + encodeURIComponent(path))
                .then(function (r) { return r.json(); })
                .then(function (data) { fillChildren(path, data); })
                .finally(function () {
                    if (chevron) chevron.setAttribute('name', 'chevron-right');
                });
        }

        highlightActiveItem = function (scrollToActive) {
            treeContainer.querySelectorAll('.tree-node-row.active').forEach(function (row) {
                row.classList.remove('active');
            });
            var path = window.location.pathname;
            var params = new URLSearchParams(window.location.search);
            var selector = null;

            if ((path === '/Folder' || path === '/Tree') && params.get('path')) {
                var encodedPath = encodeURIComponent(params.get('path'));
                selector = 'a[href="/Folder?path=' + encodedPath + '"]';
            } else if (path === '/Article/View' && params.get('id')) {
                selector = 'a[href="/Article/View?id=' + params.get('id') + '"]';
            } else if (path === '/Article/Edit' && params.get('id')) {
                selector = 'a[href="/Article/View?id=' + params.get('id') + '"]';
            }

            if (!selector) return;
            var link = treeContainer.querySelector(selector);
            if (!link) return;
            var row = link.closest('.tree-node-row');
            if (row) row.classList.add('active');

            var el = row;
            while (el) {
                el = el.parentElement;
                if (!el || el === treeContainer) break;
                if (el.classList.contains('tree-children') && el.classList.contains('hidden')) {
                    el.classList.remove('hidden');
                    var ownerPath = el.getAttribute('data-owner-path');
                    if (ownerPath) expandedPaths.add(ownerPath);
                    var chev = treeContainer.querySelector('.tree-chevron[data-toggle-path="' + ownerPath + '"]');
                    if (chev) chev.classList.add('expanded');
                }
            }
            saveExpandedSet(expandedPaths);
            if (scrollToActive && row) row.scrollIntoView({ block: 'nearest' });
        };

        var SCROLL_KEY = 'bee-tree-scroll';
        var sidebarTree = treeContainer.closest('.sidebar-tree') || treeContainer;
        window.addEventListener('beforeunload', function () {
            sessionStorage.setItem(SCROLL_KEY, sidebarTree.scrollTop);
        });
        function restoreScrollPosition() {
            var saved = sessionStorage.getItem(SCROLL_KEY);
            if (saved !== null) {
                sidebarTree.scrollTop = parseInt(saved);
                sessionStorage.removeItem(SCROLL_KEY);
            }
        }

        loadTree = function () {
            return fetch('/api-proxy/tree/children?path=/')
                .then(function (r) { return r.json(); })
                .then(function (data) {
                    if ((!data.folders || data.folders.length === 0) && (!data.articles || data.articles.length === 0)) {
                        treeContainer.innerHTML = '<p class="text-muted text-sm" style="padding:12px;">No articles yet. <a href="/Article/Edit">Create the first one</a></p>';
                        return;
                    }

                    var html = '<div class="tree-view">';
                    (data.folders || []).forEach(function (f) { html += renderFolderNode(f, 0); });
                    (data.articles || []).forEach(function (a) { html += renderArticleRow(a, -1); });
                    html += '</div>';
                    treeContainer.innerHTML = html;

                    var pathsToExpand = Array.from(expandedPaths);
                    if (pathsToExpand.length === 0) {
                        highlightActiveItem();
                        restoreScrollPosition();
                        return;
                    }

                    pathsToExpand.sort(function (a, b) { return a.length - b.length; });
                    var chain = Promise.resolve();
                    pathsToExpand.forEach(function (path) {
                        chain = chain.then(function () {
                            var container = treeContainer.querySelector('.tree-children[data-owner-path="' + path + '"]');
                            if (!container) return;
                            return loadPathChildren(path);
                        });
                    });
                    return chain.then(function () {
                        var validPaths = new Set();
                        expandedPaths.forEach(function (p) {
                            if (treeContainer.querySelector('.tree-children[data-owner-path="' + p + '"]')) validPaths.add(p);
                        });
                        expandedPaths = validPaths;
                        saveExpandedSet(expandedPaths);
                        highlightActiveItem();
                        restoreScrollPosition();
                    });
                })
                .catch(function () {
                    treeContainer.innerHTML = '<p class="text-muted text-sm" style="padding:12px;">Failed to load tree.</p>';
                });
        };

        loadTree();

        var refreshBtn = document.getElementById('btn-tree-refresh');
        if (refreshBtn) {
            refreshBtn.addEventListener('click', function () {
                var overlay = document.createElement('div');
                overlay.className = 'tree-loader-overlay';
                overlay.innerHTML = '<div class="tree-loader-spinner"></div>';
                var sidebarEl = treeContainer.closest('.sidebar') || treeContainer.parentNode;
                sidebarEl.appendChild(overlay);
                var start = Date.now();
                loadTree().then(function () {
                    var delay = Math.max(0, 500 - (Date.now() - start));
                    setTimeout(function () { if (overlay.parentNode) overlay.parentNode.removeChild(overlay); }, delay);
                }).catch(function () { if (overlay.parentNode) overlay.parentNode.removeChild(overlay); });
            });
        }

        function refreshPathNode(path) {
            var container = treeContainer.querySelector('.tree-children[data-owner-path="' + path + '"]');
            if (!container) return Promise.resolve();
            container.removeAttribute('data-loaded');
            return fetch('/api-proxy/tree/children?path=' + encodeURIComponent(path))
                .then(function (r) { return r.json(); })
                .then(function (data) {
                    fillChildren(path, data);
                    var promises = [];
                    if (data.folders) {
                        data.folders.forEach(function (f) {
                            if (expandedPaths.has(f.path)) promises.push(refreshPathNode(f.path));
                        });
                    }
                    return Promise.all(promises);
                });
        }

        treeContainer.addEventListener('click', function (e) {
            var btn = e.target.closest('.tree-refresh-node-btn');
            if (!btn) return;
            e.preventDefault();
            e.stopPropagation();
            var path = btn.getAttribute('data-path');

            var onThisFolder = location.pathname === '/Folder' &&
                (new URLSearchParams(location.search).get('path') || '/') === path;
            if (onThisFolder && window.bmbSpaNavigate) {
                var targetUrl = new URL('/Folder?path=' + encodeURIComponent(path), window.location.origin);
                window.bmbSpaNavigate(targetUrl, false);
            }

            var treeNode = btn.closest('.tree-node');
            var nodeOverlay = document.createElement('div');
            nodeOverlay.className = 'tree-node-loading-overlay';
            nodeOverlay.innerHTML = '<div class="tree-loader-spinner"></div>';
            if (treeNode) treeNode.appendChild(nodeOverlay);
            var nodeStart = Date.now();
            refreshPathNode(path).finally(function () {
                var nodeDelay = Math.max(0, 500 - (Date.now() - nodeStart));
                setTimeout(function () {
                    if (nodeOverlay.parentNode) nodeOverlay.parentNode.removeChild(nodeOverlay);
                    highlightActiveItem(false);
                }, nodeDelay);
            });
        });

        treeContainer.addEventListener('click', function (e) {
            var chevron = e.target.closest('.tree-chevron');
            if (!chevron) return;
            var path = chevron.getAttribute('data-toggle-path');
            var treeNode = chevron.closest('.tree-node');
            var children = treeNode ? treeNode.querySelector(':scope > .tree-children') : null;

            if (chevron.classList.contains('expanded')) {
                chevron.classList.remove('expanded');
                if (children) children.classList.add('hidden');
                expandedPaths.delete(path);
            } else {
                chevron.classList.add('expanded');
                if (children) children.classList.remove('hidden');
                expandedPaths.add(path);
                if (!isPathLoaded(path)) loadPathChildren(path);
            }
            saveExpandedSet(expandedPaths);
        });

        // Keyboard navigation for tree
        (function initKeyboardNav() {
            function getVisibleRows() {
                var rows = Array.from(treeContainer.querySelectorAll('.tree-node-row'));
                return rows.filter(function (r) {
                    for (var el = r.parentElement; el && el !== treeContainer; el = el.parentElement) {
                        if (el.classList.contains('tree-children') && el.classList.contains('hidden')) return false;
                    }
                    return true;
                });
            }
            function getActive() { return treeContainer.querySelector('.tree-node-row.active'); }
            function setActive(row) {
                treeContainer.querySelectorAll('.tree-node-row.active').forEach(function (r) { r.classList.remove('active'); });
                row.classList.add('active');
                row.scrollIntoView({ block: 'nearest' });
            }
            function isTypingTarget(t) {
                if (!t) return false;
                var tag = (t.tagName || '').toLowerCase();
                if (tag === 'input' || tag === 'textarea' || tag === 'select') return true;
                if (tag === 'sl-input' || tag === 'sl-textarea' || tag === 'sl-select') return true;
                if (t.isContentEditable) return true;
                if (t.closest && t.closest('sl-dialog, sl-drawer, .bee-panel')) return true;
                return false;
            }
            document.addEventListener('keydown', function (e) {
                if (e.ctrlKey || e.metaKey || e.altKey) return;
                if (isTypingTarget(document.activeElement) || isTypingTarget(e.target)) return;
                if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp' && e.key !== 'Enter') return;

                var rows = getVisibleRows();
                if (rows.length === 0) return;
                var active = getActive();
                var idx = active ? rows.indexOf(active) : -1;

                if (e.key === 'ArrowDown') {
                    e.preventDefault();
                    var next = rows[Math.min(rows.length - 1, idx + 1)] || rows[0];
                    setActive(next);
                } else if (e.key === 'ArrowUp') {
                    e.preventDefault();
                    var prev = rows[Math.max(0, idx - 1)] || rows[0];
                    setActive(prev);
                } else if (e.key === 'Enter' && active) {
                    e.preventDefault();
                    var chev = active.querySelector('.tree-chevron');
                    var articleLink = active.querySelector('a.tree-article-link');
                    var folderLink = active.querySelector('a.tree-node-folder');
                    if (chev) {
                        chev.click();
                    } else if (articleLink) {
                        articleLink.click();
                    } else if (folderLink) {
                        folderLink.click();
                    }
                }
            });
        })();
    }

    window.bmbGetSelectedFolderPath = function () {
        var path = window.location.pathname;
        var params = new URLSearchParams(window.location.search);
        if (path === '/Folder' && params.get('path') && params.get('path') !== '/') {
            return params.get('path');
        }
        if (path === '/Article/View' || path === '/Article/Edit') {
            var tc = document.getElementById('tree-container');
            if (tc) {
                var activeRow = tc.querySelector('.tree-node-row.active');
                if (activeRow) {
                    var children = activeRow.closest('.tree-children');
                    if (children) {
                        var ownerPath = children.getAttribute('data-owner-path');
                        if (ownerPath && ownerPath !== '/') return ownerPath;
                    }
                }
            }
        }
        return null;
    };

    window.bmbHighlightActiveTreeItem = highlightActiveItem;
    window.bmbReloadTree = loadTree;
    window.bmbTree = {
        loadTree: loadTree,
        loadPathChildren: loadPathChildren,
        get expandedPaths() { return expandedPaths; },
        saveExpandedSet: saveExpandedSet,
        highlightActiveItem: highlightActiveItem
    };
})();
