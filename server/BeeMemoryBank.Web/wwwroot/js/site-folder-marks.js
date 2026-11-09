// ===== Folder access marks in the tree (BMB-338) =====
// A folder that has a rule set DIRECTLY on it gets its ordinary folder icon with an orange diamond inside it in the sidebar
// tree: ONE kind of mark, whatever the rules are (hidden from someone, read-only for someone, open to someone). Subfolders
// that merely inherit a rule get nothing. Rights are rules held on users and roles, so the map is read once per tree load from one call:
//   - a superadmin (data-folder-marks="admin"): /api-proxy/access/folder-marks, names included, with a hint, and a dialog
//     that links to the Users / Roles page of every name;
//   - everybody else (data-folder-marks="self"): /api-proxy/access/readonly-paths, the caller's OWN read-only folders only,
//     marked (the same folder with the diamond) with the hint "Read only for you" and no names. The admin route is not even called for them (the Web layer would refuse
//     it, and the API again).
// When a load fails the marks, the hint and the dialog are taken away (see reload).
// Everything that comes from the data (names, paths) is written with text APIs, never as HTML.
(function () {
    var tree = document.getElementById('tree-container');
    var mode = tree ? tree.getAttribute('data-folder-marks') : null;
    if (!tree || (mode !== 'admin' && mode !== 'self')) {
        window.bmbFolderMarks = { reload: function () { return Promise.resolve(); }, decorate: function () {} };
        return;
    }

    var DEFAULT_ICON = 'folder2';
    var SVG_NS = 'http://www.w3.org/2000/svg';
    // The drawing of the mark, 16x16 like the plain icon. The contour is the unchanged path of Bootstrap's folder2 (the one the
    // plain icon draws), painted with currentColor, i.e. the row's own text colour, so it looks the same as the plain icon in
    // every theme and every row state; the diamond is the mark itself, coloured by --folder-mark-diamond in site.css.
    var CONTOUR = 'M1 3.5A1.5 1.5 0 0 1 2.5 2h2.764c.958 0 1.76.56 2.311 1.184C7.985 3.648 8.48 4 9 4h4.5A1.5 1.5 0 0 1 15 5.5v7a1.5 1.5 0 0 1-1.5 1.5h-11A1.5 1.5 0 0 1 1 12.5zM2.5 3a.5.5 0 0 0-.5.5V6h12v-.5a.5.5 0 0 0-.5-.5H9c-.964 0-1.71-.629-2.174-1.154C6.374 3.334 5.82 3 5.264 3zM14 7H2v5.5a.5.5 0 0 0 .5.5h11a.5.5 0 0 0 .5-.5z';
    var DIAMOND = 'M8 7.5 10.5 10 8 12.5 5.5 10z';
    var MAX_NAMES = 3;          // names per line of the hint; the rest is "and N more"
    var SHOW_DELAY_MS = 150;    // hovering over a row on the way somewhere else must not flash a hint

    var marks = new Map();      // folder path -> what is known about it (admin: the API row, self: { self: true })
    var loadSeq = 0;

    // ---- the data ------------------------------------------------------------------------------------------------

    // Nothing about other people may outlive the authorization that produced it: no marks, no hint, no dialog.
    function clear() {
        marks = new Map();
        hideTip();
        closeDialog();
        decorate(tree);
    }

    // Both answers depend on WHO is signed in, so the browser is told not to read or write its HTTP cache for them
    // (the API and the Web proxy also send Cache-Control: no-store). A load that fails (a refusal such as 401 or 403, an
    // answer that is not the expected JSON, a network error) clears everything, if it is the LATEST load: the sequence
    // number keeps an obsolete request, whatever it brings, from changing what a newer one has settled.
    function reload() {
        var seq = ++loadSeq;
        var url = mode === 'admin' ? '/api-proxy/access/folder-marks' : '/api-proxy/access/readonly-paths';
        return fetch(url, { cache: 'no-store', headers: { 'Accept': 'application/json' } })
            .then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            })
            .then(function (data) {
                if (seq !== loadSeq) return;   // a newer load is on its way: this answer is obsolete
                if (!data || typeof data !== 'object') throw new Error('unexpected answer');
                var next = new Map();
                if (mode === 'admin') {
                    (data.folders || []).forEach(function (f) { if (f && f.path) next.set(f.path, f); });
                } else {
                    (data.paths || []).forEach(function (p) { next.set(p, { self: true }); });
                }
                marks = next;
                hideTip();
                decorate(tree);
            })
            .catch(function () {
                if (seq === loadSeq) clear();
            });
    }

    // ---- marking the rows ------------------------------------------------------------------------------------------

    function ariaFor(info) {
        return info.self ? 'Read only for you' : 'Folder with custom permissions: show who has access';
    }

    function configure(mark, info) {
        mark.className = 'tree-folder-mark';
        mark.setAttribute('aria-label', ariaFor(info));
        if (mode === 'admin') {
            mark.setAttribute('role', 'button');
            mark.setAttribute('tabindex', '0');
        } else {
            mark.setAttribute('role', 'img');
            mark.removeAttribute('tabindex');
        }
    }

    function svgPath(cls, d) {
        var path = document.createElementNS(SVG_NS, 'path');
        path.setAttribute('class', cls);
        path.setAttribute('d', d);
        return path;
    }

    // <span.tree-folder-mark> > <svg.tree-folder-mark-svg> > <path.tree-folder-mark-contour> + <path.tree-folder-mark-diamond>,
    // built with DOM calls: no file to fetch, nothing from the data in it.
    function makeMark(info) {
        var mark = document.createElement('span');
        var svg = document.createElementNS(SVG_NS, 'svg');
        svg.setAttribute('class', 'tree-folder-mark-svg');
        svg.setAttribute('viewBox', '0 0 16 16');
        svg.setAttribute('width', '1em');
        svg.setAttribute('height', '1em');
        svg.setAttribute('fill', 'currentColor');
        svg.setAttribute('aria-hidden', 'true');
        svg.setAttribute('focusable', 'false');
        svg.appendChild(svgPath('tree-folder-mark-contour', CONTOUR));
        svg.appendChild(svgPath('tree-folder-mark-diamond', DIAMOND));
        mark.appendChild(svg);
        configure(mark, info);
        return mark;
    }

    function plainIcon() {
        var icon = document.createElement('sl-icon');
        icon.setAttribute('name', DEFAULT_ICON);
        icon.style.marginRight = '4px';
        return icon;
    }

    function applyTo(node) {
        var link = node.querySelector(':scope > .tree-node-row > a.tree-node-folder');
        if (!link) return;
        var current = link.querySelector(':scope > .tree-folder-mark, :scope > sl-icon');
        if (!current) return;
        var info = marks.get(node.getAttribute('data-path'));
        var isMark = current.classList.contains('tree-folder-mark');
        if (info) {
            if (isMark) configure(current, info);
            else link.replaceChild(makeMark(info), current);
        } else if (isMark) {
            link.replaceChild(plainIcon(), current);
        }
    }

    // Idempotent: a row that is already right is left alone, a row whose rule is gone is put back. `root` is the whole
    // tree, or the container a lazy expansion has just filled.
    function decorate(root) {
        if (!root) return;
        if (root.classList && root.classList.contains('tree-node')) applyTo(root);
        root.querySelectorAll('.tree-node').forEach(applyTo);
    }

    // ---- the hint (one shared floating element, so a big tree costs one element and the sidebar never clips it) ------

    var tip = null;
    var tipFor = null;
    var showTimer = null;

    function ensureTip() {
        if (!tip) {
            tip = document.createElement('div');
            tip.id = 'bmb-folder-mark-tip';
            tip.className = 'folder-mark-tip';
            tip.setAttribute('role', 'tooltip');
            tip.hidden = true;
            document.body.appendChild(tip);
        }
        return tip;
    }

    function label(subject) {
        return subject.type === 'role' ? subject.name + ' (role)' : subject.name;
    }

    // "Ivan, Petr, Guest and 3 more"
    function nameList(subjects) {
        var shown = subjects.slice(0, MAX_NAMES).map(label).join(', ');
        var rest = subjects.length - MAX_NAMES;
        return rest > 0 ? shown + ' and ' + rest + ' more' : shown;
    }

    function hintLines(info) {
        if (info.self) return [{ text: 'Read only for you' }];
        var lines = [{ text: 'Custom permissions', title: true }];
        if ((info.hiddenFrom || []).length) lines.push({ text: 'Hidden from: ' + nameList(info.hiddenFrom) });
        if ((info.readOnly || []).length) lines.push({ text: 'Read only: ' + nameList(info.readOnly) });
        if ((info.openTo || []).length) lines.push({ text: 'Open to: ' + nameList(info.openTo) });
        lines.push({ text: 'Click the icon to see all and change', muted: true });
        return lines;
    }

    function pathOf(mark) {
        var node = mark.closest('.tree-node');
        return node ? node.getAttribute('data-path') : null;
    }

    function place(el, anchor) {
        var margin = 8;
        var r = anchor.getBoundingClientRect();
        var w = el.offsetWidth;
        var h = el.offsetHeight;
        // Beside the sidebar, not on top of it: the hint is taller than a row and would hide the neighbouring folders.
        var sidebar = tree.closest('.sidebar');
        var left = Math.max(r.right + 10, (sidebar ? sidebar.getBoundingClientRect().right : 0) + 8);
        if (left + w > window.innerWidth - margin) left = Math.max(margin, r.left - w - 10);
        var top = r.top + r.height / 2 - h / 2;
        top = Math.min(Math.max(margin, top), Math.max(margin, window.innerHeight - h - margin));
        el.style.left = Math.round(left) + 'px';
        el.style.top = Math.round(top) + 'px';
    }

    function showTip(mark) {
        var info = marks.get(pathOf(mark));
        if (!info) return;
        var el = ensureTip();
        el.textContent = '';
        hintLines(info).forEach(function (line) {
            var div = document.createElement('div');
            div.className = line.title ? 'folder-mark-tip-title' : 'folder-mark-tip-line' + (line.muted ? ' folder-mark-tip-muted' : '');
            div.textContent = line.text;
            el.appendChild(div);
        });
        el.style.visibility = 'hidden';
        el.hidden = false;
        place(el, mark);
        el.style.visibility = '';
        if (tipFor && tipFor !== mark) tipFor.removeAttribute('aria-describedby');
        tipFor = mark;
        mark.setAttribute('aria-describedby', el.id);
    }

    function hideTip() {
        clearTimeout(showTimer);
        showTimer = null;
        if (tip) tip.hidden = true;
        if (tipFor) { tipFor.removeAttribute('aria-describedby'); tipFor = null; }
    }

    function markOf(target) {
        return target && target.closest ? target.closest('.tree-folder-mark') : null;
    }

    // The mark of the row the keyboard focus is on (the row's link, or the mark itself).
    function markForFocus(target) {
        var mark = markOf(target);
        if (mark) return mark;
        var row = target && target.closest ? target.closest('.tree-node-row') : null;
        return row ? row.querySelector('.tree-folder-mark') : null;
    }

    tree.addEventListener('mouseover', function (e) {
        var mark = markOf(e.target);
        if (!mark || mark === tipFor) return;
        clearTimeout(showTimer);
        showTimer = setTimeout(function () { showTip(mark); }, SHOW_DELAY_MS);
    });
    tree.addEventListener('mouseout', function (e) {
        var mark = markOf(e.target);
        if (!mark || mark.contains(e.relatedTarget)) return;
        hideTip();
    });
    tree.addEventListener('focusin', function (e) {
        // Keyboard focus only: a mouse click on the folder's link also focuses it, and the hint must not follow every click.
        if (e.target.matches && !e.target.matches(':focus-visible')) return;
        var mark = markForFocus(e.target);
        if (mark) { clearTimeout(showTimer); showTip(mark); }
    });
    tree.addEventListener('focusout', function (e) {
        if (markForFocus(e.target)) hideTip();
    });
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape') hideTip(); });
    // The tree scrolls inside the sidebar and the page scrolls too: a hint left behind would point at nothing.
    window.addEventListener('scroll', hideTip, true);
    window.addEventListener('resize', hideTip);

    // ---- the click: the icon sits inside the folder's link, so it is caught before the link (or the SPA's own link handler) sees it ----

    tree.addEventListener('click', function (e) {
        var mark = markOf(e.target);
        if (!mark || e.button !== 0) return;
        e.preventDefault();
        e.stopImmediatePropagation();
        var info = marks.get(pathOf(mark));
        if (!info) return;
        if (mode === 'admin') {
            hideTip();
            openDialog(pathOf(mark), info);
        } else if (tipFor === mark) {
            hideTip();    // no dialog for the caller's own state; a tap (no hover on a phone) just shows or hides the hint
        } else {
            showTip(mark);
        }
    }, true);

    tree.addEventListener('keydown', function (e) {
        if ((e.key !== 'Enter' && e.key !== ' ') || mode !== 'admin') return;
        var mark = markOf(e.target);
        if (!mark || e.target !== mark) return;
        e.preventDefault();
        e.stopPropagation();
        var info = marks.get(pathOf(mark));
        if (info) { hideTip(); openDialog(pathOf(mark), info); }
    });

    // ---- the dialog (superadmin only): the folder, the full lists, and an Edit link to the page of each name ----------

    var dialog = null;
    var dialogPath = null;
    var dialogBody = null;

    function ensureDialog() {
        if (dialog) return dialog;
        dialog = document.createElement('sl-dialog');
        dialog.id = 'dlg-folder-mark';
        dialog.className = 'folder-mark-dialog';
        dialog.setAttribute('label', 'Folder access');

        var pathRow = document.createElement('div');
        pathRow.className = 'folder-mark-dialog-path';
        var folderIcon = document.createElement('sl-icon');
        folderIcon.setAttribute('name', DEFAULT_ICON);
        dialogPath = document.createElement('code');
        pathRow.appendChild(folderIcon);
        pathRow.appendChild(dialogPath);

        dialogBody = document.createElement('div');

        var note = document.createElement('p');
        note.className = 'folder-mark-dialog-note';
        note.textContent = 'Rules on this folder apply to everything inside it. They are kept on users and roles, so changes are made on those pages.';

        var close = document.createElement('sl-button');
        close.setAttribute('slot', 'footer');
        close.setAttribute('variant', 'default');
        close.textContent = 'Close';
        close.addEventListener('click', function () { dialog.hide(); });

        dialog.appendChild(pathRow);
        dialog.appendChild(dialogBody);
        dialog.appendChild(note);
        dialog.appendChild(close);
        document.body.appendChild(dialog);
        return dialog;
    }

    // Closes the dialog and empties it, so a stale list cannot be seen again (also while it is still animating shut).
    function closeDialog() {
        if (!dialog) return;
        dialogPath.textContent = '';
        dialogBody.textContent = '';
        if (dialog.open && typeof dialog.hide === 'function') dialog.hide();
    }

    function editHref(subject) {
        return (subject.type === 'role' ? '/Roles' : '/Users') + '?access=' + encodeURIComponent(subject.id);
    }

    function addSection(title, subjects) {
        if (!subjects || subjects.length === 0) return;
        var heading = document.createElement('div');
        heading.className = 'folder-mark-dialog-heading';
        heading.textContent = title + ' (' + subjects.length + ')';
        var list = document.createElement('ul');
        list.className = 'folder-mark-dialog-list';
        subjects.forEach(function (s) {
            var item = document.createElement('li');
            var name = document.createElement('span');
            name.className = 'folder-mark-dialog-name';
            name.textContent = s.name;
            item.appendChild(name);
            if (s.type === 'role') {
                var badge = document.createElement('sl-badge');
                badge.setAttribute('variant', 'neutral');
                badge.setAttribute('pill', '');
                badge.textContent = 'role';
                item.appendChild(badge);
            }
            var edit = document.createElement('a');
            edit.className = 'folder-mark-dialog-edit';
            edit.setAttribute('href', editHref(s));
            edit.textContent = 'Edit';
            edit.setAttribute('title', 'Open the Folder Access dialog of this ' + (s.type === 'role' ? 'role' : 'user'));
            item.appendChild(edit);
            list.appendChild(item);
        });
        dialogBody.appendChild(heading);
        dialogBody.appendChild(list);
    }

    function openDialog(path, info) {
        var dlg = ensureDialog();
        dialogPath.textContent = path;
        dialogBody.textContent = '';
        addSection('Hidden from', info.hiddenFrom);
        addSection('Read only', info.readOnly);
        addSection('Open to', info.openTo);
        if (typeof dlg.show === 'function') dlg.show();
        else customElements.whenDefined('sl-dialog').then(function () { dlg.show(); });
    }

    window.bmbFolderMarks = { reload: reload, decorate: decorate };
})();
