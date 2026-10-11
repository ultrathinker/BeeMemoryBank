// The encryption page (docs/design/step-b-spec.md 10.9, 11.2): follows "Encrypt this node" while it runs, and walks the two steps of
// "Start unlocked after a restart".
(function () {
    var data = {};
    var pageData = document.getElementById('page-data');
    if (pageData) {
        try { data = JSON.parse(pageData.textContent) || {}; } catch (e) { data = {}; }
    }

    // ---- progress ----------------------------------------------------------------------------------------------------------------
    var panel = document.getElementById('seal-progress');
    var bar = document.getElementById('seal-progress-bar');
    var text = document.getElementById('seal-progress-text');

    var steps = {
        preflight: 'Checking the node',
        checkpoint: 'Folding the databases into their files',
        snapshot: 'Making the way-back snapshot',
        'encrypt-vault': 'Encrypting the vault',
        'encrypt-chat': 'Encrypting the chat history',
        'boot-store': 'Writing the boot store',
        verify: 'Checking every row of the encrypted copy',
        swap: 'Switching to the encrypted files and removing the old ones',
        scan: 'Looking for unencrypted copies',
        done: 'Done'
    };

    function show(progress) {
        if (!panel) return;
        panel.style.display = '';
        if (bar) bar.value = progress.percent || 0;
        if (text) text.textContent = steps[progress.step] || progress.step || '';
    }

    function poll() {
        fetch('/api-proxy/vault/seal/progress', { credentials: 'same-origin' })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (p) {
                if (!p) { setTimeout(poll, 3000); return; }
                if (p.phase === 'running') { show(p); setTimeout(poll, 1000); return; }
                if (p.phase === 'done') { show(p); setTimeout(function () { location.href = location.pathname + '?msg=' + encodeURIComponent('This node\'s database is encrypted.'); }, 1200); return; }
                if (p.phase === 'failed') {
                    var msg = (p.step === 'locked' ? 'Encrypted, but kept locked (' : 'Not encrypted (') + p.error_code + '): ' + (p.error || '');
                    location.href = location.pathname + '?err=' + encodeURIComponent(msg);
                    return;
                }
                // idle: the node is not encrypting (yet, or any more): nothing to follow
            })
            .catch(function () { setTimeout(poll, 3000); });   // the node is out of service while it works: ask again
    }

    if (data.running) poll();

    var form = document.getElementById('seal-form');
    if (form) {
        form.addEventListener('submit', function () {
            // The page is posted and reloaded; the reload (running) starts the polling.
            if (panel) panel.style.display = '';
            if (text) text.textContent = 'Starting...';
        });
    }

    // ---- start unlocked after a restart --------------------------------------------------------------------------------------------
    var start = document.getElementById('server-auto-unlock-start');
    var step1 = document.getElementById('server-auto-unlock-step1');
    var step2 = document.getElementById('server-auto-unlock-step2');
    if (start && step1 && step2) {
        var phrase = document.getElementById('server-auto-unlock-phrase');
        var enable = document.getElementById('server-auto-unlock-enable');
        var formEl = document.getElementById('server-auto-unlock-form');

        start.addEventListener('click', function () { step1.show(); });
        document.getElementById('server-auto-unlock-cancel1').addEventListener('click', function () { step1.hide(); });
        document.getElementById('server-auto-unlock-go').addEventListener('click', function () { step1.hide(); step2.show(); });
        document.getElementById('server-auto-unlock-cancel2').addEventListener('click', function () { step2.hide(); });

        // Exact and case-sensitive; the server checks it again.
        phrase.addEventListener('sl-input', function () { enable.disabled = phrase.value !== data.phrase; });
        enable.addEventListener('click', function () { if (phrase.value === data.phrase) formEl.submit(); });
    }
})();
