// "Invite a blind copy" (Blind nodes page): the Copy button and the countdown of the invitation just made.
// The invitation text is in the page for this one response only; nothing here stores or sends it.
(function () {
    'use strict';

    var issued = document.getElementById('invite-issued');
    if (!issued) return;

    var copy = document.getElementById('invite-copy');
    var text = document.getElementById('invite-text');
    var copied = document.getElementById('invite-copied');
    if (copy && text) {
        copy.addEventListener('click', function () {
            var done = function () {
                if (copied) {
                    copied.hidden = false;
                    setTimeout(function () { copied.hidden = true; }, 2500);
                }
            };
            if (navigator.clipboard && navigator.clipboard.writeText) {
                navigator.clipboard.writeText(text.value).then(done, function () {
                    text.select();
                    document.execCommand('copy');
                    done();
                });
            } else {
                text.select();
                document.execCommand('copy');
                done();
            }
        });
    }

    var left = document.getElementById('invite-countdown');
    var expires = Date.parse(issued.getAttribute('data-expires') || '');
    if (left && !isNaN(expires)) {
        var timer = null;
        var tick = function () {
            var seconds = Math.max(0, Math.round((expires - Date.now()) / 1000));
            if (seconds === 0) {
                left.textContent = 'expired';
                if (text) text.value = '';
                if (timer) clearInterval(timer);
                return;
            }
            left.textContent = Math.floor(seconds / 60) + ':' + ('0' + (seconds % 60)).slice(-2);
        };
        timer = setInterval(tick, 1000);
        tick();
    }
})();
