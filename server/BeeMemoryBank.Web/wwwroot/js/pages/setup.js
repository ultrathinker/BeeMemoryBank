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

    // Buttons that ask the desktop app for a native picker (DesktopShellCommands) are rendered hidden:
    // outside the app their address leads nowhere, so a browser, and Docker, never show them. The app is
    // the Windows one (WebView2 has window.chrome.webview) or the Mac one (WKWebView has no such object, so
    // the shell puts DesktopShellCommands.UserAgentToken into its user agent). Keep the token in step with
    // DesktopShellCommands.UserAgentToken: a test compares them.
    var inDesktopShell = !!(window.chrome && window.chrome.webview) ||
        navigator.userAgent.indexOf('BeeMemoryBankDesktop') !== -1;
    if (inDesktopShell) {
        document.querySelectorAll('[data-desktop-only]').forEach(function (el) { el.hidden = false; });
    }

    var btnExisting = document.getElementById('btn-mode-existing');
    var linkLegacyBack = document.getElementById('link-legacy-back');
    if (btnExisting) {
        btnExisting.addEventListener('click', function (e) {
            e.preventDefault();
            // The shell intercepts this address, shows the system folder picker and opens the chosen
            // folder as this profile (a backup is handed to the restore form instead).
            window.location.href = 'https://bmb-desktop.invalid/open-existing-profile';
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

    // A join code pasted from the other computer's Connect a device card (bmb-join:?a=<address>&t=<token>&s=<pin>): show the
    // address it carries in the address field and lock that field, because the join goes to the code's address, not to what
    // is typed. The server parses the code again and decides (JoinCode.TryParse); this only tells the person what was read.
    var joinCodeInput = document.getElementById('join-code');
    var joinAddressInput = document.getElementById('join-remote-url');
    var joinCodeStatus = document.getElementById('join-code-status');
    function addressOfJoinCode(text) {
        var t = (text || '').trim();
        if (t.toLowerCase().indexOf('bmb-join:?') !== 0) return null;
        var pairs = t.substring('bmb-join:?'.length).split('&');
        for (var i = 0; i < pairs.length; i++) {
            var eq = pairs[i].indexOf('=');
            if (eq > 0 && pairs[i].substring(0, eq) === 'a') {
                try { return decodeURIComponent(pairs[i].substring(eq + 1)); } catch (e) { return null; }
            }
        }
        return null;
    }
    function showJoinCodeStatus(text, ok) {
        if (!joinCodeStatus) return;
        joinCodeStatus.textContent = text || '';
        joinCodeStatus.style.display = text ? 'block' : 'none';
        joinCodeStatus.style.color = ok ? '' : 'var(--sl-color-danger-500)';
    }
    function onJoinCodeChanged() {
        if (!joinCodeInput || !joinAddressInput) return;
        var code = (joinCodeInput.value || '').trim();
        if (!code) {
            joinAddressInput.disabled = false;
            showJoinCodeStatus('', true);
            return;
        }
        var address = addressOfJoinCode(code);
        if (address) {
            joinAddressInput.value = address;
            joinAddressInput.disabled = true;
            showJoinCodeStatus('Join code read: this computer will join ' + address + ' and check that it is the right one before it sends the password.', true);
        } else {
            joinAddressInput.disabled = false;
            showJoinCodeStatus('This does not look like a join code. Copy all of it from Connect a device on the other computer; it starts with bmb-join:', false);
        }
    }
    if (joinCodeInput) {
        joinCodeInput.addEventListener('sl-input', onJoinCodeChanged);
        joinCodeInput.addEventListener('input', onJoinCodeChanged);
    }

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
                '<small class="text-muted">Nothing found on your network. A computer is found only while ' +
                '“Devices on my network” is switched on for it (Admin → Nodes), and only on the same network. ' +
                'Otherwise paste the join code from its Connect a device card above.</small>';
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
                // A computer found on the network serves a certificate from its own authority, which nothing here trusts:
                // the join works with its code, which pins that certificate, and not by the address alone.
                if (n.https) {
                    showJoinCodeStatus('That computer’s certificate comes from its own authority. Paste its join code above ' +
                        '(Admin → Connect a device, on that computer) so this computer can check it is the right one.', true);
                }
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
