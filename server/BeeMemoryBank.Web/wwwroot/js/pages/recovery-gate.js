(function () {
    // The blocking recovery-key step. The dialog must not be closable by Esc, by its X (hidden in the page's
    // style) or by a click on the overlay: the only way on is the confirmation it asks for.
    var dialog = document.getElementById('recovery-key-gate-dialog');
    if (dialog) {
        dialog.addEventListener('sl-request-close', function (event) { event.preventDefault(); });
    }

    // "Continue" stays disabled until the person ticks that the key was saved.
    var checkbox = document.getElementById('saved-recovery-key');
    var continueButton = document.getElementById('recovery-key-continue');
    if (checkbox && continueButton) {
        var update = function () { continueButton.disabled = !checkbox.checked; };
        checkbox.addEventListener('sl-change', update);
        update();
    }

    var copyButton = document.getElementById('recovery-key-copy');
    var keyInput = document.getElementById('recovery-key-value');
    if (copyButton && keyInput) {
        copyButton.addEventListener('click', function () {
            var text = keyInput.value || '';
            function done() {
                copyButton.innerHTML = '<sl-icon slot="prefix" name="check2"></sl-icon> Copied';
            }
            function fallback() {
                var area = document.createElement('textarea');
                area.value = text;
                area.setAttribute('readonly', '');
                area.style.position = 'fixed';
                area.style.top = '-1000px';
                document.body.appendChild(area);
                area.select();
                try { document.execCommand('copy'); done(); } catch (e) { /* the key is still on screen to copy by hand */ }
                document.body.removeChild(area);
            }
            if (navigator.clipboard && navigator.clipboard.writeText) {
                navigator.clipboard.writeText(text).then(done, fallback);
            } else {
                fallback();
            }
        });
    }
})();