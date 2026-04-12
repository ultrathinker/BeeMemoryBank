$(function () {
    // Theme switcher
    var themeNames = {
        'dark-classic': 'Dark Classic', 'dark-bee': 'Dark Bee',
        ocean: 'Deep Ocean', emerald: 'Emerald Forest', sunset: 'Sunset Amber',
        nord: 'Nord Frost', crimson: 'Crimson Night', mono: 'Midnight Mono',
        purple: 'Royal Purple', teal: 'Cyber Teal', gold: 'Solar Gold',
        volcanic: 'Volcanic', arctic: 'Arctic Silver', hacker: 'Hacker Terminal'
    };
    var themeUsername = document.body.getAttribute('data-username') || '';
    var themeStorageKey = 'bee-theme' + (themeUsername ? '-' + themeUsername : '');
    var currentTheme = localStorage.getItem(themeStorageKey) || 'ocean';
    var themeMenu = document.getElementById('theme-menu');
    var themeLabel = document.getElementById('theme-label');
    if (themeMenu) {
        if (themeLabel) themeLabel.textContent = themeNames[currentTheme] || currentTheme;
        themeMenu.querySelectorAll('sl-menu-item').forEach(function (item) {
            if (item.getAttribute('data-theme') === currentTheme) item.checked = true;
        });
        themeMenu.addEventListener('sl-select', function (e) {
            var theme = e.detail.item.getAttribute('data-theme');
            if (!theme) return;
            document.documentElement.setAttribute('data-theme', theme);
            localStorage.setItem(themeStorageKey, theme);
            if (themeLabel) themeLabel.textContent = themeNames[theme] || theme;
            themeMenu.querySelectorAll('sl-menu-item').forEach(function (mi) { mi.checked = false; });
            e.detail.item.checked = true;
        });
    }

    // Shoelace form serialization (for proper sl-input/sl-select form submission)
    function initSlForms(container) {
        (container || document).querySelectorAll('form.sl-form').forEach(function (form) {
            if (form.dataset.slInit) return;
            form.dataset.slInit = '1';
            form.addEventListener('submit', function () {
                form.querySelectorAll('sl-input, sl-select, sl-textarea, sl-checkbox').forEach(function (el) {
                    var name = el.getAttribute('name');
                    if (!name) return;
                    var existing = form.querySelector('input[type="hidden"][data-sl-sync="' + name + '"]');
                    if (existing) existing.remove();
                    var hidden = document.createElement('input');
                    hidden.type = 'hidden';
                    hidden.name = name;
                    hidden.value = el.tagName.toLowerCase() === 'sl-checkbox' ? (el.checked ? 'true' : '') : (el.value || '');
                    hidden.setAttribute('data-sl-sync', name);
                    form.appendChild(hidden);
                });
            });
        });
    }
    initSlForms();

    // ===== AJAX Tree (Lazy Loading by path) =====
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
            var d = document.createElement('div');
            d.textContent = str || '';
            return d.innerHTML;
        }

        function getLastSegment(path) {
            if (!path || path === '/') return '/';
            var trimmed = path.replace(/\/$/, '');
            var idx = trimmed.lastIndexOf('/');
            return idx < 0 ? trimmed : trimmed.substring(idx + 1);
        }

        // Render a folder tree node
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

        // Render an article row
        function renderArticleRow(article, depth) {
            var pl = 8 + (depth + 1) * 20;
            var html = '<div class="tree-node-row tree-article-row" style="padding-left:' + pl + 'px;">';
            html += '<span class="tree-article-dash">\u2013</span>';
            html += '<a href="/Article/View?id=' + article.id + '" class="tree-node-link tree-article-link">' + escapeHtml(article.title) + '</a>';
            html += '</div>';
            return html;
        }

        // Fill children container with server data
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

            // Auto-expand parents
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

        // Save/restore scroll position
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

        // Main load: root children + restore expanded paths
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
                    // Root-level articles
                    (data.articles || []).forEach(function (a) { html += renderArticleRow(a, -1); });
                    html += '</div>';
                    treeContainer.innerHTML = html;

                    // Restore expanded nodes
                    var pathsToExpand = Array.from(expandedPaths);
                    if (pathsToExpand.length === 0) {
                        highlightActiveItem();
                        restoreScrollPosition();
                        return;
                    }

                    // Load sequentially from short to long path (so parents are in DOM before children)
                    pathsToExpand.sort(function (a, b) { return a.length - b.length; });
                    var chain = Promise.resolve();
                    pathsToExpand.forEach(function (path) {
                        chain = chain.then(function () {
                            var container = treeContainer.querySelector('.tree-children[data-owner-path="' + path + '"]');
                            if (!container) return; // node not in DOM (was deleted)
                            return loadPathChildren(path);
                        });
                    });
                    return chain.then(function () {
                        // Remove paths that no longer exist in DOM
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

        // Tree refresh button
        var refreshBtn = document.getElementById('btn-tree-refresh');
        if (refreshBtn) {
            refreshBtn.addEventListener('click', function () {
                var overlay = document.createElement('div');
                overlay.className = 'tree-loader-overlay';
                overlay.innerHTML = '<div class="tree-loader-spinner"></div>';
                // Append to .sidebar (parent of treeContainer) so loadTree's innerHTML
                // replacement doesn't destroy the overlay prematurely
                var sidebarEl = treeContainer.closest('.sidebar') || treeContainer.parentNode;
                sidebarEl.appendChild(overlay);
                var start = Date.now();
                loadTree().then(function () {
                    var delay = Math.max(0, 500 - (Date.now() - start));
                    setTimeout(function () { if (overlay.parentNode) overlay.parentNode.removeChild(overlay); }, delay);
                }).catch(function () { if (overlay.parentNode) overlay.parentNode.removeChild(overlay); });
            });
        }

        // Refresh single node by path
        function refreshPathNode(path) {
            var container = treeContainer.querySelector('.tree-children[data-owner-path="' + path + '"]');
            if (!container) return Promise.resolve();
            container.removeAttribute('data-loaded');
            return fetch('/api-proxy/tree/children?path=' + encodeURIComponent(path))
                .then(function (r) { return r.json(); })
                .then(function (data) {
                    fillChildren(path, data);
                    // Recursively refresh expanded descendants
                    var promises = [];
                    if (data.folders) {
                        data.folders.forEach(function (f) {
                            if (expandedPaths.has(f.path)) promises.push(refreshPathNode(f.path));
                        });
                    }
                    return Promise.all(promises);
                });
        }

        // Node refresh button (event delegation)
        treeContainer.addEventListener('click', function (e) {
            var btn = e.target.closest('.tree-refresh-node-btn');
            if (!btn) return;
            e.preventDefault();
            e.stopPropagation();
            var path = btn.getAttribute('data-path');
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

        // Expand/collapse with lazy loading (event delegation)
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

        // ===== Sidebar Search =====
        var searchInput = document.getElementById('sidebar-search-input');
        var lastSearchBtn = document.getElementById('btn-last-search');
        var debounceTimer;
        var lastSearchCache = { query: '', html: '' };

        function highlight(text, query) {
            if (!query || !text) return escapeHtml(text || '');
            var keywords = query.split(/[\s,]+/).filter(Boolean);
            var lowerText = text.toLowerCase();
            var matches = [];
            keywords.forEach(function (kw) {
                var kwLower = kw.toLowerCase();
                var idx = lowerText.indexOf(kwLower, 0);
                while (idx !== -1) {
                    matches.push({ start: idx, end: idx + kwLower.length });
                    idx = lowerText.indexOf(kwLower, idx + 1);
                }
            });
            if (matches.length === 0) return escapeHtml(text);
            matches.sort(function (a, b) { return a.start - b.start; });
            var merged = [matches[0]];
            for (var i = 1; i < matches.length; i++) {
                var last = merged[merged.length - 1];
                if (matches[i].start < last.end) last.end = Math.max(last.end, matches[i].end);
                else merged.push(matches[i]);
            }
            var parts = [];
            var lastIdx = 0;
            merged.forEach(function (m) {
                if (m.start > lastIdx) parts.push(escapeHtml(text.substring(lastIdx, m.start)));
                parts.push('<mark>' + escapeHtml(text.substring(m.start, m.end)) + '</mark>');
                lastIdx = m.end;
            });
            if (lastIdx < text.length) parts.push(escapeHtml(text.substring(lastIdx)));
            return parts.join('');
        }

        function locateInTree(fullPath, articleId) {
            // 1. Expand sidebar if collapsed
            var sidebar = document.getElementById('app-sidebar');
            var expandTab = document.getElementById('sidebar-expand-tab');
            if (sidebar && sidebar.classList.contains('sidebar-collapsed')) {
                sidebar.classList.remove('sidebar-collapsed');
                if (expandTab) expandTab.classList.remove('visible');
                localStorage.setItem('sidebar-collapsed', 'false');
            }

            // 2. Clear search input to show the tree
            if (searchInput) {
                searchInput.value = '';
            }

            // 3. Load tree and navigate to path
            loadTree().then(function() {
                var segments = fullPath.split('/').filter(Boolean);
                var currentPath = '';
                var chain = Promise.resolve();

                // Build path segments to expand sequentially
                segments.forEach(function(seg) {
                    currentPath += '/' + seg;
                    var path = currentPath;
                    chain = chain.then(function() {
                        if (!expandedPaths.has(path)) {
                            expandedPaths.add(path);
                            saveExpandedSet(expandedPaths);
                        }
                        return loadPathChildren(path);
                    });
                });

                return chain.then(function() {
                    var selector = articleId
                        ? 'a[href="/Article/View?id=' + articleId + '"]'
                        : 'a[href="/Folder?path=' + encodeURIComponent(fullPath) + '"]';

                    var link = treeContainer.querySelector(selector);
                    if (link) {
                        var row = link.closest('.tree-node-row');
                        if (row) {
                            treeContainer.querySelectorAll('.tree-node-row.active').forEach(function(r) {
                                r.classList.remove('active');
                            });
                            row.classList.add('active');
                            row.scrollIntoView({ block: 'center', behavior: 'smooth' });

                            // Visual feedback
                            row.style.transition = 'outline 0.3s';
                            row.style.outline = '2px solid var(--accent)';
                            setTimeout(function() { row.style.outline = 'none'; }, 2000);
                        }
                    }
                });
            });
        }

        function renderSearchResults(data, query) {
            var html = '<div class="search-results-list">';
            var folders = data.folders || [];
            var articles = data.articles || [];

            if (folders.length === 0 && articles.length === 0) {
                html += '<div class="search-no-results">No results found</div>';
            } else {
                if (folders.length > 0) {
                    html += '<div class="search-result-header">Folders (' + folders.length + ')</div>';
                    folders.forEach(function (f) {
                        var nameHtml = highlight(f.name, query);
                        var encodedPath = encodeURIComponent(f.path);
                        var parentPath = f.path.substring(0, f.path.length - f.name.length);
                        var encodedParentPath = encodeURIComponent(parentPath);
                        html += '<div class="search-result-item" data-type="folder" data-path="' + escapeHtml(f.path) + '">';
                        html += '<sl-icon name="folder2" class="locate-trigger" title="Show in tree"></sl-icon>';
                        html += '<div class="search-result-text">';
                        html += '<a href="/Folder?path=' + encodedParentPath + '" class="search-result-path">' + escapeHtml(parentPath) + '</a>';
                        html += '<a href="/Folder?path=' + encodedPath + '" class="search-result-title">' + nameHtml + '</a>';
                        html += '</div></div>';
                    });
                }

                if (articles.length > 0) {
                    html += '<div class="search-result-header">Articles (' + articles.length + ')</div>';
                    articles.forEach(function (a) {
                        var titleHtml = highlight(a.title, query);
                        var encodedTreePath = encodeURIComponent(a.treePath);
                        html += '<div class="search-result-item" data-type="article" data-path="' + escapeHtml(a.treePath) + '" data-id="' + a.id + '">';
                        html += '<sl-icon name="file-earmark-text" class="locate-trigger" title="Show in tree"></sl-icon>';
                        html += '<div class="search-result-text">';
                        html += '<a href="/Folder?path=' + encodedTreePath + '" class="search-result-path">' + escapeHtml(a.treePath) + '</a>';
                        html += '<a href="/Article/View?id=' + a.id + '" class="search-result-title">' + titleHtml + '</a>';
                        html += '</div></div>';
                    });
                }
            }
            html += '</div>';
            treeContainer.innerHTML = html;
            lastSearchCache.query = query;
            lastSearchCache.html = html;
        }

        // Global event listener for search result actions (works for both Sidebar and Search Page)
        document.addEventListener('click', function(e) {
            var locateBtn = e.target.closest('.locate-trigger');
            if (locateBtn) {
                var item = locateBtn.closest('.search-result-item');
                if (item) {
                    var path = item.getAttribute('data-path');
                    var id = item.getAttribute('data-id');
                    locateInTree(path, id);
                }
                return;
            }
        });

        if (searchInput) {
            searchInput.addEventListener('sl-input', function () {
                clearTimeout(debounceTimer);
                debounceTimer = setTimeout(function () {
                    var query = searchInput.value.trim();
                    if (query.length === 0) {
                        if (lastSearchCache.query) loadTree();
                        return;
                    }
                    if (query.length < 2) {
                        treeContainer.innerHTML = '<div class="search-no-results">Type at least 2 characters</div>';
                        return;
                    }
                    treeContainer.innerHTML = '<div class="search-loading"><sl-spinner></sl-spinner></div>';
                    fetch('/api-proxy/search?q=' + encodeURIComponent(query))
                        .then(function (r) { return r.json(); })
                        .then(function (data) { renderSearchResults(data, query); })
                        .catch(function () {
                            treeContainer.innerHTML = '<div class="search-no-results">Search error</div>';
                        });
                }, 300);
            });

            searchInput.addEventListener('sl-clear', function () { loadTree(); });
        }

        if (lastSearchBtn) {
            lastSearchBtn.addEventListener('click', function () {
                if (lastSearchCache.query && lastSearchCache.html) {
                    searchInput.value = lastSearchCache.query;
                    treeContainer.innerHTML = lastSearchCache.html;
                    lastSearchBtn.style.display = 'none';
                }
            });
        }
    } // end if (treeContainer)

    // Draggable splitter
    var sidebar = document.querySelector('.sidebar');
    if (sidebar) {
        var splitter = document.createElement('div');
        splitter.className = 'splitter';
        sidebar.after(splitter);
        var savedWidth = localStorage.getItem('sidebar-width');
        if (savedWidth) sidebar.style.width = savedWidth + 'px';
        var dragging = false;
        splitter.addEventListener('mousedown', function (e) {
            e.preventDefault(); dragging = true;
            sidebar.classList.add('no-transition');
            document.body.style.cursor = 'col-resize';
            document.body.style.userSelect = 'none';
        });
        document.addEventListener('mousemove', function (e) {
            if (!dragging) return;
            var w = Math.min(Math.floor(window.innerWidth * 0.5), Math.max(160, e.clientX));
            sidebar.style.width = w + 'px';
        });
        document.addEventListener('mouseup', function () {
            if (!dragging) return;
            dragging = false;
            sidebar.classList.remove('no-transition');
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
            localStorage.setItem('sidebar-width', parseInt(sidebar.style.width));
        });
    }

    // ===== SPA Navigation =====
    function isSpaUrl(pathname) {
        return pathname === '/' ||
               pathname === '/Tree' ||
               pathname === '/Folder' ||
               pathname === '/Article/View' ||
               pathname === '/Search' ||
               pathname === '/Activity' ||
               pathname === '/Admin';
    }

    function executeScripts(container) {
        var scripts = Array.from(container.querySelectorAll('script'));
        var external = scripts.filter(function(s) { return s.src; });
        var inline = scripts.filter(function(s) { return !s.src; });

        var promises = external.map(function(oldScript) {
            return new Promise(function(resolve) {
                var newScript = document.createElement('script');
                Array.from(oldScript.attributes).forEach(function(attr) {
                    newScript.setAttribute(attr.name, attr.value);
                });
                newScript.onload = resolve;
                newScript.onerror = resolve;
                oldScript.parentNode.replaceChild(newScript, oldScript);
            });
        });

        Promise.all(promises).then(function() {
            inline.forEach(function(oldScript) {
                var newScript = document.createElement('script');
                newScript.textContent = oldScript.textContent;
                oldScript.parentNode.replaceChild(newScript, oldScript);
            });
        });
    }

    function spaAfterSwap() {
        var pageContent = document.getElementById('page-content');
        if (!pageContent) return;
        initSlForms(pageContent);
        executeScripts(pageContent);
        highlightActiveItem(true);
        var mainContent = pageContent.querySelector('.main-content');
        if (mainContent) mainContent.scrollTop = 0;
    }

    var spaNavigating = false;

    function spaNavigate(url, pushState) {
        if (spaNavigating) return;
        var targetUrl = (typeof url === 'string') ? new URL(url, window.location.origin) : url;
        if (!isSpaUrl(targetUrl.pathname)) { window.location.href = targetUrl.href; return; }
        if (pushState !== false && targetUrl.href === window.location.href) return;

        spaNavigating = true;
        // Safety timeout: if fetch hangs (network issue), unblock navigation after 30 seconds
        var spaTimeout = setTimeout(function() { spaNavigating = false; }, 30000);
        var mainContent = document.querySelector('.main-content');
        if (mainContent) { mainContent.style.opacity = '0.5'; mainContent.style.pointerEvents = 'none'; }

        fetch(targetUrl.href)
            .then(function (response) {
                if (!response.ok) throw new Error('HTTP ' + response.status);
                if (response.redirected && response.url.includes('/Login')) {
                    window.location.href = response.url; throw new Error('redirect');
                }
                return response.text();
            })
            .then(function (html) {
                var parser = new DOMParser();
                var doc = parser.parseFromString(html, 'text/html');
                var newContent = doc.getElementById('page-content');
                var currentContent = document.getElementById('page-content');
                if (!newContent || !currentContent) { window.location.href = targetUrl.href; return; }
                currentContent.innerHTML = newContent.innerHTML;
                var newTitle = doc.querySelector('title');
                if (newTitle) document.title = newTitle.textContent;
                if (pushState !== false) history.pushState({ spa: true }, '', targetUrl.href);
                spaAfterSwap();
            })
            .catch(function (err) {
                if (err.message === 'redirect') return;
                window.location.href = targetUrl.href;
            })
            .finally(function () {
                clearTimeout(spaTimeout);
                spaNavigating = false;
                if (mainContent) { mainContent.style.opacity = ''; mainContent.style.pointerEvents = ''; }
            });
    }

    document.addEventListener('click', function (e) {
        if (e.ctrlKey || e.metaKey || e.shiftKey || e.altKey || e.button !== 0) return;
        var link = e.target.closest('a[href]');
        var slBtn = !link ? e.target.closest('sl-button[href]') : null;
        var href = link ? link.getAttribute('href') : (slBtn ? slBtn.getAttribute('href') : null);
        if (!href || href.startsWith('#')) return;
        if ((link || slBtn) && (link || slBtn).getAttribute('target')) return;
        var url;
        try { url = new URL(href, window.location.origin); } catch (err) { return; }
        if (url.origin !== window.location.origin) return;
        if (!isSpaUrl(url.pathname)) return;
        e.preventDefault();
        if (slBtn) e.stopPropagation();
        spaNavigate(url, true);
    });

    window.addEventListener('popstate', function () {
        var url = new URL(window.location.href);
        if (isSpaUrl(url.pathname)) spaNavigate(url, false);
        else window.location.reload();
    });

    if (isSpaUrl(window.location.pathname)) {
        history.replaceState({ spa: true }, '', window.location.href);
    }

    // ── Sidebar collapse / expand ──────────────────────────────────────────
    var sidebar     = document.getElementById('app-sidebar');
    var collapseBtn = document.getElementById('btn-sidebar-collapse');
    var expandTab   = document.getElementById('sidebar-expand-tab');
    var SIDEBAR_KEY = 'bee-sidebar-collapsed';

    function setSidebarCollapsed(collapsed) {
        if (!sidebar) return;
        var splitter = sidebar.nextElementSibling;
        if (splitter && splitter.classList.contains('splitter')) {
            splitter.style.display = collapsed ? 'none' : '';
        }
        // Override inline styles (e.g. set by resizable splitter) so CSS class takes effect
        sidebar.style.width    = collapsed ? '0' : '';
        sidebar.style.minWidth = collapsed ? '0' : '';
        sidebar.style.overflow = collapsed ? 'hidden' : '';
        if (collapsed) {
            sidebar.classList.add('sidebar-collapsed');
            if (expandTab) expandTab.classList.add('visible');
            localStorage.setItem(SIDEBAR_KEY, '1');
        } else {
            sidebar.classList.remove('sidebar-collapsed');
            if (expandTab) expandTab.classList.remove('visible');
            localStorage.removeItem(SIDEBAR_KEY);
        }
    }

    if (collapseBtn) {
        collapseBtn.addEventListener('click', function () { setSidebarCollapsed(true); });
    }
    if (expandTab) {
        expandTab.addEventListener('click', function () { setSidebarCollapsed(false); });
    }

    // Restore collapsed state across page loads
    if (localStorage.getItem(SIDEBAR_KEY) === '1') {
        setSidebarCollapsed(true);
    }

    (function initSyncWidget() {
        var btn = document.getElementById('syncStatusBtn');
        var dropdown = document.getElementById('syncDropdown');
        var badge = document.getElementById('syncBadge');
        if (!btn || !dropdown) return;

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
                            var statusColor = n.isSynced ? 'var(--sl-color-success-600)' : 'var(--sl-color-warning-500)';
                            var isOnline = n.lastContactAt && (new Date() - new Date(n.lastContactAt) < 300000); // 5 min
                            var statusText = n.isSynced ? 'Synced' : (n.lastPushedSeq + ' / ' + n.totalLocalEvents);
                            var icon = getNodeIcon(n);
                            
                            return '<div class="sync-node-item">' +
                                '<sl-icon name="' + icon + '" style="font-size:1.1rem;color:' + (isOnline ? 'var(--accent)' : 'var(--text-secondary)') + ';"></sl-icon>' +
                                '<div class="sync-node-info">' +
                                    '<div class="sync-node-name">' + syncEscapeHtml(n.displayName) + 
                                    (isOnline ? ' <small style="color:var(--sl-color-success-600);font-size:0.65rem;">ONLINE</small>' : '') + '</div>' +
                                    '<div class="sync-node-meta">' + (n.isSynced ? 'Up to date' : (n.lastContactAt ? 'Syncing...' : 'Waiting for first sync')) + ' · ' + getRelativeTime(n.lastContactAt) + '</div>' +
                                    (n.isSynced ? '' : '<sl-progress-bar value="' + (n.lastPushedSeq / n.totalLocalEvents * 100) + '" style="--height:4px;margin-top:4px;"></sl-progress-bar>') +
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

                    // Adaptive polling: 5s when pending, 30s when synced
                    var desiredInterval = pending > 0 ? 5000 : 30000;
                    if (window._syncBadgeMs !== desiredInterval) {
                        window._syncBadgeMs = desiredInterval;
                        if (window._syncBadgeInterval) clearInterval(window._syncBadgeInterval);
                        window._syncBadgeInterval = setInterval(updateSyncBadge, desiredInterval);
                    }
                })
                .catch(function () {});
        }

        // Initial check + start polling
        updateSyncBadge();
        if (!window._syncBadgeInterval) {
            window._syncBadgeMs = 30000;
            window._syncBadgeInterval = setInterval(updateSyncBadge, 30000);
        }

        // Check for 'created' or 'updated' parameter to show sync toast
        var urlParams = new URLSearchParams(window.location.search);
        if (urlParams.has('created') || urlParams.has('updated')) {
            showSyncToast();
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
    })();
});

function updateSyncModal() {
    fetch('/api-proxy/sync/delivery-status')
        .then(function (r) { return r.json(); })
        .then(function (data) {
            var content = document.getElementById('syncModalContent');
            if (!content || !data.nodes) return;

            function escForModal(str) {
                var d = document.createElement('div');
                d.textContent = str;
                return d.innerHTML;
            }

            function getRelativeTime(dateStr) {
                if (!dateStr) return 'Never';
                var date = new Date(dateStr);
                var now = new Date();
                var diff = Math.floor((now - date) / 1000);
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
                
                return '<div class="sync-modal-node">' +
                    '<sl-icon name="' + icon + '" style="font-size:1.4rem;margin-top:2px;color:' + (isOnline ? 'var(--accent)' : 'var(--text-secondary)') + ';"></sl-icon>' +
                    '<div class="sync-modal-node-info">' +
                        '<strong>' + escForModal(n.displayName) + (isOnline ? ' <small style="color:var(--sl-color-success-600);">ONLINE</small>' : '') + '</strong><br>' +
                        '<small>' + (n.isSynced ? 'Synced' : (n.lastContactAt ? 'Syncing: ' + n.lastPushedSeq + ' / ' + n.totalLocalEvents : 'Waiting for first sync')) + ' · ' + getRelativeTime(n.lastContactAt) + '</small>' +
                        (n.isSynced ? '' : '<sl-progress-bar value="' + (n.lastPushedSeq / n.totalLocalEvents * 100) + '" style="--height:6px;margin-top:8px;"></sl-progress-bar>') +
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
                setTimeout(updateSyncModal, 3000);
            }
        })
        .catch(function () {});
}
