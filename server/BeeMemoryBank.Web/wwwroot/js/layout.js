// Layout helpers: markdown rendering, blank-line preservation, drop zone attachment,
// export dialog handling, and ambient navigation checks.

window.bmbPreserveBlankLines = function (md) {
    return (md || '').replace(/(?:\r\n|\n){3,}/g, function (run) {
        var units = run.match(/\r\n|\n/g).length;
        return '\n\n' + '&nbsp;\n\n'.repeat(units - 2);
    });
};

window.bmbRenderMarkdown = function (md, opts) {
    opts = opts || {};
    var parseOpts = { breaks: true };
    if (opts.renderer) parseOpts.renderer = opts.renderer;
    return DOMPurify.sanitize(marked.parse(window.bmbPreserveBlankLines(md), parseOpts));
};

window.bmbAttachDropZone = function (zone, pickerInput, onFiles) {
    if (!zone) return;
    var depth = 0;
    function disabled() { return zone.getAttribute('aria-disabled') === 'true'; }
    function hasFiles(e) {
        var t = e.dataTransfer && e.dataTransfer.types;
        return !!t && Array.prototype.indexOf.call(t, 'Files') >= 0;
    }
    zone.addEventListener('dragenter', function (e) {
        if (!hasFiles(e)) return;
        e.preventDefault();
        depth++;
        if (!disabled()) zone.classList.add('is-dragover');
    });
    zone.addEventListener('dragover', function (e) {
        if (!hasFiles(e)) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = disabled() ? 'none' : 'copy';
    });
    zone.addEventListener('dragleave', function () {
        depth = Math.max(0, depth - 1);
        if (!depth) zone.classList.remove('is-dragover');
    });
    zone.addEventListener('drop', function (e) {
        if (!hasFiles(e)) return;
        e.preventDefault();
        depth = 0;
        zone.classList.remove('is-dragover');
        if (disabled()) return;
        var files = Array.prototype.slice.call(e.dataTransfer.files || []);
        if (files.length) onFiles(files);
    });
    if (pickerInput) {
        zone.addEventListener('click', function () { if (!disabled()) pickerInput.click(); });
        zone.addEventListener('keydown', function (e) {
            if ((e.key === 'Enter' || e.key === ' ') && !disabled()) { e.preventDefault(); pickerInput.click(); }
        });
        pickerInput.addEventListener('change', function () {
            var files = Array.prototype.slice.call(pickerInput.files || []);
            pickerInput.value = '';
            if (files.length) onFiles(files);
        });
    }
};

window.bmbDownloadChoice = function (withImages, btn) {
    var dlg = document.getElementById('dlg-download-choice');
    if (!dlg) return;
    var payload = JSON.parse(dlg.dataset.payload || '{}');
    payload.withImages = withImages;
    // The dialog stays until the archive is made: a refusal is written in it, next to the button that was pressed (BMB-311).
    if (window.bmbDownload) {
        window.bmbDownload(payload, btn).then(function (ok) { if (ok) dlg.hide(); });
    }
};

(function () {
    function initLayout() {
        var link = document.getElementById('nav-ai-link');
        if (link) {
            fetch('/api-proxy/chat/access').then(function (r) { return r.ok ? r.json() : { allowed: true }; })
                .then(function (data) { if (!data.allowed) link.style.display = 'none'; })
                .catch(function () {});
        }

        var dlg = document.getElementById('dlg-download-choice');
        if (dlg && !dlg._bmbBound) {
            dlg._bmbBound = true;
            dlg.addEventListener('click', function (e) {
                var btn = e.target.closest('[data-download-choice]');
                if (!btn) return;
                var withImages = btn.getAttribute('data-download-choice') === 'true';
                window.bmbDownloadChoice(withImages, btn);
            });
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initLayout);
    } else {
        initLayout();
    }
})();

// ===== Node notices above the page (_Layout .app-alerts, _PeerNewerProtocolAlert) =====
// "Details" shows the whole sentence of a notice cut to one line. A notice that is closed and carries data-reshow-after="<seconds>" asks the
// node again after that time (/api-proxy/sync/status) and comes back while its state (data-reshow-state, a member of that answer) is still
// there; a page that is loaded again shows it at once, from the server. Closing is never "for ever" for a problem that lasts.
(function () {
    document.addEventListener('click', function (e) {
        var btn = e.target && e.target.closest ? e.target.closest('[data-alert-details]') : null;
        if (!btn) return;
        var alert = btn.closest('sl-alert');
        if (!alert) return;
        var expanded = alert.classList.toggle('expanded');
        btn.textContent = expanded ? 'Less' : 'Details';
        btn.setAttribute('aria-expanded', expanded ? 'true' : 'false');
    });

    function stillThere(state) {
        if (!state) return Promise.resolve(true);
        return fetch('/api-proxy/sync/status', { headers: { 'Accept': 'application/json' } })
            .then(function (r) { if (!r.ok) throw new Error('status ' + r.status); return r.json(); })
            .then(function (s) {
                var value = s ? s[state] : null;
                return value === true || (value !== null && value !== undefined && value !== false);
            })
            .catch(function () { return true; });   // the node could not be asked: the notice the page was given stands
    }

    document.addEventListener('sl-after-hide', function (e) {
        var alert = e.target;
        if (!alert || alert.tagName !== 'SL-ALERT' || !alert.hasAttribute('data-reshow-after')) return;
        var seconds = parseInt(alert.getAttribute('data-reshow-after'), 10);
        if (!(seconds > 0)) return;
        clearTimeout(alert._bmbReshow);
        alert._bmbReshow = setTimeout(function () {
            if (!document.contains(alert)) return;
            stillThere(alert.getAttribute('data-reshow-state')).then(function (yes) {
                if (yes && document.contains(alert)) alert.show();
            });
        }, seconds * 1000);
    });
})();
