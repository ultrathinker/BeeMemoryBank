(function () {
    function ready(fn) {
        Promise.all([
            customElements.whenDefined('sl-select'),
            customElements.whenDefined('sl-radio-group')
        ]).then(fn);
    }

    ready(function () {
        var prov = document.getElementById('ddns-provider');
        var simpleWrap = document.getElementById('ddns-simple-fields');
        var cfWrap = document.getElementById('ddns-cloudflare-fields');
        var tokenEl = document.getElementById('ddns-token');
        var zoneEl = document.getElementById('ddns-zone');
        var recordEl = document.getElementById('ddns-record');
        var cfTokenEl = document.getElementById('ddns-cf-token');

        function setDisabled(el, disabled) { if (el) el.disabled = disabled; }

        function syncProvider() {
            var v = (prov && prov.value) || 'duckdns';
            var isCf = v === 'cloudflare';
            if (simpleWrap) simpleWrap.style.display = isCf ? 'none' : 'block';
            if (cfWrap) cfWrap.style.display = isCf ? 'flex' : 'none';
            setDisabled(tokenEl, isCf);
            setDisabled(zoneEl, !isCf);
            setDisabled(recordEl, !isCf);
            setDisabled(cfTokenEl, !isCf);
        }
        if (prov) { prov.addEventListener('sl-change', syncProvider); syncProvider(); }

        var ipmode = document.getElementById('ddns-ipmode');
        var staticWrap = document.getElementById('ddns-static-ip-wrap');
        function syncIpMode() {
            var v = (ipmode && ipmode.value) || 'upnp';
            if (staticWrap) staticWrap.style.display = (v === 'static') ? 'block' : 'none';
        }
        if (ipmode) { ipmode.addEventListener('sl-change', syncIpMode); syncIpMode(); }

        var staging = document.getElementById('acme-staging');
        var warn = document.getElementById('acme-prod-warning');
        function syncStaging() {
            var v = (staging && staging.value) || 'true';
            if (warn) warn.style.display = (v === 'false') ? 'block' : 'none';
        }
        if (staging) { staging.addEventListener('sl-change', syncStaging); syncStaging(); }

        var btnCheckDdns = document.getElementById('btn-check-ddns-now');
        if (btnCheckDdns) {
            btnCheckDdns.addEventListener('click', function (e) {
                if (!confirm('Run a DDNS check now? This contacts your provider to update the DNS record if your IP changed.')) {
                    e.preventDefault();
                }
            });
        }

        var btnAcmeReq = document.getElementById('btn-acme-request');
        if (btnAcmeReq) {
            btnAcmeReq.addEventListener('click', function () {
                document.getElementById('dlg-acme-request')?.show();
            });
        }

        document.querySelectorAll('[data-dlg-cancel]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var dlgId = btn.getAttribute('data-dlg-cancel');
                document.getElementById(dlgId)?.hide();
            });
        });
    });
})();
