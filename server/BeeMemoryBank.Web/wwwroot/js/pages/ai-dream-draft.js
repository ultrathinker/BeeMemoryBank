// ===== AI chat: the draft that "Open in AI chat" on the Dream page brings =====
// The Dream page opens /AI?dreamAdvice=<advice id> in a new tab. This file is loaded on /AI only, and chat.js starts it (bmbAiDreamDraft.start) once the chat
// is ready. Both are scripts of the page, which come before the layout's (bmb-busy.js and the rest): the busy helper is not there yet, so the first call to
// the node waits until the page has loaded. It reads the advice id from the address, and nothing else: the parameter leaves the address at once
// (history.replaceState), so a reload or a copied link does not bring the draft back. The task is asked from the node
// (GET /api-proxy/dream/advice/{id}/prompt?target=chat, superadmin like all of Dream) and shown in the conversation as a draft that has NOT been sent:
//   Run     sends exactly that text as the person's message, in a new conversation;
//   Edit    puts the whole text into the composer; the person changes it and sends it the ordinary way (Enter);
//   Cancel  removes the draft; nothing is sent and the advice stays as it was.
// Nothing reaches the model and nothing is saved in the chat's history before Run, or before the Send of the edited text. Only then is the advice marked
// "in chat", by the node, after its own check that the advice still stands; when it does not stand any more (applied, dismissed, expired), a notice in the
// node's words takes the place of the draft and nothing is sent. While that check is on its way nothing else can send the task: Cancel, Edit and the unlink
// control wait, and a check that comes back after the draft was cancelled or unlinked anyway sends nothing and takes its mark back.
// After the send: when the node refused the turn outright (an HTTP error answer instead of a stream), the advice is put back and the draft comes back. A stream
// that broke before the node confirmed the turn may still have saved the message, so then the advice stays "in chat", nothing offers to send it again, and the
// text is shown with a warning to look at the conversation list first. The task text is the node's: it goes on the page as text, never as markup, and nothing
// of it is logged.
(function () {
    'use strict';

    var PARAM = 'dreamAdvice';
    var API = '/api-proxy/dream';
    var DREAM_PAGE = '/Dream';
    var GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
    var MAX_TASK = 100000;          // what the chat takes in one message (ChatEndpoints.MaxMessageLength)
    var DRAFT_LABEL = 'Draft from Dream — not sent yet';
    var NOTICE_LABEL = 'Task from Dream — not sent';
    var UNCONFIRMED_LABEL = 'Task from Dream — not confirmed';
    var BROKE = 'The connection broke while sending. Check the conversation list before sending again.';
    var STOPPED = 'Sending was stopped before the chat confirmed it. Check the conversation list before sending again.';
    var STILL_MARKED = 'The advice is still marked "in chat" on the Dream page: use "Back to the list" there to undo it.';
    var TASK_CLASS = 'chat-dream-task';     // on the composer while it holds the task (ai-dream-draft.css caps its height then, and only then)

    function enc(id) { return encodeURIComponent(id); }
    function backHref(id) { return DREAM_PAGE + '?advice=' + enc(id); }

    // The advice id of the address, or null when there is none or it is not an id. The parameter is taken out of the address whatever it holds; the rest of
    // the address (other parameters, the hash) and the history state of the page stay as they were.
    function takeAdviceId() {
        var loc = window.location;
        var params;
        try { params = new URLSearchParams(loc.search || ''); } catch (e) { return null; }
        if (!params.has(PARAM)) return null;
        var raw = String(params.get(PARAM) || '').trim();
        params.delete(PARAM);
        var rest = params.toString();
        try {
            window.history.replaceState(window.history.state, '', loc.pathname + (rest ? '?' + rest : '') + (loc.hash || ''));
        } catch (e) { /* the address keeps it: it still starts nothing by itself */ }
        return GUID.test(raw) ? raw.toLowerCase() : null;
    }

    // Runs fn now when the page has been read to its end, else when it has been (DOMContentLoaded comes after every classic script of the page, the
    // layout's ones included). A page that is swapped in by the SPA navigation is loaded already, and fn runs at once.
    function whenLoaded(fn) {
        if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', fn, { once: true });
        else fn();
    }

    // ---- talking to the node ---------------------------------------------------------------------------------------------------------
    function read(path, query) {
        return bmbBusy.request(API + path + (query || ''), { headers: { 'Accept': 'application/json' }, cache: 'no-store' }).then(function (r) { return r.body || {}; });
    }
    function readTask(id) {
        return read('/advice/' + enc(id) + '/prompt', '?target=chat').then(function (reply) {
            var text = reply && typeof reply.prompt === 'string' ? reply.prompt : '';
            if (!text.trim()) throw new Error('The node sent no task for the chat.');
            if (text.length > MAX_TASK) throw new Error('The task is too long for the chat. Use "Show prompt" on the Dream page and give it to your own AI.');
            return text;
        });
    }
    function readAdvice(id) {
        return read('/advice/' + enc(id)).then(function (a) { return a && a.id ? a : null; }, function () { return null; });
    }
    function antiforgery() {
        var input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : '';
    }
    function setStatus(id, status) {
        var init = { headers: { 'Accept': 'application/json', 'Content-Type': 'application/json', 'RequestVerificationToken': antiforgery() }, body: JSON.stringify({ status: status }) };
        // bmb-busy-exempt: the one helper of this file for a change; each of its callers runs inside bmbBusy.run(...)
        init.method = 'POST';
        return bmbBusy.request(API + '/advice/' + enc(id) + '/status', init);
    }
    function codeOf(err) { return err && err.body && typeof err.body === 'object' ? err.body.code : ''; }

    // A refusal that is about the advice itself (gone, closed, out of date, not a task for the chat): a notice takes the place of the draft. Anything else
    // (the vault is locked, the node does not answer) is said where the button is, and the draft stays for another try.
    function aboutTheAdvice(err) {
        return !!err && (err.status === 404 || (err.status === 409 && codeOf(err) !== 'locked'));
    }

    // Why an advice, as the node gave it, is not offered as a draft; '' while it is open. (The task request and the advice request go out together: the
    // advice may have been closed between the two.)
    function when(iso) {
        var t = Date.parse(iso || '');
        if (!isFinite(t)) return '';
        return ' on ' + new Date(t).toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
    }
    function closedWords(a) {
        if (!a) return '';
        switch (a.status) {
            case 'applied': return 'This advice was already applied' + when(a.statusAt) + '.';
            case 'dismissed': return 'This advice was dismissed' + when(a.statusAt) + '.';
            case 'expired': return 'This advice expired: ' + (String(a.expiredReason || a.statusNote || '').trim() || 'what it describes has changed.');
            case 'applying': return 'This advice is being applied right now.';
            default: return '';
        }
    }

    // Marks the advice "in chat" right before the task is sent; true when this call did. The node checks first that the advice still stands. An advice that
    // is already "in chat" (sent before, from this tab or another) cannot be set to it again: then the node is asked for the task once more, which makes the
    // same check, and the advice is left as it is.
    function claim(id) {
        return setStatus(id, 'in_chat').then(function () { return true; }, function (err) {
            if (!err || err.status !== 409 || codeOf(err) === 'locked') throw err;
            return readAdvice(id).then(function (a) {
                if (!a || a.status !== 'in_chat') throw err;
                return readTask(id).then(function () { return false; });
            });
        });
    }

    // ---- the page --------------------------------------------------------------------------------------------------------------------
    function el(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) e.className = cls;
        if (text !== undefined && text !== null) e.textContent = String(text);
        return e;
    }
    function icon(name, slot) {
        var i = document.createElement('sl-icon');
        i.setAttribute('name', name);
        if (slot) i.setAttribute('slot', slot);
        return i;
    }
    function button(label, iconName, variant) {
        var b = document.createElement('sl-button');
        b.setAttribute('size', 'small');
        if (variant) b.setAttribute('variant', variant);
        b.appendChild(icon(iconName, 'prefix'));
        b.appendChild(document.createTextNode(label));
        return b;
    }
    function backLink(id) {
        var a = el('a', 'chat-dream-back');
        a.href = backHref(id);
        a.appendChild(icon('arrow-left'));
        a.appendChild(document.createTextNode(' Back to the advice'));
        return a;
    }
    function empty(node) { while (node.firstChild) node.removeChild(node.firstChild); }
    function detach(node) { if (node && node.parentNode) node.parentNode.removeChild(node); }

    // One Run, or one Send of the edited text, is an attempt: { live }. It belongs to its draft (or to the task in the composer) and only one goes at a time.
    // Cancel, Edit, the unlink control and leaving the view void it; a check that comes back to a voided attempt sends nothing.
    function begin(owner) { owner.attempt = { live: true }; return owner.attempt; }
    function voidAttempt(owner) {
        if (owner && owner.attempt) { owner.attempt.live = false; owner.attempt = null; }
    }

    function start(chat) {
        if (!chat || !chat.messages || !chat.input) return null;
        var id = takeAdviceId();        // at once, whatever the page has loaded so far: the parameter leaves the address now

        var blocks = [];        // the drafts and notices in the conversation: { wrap, card, attempt, runButton, editButton, cancelButton }
        var link = null;        // the task after Edit: { id, advice, text, chip, off, attempt }. The next Send of the composer is the task's Send.

        // One block in the conversation, on the side of the person's own messages. Its card is replaced as the block changes (loading, draft, notice).
        function block() {
            var wrap = el('div', 'chat-dream-wrap');
            var card = el('div', 'card chat-dream-card');
            card.setAttribute('data-busy-scope', '');
            wrap.appendChild(card);
            if (chat.hideEmpty) chat.hideEmpty();
            chat.messages.appendChild(wrap);
            var b = { wrap: wrap, card: card, attempt: null };
            blocks.push(b);
            return b;
        }
        function head(card, iconName, label) {
            var h = el('div', 'chat-dream-head');
            h.appendChild(icon(iconName));
            h.appendChild(el('span', 'chat-dream-label', label));
            card.appendChild(h);
        }
        function about(card, adviceId, advice) {
            var line = el('div', 'chat-dream-about');
            var title = advice && typeof advice.title === 'string' && advice.title.trim() ? advice.title.trim() : '';
            if (title) {
                var named = el('span', 'chat-dream-about-title');
                named.appendChild(el('span', 'chat-dream-about-label', 'Advice: '));
                named.appendChild(el('span', 'chat-dream-title', title));
                line.appendChild(named);
            }
            line.appendChild(backLink(adviceId));
            card.appendChild(line);
        }
        function remove(b) {
            if (!b) return;
            voidAttempt(b);
            detach(b.wrap);
            blocks = blocks.filter(function (x) { return x !== b; });
        }
        function scrollToEnd() { chat.messages.scrollTop = chat.messages.scrollHeight; }

        function loading(adviceId) {
            var b = block();
            head(b.card, 'moon-stars', 'Draft from Dream');
            var row = el('div', 'chat-dream-loading');
            row.appendChild(document.createElement('sl-spinner'));
            row.appendChild(el('span', '', 'Loading the task from Dream…'));
            b.card.appendChild(row);
            return b;
        }

        function load(adviceId) {
            var b = loading(adviceId);
            var advice = readAdvice(adviceId);
            var again = function () { remove(b); load(adviceId); };
            readTask(adviceId).then(function (text) {
                return advice.then(function (a) {
                    // the advice as the node gave it now: one closed between the two answers gets the notice, not a draft with Run on it
                    var closed = closedWords(a);
                    if (closed) notice(b, adviceId, a, new Error(closed), a.status === 'applying' ? again : null);
                    else draft(b, { id: adviceId, advice: a, text: text });
                });
            }).then(null, function (err) {
                // Dream is the superadmin's: a link to it opened by anybody else shows nothing at all.
                if (err && (err.status === 401 || err.status === 403)) { remove(b); return; }
                return advice.then(function (a) { notice(b, adviceId, a, err, aboutTheAdvice(err) ? null : again); });
            });
        }

        // The draft: the whole text, readable like any message, marked as not sent, with the advice it is for and the way back to it.
        function draft(b, d) {
            empty(b.card);
            b.card.className = 'card chat-dream-card chat-dream-draft';
            head(b.card, 'moon-stars', DRAFT_LABEL);
            about(b.card, d.id, d.advice);
            b.card.appendChild(el('div', 'chat-dream-text', d.text));
            var actions = el('div', 'chat-dream-actions');
            var runBtn = button('Run', 'play-fill', 'primary');
            var editBtn = button('Edit', 'pencil');
            var cancelBtn = button('Cancel', 'x-lg', 'text');
            actions.appendChild(runBtn);
            actions.appendChild(editBtn);
            actions.appendChild(cancelBtn);
            b.card.appendChild(actions);
            b.card.appendChild(el('div', 'chat-dream-hint',
                'Nothing has been sent yet. Run sends this text as your message, in a new conversation; Edit puts it in the box below so you can change it first. ' +
                'Every change the AI then wants to make still asks you to Allow or Deny it.'));
            runBtn.addEventListener('click', function () { run(b, d, runBtn); });
            editBtn.addEventListener('click', function () { edit(b, d); });
            cancelBtn.addEventListener('click', function () { remove(b); });
            b.runButton = runBtn;
            b.editButton = editBtn;
            b.cancelButton = cancelBtn;
            return b;
        }
        // Edit and Cancel wait while Run's check is on its way (Run itself is held by bmbBusy).
        function hold(b, on) {
            if (b.editButton) b.editButton.disabled = on;
            if (b.cancelButton) b.cancelButton.disabled = on;
        }

        // In place of the draft: what happened to the advice, in the node's words, the way back to it, and nothing sent.
        function notice(b, adviceId, advice, err, retry) {
            empty(b.card);
            b.card.className = 'card chat-dream-card chat-dream-notice';
            b.card.setAttribute('role', 'status');
            head(b.card, 'info-circle', NOTICE_LABEL);
            about(b.card, adviceId, advice);
            b.card.appendChild(el('div', 'chat-dream-message', (err && err.message) || 'This advice cannot go to the chat now.'));
            var actions = el('div', 'chat-dream-actions');
            if (retry) {
                var again = button('Try again', 'arrow-clockwise');
                again.addEventListener('click', retry);
                actions.appendChild(again);
            }
            var close = button('Close', 'x-lg', 'text');
            close.addEventListener('click', function () { remove(b); });
            actions.appendChild(close);
            b.card.appendChild(actions);
            return b;
        }

        // The task went, but the node never confirmed it (the stream broke, or was stopped, before it named the conversation): it may be saved already. The
        // advice stays "in chat", nothing here offers to send it again, and the text stays on the page to be copied.
        function unconfirmed(task, stopped) {
            var b = block();
            b.card.className = 'card chat-dream-card chat-dream-notice chat-dream-unconfirmed';
            b.card.setAttribute('role', 'alert');
            head(b.card, 'exclamation-triangle', UNCONFIRMED_LABEL);
            about(b.card, task.id, task.advice);
            b.card.appendChild(el('div', 'chat-dream-message', stopped ? STOPPED : BROKE));
            b.card.appendChild(el('div', 'chat-dream-text', task.text));
            var actions = el('div', 'chat-dream-actions');
            var close = button('Close', 'x-lg', 'text');
            close.addEventListener('click', function () { remove(b); });
            actions.appendChild(close);
            b.card.appendChild(actions);
            scrollToEnd();
            return b;
        }

        // ---- Run ---------------------------------------------------------------------------------------------------------------------
        function run(b, d, runBtn) {
            if (b.attempt) return;                     // this draft's check is on its way already
            if (chat.isSending()) {
                bmbBusy.showResult(runBtn, false, 'The chat is still answering. Wait for it, or stop it, then press Run again.');
                return;
            }
            var attempt = begin(b);
            var marked = false;
            hold(b, true);
            bmbBusy.run(runBtn, function () {
                return claim(d.id).then(function (m) { marked = m; });
            }, {
                quiet: true,
                error: function (err) {
                    if (!attempt.live) return;         // the draft was cancelled meanwhile: what the node said about it does not matter any more
                    b.attempt = null;
                    hold(b, false);
                    if (aboutTheAdvice(err)) notice(b, d.id, d.advice, err, null);
                    else bmbBusy.showResult(runBtn, false, (err && err.message) || 'Something went wrong.');
                }
            }).then(function (ok) {
                if (!ok) {                             // a failure (said above), or the button was still busy: either way the attempt is over
                    if (b.attempt === attempt) { b.attempt = null; hold(b, false); }
                    return;
                }
                if (!attempt.live) {                   // cancelled (or edited, or the view left) while the node checked: nothing goes, the mark comes back
                    if (marked) putBack(d.id, null, d.advice);
                    return;
                }
                b.attempt = null;
                hold(b, false);
                if (chat.isSending()) {                // a message typed meanwhile went first: the draft waits, as it was before the press
                    if (marked) putBack(d.id, runBtn, d.advice);
                    bmbBusy.showResult(runBtn, false, 'The chat is answering another message. Press Run again when it has finished.');
                    return;
                }
                remove(b);
                chat.startNew();
                sent(d, marked, chat.sendText(d.text, { draft: true }), function () {
                    var again = draft(block(), d);
                    scrollToEnd();
                    return again.runButton;
                });
            });
        }

        // After the task went to the chat. The turn the node took (it named the conversation) is in the chat's history: the advice stays "in chat". A turn the
        // node refused outright (an HTTP error answer before any stream), or one that never started, saved nothing: the advice is put back as it was before the
        // press and the task is given back for another try. Anything else (the stream broke, or was stopped, before the node named the conversation) may have
        // been saved: the advice stays "in chat" and the text is shown with the warning, without a way to send it again.
        function sent(task, marked, turn, giveBack) {
            Promise.resolve(turn).then(function (t) {
                if (t && t.accepted) return;
                if (t && !t.refused) { unconfirmed(task, !!t.aborted); return; }
                var anchor = giveBack();
                if (marked) putBack(task.id, anchor, task.advice);
            });
        }

        // The advice back from "in chat" to new, when this page marked it and the task did not go after all. A failure is said at the anchor, or, when there is
        // none any more (the draft was cancelled), in a notice of its own.
        function putBack(adviceId, anchor, advice) {
            bmbBusy.run(anchor, function () { return setStatus(adviceId, 'new'); }, {
                quiet: true,
                error: function () {
                    if (anchor) bmbBusy.showResult(anchor, false, STILL_MARKED);
                    else { notice(block(), adviceId, advice, new Error(STILL_MARKED), null); scrollToEnd(); }
                }
            });
        }

        // ---- Edit, and the Send of the edited text -------------------------------------------------------------------------------------
        function edit(b, d) {
            remove(b);
            chat.input.value = d.text;
            linkTo(d);
            try {
                if (typeof chat.input.focus === 'function') chat.input.focus();
                if (typeof chat.input.setSelectionRange === 'function') chat.input.setSelectionRange(0, 0);
                if (typeof chat.input.scrollPosition === 'function') chat.input.scrollPosition({ top: 0 });
            } catch (e) { /* the text is in the box anyway */ }
        }

        // The line above the composer while it holds the task: which advice it is for, the way back, and a way to send the text as an ordinary message instead.
        function linkTo(d) {
            unlink();
            var chip = el('div', 'chat-dream-link');
            chip.setAttribute('data-busy-scope', '');
            chip.appendChild(icon('moon-stars'));
            var title = d.advice && typeof d.advice.title === 'string' && d.advice.title.trim() ? d.advice.title.trim() : 'an advice';
            chip.appendChild(el('span', 'chat-dream-link-text', 'The task from Dream for "' + title + '". Sending it marks the advice "in chat".'));
            chip.appendChild(backLink(d.id));
            var off = document.createElement('sl-icon-button');
            off.setAttribute('name', 'x-lg');
            off.setAttribute('label', 'Send it as an ordinary message (the advice is not marked)');
            off.addEventListener('click', unlink);
            chip.appendChild(off);
            var host = chat.composer && chat.composer.parentNode;
            if (host) host.insertBefore(chip, chat.composer);
            if (chat.input.classList) chat.input.classList.add(TASK_CLASS);
            link = { id: d.id, advice: d.advice, text: d.text, chip: chip, off: off, attempt: null };
        }
        function unlink() {
            if (!link) return;
            voidAttempt(link);
            detach(link.chip);
            if (chat.input.classList) chat.input.classList.remove(TASK_CLASS);
            link = null;
        }
        function onComposerInput() {
            if (link && !String(chat.input.value || '').trim()) unlink();   // the person emptied the box: what they type next is their own
        }
        chat.input.addEventListener('sl-input', onComposerInput);
        chat.input.addEventListener('input', onComposerInput);

        // chat.js asks this before every Send of the composer. True when the text is the task after Edit: the node checks the advice first, and the text is sent
        // only if it still stands (else a notice, and the text stays in the box, unlinked). False: an ordinary message, chat.js sends it.
        function composerSend(text) {
            if (!link || !text) return false;
            var l = link;
            if (l.attempt) return true;                // the check of this text is on its way already
            var attempt = begin(l);
            var marked = false;
            var task = { id: l.id, advice: l.advice, text: text };
            chat.input.disabled = true;                // the text that is checked is the text that is sent
            l.off.disabled = true;                     // and the unlink control waits for the check
            bmbBusy.run(l.chip, function () {
                return claim(l.id).then(function (m) { marked = m; });
            }, {
                quiet: true,
                error: function (err) {
                    if (!attempt.live) return;
                    l.attempt = null;
                    l.off.disabled = false;
                    if (!aboutTheAdvice(err)) { bmbBusy.showResult(l.chip, false, (err && err.message) || 'Something went wrong.'); return; }
                    unlink();
                    notice(block(), l.id, l.advice, err, null);
                    scrollToEnd();
                }
            }).then(function (ok) {
                chat.input.disabled = false;
                if (!ok) {
                    if (l.attempt === attempt) { l.attempt = null; l.off.disabled = false; }
                    return;
                }
                if (!attempt.live) {                   // unlinked while the node checked: the text stays in the box as an ordinary one, the mark comes back
                    if (marked) putBack(l.id, null, l.advice);
                    return;
                }
                l.attempt = null;
                l.off.disabled = false;
                if (chat.isSending()) {
                    if (marked) putBack(l.id, l.chip, l.advice);
                    bmbBusy.showResult(l.chip, false, 'The chat is answering another message. Send this again when it has finished.');
                    return;
                }
                unlink();
                sent(task, marked, chat.sendText(text), function () {
                    if (!String(chat.input.value || '').trim()) chat.input.value = text;
                    linkTo(task);
                    return link.chip;
                });
            });
            return true;
        }

        // chat.js calls this when the conversation area is emptied for another conversation: a draft or a notice that was there is gone with it, and so is a
        // Run that was still being checked.
        function drop() { blocks.slice().forEach(remove); }

        // The page's own scripts come before the layout's (RenderBody, then bmb-busy.js and the site scripts), so when chat.js starts this the busy helper is
        // not there yet. Nothing of it is used here until the page has been read to its end: the first call to the node waits for that, and every button
        // reaches bmbBusy only when it is pressed.
        if (id) whenLoaded(function () { if (window.bmbBusy) load(id); });
        return { composerSend: composerSend, drop: drop };
    }

    window.bmbAiDreamDraft = { PARAM: PARAM, start: start };
})();
