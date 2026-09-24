// Shared non-closable "please wait" modal for every export AND import action in the app
// (Export All / Export Folder / Export Article / Import from Obsidian / Bee-Import).
window.bmbShowBusyModal = function (title, message) {
    var modal = document.getElementById('bmb-busy-modal');
    if (!modal) {
        modal = document.createElement('sl-dialog');
        modal.id = 'bmb-busy-modal';
        modal.className = 'bmb-busy-modal';
        modal.setAttribute('no-header', '');
        modal.innerHTML =
            '<div class="bmb-busy-body">' +
            '  <sl-spinner class="bmb-busy-spinner"></sl-spinner>' +
            '  <div class="bmb-busy-text">' +
            '    <div class="bmb-busy-title" id="bmb-busy-title"></div>' +
            '    <div class="bmb-busy-msg" id="bmb-busy-msg"></div>' +
            '  </div>' +
            '</div>';
        document.body.appendChild(modal);
        modal.addEventListener('sl-request-close', function (e) {
            e.preventDefault();
        });
    }
    document.getElementById('bmb-busy-title').textContent = title || 'Working…';
    document.getElementById('bmb-busy-msg').textContent = message || 'Please wait, this may take a moment.';
    modal.show();
};

window.bmbHideBusyModal = function () {
    var modal = document.getElementById('bmb-busy-modal');
    if (modal) modal.hide();
};

window.bmbDownload = async function (payload) {
    window.bmbShowBusyModal('Preparing archive…', "This can take a while for large exports — please don't close this window.");
    try {
        var r = await fetch('/api-proxy/downloads/prepare', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        if (!r.ok) {
            var err = 'Download failed';
            try { var j = await r.json(); err = j.error || err; } catch (_) {}
            throw new Error(err);
        }
        var data = await r.json();
        window.bmbHideBusyModal();
        var a = document.createElement('a');
        a.href = '/api-proxy/downloads/' + encodeURIComponent(data.token);
        a.download = data.fileName;
        document.body.appendChild(a);
        a.click();
        a.remove();
    } catch (e) {
        window.bmbHideBusyModal();
        alert(e.message || 'Download failed');
    }
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

    function setSidebarCollapsed(collapsed) {
        if (!appSidebar) return;
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
            localStorage.setItem(SIDEBAR_KEY, '1');
        } else {
            appSidebar.classList.remove('sidebar-collapsed');
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
    if (localStorage.getItem(SIDEBAR_KEY) === '1') {
        setSidebarCollapsed(true);
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
