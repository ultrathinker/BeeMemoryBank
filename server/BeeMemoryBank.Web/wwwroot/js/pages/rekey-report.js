(function () {
    const $ = id => document.getElementById(id);

    // Text only: the report names paths and peers, never HTML.
    function li(text) {
        const item = document.createElement('li');
        item.textContent = text;
        return item;
    }

    const results = {
        'done': ['success', 'The re-key is done.', ''],
        'preflight-refused': ['warning', 'The re-key did not start.', 'The check before it found the problems below. Nothing was changed; the vault is as it was.'],
        'failed': ['danger', 'The re-key failed before the switch.', 'The old vault is still the one in use; nothing in it was changed.'],
        'swap-pending': ['warning', 'The switch to the new vault is not finished.', 'It finishes at the next start of the node.'],
    };

    function show(data) {
        const report = data.report;
        const [variant, title, detail] = results[report.result] || ['neutral', `Result: ${report.result}`, ''];
        $('rekey-result').variant = variant;
        $('rekey-result-title').textContent = title;
        $('rekey-result-detail').textContent = report.error ? `${detail} ${report.error}`.trim() : detail;
        $('rekey-next').style.display = report.result === 'done' ? '' : 'none';

        if (report.oldVault) {
            $('rekey-old-vault').style.display = '';
            $('rekey-old-vault-path').textContent = report.oldVault;
            $('rekey-old-vault-gone').style.display = data.oldVaultExists ? 'none' : '';
        }

        const preflight = report.preflight;
        if (preflight && (preflight.blocking.length || preflight.warnings.length)) {
            $('rekey-warnings-card').style.display = '';
            for (const p of preflight.blocking) $('rekey-blocking').appendChild(li(`${p.table} ${p.rowKey}: ${p.problem}`));
            for (const w of preflight.warnings) $('rekey-warnings').appendChild(li(w));
        }

        for (const step of report.steps || []) {
            const counts = Object.entries(step.counts || {});
            for (const [what, n] of counts.length ? counts : [['', '']]) {
                const tr = document.createElement('tr');
                for (const [text, right] of [[step.name, false], [what, false], [String(n), true]]) {
                    const td = document.createElement('td');
                    td.textContent = text;
                    if (right) td.style.textAlign = 'right';
                    tr.appendChild(td);
                }
                $('rekey-steps').appendChild(tr);
            }
            for (const note of step.notes || []) $('rekey-lists').appendChild(li(`${step.name}: ${note}`));
        }
        for (const [label, list] of [['Revoked node', report.revokedPeers], ['Cleared slot', report.clearedSlots],
            ['Revoked agent', report.clearedAgents], ['User to reset (by you)', report.resetUsers]])
            for (const x of list || []) $('rekey-lists').appendChild(li(`${label}: ${x}`));

        $('rekey-body').style.display = '';
    }

    async function load() {
        try {
            const resp = await fetch('/api-proxy/rekey/report');
            if (resp.status === 404) { $('rekey-none').style.display = ''; return; }
            if (!resp.ok) throw new Error(`The report could not be loaded (${resp.status}).`);
            show(await resp.json());
        } catch (e) {
            $('rekey-error-text').textContent = e.message;
            $('rekey-error').style.display = '';
        } finally {
            $('rekey-loading').style.display = 'none';
        }
    }

    load();
})();
