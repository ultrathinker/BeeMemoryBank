// ===== Dream page: "Open in AI chat" and "Show prompt" =====
// The two buttons of a card of advice that dream.js draws and leaves to this file (bmbDream.handoff).
//   Show prompt      asks the node for the text for a personal AI (GET /advice/{id}/prompt?target=agent) and shows it in a dialog with a Copy button. Nothing changes.
//   Open in AI chat  opens the built-in chat in a NEW tab at /AI?dreamAdvice=<id>; this tab stays as it was. Only the id goes in the address: the chat page asks
//                    the node for the task itself and shows it there as a draft, which is sent only when the person presses Run, or edits it and sends it
//                    (pages/ai-dream-draft.js). The advice is marked "in chat" then, not here: opening the tab and closing it changes nothing.
// The text is the node's: it is put on the page as the value of a text field and never as markup, and nothing of it is logged.
(function () {
    'use strict';

    var dream = window.bmbDream;
    var M = window.bmbDreamModel;
    var dialog = document.getElementById('dlg-dream-prompt');
    if (!dream || !dream.handoff || !dream.api || !M || !dialog || !window.bmbBusy) return;   // dream.js keeps its placeholders

    var CHAT_PAGE = '/AI';
    var CHAT_PARAM = 'dreamAdvice';     // what pages/ai-dream-draft.js reads (bmbAiDreamDraft.PARAM)

    function $(id) { return document.getElementById(id); }
    function enc(id) { return encodeURIComponent(id); }

    function promptOf(reply) {
        return reply && typeof reply.prompt === 'string' ? reply.prompt : '';
    }

    // What went wrong, in the node's words; an advice the node says is no longer valid is read again, so the card says so too.
    function fail(button, advice, err) {
        bmbBusy.showResult(button, false, M.errorText(err));
        if (err && err.status === 409 && advice && advice.id) dream.refreshAdvice(advice.id);
    }

    // ---- Show prompt -----------------------------------------------------------------------------------------------------------------
    function showPrompt(advice, button) {
        if (!advice || !advice.id) return;
        var text = '';
        bmbBusy.run(button, function () {
            return dream.api.get('/advice/' + enc(advice.id) + '/prompt', { target: 'agent' }).then(function (reply) {
                text = promptOf(reply);
                if (!text.trim()) throw new Error('The node sent no prompt for this advice.');
            });
        }, { quiet: true, error: function (err) { fail(button, advice, err); } }).then(function (ok) {
            if (ok) openPromptDialog(advice, text);
        });
    }

    function openPromptDialog(advice, text) {
        var v = M.adviceView(advice, {});
        $('dream-prompt-title').textContent = v.kind.label + ': ' + v.title;
        $('dream-prompt-text').value = text;
        $('dream-prompt-note').textContent = text.length.toLocaleString('en-US') + ' characters.';
        dialog.show();
    }

    function copyPrompt() {
        var field = $('dream-prompt-text');
        var text = field.value || '';
        var note = $('dream-prompt-note');
        if (!text) return;
        function done() { note.textContent = 'Copied. Paste it into your AI.'; }
        function byHand() {
            // No clipboard for this page (plain http on a network address, or a refusal): the text is selected, the keyboard does the rest.
            try { if (typeof field.select === 'function') field.select(); } catch (e) { /* the text is on screen anyway */ }
            var copied = false;
            try { copied = !!document.execCommand('copy'); } catch (e) { copied = false; }
            note.textContent = copied ? 'Copied. Paste it into your AI.' : 'The browser would not copy it. The text is selected: press Ctrl+C.';
        }
        if (navigator.clipboard && navigator.clipboard.writeText && window.isSecureContext !== false) navigator.clipboard.writeText(text).then(done, byHand);
        else byHand();
    }
    $('dream-prompt-copy').addEventListener('click', copyPrompt);
    dialog.addEventListener('sl-after-hide', function (e) {
        if (e.target !== dialog) return;   // the hide events of the components inside the dialog bubble up to it
        $('dream-prompt-text').value = '';
        $('dream-prompt-note').textContent = '';
    });

    // ---- Open in AI chat -------------------------------------------------------------------------------------------------------------
    // Why an advice that the card already shows as closed does not go to the chat, in words for the button; '' while it is open.
    function closedReason(advice) {
        var when = advice.statusAt ? ' on ' + M.formatWhen(advice.statusAt) : '';
        switch (advice.status) {
            case 'applied': return 'This advice was already applied' + when + '.';
            case 'dismissed': return 'This advice was dismissed' + when + '.';
            case 'expired': return 'This advice expired: ' + (String(advice.expiredReason || advice.statusNote || '').trim() || 'what it describes has changed.');
            case 'applying': return 'This advice is being applied right now.';
            default: return '';
        }
    }

    // The tab is opened right in the click (nothing is awaited before it, so no pop-up blocker stops it), and it gets no handle on this page (noopener).
    function openInChat(advice, button) {
        if (!advice || !advice.id) return;
        var closed = closedReason(advice);
        if (closed) {
            bmbBusy.showResult(button, false, closed);
            return;
        }
        var status = dream.status();
        if (status && status.chatAvailable === false) {
            bmbBusy.showResult(button, false, 'The built-in chat is not available on this node. Use "Show prompt" and give the text to your own AI.');
            return;
        }
        window.open(CHAT_PAGE + '?' + CHAT_PARAM + '=' + enc(advice.id), '_blank', 'noopener');
        bmbBusy.showResult(button, true, 'The task opens in the chat in a new tab. Read it there, then press Run, or Edit it first.');
    }

    dream.handoff.chat = openInChat;
    dream.handoff.prompt = showPrompt;
})();
