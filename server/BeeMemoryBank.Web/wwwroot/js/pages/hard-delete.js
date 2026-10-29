(function () {
    // The page is swapped in and out by the single-page navigation (site-spa.js), which aborts the page signal before it replaces the content. What this script starts
    // later (a request, a timer) must end with the page: a late answer would otherwise write into, or put a toast on, whatever page is shown by then. Every listener
    // below is on an element of the page, which goes with the page; there is none on the document or the window.
    const pageSignal = window.bmbGetPageSignal ? window.bmbGetPageSignal() : null;
    const fetchOpts = pageSignal ? { signal: pageSignal } : {};
    const gone = () => !!(pageSignal && pageSignal.aborted);

    let currentPage = 1;
    let totalCount = 0;

    const filterInput = document.getElementById('filter-input');
    const statusSelect = document.getElementById('status-select');
    const pageSizeSelect = document.getElementById('pagesize-select');
    const listBody = document.getElementById('list-body');
    const loadingOverlay = document.getElementById('loading-overlay');
    const tableContainer = document.getElementById('table-container');
    const paginationInfo = document.getElementById('pagination-info');
    const btnPrev = document.getElementById('btn-prev');
    const btnNext = document.getElementById('btn-next');

    const dlgDelete = document.getElementById('dlg-confirm-delete');
    const previewText = document.getElementById('preview-text');
    const previewContent = document.getElementById('preview-content');
    const previewSpinner = document.getElementById('preview-spinner');
    const confirmInstruction = document.getElementById('confirm-instruction');
    const confirmInput = document.getElementById('confirm-input');
    const confirmCheckbox = document.getElementById('confirm-checkbox');
    const btnFinalDelete = document.getElementById('btn-final-delete');

    let currentItem = null;
    let debounceTimer = null;
    let currentItems = [];
    let sortField = 'title';
    let sortDir = 'asc';

    async function loadList() {
        if (gone()) return;
        if (!loadingOverlay || !tableContainer || !pageSizeSelect || !filterInput || !statusSelect) return;
        loadingOverlay.style.display = 'block';
        tableContainer.style.opacity = '0.5';

        const pageSize = pageSizeSelect.value || '100';
        const filter = filterInput.value || '';
        const status = statusSelect.value || 'All';

        try {
            const url = `/api-proxy/hard-delete/list?page=${currentPage}&pageSize=${pageSize}&filter=${encodeURIComponent(filter)}&status=${status}`;
            const r = await fetch(url, fetchOpts);
            if (!r.ok) throw new Error('Failed to load list');
            const data = await r.json();
            if (gone()) return;

            totalCount = data.totalCount;
            currentItems = data.items || [];
            sortAndRender();
            updatePagination();
        } catch (e) {
            if (gone()) return;   // the page was left (the request was aborted, or answered late): there is nobody to tell
            console.error(e);
            toast('danger', 'Error loading list');
        } finally {
            if (!gone()) {
                loadingOverlay.style.display = 'none';
                tableContainer.style.opacity = '1';
            }
        }
    }

    function sortAndRender() {
        const dir = sortDir === 'asc' ? 1 : -1;
        const cmp = (a, b) => {
            if (a.type !== b.type) return a.type === 'folder' ? -1 : 1;
            let av, bv;
            switch (sortField) {
                case 'status': av = a.status || ''; bv = b.status || ''; return av.localeCompare(bv) * dir;
                case 'createdAt': av = new Date(a.createdAt).getTime(); bv = new Date(b.createdAt).getTime(); return (av - bv) * dir;
                case 'size': av = a.size || 0; bv = b.size || 0; return (av - bv) * dir;
                case 'title':
                default: av = a.title || ''; bv = b.title || ''; return av.localeCompare(bv, undefined, { sensitivity: 'base' }) * dir;
            }
        };
        const sorted = currentItems.slice().sort(cmp);
        renderList(sorted);
        updateSortArrows();
    }

    function updateSortArrows() {
        document.querySelectorAll('th.sortable').forEach(th => {
            const arrow = th.querySelector('.sort-arrow');
            if (!arrow) return;
            arrow.textContent = th.dataset.sort === sortField ? (sortDir === 'asc' ? ' ▲' : ' ▼') : '';
        });
    }

    document.querySelectorAll('th.sortable').forEach(th => {
        th.addEventListener('click', () => {
            const f = th.dataset.sort;
            if (sortField === f) sortDir = sortDir === 'asc' ? 'desc' : 'asc';
            else { sortField = f; sortDir = 'asc'; }
            sortAndRender();
        });
    });

    function renderList(items) {
        if (!listBody) return;
        listBody.innerHTML = '';
        if (items.length === 0) {
            // No filter set and nothing in the list: the vault itself is empty, which is not "nothing matched".
            const unfiltered = !(filterInput && filterInput.value) && (!statusSelect || statusSelect.value === 'All');
            listBody.innerHTML = '<tr><td colspan="6" style="text-align:center;padding:40px;color:var(--sl-color-neutral-500);">'
                + (unfiltered ? 'Nothing to delete: this vault has no articles or folders yet.' : 'No items found matching the filters.') + '</td></tr>';
            return;
        }

        items.forEach(item => {
            const tr = document.createElement('tr');
            const icon = item.type === 'folder' ? 'folder' : 'file-text';
            const iconColor = item.type === 'folder' ? 'var(--sl-color-primary-500)' : 'var(--sl-color-neutral-500)';

            const date = new Date(item.createdAt).toLocaleDateString();
            const sizeStr = item.type === 'article' ? formatSize(item.size) : '-';
            const statusClass = item.status === 'A' ? 'status-active' : 'status-deleted';
            const statusLabel = item.status === 'A' ? 'Active' : 'Deleted';

            tr.innerHTML = `
                <td><sl-icon name="${icon}" style="color:${iconColor};font-size:1.2rem;"></sl-icon></td>
                <td>
                    <span class="item-path">${escapeHtml(item.path)}</span>
                    <span class="item-title">${escapeHtml(item.title)}</span>
                </td>
                <td><span class="status-badge ${statusClass}">${statusLabel}</span></td>
                <td class="hide-lt-620"><span style="font-size:12px;color:var(--sl-color-neutral-500);">${date}</span></td>
                <td class="hide-lt-620" style="text-align:right;font-size:12px;color:var(--sl-color-neutral-500);">${sizeStr}</td>
                <td style="text-align:right; white-space:nowrap;">
                    ${item.status === 'D' ? `
                        <sl-button variant="success" size="small" outline circle title="Restore" class="btn-restore-row" style="margin-right:4px;">
                            <sl-icon name="arrow-counterclockwise"></sl-icon>
                        </sl-button>
                    ` : ''}
                    <sl-button variant="danger" size="small" outline circle title="Delete Forever" class="btn-delete-row">
                        <sl-icon name="trash"></sl-icon>
                    </sl-button>
                </td>
            `;

            tr.querySelector('.btn-delete-row').addEventListener('click', () => openDeleteDialog(item));
            const restoreBtn = tr.querySelector('.btn-restore-row');
            if (restoreBtn) restoreBtn.addEventListener('click', () => openRestoreDialog(item));
            listBody.appendChild(tr);
        });
    }

    function escapeHtml(text) {
        return String(text == null ? '' : text)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function formatSize(bytes) {
        if (bytes === 0) return '0 B';
        const k = 1024;
        const sizes = ['B', 'KB', 'MB', 'GB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
    }

    function updatePagination() {
        if (!paginationInfo || !pageSizeSelect || !btnPrev || !btnNext) return;
        const pageSize = parseInt(pageSizeSelect.value);
        const start = (currentPage - 1) * pageSize + 1;
        const end = Math.min(currentPage * pageSize, totalCount);
        paginationInfo.textContent = totalCount > 0 ? `${start}–${end} of ${totalCount}` : '0 of 0';

        btnPrev.disabled = currentPage === 1;
        btnNext.disabled = end >= totalCount;
    }

    async function openDeleteDialog(item) {
        currentItem = item;
        dlgDelete?.show();

        previewSpinner.style.display = 'block';
        previewContent.style.display = 'none';
        btnFinalDelete.disabled = true;
        confirmInput.value = '';
        confirmCheckbox.checked = false;

        const expectedText = item.type === 'folder' ? item.path : item.title;
        confirmInstruction.innerHTML = 'Please type <strong>' + escapeHtml(expectedText) + '</strong> to confirm:';

        if (item.type === 'folder') {
            try {
                // bmb-busy-exempt: a read (a POST only because of its body); the dialog shows its own spinner while the preview loads
                const r = await fetch('/api-proxy/hard-delete/folder/preview', Object.assign({
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ path: item.path })
                }, fetchOpts));
                if (r.ok) {
                    const p = await r.json();
                    if (gone()) return;
                    previewText.innerHTML = `This will delete <strong>${p.articlesCount}</strong> articles, <strong>${p.subfoldersCount}</strong> folders, and <strong>${p.mediaCount}</strong> media files.`;
                } else {
                    previewText.textContent = 'Failed to load folder preview.';
                }
            } catch (e) {
                if (gone()) return;
                previewText.textContent = 'Error loading preview.';
            }
            if (gone()) return;
        } else {
            previewText.innerHTML = `Deleting article: <strong>${escapeHtml(item.title)}</strong><br><span style="font-size:11px;">Path: ${escapeHtml(item.path)}</span>`;
        }

        previewSpinner.style.display = 'none';
        previewContent.style.display = 'block';
    }

    function validateDelete() {
        if (!currentItem) return;
        const expected = currentItem.type === 'folder' ? currentItem.path : currentItem.title;
        const match = confirmInput.value === expected;
        btnFinalDelete.disabled = !match || !confirmCheckbox.checked;
    }

    if (confirmInput) confirmInput.addEventListener('sl-input', validateDelete);
    if (confirmCheckbox) confirmCheckbox.addEventListener('sl-change', validateDelete);

    if (btnFinalDelete) {
        btnFinalDelete.addEventListener('click', async () => {
            const url = currentItem.type === 'folder'
                ? '/api-proxy/hard-delete/folder'
                : `/api-proxy/hard-delete/article/${currentItem.id}`;
            const body = currentItem.type === 'folder' ? JSON.stringify({ path: currentItem.path }) : null;
            // A long operation (BMB-311): a folder is deleted with everything in it; the pop-up covers the page, a refusal is written in the dialog.
            await bmbBusy.run(btnFinalDelete, async function () {
                const res = (await bmbBusy.request(url, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: body
                })).body || {};
                if (gone()) return;   // the page was left while it ran: bmbBusy says so once (success below); the deletion itself is not taken back
                toast('success', `Hard delete successful. Deleted ${res.deletedArticles} articles, ${res.deletedFolders} folders, ${res.deletedMedia} media files.`);
                dlgDelete?.hide();
                loadList();
            }, { long: true, title: 'Deleting for good', text: 'Please wait. Do not close this page.', success: lateNotice });
        });
    }

    // Restore flow
    const dlgRestore = document.getElementById('dlg-confirm-restore');
    const restoreTargetName = document.getElementById('restore-target-name');
    const restoreArticleNote = document.getElementById('restore-article-note');
    const restoreFolderNote = document.getElementById('restore-folder-note');
    const btnRestoreCancel = document.getElementById('btn-restore-cancel');
    const btnRestoreConfirm = document.getElementById('btn-restore-confirm');
    let restoreItem = null;

    // bmbBusy.run shows this text next to the button when it has some; the page shows its own toast when it is there, so nothing is said then. When the page was left
    // the toast has no place any more, and the text makes bmbBusy say once, on the screen, that what was started has finished (what a request that was sent cannot be taken back).
    function lateNotice() { return gone() ? 'finished' : ''; }

    function openRestoreDialog(item) {
        restoreItem = item;
        if (restoreTargetName) restoreTargetName.textContent = '[RESTORED] ' + item.title;
        if (restoreArticleNote) restoreArticleNote.style.display = item.type === 'article' ? 'inline' : 'none';
        if (restoreFolderNote) restoreFolderNote.style.display = item.type === 'folder' ? 'inline' : 'none';
        dlgRestore?.show();
    }

    if (btnRestoreCancel) btnRestoreCancel.addEventListener('click', () => dlgRestore?.hide());

    if (btnRestoreConfirm) {
        btnRestoreConfirm.addEventListener('click', async () => {
            if (!restoreItem) return;
            const item = restoreItem;
            const url = item.type === 'folder'
                ? `/api-proxy/hard-delete/restore/folder/${item.id}`
                : `/api-proxy/hard-delete/restore/article/${item.id}`;
            // A long operation (BMB-311): a folder comes back with everything in it; a refusal is written in the dialog, next to the button.
            await bmbBusy.run(btnRestoreConfirm, async function () {
                const data = (await bmbBusy.request(url, { method: 'POST' })).body || {};
                const name = item.type === 'folder'
                    ? (data.newFolderPath || `[RESTORED] ${item.title}`)
                    : (data.newTitle || `[RESTORED] ${item.title}`);
                const extra = item.type === 'folder' && data.restoredSubfolderCount > 0
                    ? ` (+ ${data.restoredSubfolderCount} subfolder${data.restoredSubfolderCount === 1 ? '' : 's'})`
                    : '';
                if (gone()) return;
                toast('success', `Restored: ${name}${extra}`);
                dlgRestore?.hide();
                loadList();
            }, { long: true, title: 'Restoring', text: 'Please wait. Do not close this page.', success: lateNotice });
        });
    }

    if (pageSignal) pageSignal.addEventListener('abort', () => clearTimeout(debounceTimer));

    if (filterInput) {
        filterInput.addEventListener('sl-input', () => {
            clearTimeout(debounceTimer);
            debounceTimer = setTimeout(() => {
                currentPage = 1;
                loadList();
            }, 300);
        });
    }

    if (statusSelect) statusSelect.addEventListener('sl-change', () => { currentPage = 1; loadList(); });
    if (pageSizeSelect) pageSizeSelect.addEventListener('sl-change', () => { currentPage = 1; loadList(); });

    if (btnPrev) btnPrev.addEventListener('click', () => { if (currentPage > 1) { currentPage--; loadList(); } });
    if (btnNext) btnNext.addEventListener('click', () => { if (currentPage * pageSizeSelect.value < totalCount) { currentPage++; loadList(); } });

    // Open audit log button
    const btnOpenAudit = document.getElementById('btn-open-audit');
    if (btnOpenAudit) btnOpenAudit.addEventListener('click', () => dlgAudit?.show());

    document.querySelectorAll('[data-dlg-cancel]').forEach(btn => {
        btn.addEventListener('click', () => {
            const dlgId = btn.getAttribute('data-dlg-cancel');
            document.getElementById(dlgId)?.hide();
        });
    });

    // Audit Log
    const dlgAudit = document.getElementById('dlg-audit');
    const auditBody = document.getElementById('audit-body');
    const auditLoading = document.getElementById('audit-loading');
    const auditContainer = document.getElementById('audit-container');
    const auditInfo = document.getElementById('audit-info');
    const auditPrev = document.getElementById('audit-prev');
    const auditNext = document.getElementById('audit-next');
    const auditPageSize = 50;
    let auditPage = 1;
    let auditTotal = 0;

    async function loadAudit() {
        if (!auditLoading || !auditContainer || !auditBody) return;
        auditLoading.style.display = 'block';
        auditContainer.style.display = 'none';
        auditBody.innerHTML = '';
        try {
            const r = await fetch(`/api-proxy/hard-delete/audit?page=${auditPage}&pageSize=${auditPageSize}`, fetchOpts);
            if (r.ok) {
                const data = await r.json();
                if (gone()) return;
                auditTotal = data.totalCount;
                renderAudit(data.items);
                auditContainer.style.display = 'block';
                const from = auditTotal === 0 ? 0 : (auditPage - 1) * auditPageSize + 1;
                const to = Math.min(auditPage * auditPageSize, auditTotal);
                if (auditInfo) auditInfo.textContent = `${from}–${to} of ${auditTotal}`;
                if (auditPrev) auditPrev.disabled = auditPage <= 1;
                if (auditNext) auditNext.disabled = auditPage * auditPageSize >= auditTotal;
            }
        } catch (e) { if (!gone()) console.error(e); }
        finally { if (!gone()) auditLoading.style.display = 'none'; }
    }

    if (dlgAudit) dlgAudit.addEventListener('sl-show', () => { auditPage = 1; loadAudit(); });
    if (auditPrev) auditPrev.addEventListener('click', () => { if (auditPage > 1) { auditPage--; loadAudit(); } });
    if (auditNext) auditNext.addEventListener('click', () => { if (auditPage * auditPageSize < auditTotal) { auditPage++; loadAudit(); } });

    function renderAudit(items) {
        if (!auditBody) return;
        if (!items || items.length === 0) {
            auditBody.innerHTML = '<tr><td colspan="4" style="text-align:center;padding:20px;">No audit entries.</td></tr>';
            return;
        }
        items.forEach(item => {
            const tr = document.createElement('tr');
            const date = new Date(item.occurredAt).toLocaleString();
            const entity = `[${escapeHtml(item.entityType)}] ${escapeHtml(item.entityTitle || item.entityIdentifier)}`;
            const counts = `${item.deletedArticles}A / ${item.deletedFolders}F / ${item.deletedMedia}M`;
            const actor = item.userId ? `User #${item.userId}` : (item.sourceNodeId ? `Node ${item.sourceNodeId.substring(0,8)}` : 'System');

            tr.innerHTML = `
                <td>${date}</td>
                <td class="col-entity" style="max-width:300px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;" title="${escapeHtml(item.entityIdentifier)}">${entity}</td>
                <td>${counts}</td>
                <td>${escapeHtml(actor)}</td>
            `;
            auditBody.appendChild(tr);
        });
    }

    function toast(variant, message) {
        if (gone()) return Promise.resolve();   // never onto the page that replaced this one
        const alert = Object.assign(document.createElement('sl-alert'), {
            variant: variant,
            closable: true,
            duration: 5000,
            innerHTML: `
                <sl-icon slot="icon" name="${variant === 'success' ? 'check2-circle' : 'exclamation-octagon'}"></sl-icon>
                ${escapeHtml(message)}
            `
        });
        document.body.append(alert);
        return alert.toast();
    }

    // Wait for Shoelace components to register before reading their .value
    Promise.all([
        customElements.whenDefined('sl-select'),
        customElements.whenDefined('sl-input'),
    ]).then(() => { if (!gone()) loadList(); });
})();
