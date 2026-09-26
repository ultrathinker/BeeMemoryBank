// Setup wizard logic (mode selection, forms, on-request network scan, and submission loader)
(function () {
    function goToForm(mode) {
        var panelMode = document.getElementById('panel-mode');
        var panelForm = document.getElementById('panel-form');
        if (panelMode) panelMode.classList.remove('active');
        if (panelForm) panelForm.classList.add('active');

        var formStandalone = document.getElementById('form-standalone');
        var formJoin = document.getElementById('form-join');
        if (formStandalone) formStandalone.style.display = (mode === 'standalone') ? 'block' : 'none';
        if (formJoin) formJoin.style.display = (mode === 'join') ? 'block' : 'none';
    }

    var btnExisting = document.getElementById('btn-mode-existing');
    var linkLegacyBack = document.getElementById('link-legacy-back');
    if (btnExisting) {
        btnExisting.addEventListener('click', function (e) {
            e.preventDefault();
            // Inside the Windows app (WebView2) the shell intercepts this address, shows the
            // system folder picker and opens the chosen folder as this profile. A plain browser
            // cannot pick a folder on the node, so it gets the path-and-copy form instead.
            if (window.chrome && window.chrome.webview) {
                window.location.href = 'https://bmb-desktop.invalid/open-existing-profile';
                return;
            }
            var panelMode = document.getElementById('panel-mode');
            var panelLegacy = document.getElementById('panel-legacy');
            if (panelMode) panelMode.classList.remove('active');
            if (panelLegacy) panelLegacy.classList.add('active');
        });
    }
    if (linkLegacyBack) {
        linkLegacyBack.addEventListener('click', function (e) {
            e.preventDefault();
            var panelLegacy = document.getElementById('panel-legacy');
            var panelMode = document.getElementById('panel-mode');
            if (panelLegacy) panelLegacy.classList.remove('active');
            if (panelMode) panelMode.classList.add('active');
        });
    }

    var btnStandalone = document.getElementById('btn-mode-standalone');
    var btnJoin = document.getElementById('btn-mode-join');
    if (btnStandalone) btnStandalone.addEventListener('click', function () { goToForm('standalone'); });
    if (btnJoin) btnJoin.addEventListener('click', function () { goToForm('join'); });

    // The network scan runs only when asked for, never by itself.
    var btnScan = document.getElementById('btn-scan-network');
    if (btnScan) btnScan.addEventListener('click', scanNetwork);

    function escapeHtml(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function scanNetwork() {
        var box = document.getElementById('discovered-nodes');
        if (!box) return;
        box.innerHTML =
            '<small class="text-muted">' +
            '<sl-spinner style="font-size:0.85em;vertical-align:-2px;"></sl-spinner> ' +
            'Looking for devices\u2026</small>';
        fetch('/Setup?handler=DiscoveredNodes', { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok ? r.json() : []; })
            .then(function (nodes) { renderDiscovered(nodes || []); })
            .catch(function () { renderDiscovered([]); });
    }

    function renderDiscovered(nodes) {
        var box = document.getElementById('discovered-nodes');
        if (!box) return;
        box.innerHTML = '';
        if (!nodes.length) {
            box.innerHTML =
                '<small class="text-muted">Nothing found on your network. ' +
                'Type the address of the other device above.</small>';
            return;
        }
        var head = document.createElement('small');
        head.className = 'text-muted';
        head.textContent = nodes.length + ' device' + (nodes.length > 1 ? 's' : '') +
            ' found \u2014 click one to use it:';
        box.appendChild(head);
        nodes.forEach(function (n) {
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'mode-card';
            btn.style.cssText = 'padding:12px; border-width:1px; align-items:center;';
            var label = escapeHtml(n.name || n.host || n.url);
            var sub = escapeHtml(n.url) + (n.version ? ' \u00b7 v' + escapeHtml(n.version) : '');
            btn.innerHTML =
                '<sl-icon name="hdd-network"></sl-icon>' +
                '<div class="mode-card-text"><strong>' + label + '</strong>' +
                '<small>' + sub + '</small></div>';
            btn.addEventListener('click', function () {
                var inp = document.getElementById('join-remote-url');
                if (inp) { inp.value = n.url; inp.focus(); }
            });
            box.appendChild(btn);
        });
    }

    var dlg = document.getElementById('setup-loader');
    var title = document.getElementById('setup-loader-title');
    var msg = document.getElementById('setup-loader-msg');
    document.querySelectorAll('form[data-setup-form]').forEach(function (form) {
        form.addEventListener('submit', function () {
            var kind = form.getAttribute('data-setup-form');
            if (kind === 'join') {
                if (title) title.textContent = 'Connecting\u2026';
                if (msg) msg.textContent = 'Copying your notes from the other device. This may take up to a minute.';
            } else {
                if (title) title.textContent = 'Setting up\u2026';
                if (msg) msg.textContent = 'This takes a few seconds.';
            }
            form.querySelectorAll('[data-submit-btn]').forEach(function (b) { b.loading = true; b.disabled = true; });
            if (dlg && typeof dlg.show === 'function') dlg.show();
            else if (dlg) dlg.open = true;
        });
    });
})();
