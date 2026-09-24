// ===== Sidebar Search =====
(function () {
    var searchInput = document.getElementById('sidebar-search-input');
    var lastSearchBtn = document.getElementById('btn-last-search');
    var treeContainer = document.getElementById('tree-container');
    var debounceTimer;
    var lastSearchCache = { query: '', html: '' };

    function escapeHtml(str) {
        return String(str == null ? '' : str)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

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

    function locateInTree(folderPath) {
        var sidebar = document.getElementById('app-sidebar');
        var expandTab = document.getElementById('sidebar-expand-tab');
        if (sidebar && sidebar.classList.contains('sidebar-collapsed')) {
            sidebar.classList.remove('sidebar-collapsed');
            if (expandTab) expandTab.classList.remove('visible');
            localStorage.setItem('sidebar-collapsed', 'false');
        }

        if (searchInput) searchInput.value = '';

        if (!window.bmbTree || !window.bmbTree.loadTree) return;
        window.bmbTree.loadTree().then(function () {
            var segments = folderPath.split('/').filter(Boolean);
            var currentPath = '';
            var chain = Promise.resolve();
            segments.forEach(function (seg) {
                currentPath += '/' + seg;
                var p = currentPath;
                chain = chain.then(function () {
                    window.bmbTree.expandedPaths.add(p);
                    return window.bmbTree.loadPathChildren(p);
                });
            });

            return chain.then(function () {
                window.bmbTree.saveExpandedSet(window.bmbTree.expandedPaths);

                if (!treeContainer) return;
                var node = treeContainer.querySelector('.tree-node[data-path="' + folderPath + '"]');
                if (!node) return;
                var row = node.querySelector(':scope > .tree-node-row');
                if (!row) return;

                var ownChildren = node.querySelector(':scope > .tree-children');
                if (ownChildren) ownChildren.classList.remove('hidden');
                var ownChevron = row.querySelector('.tree-chevron');
                if (ownChevron) ownChevron.classList.add('expanded');

                var el = node;
                while (el && el !== treeContainer) {
                    el = el.parentElement;
                    if (el && el.classList && el.classList.contains('tree-children')) {
                        el.classList.remove('hidden');
                        var ownerPath = el.getAttribute('data-owner-path');
                        if (ownerPath) {
                            window.bmbTree.expandedPaths.add(ownerPath);
                            var pchev = treeContainer.querySelector('.tree-chevron[data-toggle-path="' + ownerPath + '"]');
                            if (pchev) pchev.classList.add('expanded');
                        }
                    }
                }
                window.bmbTree.saveExpandedSet(window.bmbTree.expandedPaths);

                treeContainer.querySelectorAll('.tree-node-row.active').forEach(function (r) {
                    r.classList.remove('active');
                });
                row.classList.add('active');
                row.scrollIntoView({ block: 'center', behavior: 'smooth' });

                row.classList.remove('locate-blink');
                void row.offsetWidth;
                row.classList.add('locate-blink');
                setTimeout(function () { row.classList.remove('locate-blink'); }, 900);
            });
        });
    }

    function renderSearchResults(data, query) {
        if (!treeContainer) return;
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

    document.addEventListener('click', function (e) {
        var locateBtn = e.target.closest('.locate-trigger');
        if (locateBtn) {
            var item = locateBtn.closest('.search-result-item');
            if (item) {
                locateInTree(item.getAttribute('data-path'));
            }
        }
    });

    if (searchInput) {
        searchInput.addEventListener('sl-input', function () {
            clearTimeout(debounceTimer);
            debounceTimer = setTimeout(function () {
                var query = searchInput.value.trim();
                if (query.length === 0) {
                    if (lastSearchCache.query && window.bmbTree) window.bmbTree.loadTree();
                    return;
                }
                if (query.length < 2) {
                    if (treeContainer) treeContainer.innerHTML = '<div class="search-no-results">Type at least 2 characters</div>';
                    return;
                }
                if (treeContainer) treeContainer.innerHTML = '<div class="search-loading"><sl-spinner></sl-spinner></div>';
                fetch('/api-proxy/search?q=' + encodeURIComponent(query))
                    .then(function (r) { return r.json(); })
                    .then(function (data) { renderSearchResults(data, query); })
                    .catch(function () {
                        if (treeContainer) treeContainer.innerHTML = '<div class="search-no-results">Search error</div>';
                    });
            }, 300);
        });

        searchInput.addEventListener('sl-clear', function () {
            if (window.bmbTree) window.bmbTree.loadTree();
        });
    }

    if (lastSearchBtn) {
        lastSearchBtn.addEventListener('click', function () {
            if (lastSearchCache.query && lastSearchCache.html && treeContainer) {
                if (searchInput) searchInput.value = lastSearchCache.query;
                treeContainer.innerHTML = lastSearchCache.html;
                lastSearchBtn.style.display = 'none';
            }
        });
    }

    window.bmbLocateInTree = locateInTree;
})();
