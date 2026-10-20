// BMB-316: the dialog that asks for the password when the save of a password-protected article finds its unlock gone.
//
// An article that was opened for editing from the unlock of the page before (the node keeps that unlock for a quarter of an hour) cannot be saved once the
// time is out: the node answers 400 "a passphrase is required" (code PassphraseRequired). The person has written something in the editor by then, so the
// page must not answer with a line of red text: it asks for the password in a dialog, over the editor, that stays as it is. Cancel (or Esc, or a click
// beside the dialog) gives the editor back as it was - it can still be edited and copied from; the right password saves the text that is there.
//
//   bmbProtectedSave.attach({ dialog, input, error, saveButton, save(button, passphrase, fail) }) -> { ask(problem), isOpen() }
//     save: sends the edited article with the passphrase (button is the control that spins meanwhile) and, when it fails, calls fail(error) (the error of bmbBusy.request: status, body, message)
(function () {
    function said(err) {
        return err && err.body && typeof err.body.error === 'string' ? err.body.error : (err && err.message) || '';
    }

    // The node says that the unlock of the article is gone. The code is the sign; the words are the same for a node that does not send the code (or a proxy
    // that does not pass it), so that this never falls back to a toast over text that is about to be lost.
    function needsPassword(err) {
        if (!err || err.status !== 400) return false;
        return !!(err.body && err.body.code === 'PassphraseRequired') || /passphrase is required/i.test(said(err));
    }

    // The password that was given is not the article's (a 401 about the passphrase; a 401 of the web session says something else and is not this).
    function wrongPassword(err) {
        return !!err && err.status === 401 && /passphrase/i.test(said(err));
    }

    function attach(o) {
        var dialog = o.dialog, input = o.input, errorLine = o.error, saveButton = o.saveButton;

        function setError(text) {
            errorLine.textContent = text || '';
            errorLine.style.display = text ? '' : 'none';
        }

        function ask(problem) {
            input.value = '';
            setError(problem);
            dialog.show();
        }

        function confirm() {
            var password = input.value || '';
            if (!password) { input.focus(); return; }
            setError('');
            // The dialog stays open while it is saved, and when the save fails: the reason is written in it, and the text of the editor is not touched.
            o.save(saveButton, password, function (err) {
                setError(wrongPassword(err) ? 'Wrong password. Try again.'
                    : needsPassword(err) ? 'The password is needed to save.'
                    : (err && err.message) || 'The article could not be saved.');
                input.focus();
            });
        }

        saveButton.addEventListener('click', confirm);
        input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { e.preventDefault(); confirm(); }
        });
        // sl-after-show / sl-after-hide also come up from what is inside the dialog: only its own count.
        dialog.addEventListener('sl-after-show', function (e) { if (e.target === dialog) input.focus(); });
        dialog.addEventListener('sl-after-hide', function (e) {
            if (e.target !== dialog) return;
            input.value = '';   // the password is not left in the page
            setError('');
        });

        return { ask: ask, isOpen: function () { return !!dialog.open; } };
    }

    // BMB-318: the strip over the editor that says how long the node still remembers the password ("Temporarily unlocked - locks automatically in N min"),
    // as the article page says it over the text. When the time is out it says that saving will ask for the password, and that the text stays.
    //   bmbProtectedSave.unlockBanner({ banner, counting, remaining, note, over, now? }) -> { start(seconds|null), expire(), stop() }
    function unlockBanner(o) {
        var timer = null;
        var clock = o.now || function () { return Date.now(); };
        function show(el, on) { if (el) el.style.display = on ? '' : 'none'; }
        var format = formatSpan;
        function stop() { if (timer) { clearInterval(timer); timer = null; } }
        function expire() {
            stop();
            show(o.banner, true); show(o.counting, false); show(o.note, false); show(o.over, true);
            if (o.onExpire) o.onExpire();
        }
        function start(seconds) {
            stop();
            if (seconds == null) { show(o.banner, false); return; }
            var deadline = clock() + seconds * 1000;
            show(o.banner, true);
            function tick() {
                var left = Math.max(0, Math.round((deadline - clock()) / 1000));
                if (left <= 0) { expire(); return; }
                o.remaining.textContent = format(left);
                show(o.counting, true); show(o.note, true); show(o.over, false);
            }
            tick();
            if (timer === null && deadline > clock()) timer = setInterval(tick, 1000);
        }
        return { start: start, expire: expire, stop: stop };
    }

    // A span of time the way the strip says it: seconds under a minute, minutes under an hour, then hours and the minutes that are left ("8 h", "2 h 15 min").
    // Minutes round UP, so the strip never says "0 min" for a password that is still remembered.
    function formatSpan(sec) {
        if (sec < 60) return sec + ' s';
        var min = Math.ceil(sec / 60);
        if (min < 60) return min + ' min';
        var h = Math.floor(min / 60), m = min % 60;
        return m ? h + ' h ' + m + ' min' : h + ' h';
    }

    // BMB-319: the time ONE article keeps its password for (15 min .. 8 h), chosen on a slider that the "change" link of the strip opens, on the article page and in the
    // editor. It applies when the slider is let go, the slider goes away and the strip counts the new time; the node keeps it for this article across View, Edit and saves
    // and forgets it when the article locks, so a refusal of the node (409: it is locked already) just closes the slider and says so.
    //   bmbProtectedSave.durationPicker({ link, panel, range, picked, request(seconds) -> Promise<{unlockExpiresInSeconds, unlockDurationSeconds}>, applied(body), failed(err) })
    //     -> { setSeconds(seconds), close(), isOpen() }
    var MIN_MINUTES = 15, MAX_MINUTES = 480, STEP_MINUTES = 15;
    function durationPicker(o) {
        function minutesOf(sec) {
            var m = Math.round((Number(sec) || 0) / 60 / STEP_MINUTES) * STEP_MINUTES;
            return Math.min(MAX_MINUTES, Math.max(MIN_MINUTES, m));
        }
        function isOpen() { return o.panel.style.display !== 'none'; }
        function open(on) {
            o.panel.style.display = on ? '' : 'none';
            o.link.textContent = on ? 'close' : 'change';
            o.link.setAttribute('aria-expanded', on ? 'true' : 'false');
        }
        function setSeconds(sec) {
            var m = minutesOf(sec);
            o.range.value = m;
            o.picked.textContent = formatSpan(m * 60);
        }
        o.link.addEventListener('click', function (e) { e.preventDefault(); open(!isOpen()); });
        // sl-input comes while the handle moves, sl-change when it is let go (and not when the page sets the value itself)
        o.range.addEventListener('sl-input', function () { o.picked.textContent = formatSpan(Number(o.range.value) * 60); });
        o.range.addEventListener('sl-change', function () {
            var seconds = Number(o.range.value) * 60;
            o.range.disabled = true;
            Promise.resolve().then(function () { return o.request(seconds); }).then(function (body) {
                if (body && body.unlockDurationSeconds != null) setSeconds(body.unlockDurationSeconds);
                open(false);
                o.applied(body || {});
            }, function (err) {
                open(false);
                o.failed(err);
            }).then(function () { o.range.disabled = false; });
        });
        open(false);
        return { setSeconds: setSeconds, close: function () { open(false); }, isOpen: isOpen };
    }

    window.bmbProtectedSave = { needsPassword: needsPassword, wrongPassword: wrongPassword, attach: attach, unlockBanner: unlockBanner, durationPicker: durationPicker, formatSpan: formatSpan };
})();
