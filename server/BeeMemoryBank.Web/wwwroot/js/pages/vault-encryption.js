// The Database encryption page of the Admin area (docs/design/step-b-spec.md 10.9, 11.2): follows "Encrypt this node" while it runs, and walks the two steps of
// "Start unlocked after a restart".
(function () {
    var data = {};
    var pageData = document.getElementById('page-data');
    if (pageData) data = { running: pageData.dataset.running === 'true', phrase: pageData.dataset.phrase };

    // The pages of the Admin area are swapped in by the single-page navigation: the polling below dies with the page that started it, or it would send the NEXT page
    // to the address of the progress when the encryption ends.
    var stopped = false;
    var pageSignal = window.bmbGetPageSignal ? window.bmbGetPageSignal() : null;
    if (pageSignal) pageSignal.addEventListener('abort', function () { stopped = true; });

    // ---- progress ----------------------------------------------------------------------------------------------------------------
    var panel = document.getElementById('seal-progress');
    var bar = document.getElementById('seal-progress-bar');
    var text = document.getElementById('seal-progress-text');

    var steps = {
        preflight: 'Checking the node',
        checkpoint: 'Folding the databases into their files',
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
        if (stopped) return;
        fetch('/api-proxy/vault/seal/progress', { credentials: 'same-origin' })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (p) {
                if (stopped) return;
                if (!p) { setTimeout(poll, 3000); return; }
                if (p.phase === 'running') { show(p); setTimeout(poll, 1000); return; }
                if (p.phase === 'done') { show(p); setTimeout(function () { location.href = location.pathname + '?msg=' + encodeURIComponent('This node\'s database is encrypted.') + '#card-database-encryption'; }, 1200); return; }
                if (p.phase === 'failed') {
                    var msg = (p.step === 'locked' ? 'Encrypted, but kept locked (' : 'Not encrypted (') + p.error_code + '): ' + (p.error || '');
                    location.href = location.pathname + '?err=' + encodeURIComponent(msg) + '#card-database-encryption';
                    return;
                }
                // idle: the node is not encrypting (yet, or any more): nothing to follow
            })
            .catch(function () { setTimeout(poll, 3000); });   // the node is out of service while it works: ask again
    }

    if (data.running) poll();

    var form = document.getElementById('seal-form');
    if (form) {
        form.addEventListener('submit', function (e) {
            // The question of the form (site.js, in the capture phase) has been asked already: a declined one prevented the send, and nothing is starting.
            if (e.defaultPrevented) return;
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
        enable.addEventListener('click', function () { if (phrase.value === data.phrase) bmbBusy.submit(formEl); });
    }
})();
