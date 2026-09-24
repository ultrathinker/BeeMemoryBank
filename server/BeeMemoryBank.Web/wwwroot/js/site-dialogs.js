// ===== Reusable Folder Picker, Path Selector, and Dialog Helpers =====
(function () {
    function escHtml(s) {
        return String(s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    var _bmbFpDropdownIdSeq = 0;

    if (!window._bmbFpGlobalClickBound) {
        window._bmbFpGlobalClickBound = true;
        document.addEventListener('click', function (e) {
            var openDropdowns = document.querySelectorAll('.bmb-fp-dropdown:not([hidden])');
            for (var i = 0; i < openDropdowns.length; i++) {
                var dd = openDropdowns[i];
                var picker = dd.closest('.bmb-folder-picker') || dd.parentElement;
                if (picker && !picker.contains(e.target)) dd.hidden = true;
            }
        });
    }

    window.bmbFolderPicker = function (opts) {
        var rootEl = opts.rootEl;
        var state = rootEl._bmbFpState;

        if (!state) {
            var dropdown = rootEl.querySelector('.bmb-fp-dropdown');
            var input = rootEl.querySelector('.bmb-fp-input');
            if (!dropdown.id) dropdown.id = 'bmb-fp-dd-' + (++_bmbFpDropdownIdSeq);
            input.setAttribute('aria-autocomplete', 'list');
            input.setAttribute('aria-haspopup', 'listbox');
            input.setAttribute('aria-controls', dropdown.id);
            dropdown.setAttribute('role', 'listbox');

            state = rootEl._bmbFpState = {
                input: input,
                dropdown: dropdown,
                selected: null,
                searchTimer: null,
                onChange: null
            };

            function pickPath(p) {
                state.selected = p;
                state.input.value = p;
                state.dropdown.hidden = true;
                if (state.onChange) state.onChange(p);
            }

            function renderResults(folders, hasMore) {
                var html = '<div class="bmb-fp-item" role="option" data-path="/">' +
                    '<sl-icon name="house" style="margin-right:6px;"></sl-icon>/ <span style="color:var(--sl-color-neutral-500);font-size:0.85em;">(root)</span></div>';
                if (folders && folders.length) {
                    html += folders.map(function (f) {
                        return '<div class="bmb-fp-item" role="option" data-path="' + escHtml(f.path) + '">' +
                            '<sl-icon name="folder2" style="margin-right:6px;"></sl-icon>' + escHtml(f.path) + '</div>';
                    }).join('');
                } else {
                    html += '<div style="padding:8px 12px;color:var(--sl-color-neutral-500);font-size:0.9rem;">No matching folders</div>';
                }
                if (hasMore) {
                    html += '<div style="padding:6px 12px;color:var(--sl-color-neutral-400);font-size:0.8rem;text-align:center;">Type more to narrow…</div>';
                }
                state.dropdown.innerHTML = html;
                state.dropdown.hidden = false;
            }

            function search(q) {
                if (!q) {
                    renderResults([], false);
                    return;
                }
                fetch('/api-proxy/folders/search?q=' + encodeURIComponent(q) + '&limit=12')
                    .then(function (r) { return r.json(); })
                    .then(function (data) { renderResults(data.folders || [], !!data.hasMore); })
                    .catch(function () { state.dropdown.hidden = true; });
            }

            state.input.addEventListener('sl-input', function () {
                clearTimeout(state.searchTimer);
                var q = (state.input.value || '').trim();
                state.searchTimer = setTimeout(function () { search(q); }, 200);
            });

            state.input.addEventListener('sl-focus', function () {
                if (state.dropdown.innerHTML === '') renderResults([], false);
                else state.dropdown.hidden = false;
            });

            state.dropdown.addEventListener('click', function (e) {
                var item = e.target.closest('.bmb-fp-item');
                if (!item) return;
                pickPath(item.dataset.path);
            });
        }

        state.onChange = opts.onChange || null;
        state.selected = opts.initialPath || '/';
        state.input.value = state.selected;
        state.dropdown.hidden = true;
        state.dropdown.innerHTML = '';

        return {
            setPath: function (p) {
                state.selected = p;
                state.input.value = p;
            },
            getPath: function () { return state.selected; },
            focus: function () { state.input.focus(); }
        };
    };

    window.bmbInitPathSelector = function (opts) {
        var rootEl = opts.rootEl;
        var state = rootEl._bmbState;

        if (!state) {
            state = rootEl._bmbState = {
                basePath: '/',
                effective: '/',
                display: rootEl.querySelector('.bmb-path-display'),
                pathText: rootEl.querySelector('.bmb-path-text'),
                editBtn: rootEl.querySelector('.bmb-path-edit-btn'),
                pickerBox: rootEl.querySelector('.bmb-path-picker'),
                pickerApi: null
            };

            state.display.setAttribute('tabindex', '0');
            state.display.setAttribute('role', 'button');
            state.display.setAttribute('aria-expanded', 'false');
            state.display.setAttribute('aria-label', 'Change creation location');

            state.pickerApi = window.bmbFolderPicker({
                rootEl: state.pickerBox,
                initialPath: '/',
                onChange: function (p) {
                    state.effective = p || '/';
                    state.pathText.textContent = state.effective;
                    state.pickerBox.hidden = true;
                    state.display.setAttribute('aria-expanded', 'false');
                }
            });

            function toggle(e) {
                if (e && e.target.closest('.bmb-path-picker')) return;
                state.pickerBox.hidden = !state.pickerBox.hidden;
                state.display.setAttribute('aria-expanded', state.pickerBox.hidden ? 'false' : 'true');
                if (!state.pickerBox.hidden) state.pickerApi.focus();
            }

            state.display.addEventListener('click', toggle);
            state.display.addEventListener('keydown', function (e) {
                if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    toggle();
                }
            });
        }

        var basePath = opts.basePath || '/';
        state.basePath = basePath;
        state.effective = basePath;
        state.pathText.textContent = basePath;
        state.pickerBox.hidden = true;
        state.display.setAttribute('aria-expanded', 'false');
        state.pickerApi.setPath(basePath);

        return {
            getEffectivePath: function () { return state.effective || '/'; }
        };
    };

    var _sidebarSelectedPath = null;
    var newArticleLink = document.getElementById('btn-new-article-sidebar');
    if (newArticleLink) {
        newArticleLink.addEventListener('click', function (e) {
            e.preventDefault();
            var basePath = (window.bmbGetSelectedFolderPath && window.bmbGetSelectedFolderPath()) || '/';
            window.location.href = '/Article/Edit?treePath=' + encodeURIComponent(basePath);
        });
    }

    var addFolderLink = document.getElementById('btn-add-folder-sidebar');
    var addFolderDlg = document.getElementById('dlg-add-folder-sidebar');
    var addFolderSelectorRoot = document.getElementById('add-folder-path-selector');
    var createFolderBtn = document.getElementById('btn-create-folder-sidebar');
    var cancelFolderBtn = document.getElementById('btn-cancel-folder-sidebar');
    var addFolderPathSelector = null;

    if (addFolderLink && addFolderDlg) {
        addFolderLink.addEventListener('click', function (e) {
            e.preventDefault();
            _sidebarSelectedPath = (window.bmbGetSelectedFolderPath && window.bmbGetSelectedFolderPath()) || '/';
            addFolderPathSelector = window.bmbInitPathSelector({
                rootEl: addFolderSelectorRoot,
                basePath: _sidebarSelectedPath
            });
            addFolderDlg.show();
        });
    }
    if (cancelFolderBtn && addFolderDlg) {
        cancelFolderBtn.addEventListener('click', function () {
            addFolderDlg.hide();
        });
    }
    if (createFolderBtn) {
        createFolderBtn.addEventListener('click', function () {
            var nameInput = document.getElementById('add-folder-sidebar-name');
            var name = (nameInput.value || '').trim();
            if (!name) return;
            createFolderBtn.loading = true;
            var basePath = addFolderPathSelector ? addFolderPathSelector.getEffectivePath() : '/';
            var path = basePath.replace(/\/$/, '') + '/' + name;
            fetch('/api-proxy/folders', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ path: path })
            }).then(function (r) {
                if (r.ok) {
                    if (addFolderDlg) addFolderDlg.hide();
                    nameInput.value = '';
                    window.location.href = '/Folder?path=' + encodeURIComponent(path);
                } else {
                    r.json().then(function (d) { alert(d.error || 'Failed to create folder'); }).catch(function () { alert('Failed to create folder'); });
                }
            }).catch(function () { alert('Error'); }).finally(function () { createFolderBtn.loading = false; });
        });
    }

    // Enter-to-submit in sl-dialog
    (function () {
        function findPrimaryButton(dialog) {
            var footerBtns = dialog.querySelectorAll('sl-button[slot="footer"]');
            for (var i = 0; i < footerBtns.length; i++) if (footerBtns[i].getAttribute('variant') === 'primary') return footerBtns[i];
            for (var j = 0; j < footerBtns.length; j++) if (footerBtns[j].getAttribute('variant') === 'danger') return footerBtns[j];
            return footerBtns[0] || null;
        }
        function attach(dialog) {
            if (dialog.dataset.beeEnterBound === '1') return;
            dialog.dataset.beeEnterBound = '1';
            dialog.addEventListener('keydown', function (e) {
                if (e.key !== 'Enter' || e.shiftKey) return;
                var t = e.target;
                var tag = (t.tagName || '').toLowerCase();
                if (tag === 'textarea' || tag === 'sl-textarea') return;
                var btn = findPrimaryButton(dialog);
                if (!btn || btn.disabled) return;
                e.preventDefault();
                btn.click();
            });
        }
        document.querySelectorAll('sl-dialog').forEach(attach);
        var mo = new MutationObserver(function (muts) {
            muts.forEach(function (m) {
                m.addedNodes.forEach(function (n) {
                    if (!n.querySelectorAll) return;
                    if (n.tagName && n.tagName.toLowerCase() === 'sl-dialog') attach(n);
                    n.querySelectorAll && n.querySelectorAll('sl-dialog').forEach(attach);
                });
            });
        });
        mo.observe(document.body, { childList: true, subtree: true });
    })();
})();
