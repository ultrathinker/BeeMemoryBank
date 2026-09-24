// Tags management page logic
(function () {
    var pageDataEl = document.getElementById('page-data');
    var isSuperadmin = false;
    if (pageDataEl) {
        try {
            var data = JSON.parse(pageDataEl.textContent);
            isSuperadmin = !!(data && data.isSuperadmin);
        } catch (e) {}
    }

    var conceptTags = [];
    var searchFilter = '';
    var currentPage = 1;
    var pageSize = 200;

    function escHtml(s) {
        return String(s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function matchFilter(name) {
        if (!searchFilter) return true;
        return name.toLowerCase().indexOf(searchFilter.toLowerCase()) !== -1;
    }

    function renderConcept() {
        var filtered = conceptTags.filter(function (t) { return matchFilter(t.name); });
        var tbody = document.getElementById('concept-tbody');
        var empty = document.getElementById('concept-empty');
        var table = document.getElementById('concept-table');
        var pager = document.getElementById('concept-pager');
        var pagerInfo = document.getElementById('concept-pager-info');
        if (!tbody || !empty || !table || !pager || !pagerInfo) return;

        if (filtered.length === 0) {
            table.style.display = 'none';
            pager.style.display = 'none';
            empty.style.display = 'block';
            return;
        }
        empty.style.display = 'none';
        table.style.display = 'table';
        var totalPages = Math.max(1, Math.ceil(filtered.length / pageSize));
        if (currentPage > totalPages) currentPage = totalPages;
        if (currentPage < 1) currentPage = 1;
        var pageItems = filtered.slice((currentPage - 1) * pageSize, currentPage * pageSize);
        tbody.innerHTML = pageItems.map(function (t) {
            var actions = '';
            if (isSuperadmin) {
                var n = escHtml(t.name);
                actions =
                    '<sl-button size="small" variant="default" data-action="rename" data-name="' + n + '"><sl-icon slot="prefix" name="pencil"></sl-icon></sl-button> ' +
                    '<sl-button size="small" variant="default" data-action="merge" data-name="' + n + '"><sl-icon slot="prefix" name="union"></sl-icon></sl-button> ' +
                    '<sl-button size="small" variant="danger" data-action="delete" data-name="' + n + '"><sl-icon slot="prefix" name="trash"></sl-icon></sl-button>';
            }
            return '<tr style="border-bottom:1px solid var(--sl-color-neutral-200);">' +
                '<td style="padding:8px;"><a href="/Graph?concept=' + encodeURIComponent(t.name) + '">' + escHtml(t.name) + '</a></td>' +
                '<td style="padding:8px;text-align:right;">' + t.articleCount + '</td>' +
                '<td style="padding:8px;text-align:right;">' + actions + '</td>' +
                '</tr>';
        }).join('');
        if (totalPages > 1) {
            pagerInfo.textContent = 'Page ' + currentPage + ' of ' + totalPages + ' \u00B7 ' + filtered.length + ' tags';
            pager.style.display = 'flex';
        } else {
            pager.style.display = 'none';
        }
    }

    function loadConcept() {
        fetch('/api-proxy/concept-tags')
            .then(function (r) { return r.ok ? r.json() : []; })
            .then(function (data) {
                conceptTags = (data || []).sort(function (a, b) { return a.name.localeCompare(b.name); });
                var loading = document.getElementById('concept-loading');
                if (loading) loading.style.display = 'none';
                renderConcept();
            });
    }

    loadConcept();

    var prevBtn = document.getElementById('concept-pager-prev');
    if (prevBtn) {
        prevBtn.addEventListener('click', function () {
            if (currentPage > 1) { currentPage--; renderConcept(); }
        });
    }
    var nextBtn = document.getElementById('concept-pager-next');
    if (nextBtn) {
        nextBtn.addEventListener('click', function () {
            var filtered = conceptTags.filter(function (t) { return matchFilter(t.name); });
            var totalPages = Math.max(1, Math.ceil(filtered.length / pageSize));
            if (currentPage < totalPages) { currentPage++; renderConcept(); }
        });
    }

    var searchInput = document.getElementById('tag-search');
    if (searchInput) {
        searchInput.addEventListener('sl-input', function (e) {
            searchFilter = e.target.value || '';
            currentPage = 1;
            renderConcept();
        });
    }

    var renameCurrent = null;
    var mergeSource = null;
    var deleteName = null;
    var dlgRename = document.getElementById('dlg-rename');
    var dlgMerge = document.getElementById('dlg-merge');
    var dlgDelete = document.getElementById('dlg-delete');

    function openRename(name) {
        renameCurrent = name;
        var cur = document.getElementById('rename-current');
        var inp = document.getElementById('rename-new-name');
        if (cur) cur.textContent = name;
        if (inp) inp.value = name;
        if (dlgRename) dlgRename.show();
        setTimeout(function () { if (inp) inp.focus(); }, 100);
    }
    function openMerge(name) {
        mergeSource = name;
        var src = document.getElementById('merge-source');
        var tgt = document.getElementById('merge-target');
        if (src) src.textContent = name;
        if (tgt) tgt.value = '';
        if (dlgMerge) dlgMerge.show();
        setTimeout(function () { if (tgt) tgt.focus(); }, 100);
    }
    function openDelete(name) {
        deleteName = name;
        var del = document.getElementById('delete-name');
        if (del) del.textContent = name;
        if (dlgDelete) dlgDelete.show();
    }

    var tbody = document.getElementById('concept-tbody');
    if (tbody) {
        tbody.addEventListener('click', function (e) {
            var btn = e.target.closest('sl-button[data-action]');
            if (!btn) return;
            var action = btn.dataset.action;
            var name = btn.dataset.name;
            if (action === 'rename') openRename(name);
            else if (action === 'merge') openMerge(name);
            else if (action === 'delete') openDelete(name);
        });
    }

    var btnRenameConfirm = document.getElementById('btn-rename-confirm');
    if (btnRenameConfirm) {
        btnRenameConfirm.addEventListener('click', function () {
            var btn = this;
            var newName = (document.getElementById('rename-new-name') || {}).value || '';
            newName = newName.trim();
            if (!newName || newName === renameCurrent) { if (dlgRename) dlgRename.hide(); return; }
            btn.loading = true;
            fetch('/api-proxy/concept-tags/' + encodeURIComponent(renameCurrent), {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ newName: newName })
            }).then(function (r) {
                if (r.ok || r.status === 204) {
                    if (dlgRename) dlgRename.hide();
                    loadConcept();
                } else {
                    r.json().then(function (d) { alert(d.error || 'Rename failed'); }).catch(function () { alert('Rename failed'); });
                }
            }).finally(function () { btn.loading = false; });
        });
    }

    var btnMergeConfirm = document.getElementById('btn-merge-confirm');
    if (btnMergeConfirm) {
        btnMergeConfirm.addEventListener('click', function () {
            var btn = this;
            var target = (document.getElementById('merge-target') || {}).value || '';
            target = target.trim();
            if (!target) return;
            btn.loading = true;
            fetch('/api-proxy/concept-tags/merge', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ source: mergeSource, target: target })
            }).then(function (r) {
                if (r.ok || r.status === 204) {
                    if (dlgMerge) dlgMerge.hide();
                    loadConcept();
                } else {
                    r.json().then(function (d) { alert(d.error || 'Merge failed'); }).catch(function () { alert('Merge failed'); });
                }
            }).finally(function () { btn.loading = false; });
        });
    }

    var btnDeleteConfirm = document.getElementById('btn-delete-confirm');
    if (btnDeleteConfirm) {
        btnDeleteConfirm.addEventListener('click', function () {
            var btn = this;
            btn.loading = true;
            fetch('/api-proxy/concept-tags/' + encodeURIComponent(deleteName), { method: 'DELETE' })
                .then(function (r) {
                    if (r.ok || r.status === 204) {
                        if (dlgDelete) dlgDelete.hide();
                        loadConcept();
                    } else {
                        r.json().then(function (d) { alert(d.error || 'Delete failed'); }).catch(function () { alert('Delete failed'); });
                    }
                }).finally(function () { btn.loading = false; });
        });
    }
})();
