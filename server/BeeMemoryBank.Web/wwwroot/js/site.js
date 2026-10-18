// The shared non-closable "please wait" modal (bmbShowBusyModal / bmbHideBusyModal) and every button that sends something live in bmb-busy.js (BMB-311).

// Prepares an archive (Export All / Export Folder / Export Article) and hands it to the browser; resolves true when it did.
window.bmbDownload = function (payload, btn) {
    return bmbBusy.run(btn || null, function () {
        return bmbBusy.request('/api-proxy/downloads/prepare', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        }).then(function (res) {
            var data = res.body || {};
            var a = document.createElement('a');
            a.href = '/api-proxy/downloads/' + encodeURIComponent(data.token);
            a.download = data.fileName;
            document.body.appendChild(a);
            a.click();
            a.remove();
        });
    }, {
        long: true,
        title: 'Preparing archive\u2026',
        text: "This can take a while for large exports \u2014 please don't close this window.",
        quiet: true
    });
};

window.openDownloadDialog = function (payload) {
    var dlg = document.getElementById('dlg-download-choice');
    if (!dlg) return;
    dlg.dataset.payload = JSON.stringify(payload || {});
    dlg.show();
};

window.bmbCopyToClipboard = function (text, el) {
    function flash() {
        if (!el) return;
        el.classList.add('bmb-copy-flash');
        setTimeout(function () { el.classList.remove('bmb-copy-flash'); }, 500);
        var toast = document.createElement('div');
        toast.className = 'bmb-copy-toast';
        toast.textContent = 'Copied';
        document.body.appendChild(toast);
        var r = el.getBoundingClientRect();
        toast.style.left = (r.left + r.width / 2 - 30) + 'px';
        toast.style.top = (r.top - 28) + 'px';
        requestAnimationFrame(function () { toast.classList.add('visible'); });
        setTimeout(function () {
            toast.classList.remove('visible');
            setTimeout(function () { if (toast.parentNode) toast.parentNode.removeChild(toast); }, 200);
        }, 900);
    }
    function fallback() {
        var ta = document.createElement('textarea');
        ta.value = text;
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();
        try { document.execCommand('copy'); } catch (e) {}
        document.body.removeChild(ta);
        flash();
    }
    if (navigator.clipboard && window.isSecureContext) {
        navigator.clipboard.writeText(text).then(flash, fallback);
    } else {
        fallback();
    }
};

document.addEventListener('click', function (e) {
    var el = e.target.closest('[data-bmb-copy]');
    if (!el) return;
    var text = el.getAttribute('data-bmb-copy') || el.textContent.trim();
    window.bmbCopyToClipboard(text, el);
});

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
window.bmbInitSlForms = initSlForms;

$(function () {
    // Theme switcher
    var themeNames = {
        light: 'Light',
        'dark-classic': 'Dark Classic', 'dark-bee': 'Dark Bee',
        ocean: 'Deep Ocean', emerald: 'Emerald Forest', sunset: 'Sunset Amber',
        nord: 'Nord Frost', crimson: 'Crimson Night', mono: 'Midnight Mono',
        purple: 'Royal Purple', teal: 'Cyber Teal', gold: 'Solar Gold',
        volcanic: 'Volcanic', arctic: 'Arctic Silver', hacker: 'Hacker Terminal'
    };
    function applyShoelaceThemeClass(theme) {
        var html = document.documentElement;
        var body = document.body;
        if (theme === 'light') {
            html.classList.remove('sl-theme-dark');
            html.classList.add('sl-theme-light');
            if (body) { body.classList.remove('sl-theme-dark'); body.classList.add('sl-theme-light'); }
        } else {
            html.classList.remove('sl-theme-light');
            html.classList.add('sl-theme-dark');
            if (body) { body.classList.remove('sl-theme-light'); body.classList.add('sl-theme-dark'); }
        }
    }
    var themeUsername = document.body.getAttribute('data-username') || document.documentElement.getAttribute('data-username') || '';
    var themeStorageKey = 'bee-theme' + (themeUsername ? '-' + themeUsername : '');
    var currentTheme = localStorage.getItem(themeStorageKey) || 'crimson';
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
            applyShoelaceThemeClass(theme);
            localStorage.setItem(themeStorageKey, theme);
            if (themeLabel) themeLabel.textContent = themeNames[theme] || theme;
            themeMenu.querySelectorAll('sl-menu-item').forEach(function (mi) { mi.checked = false; });
            e.detail.item.checked = true;
        });
    }

    initSlForms();

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

    // Sidebar collapse / expand
    var appSidebar  = document.getElementById('app-sidebar');
    var collapseBtn = document.getElementById('btn-sidebar-collapse');
    var expandTab   = document.getElementById('sidebar-expand-tab');
    var SIDEBAR_KEY = 'bee-sidebar-collapsed';

    // Narrow windows show the tree as a drawer over the content (see the max-width: 719px
    // rules in site.css). There it starts closed, closes itself after a pick, and opening or
    // closing it is not remembered, so the wide-window preference stays what the user chose.
    var narrowQuery = window.matchMedia('(max-width: 719px)');

    function setSidebarCollapsed(collapsed, remember) {
        if (!appSidebar) return;
        if (remember === undefined) remember = !narrowQuery.matches;
        var sp = appSidebar.nextElementSibling;
        if (sp && sp.classList.contains('splitter')) {
            sp.style.display = collapsed ? 'none' : '';
        }
        appSidebar.style.width    = collapsed ? '0' : '';
        appSidebar.style.minWidth = collapsed ? '0' : '';
        appSidebar.style.overflow = collapsed ? 'hidden' : '';
        if (collapsed) {
            appSidebar.classList.add('sidebar-collapsed');
            if (expandTab) expandTab.classList.add('visible');
            if (remember) localStorage.setItem(SIDEBAR_KEY, '1');
        } else {
            appSidebar.classList.remove('sidebar-collapsed');
            if (expandTab) expandTab.classList.remove('visible');
            if (remember) localStorage.removeItem(SIDEBAR_KEY);
        }
    }

    if (collapseBtn) {
        collapseBtn.addEventListener('click', function () { setSidebarCollapsed(true); });
    }
    if (expandTab) {
        expandTab.addEventListener('click', function () { setSidebarCollapsed(false); });
    }
    if (narrowQuery.matches || localStorage.getItem(SIDEBAR_KEY) === '1') {
        setSidebarCollapsed(true, false);
    }

    var headerTreeBtn = document.getElementById('btn-header-tree');
    if (headerTreeBtn) {
        // Pages without a tree, or that hide it on purpose (the AI chat), get no button.
        if (!appSidebar || getComputedStyle(appSidebar).display === 'none') headerTreeBtn.style.display = 'none';
        else headerTreeBtn.addEventListener('click', function (e) {
            e.stopPropagation();
            setSidebarCollapsed(!appSidebar.classList.contains('sidebar-collapsed'), false);
        });
    }

    if (appSidebar) {
        // Drawer mode: picking an article or folder closes the tree so the page is visible.
        appSidebar.addEventListener('click', function (e) {
            if (!narrowQuery.matches) return;
            var link = e.target.closest('a[href]');
            if (link && link.getAttribute('href') !== '#') setSidebarCollapsed(true, false);
        });
        // ...and so does a tap on the content next to it.
        var content = document.querySelector('.main-content');
        if (content) content.addEventListener('click', function () {
            if (narrowQuery.matches && !appSidebar.classList.contains('sidebar-collapsed')) setSidebarCollapsed(true, false);
        });
        // Crossing the breakpoint: going narrow closes the drawer; going wide restores the
        // remembered wide-window state.
        narrowQuery.addEventListener('change', function (q) {
            setSidebarCollapsed(q.matches || localStorage.getItem(SIDEBAR_KEY) === '1', false);
        });
    }
});

function maskSecrets() {
    if (window.CSS && CSS.supports && CSS.supports('-webkit-text-security', 'disc')) return;
    function mask(root) {
        (root || document).querySelectorAll('sl-input.bmb-secret').forEach(function(el) {
            el.setAttribute('type', 'password');
        });
    }
    if (window.customElements && customElements.whenDefined) {
        customElements.whenDefined('sl-input').then(function() { mask(); });
    } else {
        mask();
    }
}
if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', maskSecrets);
else maskSecrets();

// ---- A form that asks first (form.form-confirm data-confirm="...") -----------------------------------------------------------------------
// Asked here, for every page: the encryption page has no script of the Admin page, and its "Encrypt this node" and "Erase these copies" went out without
// the question their forms carry.
document.addEventListener('submit', function (e) {
    var form = e.target && e.target.closest ? e.target.closest('form.form-confirm') : null;
    if (!form) return;
    var msg = form.dataset.confirm;
    if (msg && !confirm(msg)) e.preventDefault();
}, true);

// ---- A form that sends something slow (<form data-busy>) is looked after by bmb-busy.js, and by nothing else ---------------------------------------
// There used to be a second copy of that here (BMB-303): it took over the function that marks a form busy, added a second submit listener and let the form go after
// two minutes while the node was still working. One state owner now: wwwroot/js/bmb-busy.js, loaded before this file by the layout.

// ---- The page opens at the card of the button that was pressed (a fragment: /Admin#card-metadata) ---------------------------------------
window.bmbScrollToHash = function () {
    var id = '';
    try { id = decodeURIComponent((location.hash || '').slice(1)); } catch (err) { return false; }
    var el = id ? document.getElementById(id) : null;
    if (!el) return false;
    el.scrollIntoView({ block: 'start' });
    return true;
};
// Shoelace upgrades its elements after the first layout: look once more when the page is complete.
window.addEventListener('load', function () { setTimeout(window.bmbScrollToHash, 50); });
