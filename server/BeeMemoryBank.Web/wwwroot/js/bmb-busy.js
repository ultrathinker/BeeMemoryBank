// ===== BMB-311: ONE helper for every button of the Web UI that sends something =====
//
// A short operation (a switch, a rename, a save): the button is disabled at once and spins until the answer comes.
// A long operation (encrypting, a backup, a restore, a key change, an update, a pairing, an import, a snapshot ...): a modal pop-up with a loader and
// "please wait" covers the page, nothing under it can be clicked a second time, and the result - the success, or the whole error text - is shown where
// the button was (in its dialog, its form or its card).
//
// Two ways in, both through this file:
//   1. A form: <form method="post" data-busy="short|long" data-busy-title="Encrypting this node" data-busy-text="..." data-busy-exempt="why not">
//      (data-busy with no value is short). On submit - after any confirmation - the form is marked busy; the page is replaced by the answer, which
//      shows its result in the card of the button (BMB-303). A form that is not to be marked says why: data-busy-exempt="reason".
//   2. Script: bmbBusy.run(button, function () { return bmbBusy.request(url, init).then(...); }, { long: true, title: '...', success: '...' }).
//      The work returns a promise; whatever it throws is shown in full next to the button. bmbBusy.request(...) is fetch with the node's refusals
//      turned into words (the body's detail/error/message/title, a JSON string, or the text of the answer; else what the status means).
//
// The lock lasts as long as the work does. A time limit only changes what the page SAYS ("still running, do not start it again"): it never gives the
// control back, because a lock that let go on a timer would allow a second start of something that is still going on. The control comes back when the
// work ends (an answer, a failure) or when the page is left; the server refuses a second start on its own (409 already running).
//
// The check that no button bypasses this is tests/BeeMemoryBank.Integration.Tests/BusyHelperGuardTests.cs.
(function () {
    'use strict';

    var SHORT_NOTICE_MS = 120000;           // after this a short operation says that it is still running ...
    var LONG_NOTICE_MS = 30 * 60 * 1000;    // ... and a long one after half an hour. The lock stays.
    var STILL_RUNNING = 'Still waiting for the node. This is taking longer than usual and may still be running: do not start it again. ' +
        'Reload this page later to see whether it has finished.';
    var PREVIEW_LIMIT = 1000;               // how much of a long message is in the line itself; the whole of it is in the expandable area under it
    var depth = 0;
    var opener = null;                      // what had the focus when the pop-up came up
    var running = new WeakSet();            // the targets whose work is in flight
    var operations = new Set();             // the operations of the page that is shown now (see the cleanup below)

    function create(tag, cls, html) {
        var el = document.createElement(tag);
        if (cls) el.className = cls;
        if (html != null) el.innerHTML = html;
        return el;
    }

    // ---- the pop-up of a long operation (also used directly by the exports and imports: bmbShowBusyModal) ---------------------------------
    function showModal(title, message) {
        if (depth === 0) opener = document.activeElement || null;
        depth++;
        var modal = document.getElementById('bmb-busy-modal');
        if (!modal) {
            modal = document.createElement('sl-dialog');
            modal.id = 'bmb-busy-modal';
            modal.className = 'bmb-busy-modal';
            modal.setAttribute('no-header', '');
            // What a screen reader announces: the name (what is running) and the description (the line that says what to do).
            modal.setAttribute('role', 'alertdialog');
            modal.setAttribute('aria-modal', 'true');
            modal.setAttribute('aria-labelledby', 'bmb-busy-title');
            modal.setAttribute('aria-describedby', 'bmb-busy-msg');
            modal.innerHTML =
                '<div class="bmb-busy-body" style="display:flex;gap:16px;align-items:center;">' +
                '  <sl-spinner class="bmb-busy-spinner" style="font-size:2.4rem;flex-shrink:0;"></sl-spinner>' +
                '  <div class="bmb-busy-text">' +
                '    <div class="bmb-busy-title" id="bmb-busy-title" style="font-weight:600;"></div>' +
                '    <div class="bmb-busy-msg" id="bmb-busy-msg"></div>' +
                '  </div>' +
                '</div>';
            document.body.appendChild(modal);
            // Nothing closes it but the end of the work: not Escape, not a click outside.
            modal.addEventListener('sl-request-close', function (e) { e.preventDefault(); });
        }
        document.getElementById('bmb-busy-title').textContent = title || 'Working…';
        document.getElementById('bmb-busy-msg').textContent = message || 'Please wait. This can take a while. Do not close this page.';
        modal.setAttribute('aria-busy', 'true');
        if (typeof modal.show === 'function') modal.show(); else modal.setAttribute('open', '');
        if (typeof modal.focus === 'function') modal.focus();   // the focus moves into the dialog (it traps it there while it is open)
    }

    function hideModal() {
        depth = Math.max(0, depth - 1);
        if (depth > 0) return;
        var modal = document.getElementById('bmb-busy-modal');
        if (modal) {
            modal.removeAttribute('aria-busy');
            if (typeof modal.hide === 'function') modal.hide(); else modal.removeAttribute('open');
        }
        // The focus goes back to where it was, when that is still on the page.
        var back = opener;
        opener = null;
        if (back && typeof back.focus === 'function' && document.contains(back)) back.focus();
    }

    // A long piece of work that knows how far it is says so in the pop-up's line.
    function setModalMessage(message) {
        var msg = document.getElementById('bmb-busy-msg');
        if (msg) msg.textContent = message;
    }

    window.bmbShowBusyModal = showModal;
    window.bmbHideBusyModal = hideModal;
    window.bmbSetBusyModalMessage = setModalMessage;

    // ---- the button -----------------------------------------------------------------------------------------------------------------
    function setBusy(el, on) {
        if (!el) return;
        el.dataset.busyNow = on ? '1' : '';
        if ('loading' in el) el.loading = on;
        if ('disabled' in el) el.disabled = on;
        if (on) el.setAttribute('aria-busy', 'true'); else el.removeAttribute('aria-busy');
    }

    function isBusy(el) { return !!(el && el.dataset && el.dataset.busyNow === '1'); }

    // A message, as TEXT (never markup: an error can carry anything), WHOLE. A long one shows its first part in the line and all of it in an area that opens
    // (the card asks for the full error text: what the node said is the news, and a cut-off sentence is not).
    function appendMessage(parent, text) {
        text = String(text);
        if (text.length <= PREVIEW_LIMIT) { parent.appendChild(document.createTextNode(text)); return; }
        parent.appendChild(document.createTextNode(text.slice(0, PREVIEW_LIMIT) + '…'));
        var details = create('details', 'bmb-busy-full');
        var summary = create('summary');
        summary.textContent = 'Show the whole message (' + text.length + ' characters)';
        var full = create('pre', 'bmb-busy-full-text');
        full.textContent = text;
        full.style.whiteSpace = 'pre-wrap';
        full.style.overflowWrap = 'anywhere';
        full.style.maxHeight = '40vh';
        full.style.overflow = 'auto';
        details.appendChild(summary);
        details.appendChild(full);
        parent.appendChild(details);
    }

    // ---- the result, where the button was ---------------------------------------------------------------------------------------------
    function scopeOf(el) {
        if (!el) return document.body;
        return el.closest('[data-busy-scope]') || el.closest('sl-dialog') || el.closest('form') || el.closest('.card') || el.parentElement || document.body;
    }

    // kind: true / 'success', false / 'error', or 'warning' (the line that says that something is still going on)
    function showResult(anchor, kind, text) {
        var scope = scopeOf(anchor);
        var host = null;
        for (var i = 0; i < scope.children.length; i++) {
            if (scope.children[i].classList && scope.children[i].classList.contains('bmb-busy-result-host')) { host = scope.children[i]; break; }
        }
        if (!host) {
            host = create('div', 'bmb-busy-result-host');
            host.setAttribute('aria-live', 'polite');
            scope.appendChild(host);
        }
        host.innerHTML = '';
        if (!text) return;
        var variant = kind === true || kind === 'success' ? 'success' : kind === 'warning' ? 'warning' : 'danger';
        var alert = create('sl-alert', 'bmb-busy-result');
        var icon = create('sl-icon');
        icon.slot = 'icon';
        icon.name = variant === 'success' ? 'check-circle' : variant === 'warning' ? 'hourglass-split' : 'exclamation-octagon';
        alert.appendChild(icon);
        appendMessage(alert, text);   // text, never markup: an error can carry anything
        alert.variant = variant;
        alert.setAttribute('variant', variant);
        alert.setAttribute('open', '');
        alert.setAttribute('closable', '');
        alert.setAttribute('role', variant === 'danger' ? 'alert' : 'status');
        alert.style.marginTop = '10px';
        host.appendChild(alert);
        if (alert.scrollIntoView) alert.scrollIntoView({ block: 'nearest' });
    }

    // The result of an operation whose page is gone (a page of the single-page navigation was replaced while it ran): it is not drawn into the page that
    // is shown now, which has nothing to do with it, but said once, on the screen, as a notice.
    function notifyLate(ok, text) {
        var alert = create('sl-alert');
        var variant = ok ? 'success' : 'danger';
        alert.variant = variant;
        alert.setAttribute('variant', variant);
        alert.setAttribute('closable', '');
        alert.setAttribute('role', ok ? 'status' : 'alert');
        alert.appendChild(document.createTextNode('A request you started on the page you left ' + (ok ? 'has finished.' : 'failed: ')));
        if (!ok) appendMessage(alert, text);
        document.body.appendChild(alert);
        if (typeof alert.toast === 'function') alert.toast(); else alert.setAttribute('open', '');
    }

    function messageOf(error) {
        if (!error) return 'The operation failed.';
        if (typeof error === 'string') return error;
        return error.message || String(error);
    }

    // ---- script: run a piece of work behind a button ----------------------------------------------------------------------------------
    //   target: the control that was used (sl-button, sl-switch, button ...) or null
    //   options: long (modal), title, text (for the modal), success (a string, or a function of the work's result), quiet (no result line on
    //   success), error (a function that shows the failure itself, instead of the line next to the button), scope (where the line goes)
    // Always resolves: true when the work ended well, false when it failed (the failure is shown) or was already running.
    function run(target, work, options) {
        options = options || {};
        if (target && running.has(target)) return Promise.resolve(false);   // no second click
        if (target) running.add(target);
        setBusy(target, true);
        var anchor = options.scope || target;
        if (!options.error) showResult(anchor, true, '');

        var op = { target: target, anchor: anchor, long: !!options.long, modal: false, released: false, orphan: false, timer: null };
        operations.add(op);
        if (op.long) { showModal(options.title, options.text); op.modal = true; }

        function closeModal() {
            if (op.modal) { op.modal = false; hideModal(); }
        }
        function release() {
            if (op.released) return;
            op.released = true;
            clearTimeout(op.timer);
            closeModal();
            setBusy(target, false);
            if (target) running.delete(target);
            operations.delete(op);
        }
        // Not a release: after the notice time the page says that this is still going on, and keeps the control locked.
        op.timer = setTimeout(function () {
            if (op.released || op.orphan) return;
            if (op.long) setModalMessage(STILL_RUNNING); else showResult(anchor, 'warning', STILL_RUNNING);
        }, op.long ? LONG_NOTICE_MS : SHORT_NOTICE_MS);
        op.leave = function () {   // the page was replaced
            op.orphan = true;
            clearTimeout(op.timer);
            closeModal();
        };

        return Promise.resolve()
            .then(work)
            .then(function (result) {
                // The modal goes first: what is shown next is seen on the page, not under it.
                var orphan = op.orphan;
                release();
                if (!options.quiet) {
                    var text = typeof options.success === 'function' ? options.success(result) : options.success;
                    if (text) { if (orphan) notifyLate(true); else showResult(anchor, true, text); }
                }
                return true;
            }, function (error) {
                var orphan = op.orphan;
                release();
                if (orphan) notifyLate(false, messageOf(error));
                else if (options.error) options.error(error);
                else showResult(anchor, false, messageOf(error));
                return false;
            });
    }

    // What the node said, in a bounded, safe order: the ProblemDetails "detail" first, then "error", "message", "title", a JSON string, and last the text of
    // the answer itself (not a page of HTML, which is some proxy's). Always returned as text, to be put into the page as text and never as markup.
    function saidOf(body, text) {
        function pick(value) {
            if (typeof value === 'string') return value.trim();
            if (value && typeof value === 'object' && typeof value.message === 'string') return value.message.trim();   // {error: {message}}
            return '';
        }
        var said = '';
        if (typeof body === 'string') said = body.trim();
        else if (body && typeof body === 'object') said = pick(body.detail) || pick(body.error) || pick(body.message) || pick(body.title);
        if (!said && typeof text === 'string') {
            var raw = text.trim();
            if (raw && raw.charAt(0) !== '<') said = raw;
        }
        return said;
    }

    // fetch, with the node's refusals turned into words. Resolves {status, body, text, response}; throws an Error (with status and body) otherwise.
    var STATUS_WORDS = {
        401: 'Your session has ended. Sign in again.',
        403: 'This account is not allowed to do this, or the vault is locked.',
        404: 'The node does not know this (it may have been removed). Reload the page.',
        409: 'This is already running, or it conflicts with something that is. Wait until it has finished.'
    };
    function request(url, init) {
        return fetch(url, init).then(function (response) {
            return response.text().then(function (text) {
                var body = null;
                try { body = text ? JSON.parse(text) : null; } catch (e) { body = null; }
                if (response.ok) return { status: response.status, body: body, text: text, response: response };
                var said = saidOf(body, text);
                var error = new Error(said || STATUS_WORDS[response.status] || ('The node answered ' + response.status + '.'));
                error.status = response.status;
                error.body = body;
                throw error;
            });
        }, function () {
            throw new Error('The node did not answer. Check that it is running and try again.');
        });
    }

    // ---- a form: data-busy ------------------------------------------------------------------------------------------------------------
    var SUBMIT_BUTTONS = 'sl-button[type="submit"], button[type="submit"], [data-submit-btn]';
    var formTimers = new WeakMap();

    function resetForm(form) {
        form.dataset.busyNow = '';
        clearTimeout(formTimers.get(form));
        form.querySelectorAll(SUBMIT_BUTTONS).forEach(function (b) { setBusy(b, false); });
        form.querySelectorAll('[data-busy-note]').forEach(function (n) { n.hidden = true; });
        if (form._bmbModal) { form._bmbModal = false; hideModal(); }   // the pop-up of a send that did not happen (a declined confirmation) goes with it
    }

    // What the person SEES while the work goes on: the buttons spin, the note is shown, the pop-up of a long operation is up. Not yet `disabled` when
    // <paramref name="seenOnly"/>: the data of a form is taken when the submit event ends, and a disabled submit button is not part of it.
    function paintBusy(el, on) {
        if (!el) return;
        el.dataset.busyNow = on ? '1' : '';
        if ('loading' in el) el.loading = on;
        if (on) el.setAttribute('aria-busy', 'true'); else el.removeAttribute('aria-busy');
    }

    function markForm(form, isLong, seenOnly) {
        form.dataset.busyNow = '1';
        form.querySelectorAll(SUBMIT_BUTTONS).forEach(function (b) { if (seenOnly) paintBusy(b, true); else setBusy(b, true); });
        form.querySelectorAll('[data-busy-note]').forEach(function (n) { n.hidden = false; });
        if (isLong && !form._bmbModal) { form._bmbModal = true; showModal(form.getAttribute('data-busy-title'), form.getAttribute('data-busy-text')); }
        // The page is replaced when the answer comes, so nothing has to be undone. If the answer takes very long, the page says so - and the form stays
        // locked: letting go here would allow a second send of something that may still be running.
        clearTimeout(formTimers.get(form));
        formTimers.set(form, setTimeout(function () {
            if (form.dataset.busyNow !== '1') return;   // the page was replaced long ago, or the form was let go
            if (isLong) setModalMessage(STILL_RUNNING); else showResult(form, 'warning', STILL_RUNNING);
        }, isLong ? LONG_NOTICE_MS : SHORT_NOTICE_MS));
    }

    // A script that sends a form (a switch that was flipped, a confirmed button) sends it through here, so the form's data-busy applies.
    function submitForm(form) {
        if (!form) return;
        if (form.dataset.busyNow === '1') return;   // no second send
        if (form.hasAttribute('data-busy')) markForm(form, form.getAttribute('data-busy') === 'long');
        HTMLFormElement.prototype.submit.call(form);
    }

    window.bmbBusy = { run: run, request: request, submit: submitForm, showResult: showResult, setBusy: setBusy, isBusy: isBusy, showModal: showModal, hideModal: hideModal, setModalMessage: setModalMessage };

    window.bmbSetFormBusy = function (form, on) {   // kept for the callers that passed a form
        if (on) markForm(form, form.getAttribute('data-busy') === 'long'); else resetForm(form);
    };

    document.addEventListener('submit', function (e) {
        var form = e.target;
        if (!form || !form.matches || !form.matches('form[data-busy]')) return;
        if (form.dataset.busyNow === '1') {   // no second send: not from a second click, not from a script that submits in the same task
            e.preventDefault();
            e.stopImmediatePropagation();
            return;
        }
        // Reserved NOW, in this task, and SHOWN now: the buttons spin, the note and the pop-up are up at the first submit event (two submits in one task would both
        // find the form free, and a person who sees nothing clicks again). Only disabling the buttons waits (a disabled submit button is not part of the data that
        // is taken when this event ends).
        var isLong = form.getAttribute('data-busy') === 'long';
        markForm(form, isLong, true);
        // After the other submit handlers have run: a declined confirmation prevented the send, and everything above ends with it.
        setTimeout(function () {
            if (e.defaultPrevented) { resetForm(form); return; }
            form.querySelectorAll(SUBMIT_BUTTONS).forEach(function (b) { setBusy(b, true); });
        }, 0);
    }, true);

    // ---- leaving the page: the single-page navigation (site-spa.js) replaces the content and says so ----------------------------------------
    // What was started on the page that is gone keeps running on the node (a request that was sent cannot be taken back), but its pop-up closes and its
    // result is not drawn into the page that replaced it: it is said once, as a notice, when it arrives.
    document.addEventListener('bmb:page-cleanup', function () {
        // The content is swapped right after this event: what has no place on the page once it has been is what the page left behind (a control of the
        // header, which stays, keeps its operation).
        setTimeout(function () {
            operations.forEach(function (op) {
                var node = op.target || op.anchor;
                if (node && !document.contains(node)) op.leave();
            });
        }, 0);
    });

    // The Back button brings the page back as it was left, busy: let it go.
    window.addEventListener('pageshow', function (e) {
        if (!e.persisted) return;
        document.querySelectorAll('form[data-busy]').forEach(resetForm);
        while (depth > 0) hideModal();
    });
})();
