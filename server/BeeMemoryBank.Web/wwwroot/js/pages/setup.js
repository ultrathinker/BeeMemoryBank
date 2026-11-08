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
        // the join pages start with the choice of how to connect
        setView(mode === 'join' ? 'join-choose' : 'standalone');
    }

    function setView(view) {
        var card = document.getElementById('setup-card');
        if (card) card.setAttribute('data-view', view);
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

    // A join code pasted from the other computer's Connect a device dialog (bmb-join:?a=<address>&t=<token>&s=<pin>): show the
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
    var NOT_A_JOIN_CODE = 'This does not look like a join code. Copy all of it from Connect a device on the other computer (Admin, Devices); it starts with bmb-join:';
    function setValidity(el, message) {
        if (el && typeof el.setCustomValidity === 'function') el.setCustomValidity(message || '');
    }
    function onJoinCodeChanged() {
        if (!joinCodeInput || !joinAddressInput) return;
        var code = (joinCodeInput.value || '').trim();
        if (!code) {
            joinAddressInput.disabled = false;
            showJoinCodeStatus('', true);
            setValidity(joinCodeInput, '');
            return;
        }
        var address = addressOfJoinCode(code);
        if (address) {
            joinAddressInput.value = address;
            joinAddressInput.disabled = true;
            showJoinCodeStatus('Join code read: this computer will join ' + address + ' and check that it is the right one before it sends the password.', true);
            setValidity(joinCodeInput, '');
        } else {
            joinAddressInput.disabled = false;
            showJoinCodeStatus(NOT_A_JOIN_CODE, false);
            // Not sent: the page would come back from the node with the same refusal, and the passwords would have to be typed again.
            setValidity(joinCodeInput, NOT_A_JOIN_CODE);
        }
    }

    // The chosen way's field is the one the browser insists on (the other one is hidden and not sent).
    function requireJoinWay(way) {
        if (joinCodeInput) joinCodeInput.required = way === 'code';
        if (joinAddressInput) joinAddressInput.required = way === 'url';
    }

    // The checks the node makes, made here too before a form is sent: a mistake in them does not reload the page, so nothing that was typed is
    // lost - the passwords included, which a page shown again from the node never carries (they are not written back into it). The node checks
    // everything again. The messages are the node's (Setup.cshtml.cs, PasswordHintPolicy), and so are the hint's limits (data-hint-min/max).
    function hintProblem(hint, password, username) {
        var text = (hint.value || '').trim();
        if (!text) return '';   // the required attribute says that one
        var min = parseInt(hint.getAttribute('data-hint-min'), 10) || 1;
        var max = parseInt(hint.getAttribute('data-hint-max'), 10) || 120;
        if (text.length < min || text.length > max) return 'The password hint must be ' + min + ' to ' + max + ' characters long.';
        if (/[\u0000-\u001f\u007f-\u009f]/.test(text)) return 'The password hint must not contain line breaks or control characters.';
        var h = text.toLowerCase();
        var p = (password || '').trim().toLowerCase();
        var u = (username || '').trim().toLowerCase();
        if (p && h.indexOf(p) !== -1) return 'The password hint must not contain the password.';
        if (u && h.indexOf(u) !== -1) return 'The password hint must not contain the username.';
        return '';
    }
    function wireSetupFormChecks(form) {
        var password = form.querySelector('[data-setup-password]');
        var confirm = form.querySelector('[data-setup-confirm]');
        var hint = form.querySelector('[data-setup-hint]');
        var username = form.querySelector('[data-setup-username]');
        function check() {
            if (password && confirm) {
                setValidity(confirm, confirm.value && confirm.value !== password.value ? 'Passwords do not match.' : '');
            }
            if (hint) setValidity(hint, hintProblem(hint, password ? password.value : '', username ? username.value : ''));
        }
        [password, confirm, hint, username].forEach(function (el) {
            if (!el) return;
            el.addEventListener('sl-input', check);
            el.addEventListener('input', check);
        });
        // Before Shoelace's own submit check (a capture listener on the form runs first), so what it checks is up to date.
        form.addEventListener('submit', check, true);
        check();
    }
    if (joinCodeInput) {
        joinCodeInput.addEventListener('sl-input', onJoinCodeChanged);
        joinCodeInput.addEventListener('input', onJoinCodeChanged);
    }

    // Join, page 1 -> page 2: the person chose a code or an address. Only the chosen way is sent, so the other field is emptied.
    function chooseJoinWay(way) {
        var hidden = document.getElementById('join-way');
        if (hidden) hidden.value = way;
        if (way === 'code') {
            if (joinAddressInput) { joinAddressInput.value = ''; joinAddressInput.disabled = false; }
            onJoinCodeChanged();
        } else {
            if (joinCodeInput) joinCodeInput.value = '';
            if (joinAddressInput) joinAddressInput.disabled = false;
            showJoinCodeStatus('', true);
            setValidity(joinCodeInput, '');
        }
        requireJoinWay(way);
        setView(way === 'code' ? 'join-code' : 'join-url');
        var target = way === 'code' ? joinCodeInput : joinAddressInput;
        if (target && target.focus) setTimeout(function () { target.focus(); }, 50);
    }
    var btnJoinWayCode = document.getElementById('btn-join-way-code');
    var btnJoinWayUrl = document.getElementById('btn-join-way-url');
    if (btnJoinWayCode) btnJoinWayCode.addEventListener('click', function () { chooseJoinWay('code'); });
    if (btnJoinWayUrl) btnJoinWayUrl.addEventListener('click', function () { chooseJoinWay('url'); });

    // Back from the join details goes to the choice of how to connect; from anywhere else it is the plain link (the start page).
    var linkFormBack = document.getElementById('link-form-back');
    if (linkFormBack) {
        linkFormBack.addEventListener('click', function (e) {
            var card = document.getElementById('setup-card');
            var view = card ? card.getAttribute('data-view') : '';
            if (view === 'join-code' || view === 'join-url') {
                e.preventDefault();
                setView('join-choose');
            }
        });
    }

    // Once the Shoelace inputs are defined (their value and setCustomValidity exist only then): the checks of both forms, and a join form that
    // came back from the node with an error opens on the way that was chosen, its code read again.
    function initSetupForms() {
        document.querySelectorAll('form[data-setup-form]').forEach(wireSetupFormChecks);
        var card = document.getElementById('setup-card');
        var view = card ? card.getAttribute('data-view') : '';
        if (view === 'join-code' || view === 'join-url') {
            requireJoinWay(view === 'join-code' ? 'code' : 'url');
            if (view === 'join-code') onJoinCodeChanged();
        }
    }
    if (window.customElements && customElements.whenDefined) {
        customElements.whenDefined('sl-input').then(initSetupForms);
    } else {
        initSetupForms();
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
                '<small class="text-muted">Nothing found on your network. A computer shows up while it is on this network and direct sync is on for it ' +
                '(Admin \u2192 Devices \u2192 Network settings). Otherwise ask for its code: Admin \u2192 Devices \u2192 Connect a device, on that computer.</small>';
            return;
        }
        var head = document.createElement('small');
        head.className = 'text-muted';
        head.textContent = nodes.length + ' device' + (nodes.length > 1 ? 's' : '') +
            ' found \u2014 each one still needs its join code:';
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
                // Finding a computer only says which one to ask: the join goes to the address inside its code, and the code is what lets this computer check
                // that it reached the right one. So nothing is filled in; the person is told where the code is and the code box is ready.
                showJoinCodeStatus((n.name || n.host || n.url) + ' is on this network. On that computer open Admin, then Devices, then Connect a device, ' +
                    'copy the code it shows and paste it above.', true);
                if (joinCodeInput && joinCodeInput.focus) joinCodeInput.focus();
            });
            box.appendChild(btn);
        });
    }

    // The "please wait" pop-up and the busy buttons of the three forms are the one busy helper's (bmb-busy.js, BMB-311): the forms carry
    // data-busy="long", a title and a text.
})();
