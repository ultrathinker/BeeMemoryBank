// Setup wizard logic (mode selection, forms, network scan, and submission loader)
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

        updateStepDots(2);
        if (mode === 'join') scanNetwork();
    }

    function updateStepDots(activeStep) {
        var dots = document.querySelectorAll('.step-dot');
        var lines = document.querySelectorAll('.step-line');
        dots.forEach(function (dot, i) {
            dot.classList.remove('active', 'done');
            if (i + 1 < activeStep) dot.classList.add('done');
            else if (i + 1 === activeStep) dot.classList.add('active');
        });
        lines.forEach(function (line, i) {
            line.classList.toggle('done', i + 1 < activeStep);
        });
    }

    var linkRestoreLegacy = document.getElementById('link-restore-legacy');
    var linkLegacyBack = document.getElementById('link-legacy-back');
    if (linkRestoreLegacy) {
        linkRestoreLegacy.addEventListener('click', function (e) {
            e.preventDefault();
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

    var btnScan = document.getElementById('btn-scan-network');
    if (btnScan) btnScan.addEventListener('click', scanNetwork);

    var joinFormOnLoad = document.getElementById('form-join');
    if (joinFormOnLoad && joinFormOnLoad.style.display !== 'none') scanNetwork();

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
            'Searching your network\u2026</small>';
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
                '<small class="text-muted">No BeeMemoryBank nodes found on your network. ' +
                'Enter the remote node URL manually below.</small>';
            return;
        }
        var head = document.createElement('small');
        head.className = 'text-muted';
        head.textContent = nodes.length + ' node' + (nodes.length > 1 ? 's' : '') +
            ' found on your network \u2014 click to use:';
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
                if (title) title.textContent = 'Joining network\u2026';
                if (msg) msg.textContent = 'Exchanging keys, downloading signed snapshot, importing data. This may take up to a minute.';
            } else {
                if (title) title.textContent = 'Creating network\u2026';
                if (msg) msg.textContent = 'Generating keys and initializing local database.';
            }
            form.querySelectorAll('[data-submit-btn]').forEach(function (b) { b.loading = true; b.disabled = true; });
            if (dlg && typeof dlg.show === 'function') dlg.show();
            else if (dlg) dlg.open = true;
        });
    });
})();
