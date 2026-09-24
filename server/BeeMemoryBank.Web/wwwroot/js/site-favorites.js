// ===== Favorites (starred articles, pinned above the folder tree) =====
(function () {
    var COLLAPSE_KEY = 'bee-favorites-collapsed';
    var state = { items: [], manualOrder: false };
    var busy = false;

    function block() { return document.getElementById('favorites-block'); }
    function listEl() { return document.getElementById('favorites-list'); }
    function countEl() { return document.getElementById('favorites-count'); }
    function alphaBtn() { return document.getElementById('btn-favorites-alpha'); }
    function toggleBtn() { return document.getElementById('btn-favorites-toggle'); }
    function starBtn() { return document.getElementById('btn-favorite-star'); }

    function currentArticleId() {
        var btn = starBtn();
        if (btn) return btn.getAttribute('data-article-id');
        try {
            var url = new URL(window.location.href);
            if (url.pathname !== '/Article/View') return null;
            var id = url.searchParams.get('id');
            return id ? id.toLowerCase() : null;
        } catch (e) {
            return null;
        }
    }

    function present() { return !!(block() || starBtn()); }

    function isCollapsed() {
        try { return localStorage.getItem(COLLAPSE_KEY) === '1'; } catch (e) { return false; }
    }

    function applyCollapsed() {
        var list = listEl();
        var toggle = toggleBtn();
        var collapsed = isCollapsed();
        if (list) list.hidden = collapsed;
        if (toggle) toggle.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
        var host = block();
        var chevron = host && host.querySelector('.favorites-chevron');
        if (chevron) chevron.name = collapsed ? 'chevron-right' : 'chevron-down';
    }

    function setBusy(value) {
        busy = value;
        var list = listEl();
        if (list) list.classList.toggle('fav-busy', value);
    }

    function send(url, method, body) {
        var opts = { method: method };
        if (body) {
            opts.headers = { 'Content-Type': 'application/json' };
            opts.body = JSON.stringify(body);
        }
        return fetch(url, opts).then(function (r) {
            if (!r.ok) throw new Error('request failed');
            return r;
        });
    }

    function isStarred(id) {
        return !!id && state.items.some(function (i) { return i.id === id; });
    }

    function renderStarButton() {
        var btn = starBtn();
        if (!btn) return;
        var starred = isStarred(btn.getAttribute('data-article-id'));
        var icon = btn.querySelector('sl-icon');
        var label = btn.querySelector('[data-fav-label]');
        if (icon) icon.name = starred ? 'star-fill' : 'star';
        if (label) label.textContent = starred ? 'Favorited' : 'Favorite';
        btn.classList.toggle('is-favorited', starred);
        btn.title = starred ? 'Remove from favorites' : 'Add to favorites';
    }

    function actionButton(icon, label, disabled, onClick, extraClass) {
        var btn = document.createElement('sl-icon-button');
        btn.setAttribute('name', icon);
        btn.setAttribute('label', label);
        btn.title = label;
        btn.className = 'fav-action' + (extraClass ? ' ' + extraClass : '');
        if (disabled) btn.setAttribute('disabled', '');
        else btn.addEventListener('click', function (e) {
            e.preventDefault();
            if (busy) return;
            onClick();
        });
        return btn;
    }

    function renderList() {
        var host = block();
        var list = listEl();
        if (!host || !list) return;

        var activeId = currentArticleId();
        var count = countEl();
        var alpha = alphaBtn();

        host.hidden = state.items.length === 0;
        if (count) count.textContent = state.items.length ? String(state.items.length) : '';
        if (alpha) alpha.style.display = state.manualOrder ? '' : 'none';

        list.innerHTML = '';
        state.items.forEach(function (item, index) {
            var row = document.createElement('div');
            row.className = 'fav-row';
            if (activeId && item.id.toLowerCase() === activeId.toLowerCase()) row.classList.add('active');

            var link = document.createElement('a');
            link.className = 'fav-link';
            link.href = '/Article/View?id=' + encodeURIComponent(item.id);
            link.title = item.treePath || '';
            if (item.protected) {
                var lock = document.createElement('sl-icon');
                lock.setAttribute('name', 'shield-lock');
                lock.className = 'fav-lock';
                link.appendChild(lock);
            }
            var titleEl = document.createElement('span');
            titleEl.className = 'fav-title';
            titleEl.textContent = item.title || '(untitled)';
            link.appendChild(titleEl);
            row.appendChild(link);

            var actions = document.createElement('span');
            actions.className = 'fav-actions';
            actions.appendChild(actionButton('chevron-up', 'Move up', index === 0, function () {
                move(item.id, 'up');
            }));
            actions.appendChild(actionButton('chevron-down', 'Move down', index === state.items.length - 1, function () {
                move(item.id, 'down');
            }));
            actions.appendChild(actionButton('star-fill', 'Remove from favorites', false, function () {
                setBusy(true);
                send('/api-proxy/favorites/' + encodeURIComponent(item.id), 'DELETE')
                    .then(refresh)
                    .catch(resync)
                    .finally(function () { setBusy(false); });
            }, 'fav-unstar'));
            row.appendChild(actions);

            list.appendChild(row);
        });
    }

    function move(id, direction) {
        setBusy(true);
        send('/api-proxy/favorites/' + encodeURIComponent(id) + '/move', 'POST', { direction: direction })
            .then(refresh)
            .catch(resync)
            .finally(function () { setBusy(false); });
    }

    function resync() { return refresh(); }

    function render() {
        renderList();
        renderStarButton();
    }

    function refresh() {
        return fetch('/api-proxy/favorites', { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok ? r.json() : { items: [], manualOrder: false }; })
            .then(function (data) {
                state.items = (data && data.items) || [];
                state.manualOrder = !!(data && data.manualOrder);
                render();
            })
            .catch(function () {});
    }

    document.addEventListener('click', function (e) {
        var btn = e.target.closest ? e.target.closest('#btn-favorite-star') : null;
        if (!btn) return;
        e.preventDefault();
        if (busy) return;

        var articleId = btn.getAttribute('data-article-id');
        if (!articleId) return;
        var starred = isStarred(articleId);
        setBusy(true);
        btn.loading = true;
        send('/api-proxy/favorites/' + encodeURIComponent(articleId), starred ? 'DELETE' : 'POST')
            .then(refresh)
            .catch(resync)
            .finally(function () {
                btn.loading = false;
                setBusy(false);
            });
    });

    document.addEventListener('click', function (e) {
        var toggle = e.target.closest ? e.target.closest('#btn-favorites-toggle') : null;
        if (!toggle) return;
        try { localStorage.setItem(COLLAPSE_KEY, isCollapsed() ? '0' : '1'); } catch (err) { }
        applyCollapsed();
    });

    document.addEventListener('click', function (e) {
        var alpha = e.target.closest ? e.target.closest('#btn-favorites-alpha') : null;
        if (!alpha || busy) return;
        setBusy(true);
        send('/api-proxy/favorites/reset-order', 'POST')
            .then(refresh)
            .catch(resync)
            .finally(function () { setBusy(false); });
    });

    document.addEventListener('bmb:page-swapped', function () {
        applyCollapsed();
        render();
    });

    applyCollapsed();
    if (present()) refresh();
})();
