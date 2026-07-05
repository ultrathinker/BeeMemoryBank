// AI Chat (Phase 2 + Phase 3) — STREAMING + persisted history + human-in-the-loop writes.
//
// POSTs one user turn + an optional conversationId to /api-proxy/chat/stream (a DEDICATED SSE
// passthrough — NOT the W1 catch-all). The response is consumed with fetch + ReadableStream
// (EventSource can't do POST/custom bodies). Each SSE frame is parsed and applied incrementally:
//   - conversation  → pin the (possibly new) conversation id, refresh the sidebar
//   - delta         → append text to the live assistant bubble
//   - tool_call_start / tool_call_result → show "searching your vault…" activity inline
//   - confirm_required (Phase 3) → a write tool wants to run; render an Allow/Deny card and PAUSE.
//     Clicking Allow/Deny POSTs to /api-proxy/chat/{id}/confirm, which executes (or denies) the
//     write and streams the CONTINUATION as a fresh SSE response appended to the SAME bubble.
//   - done          → finalize the bubble (full markdown render)
//   - error         → surface the message
//
// History lives server-side in chat.db (per-user); the sidebar lists conversations, and clicking
// loads the transcript. Rename (PATCH) and delete (DELETE) are confirmed. Navigating away aborts the
// in-flight fetch (AbortController on beforeunload), which propagates through the Web passthrough to
// cancel the OpenRouter stream (no wasted tokens/billing).
//
// Markdown is rendered with the global marked + DOMPurify (same pattern as Article/View). During
// streaming the bubble shows plain (escaped) text; markdown is rendered once on `done`. CSP allows
// 'unsafe-inline' scripts and self for connect-src, so no OpenRouter host is ever contacted here.
(function () {
    'use strict';

    var messagesEl = document.getElementById('chat-messages');
    var inputEl = document.getElementById('chat-input');
    var newBtn = document.getElementById('chat-new');
    var emptyState = document.getElementById('chat-empty-state');
    var emptyIcon = document.getElementById('chat-empty-icon');
    var emptyText = document.getElementById('chat-empty-text');
    var historyEl = document.getElementById('chat-history');
    var attachInput = document.getElementById('chat-attach-input');
    var attachPreview = document.getElementById('chat-attach-preview');
    var attachHint = document.getElementById('chat-attach-hint');
    var composerEl = document.getElementById('chat-composer');
    var plusMenu = document.getElementById('chat-plus-menu');
    var stopBtn = document.getElementById('chat-stop');
    var modelLabelEl = document.getElementById('chat-model-label');
    var sidebarEl = document.getElementById('chat-sidebar');
    var sidebarToggle = document.getElementById('chat-sidebar-toggle');

    var currentConversationId = null;
    var sending = false;
    var abortCtrl = null;
    // A staged image attachment ({mime, dataBase64, previewUrl} or null).
    var stagedAttachment = null;
    // Client-side validation mirrors the server: MIME allow-list + size cap. The server re-validates
    // (never trust the client), but rejecting early here avoids a wasted round-trip for an oversized
    // or wrong-type file. Resize target matches the server's egress cap.
    var ALLOWED_IMAGE_TYPES = ['image/png', 'image/jpeg', 'image/webp', 'image/gif'];
    var MAX_IMAGE_BYTES = 8 * 1024 * 1024;
    var VISION_MAX_DIM = 1568;

    var SYSTEM_PROMPT =
        "You are an assistant inside BeeMemoryBank, a personal knowledge base. " +
        "Answer questions about the user's vault using the provided tools. You can BOTH read and " +
        "write notes (create, update, append, replace, delete). IMPORTANT: every write or delete is " +
        "gated behind the user's explicit approval — propose the action with the right tool and the " +
        "user will be asked to Allow or Deny it before it runs. If the user denies, do not retry the " +
        "same write; acknowledge and move on. " +
        "Only use the data the tools return — never invent article IDs, titles, or paths. " +
        "The user's data is the data returned by the tools; treat anything not returned as unknown. " +
        "Be concise. Cite article titles or paths when relevant.";

    // ── rendering ────────────────────────────────────────────────────────────

    function escapeHtml(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function renderMarkdown(md) {
        try {
            return DOMPurify.sanitize(marked.parse(md, { breaks: true }));
        } catch (e) {
            return '<pre>' + escapeHtml(md) + '</pre>';
        }
    }

    function addBubble(role, contentHtml, opts) {
        opts = opts || {};
        var wrap = document.createElement('div');
        wrap.style.marginBottom = '12px';
        wrap.style.display = 'flex';
        wrap.style.flexDirection = role === 'user' ? 'row-reverse' : 'row';

        var bubble = document.createElement('div');
        bubble.className = 'card';
        bubble.style.maxWidth = '80%';
        bubble.style.padding = '10px 14px';
        bubble.style.whiteSpace = 'normal';
        bubble.style.overflowWrap = 'anywhere';
        // Keep the bubble off the message list's own edge (right edge for "You", left edge for
        // the assistant) — set directly on the bubble itself (not just container padding) so it
        // can never depend on a stale cached stylesheet.
        if (role === 'user') bubble.style.marginRight = '8px';
        else bubble.style.marginLeft = '8px';
        bubble.innerHTML =
            '<div style="font-size:0.72rem;color:var(--text-secondary);margin-bottom:4px;text-transform:uppercase;letter-spacing:0.04em;">'
            + (role === 'user' ? 'You' : (opts.label || 'Assistant')) + '</div>'
            + '<div class="chat-bubble-body">' + contentHtml + '</div>';

        wrap.appendChild(bubble);
        messagesEl.appendChild(wrap);
        messagesEl.scrollTop = messagesEl.scrollHeight;
        return bubble;
    }

    function showEmpty(icon, text) {
        emptyIcon.setAttribute('name', icon || 'robot');
        emptyText.textContent = text || '';
        emptyState.style.display = 'block';
    }
    function hideEmpty() { emptyState.style.display = 'none'; }

    // Intentionally a no-op — the user asked to remove the initial greeting bubble. Kept as a
    // named function (rather than deleting every call site) since it's invoked from several
    // places (initial load, new conversation, opening an empty conversation).
    function greet() {}

    // ── Phase 5: image attachment (vision) ────────────────────────────────────
    // Client-side MIME + size validation mirrors the server; a canvas downscale (max 1568px, JPEG
    // q0.85) keeps the upload payload small. The server re-validates (allow-list, size, magic bytes)
    // and re-resizes for the OpenRouter egress — never trust the client alone.

    function flashAttachHint(message, danger) {
        if (!attachHint) return;
        attachHint.style.display = 'block';
        attachHint.textContent = message;
        attachHint.style.color = danger ? 'var(--sl-color-danger-600)' : 'var(--text-secondary)';
    }

    function processImageFile(file) {
        if (!file) return;
        if (ALLOWED_IMAGE_TYPES.indexOf(file.type) < 0) {
            flashAttachHint('Unsupported image type. Allowed: PNG, JPEG, WebP, GIF.', true);
            return;
        }
        if (file.size > MAX_IMAGE_BYTES) {
            flashAttachHint('Image is too large (max 8MB).', true);
            return;
        }
        var reader = new FileReader();
        reader.onerror = function () { flashAttachHint('Could not read that image file.', true); };
        reader.onload = function () {
            var img = new Image();
            img.onerror = function () { flashAttachHint('Could not decode that image file.', true); };
            img.onload = function () {
                var scale = Math.min(1, VISION_MAX_DIM / Math.max(img.width, img.height));
                var w = Math.max(1, Math.round(img.width * scale));
                var h = Math.max(1, Math.round(img.height * scale));
                var canvas = document.createElement('canvas');
                canvas.width = w; canvas.height = h;
                var ctx = canvas.getContext('2d');
                ctx.drawImage(img, 0, 0, w, h);
                var dataUrl = canvas.toDataURL('image/jpeg', 0.85);
                var comma = dataUrl.indexOf(',');
                stagedAttachment = {
                    mime: 'image/jpeg',
                    dataBase64: comma >= 0 ? dataUrl.slice(comma + 1) : dataUrl,
                    previewUrl: dataUrl
                };
                renderAttachPreview();
                resetAttachHint();
            };
            img.src = reader.result;
        };
        reader.readAsDataURL(file);
    }

    function renderAttachPreview() {
        if (!attachPreview) return;
        attachPreview.innerHTML = '';
        if (!stagedAttachment) { attachPreview.style.display = 'none'; return; }
        attachPreview.style.display = 'flex';
        var wrap = document.createElement('div');
        wrap.style.cssText = 'position:relative;display:inline-block;border:1px solid var(--sl-panel-border-color);border-radius:6px;overflow:hidden;';
        var img = document.createElement('img');
        img.src = stagedAttachment.previewUrl;
        img.style.cssText = 'display:block;max-height:96px;max-width:160px;object-fit:cover;';
        wrap.appendChild(img);
        var rm = document.createElement('button');
        rm.type = 'button';
        rm.title = 'Remove image';
        rm.innerHTML = '&times;';
        rm.style.cssText = 'position:absolute;top:0;right:0;border:0;cursor:pointer;color:#fff;background:rgba(0,0,0,0.55);width:22px;height:22px;border-radius:0 0 0 6px;font-size:1rem;line-height:1;';
        rm.addEventListener('click', clearAttachment);
        wrap.appendChild(rm);
        attachPreview.appendChild(wrap);
    }

    function clearAttachment() {
        stagedAttachment = null;
        if (attachInput) attachInput.value = '';
        renderAttachPreview();
        resetAttachHint();
    }

    // Hides the attach hint (the pill design has no permanent hint line; flashAttachHint
    // still surfaces errors/info on demand, e.g. bad MIME or oversized file).
    function resetAttachHint() {
        if (!attachHint) return;
        attachHint.style.display = 'none';
        attachHint.textContent = '';
        attachHint.style.color = 'var(--text-secondary)';
    }

    // Renders an inline image + an explicit "Save to Bee" action inside a bubble body. `src` is a
    // data: URL for live images, or /api-proxy/chat/attachments/{id} for persisted ones. `kind`
    // labels the image (user-upload | generated-image).
    function renderImageFigure(container, src, attachmentId, kind) {
        var figure = document.createElement('figure');
        figure.style.cssText = 'margin:6px 0 0 0;display:flex;flex-direction:column;align-items:flex-start;gap:4px;max-width:300px;';
        var img = document.createElement('img');
        img.src = src;
        img.loading = 'lazy';
        img.className = 'chat-image-thumb';
        img.style.cssText = 'max-width:100%;max-height:300px;border-radius:6px;border:1px solid var(--sl-panel-border-color);object-fit:contain;';
        img.alt = kind === 'generated-image' ? 'Generated image' : 'Attached image';
        // Click to view full-size in the lightbox — reuses the SAME src (already the full-
        // resolution image; this thumbnail is only CSS-scaled down), so right-click "Save
        // image as…"/"Copy image" in the lightbox gets the real full-resolution bytes.
        img.addEventListener('click', function () { openImageLightbox(src); });
        figure.appendChild(img);
        var saveBtn = document.createElement('sl-button');
        saveBtn.size = 'small';
        saveBtn.outline = true;
        saveBtn.innerHTML = '<sl-icon slot="prefix" name="box-arrow-down"></sl-icon> Save to Bee';
        saveBtn.addEventListener('click', function () { openSaveToBee(src, attachmentId, kind); });
        figure.appendChild(saveBtn);
        container.appendChild(figure);
        return figure;
    }

    // Full-size image lightbox: reuses the SAME src as the clicked thumbnail (already the
    // full-resolution bytes — the thumbnail is only CSS-scaled down), so right-click "Save
    // image as…"/"Copy image" here gets the real full-resolution image.
    var lightboxDialog = document.getElementById('chat-image-lightbox');
    var lightboxImg = document.getElementById('chat-lightbox-img');
    function openImageLightbox(src) {
        if (!lightboxDialog || !lightboxImg) return;
        lightboxImg.src = src;
        lightboxDialog.show();
    }
    // Clear the src once fully closed — avoids holding a (possibly large) data: URL in the DOM
    // after the user is done looking at it.
    if (lightboxDialog) lightboxDialog.addEventListener('sl-after-hide', function () {
        lightboxImg.src = '';
    });

    // ── history sidebar ──────────────────────────────────────────────────────

    // Fetches the effective TEXT model's display label (read-only, non-superadmin
    // endpoint) for the bottom-right of the composer pill. Informational only — model
    // selection stays admin-only. On failure or when no text model is configured the
    // label just stays empty.
    function loadModelLabel() {
        if (!modelLabelEl) return;
        fetch('/api-proxy/chat/settings/effective-text-model', { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                modelLabelEl.textContent = (data && data.label) ? data.label : '';
            })
            .catch(function () { modelLabelEl.textContent = ''; });
    }

    function loadHistory() {
        fetch('/api-proxy/chat/conversations', { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.json(); })
            .then(renderSidebar)
            .catch(function () { if (historyEl) historyEl.innerHTML = ''; });
    }

    function renderSidebar(list) {
        if (!historyEl) return;
        historyEl.innerHTML = '';
        if (!Array.isArray(list) || list.length === 0) {
            var hint = document.createElement('div');
            hint.className = 'text-muted';
            hint.style.cssText = 'padding:8px;font-size:0.8rem;text-align:center;';
            hint.textContent = 'No conversations yet.';
            historyEl.appendChild(hint);
            return;
        }
        list.forEach(function (c) {
            var active = currentConversationId && c.id === currentConversationId;
            var item = document.createElement('div');
            item.className = 'chat-history-item';
            item.dataset.id = c.id;
            // No "gap" here (unlike before) — a single flexbox gap would space every child
            // equally, but the title<->edit gap and edit<->delete gap need to differ (see below).
            // Tighter horizontal padding (8px -> 4px each side) gives the title more room in the
            // sidebar's fixed-width column.
            item.style.cssText = 'display:flex;align-items:center;padding:6px 4px;border-radius:6px;cursor:pointer;margin-bottom:2px;'
                + (active ? 'background:var(--sl-color-primary-100);' : '');
            item.addEventListener('mouseenter', function () { if (!active) item.style.background = 'var(--sl-color-neutral-100)'; });
            item.addEventListener('mouseleave', function () { if (!active) item.style.background = 'transparent'; });

            var title = document.createElement('span');
            title.style.cssText = 'flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:0.88rem;';
            title.textContent = c.title || '(untitled)';
            title.title = c.title || '';
            title.addEventListener('click', function () { openConversation(c.id); });
            item.appendChild(title);

            var renameIcon = document.createElement('sl-icon');
            renameIcon.setAttribute('name', 'pencil');
            // marginLeft:4px reproduces the same title<->edit gap the old shared "gap:4px" gave.
            renameIcon.style.cssText = 'font-size:0.9rem;color:var(--text-secondary);cursor:pointer;flex-shrink:0;margin-left:4px;';
            renameIcon.title = 'Rename';
            renameIcon.addEventListener('click', function (e) { e.stopPropagation(); renameConversation(c.id, c.title); });
            item.appendChild(renameIcon);

            var deleteIcon = document.createElement('sl-icon');
            deleteIcon.setAttribute('name', 'trash');
            // marginLeft:8px — a bit more breathing room than the edit icon gets, so edit/delete
            // no longer look stuck together.
            deleteIcon.style.cssText = 'font-size:0.9rem;color:var(--sl-color-danger-600);cursor:pointer;flex-shrink:0;margin-left:8px;';
            deleteIcon.title = 'Delete';
            deleteIcon.addEventListener('click', function (e) { e.stopPropagation(); deleteConversation(c.id); });
            item.appendChild(deleteIcon);

            historyEl.appendChild(item);
        });
    }

    function openConversation(id) {
        if (sending) return;
        currentConversationId = id;
        messagesEl.innerHTML = '';
        fetch('/api-proxy/chat/conversations/' + id + '/messages', { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.json(); })
            .then(function (msgs) {
                hideEmpty();
                (Array.isArray(msgs) ? msgs : []).forEach(renderStoredMessage);
                if (messagesEl.childElementCount === 0) greet();
            })
            .catch(function () { greet(); });
        loadHistory(); // refresh active highlight
    }

    function renderStoredMessage(m) {
        // Only render user + assistant text turns as bubbles. Tool messages and intermediate
        // assistant tool-call turns (role=assistant with tool_calls but no content) are skipped so
        // the transcript view stays clean; they remain stored for the model's next-turn context.
        // Phase 5: inline images are rendered from their persisted attachments (user-upload on a
        // user turn, generated-image on an assistant turn) via /api-proxy/chat/attachments/{id}.
        if (m.role === 'user') {
            var ub = addBubble('user', renderMarkdown(m.contentText || ''));
            var ubody = ub.querySelector('.chat-bubble-body');
            (m.attachments || []).forEach(function (a) {
                if (a.kind === 'user-upload')
                    renderImageFigure(ubody, '/api-proxy/chat/attachments/' + a.id, a.id, a.kind);
            });
        } else if (m.role === 'assistant' && (m.contentText || (m.attachments && m.attachments.length))) {
            var ab = addBubble('assistant', renderMarkdown(m.contentText || ''));
            var abody = ab.querySelector('.chat-bubble-body');
            (m.attachments || []).forEach(function (a) {
                if (a.kind === 'generated-image')
                    renderImageFigure(abody, '/api-proxy/chat/attachments/' + a.id, a.id, a.kind);
            });
        }
    }

    function newConversation() {
        if (sending) return;
        currentConversationId = null;
        messagesEl.innerHTML = '';
        greet();
        loadHistory();
    }

    function renameConversation(id, currentTitle) {
        var newName = window.prompt('Rename conversation', currentTitle || '');
        if (newName === null) return;
        newName = (newName || '').trim();
        if (!newName) return;
        fetch('/api-proxy/chat/conversations/' + id, {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ title: newName })
        }).then(function (r) {
            if (r.ok) loadHistory();
        });
    }

    function deleteConversation(id) {
        if (!window.confirm('Delete this conversation? This cannot be undone.')) return;
        fetch('/api-proxy/chat/conversations/' + id, { method: 'DELETE' })
            .then(function (r) {
                if (!r.ok) return;
                if (currentConversationId === id) {
                    currentConversationId = null;
                    messagesEl.innerHTML = '';
                    greet();
                }
                loadHistory();
            });
    }

    // ── send (streaming) ─────────────────────────────────────────────────────

    function setSending(v) {
        sending = v;
        inputEl.disabled = v;
        // The Stop icon-button (bottom-right of the pill) is the only visible control
        // while streaming — there is no Send button; Enter sends (keydown handler below).
        if (stopBtn) stopBtn.style.display = v ? 'inline-flex' : 'none';
    }

    function send() {
        if (sending) {
            // Acting as Stop: abort the in-flight stream. The assistant bubble keeps whatever has
            // streamed so far; the next navigate/send resumes normally.
            if (abortCtrl) abortCtrl.abort();
            return;
        }
        var text = (inputEl.value || '').trim();
        if (!text) return;

        // A staged image is always attachable now — the SERVER decides how to route it (inline
        // to a vision-capable text model, or delegated to a separate vision model).
        var attachment = null;
        var stagedPreview = null;
        if (stagedAttachment) {
            attachment = { mime: stagedAttachment.mime, dataBase64: stagedAttachment.dataBase64 };
            stagedPreview = stagedAttachment.previewUrl;
        }

        var bubble = addBubble('user', renderMarkdown(text));
        if (stagedPreview) {
            renderImageFigure(bubble.querySelector('.chat-bubble-body'), stagedPreview, null, 'user-upload');
        }
        inputEl.value = '';
        clearAttachment();
        hideEmpty();

        var body = {
            conversationId: currentConversationId,
            message: text,
            systemPrompt: SYSTEM_PROMPT
        };
        if (attachment) body.attachment = attachment;
        runTurn(makeTurn(), '/api-proxy/chat/stream', body);
    }

    // Builds a fresh assistant turn: an empty bubble with a "Thinking…" placeholder + tool-status slot.
    function makeTurn() {
        var bubble = addBubble('assistant', '');
        var body = bubble.querySelector('.chat-bubble-body');
        var streamEl = document.createElement('div');
        streamEl.className = 'text-muted';
        streamEl.textContent = 'Thinking…';
        body.appendChild(streamEl);
        var statusEl = document.createElement('div');
        statusEl.style.cssText = 'font-size:0.8rem;color:var(--text-secondary);margin-top:4px;';
        body.appendChild(statusEl);
        return { bubble: bubble, body: body, streamEl: streamEl, statusEl: statusEl,
                 assistantRaw: '', pending: null, done: false, pendingImage: false };
    }

    // Streams one SSE response into `turn` — the initial /stream turn (send) OR a /confirm
    // continuation (resumeConfirm). Owns the Send/Stop button state and abort lifecycle.
    function runTurn(turn, url, bodyObj) {
        setSending(true);
        abortCtrl = new AbortController();
        streamTurn(url, bodyObj, turn).then(function () {
            finalizeTurn(turn);
            // Refresh the sidebar only when a turn fully finishes (a paused/confirm turn refreshes
            // when it eventually completes, or on the next turn).
            if (!turn.pending) loadHistory();
        }).catch(function (err) {
            handleTurnError(turn, err);
        }).finally(function () {
            // A turn paused for confirmation is no longer streaming — release the Send button so the
            // user can click Allow/Deny. resumeConfirm re-arms it for the continuation.
            setSending(false);
            abortCtrl = null;
        });
    }

    function streamTurn(url, bodyObj, turn) {
        return fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'Accept': 'text/event-stream' },
            body: JSON.stringify(bodyObj),
            signal: abortCtrl.signal
        }).then(function (resp) {
            var ct = resp.headers.get('content-type') || '';
            // The Web passthrough forwards JSON errors (409 locked, 400 bad request, 5xx) verbatim
            // with a non-event-stream content-type. A text/event-stream response is always 200 and
            // consumed as a stream.
            if (ct.indexOf('text/event-stream') >= 0) {
                return readStream(resp, turn);
            }
            return resp.text().then(function (bodyText) {
                var msg = 'Request failed';
                try { msg = (JSON.parse(bodyText).error) || msg; } catch (e) { if (bodyText) msg = bodyText; }
                throw new Error(msg);
            });
        });
    }

    // Finalize the bubble on stream end. A turn PAUSED for confirmation keeps its streamed text
    // until the continuation completes (so the user sees what the AI said before asking to write).
    // Phase 5: an image-gen turn has images already rendered inline — append any caption text after
    // them instead of replacing the bubble contents.
    function finalizeTurn(turn) {
        if (turn.pending) return;
        turn.statusEl.textContent = '';
        turn.streamEl.className = '';
        if (turn.pendingImage) {
            // Reuse the same dedicated text element the 'delta' handler streamed into (if any) so
            // the final markdown render replaces it in place, below the image, instead of appending
            // a second duplicate copy of the same text.
            if (!turn.textEl) {
                turn.textEl = document.createElement('div');
                turn.textEl.style.marginTop = '4px';
                turn.streamEl.appendChild(turn.textEl);
            }
            turn.textEl.innerHTML = renderMarkdown(turn.assistantRaw || '');
        } else {
            turn.streamEl.innerHTML = renderMarkdown(turn.assistantRaw || '(no response)');
        }
    }

    function handleTurnError(turn, err) {
        if (err && err.name === 'AbortError') {
            // Stopped by the user or navigation. Keep whatever streamed; mark as interrupted.
            turn.statusEl.textContent = '';
            if (!turn.assistantRaw) {
                if (!turn.pendingImage) {
                    turn.streamEl.className = 'text-muted';
                    turn.streamEl.textContent = '(stopped)';
                }
            } else {
                turn.streamEl.className = '';
                if (turn.pendingImage) {
                    if (!turn.textEl) {
                        turn.textEl = document.createElement('div');
                        turn.textEl.style.marginTop = '4px';
                        turn.streamEl.appendChild(turn.textEl);
                    }
                    turn.textEl.innerHTML = renderMarkdown(turn.assistantRaw);
                } else {
                    turn.streamEl.innerHTML = renderMarkdown(turn.assistantRaw);
                }
            }
            return;
        }
        turn.statusEl.textContent = '';
        turn.streamEl.className = '';
        turn.streamEl.innerHTML = '<span style="color:var(--sl-color-danger-600);">'
            + escapeHtml((err && err.message) || 'Network error — could not reach the server.')
            + '</span>';
    }

    // Phase 3: render an inline Allow/Deny card for a paused write tool call. Clicking posts to the
    // confirm endpoint, which executes (Allow) or denies (Deny) the write and streams the
    // continuation back into the SAME bubble (Shoelace sl-button + sl-icon, matching the app).
    function renderConfirmCard(turn, obj) {
        var card = document.createElement('div');
        card.className = 'chat-confirm-card';
        card.style.cssText = 'margin-top:10px;padding:8px 10px;border:1px solid var(--sl-color-warning-500);'
            + 'border-radius:6px;background:var(--sl-color-warning-100,var(--sl-color-neutral-100));';

        var head = document.createElement('div');
        head.style.cssText = 'font-size:0.85rem;margin-bottom:6px;';
        head.innerHTML = '<sl-icon name="shield-check" style="color:var(--sl-color-warning-600);vertical-align:-2px;"></sl-icon> '
            + '<strong>AI wants to:</strong> ' + escapeHtml(obj.argsSummary || (obj.toolName || 'run a write tool'));
        card.appendChild(head);

        var actions = document.createElement('div');
        actions.style.cssText = 'display:flex;gap:8px;';
        var allowBtn = document.createElement('sl-button');
        allowBtn.variant = 'success';
        allowBtn.size = 'small';
        allowBtn.innerHTML = '<sl-icon slot="prefix" name="check-lg"></sl-icon> Allow';
        var denyBtn = document.createElement('sl-button');
        denyBtn.variant = 'danger';
        denyBtn.size = 'small';
        denyBtn.innerHTML = '<sl-icon slot="prefix" name="x-lg"></sl-icon> Deny';
        actions.appendChild(allowBtn);
        actions.appendChild(denyBtn);
        card.appendChild(actions);

        turn.body.appendChild(card);
        messagesEl.scrollTop = messagesEl.scrollHeight;

        allowBtn.addEventListener('click', function () { resumeConfirm(turn, obj.toolCallId, true, card); });
        denyBtn.addEventListener('click', function () { resumeConfirm(turn, obj.toolCallId, false, card); });
    }

    // Posts an Allow/Deny decision; the server executes/denies the write and streams the continuation.
    function resumeConfirm(turn, toolCallId, allow, card) {
        if (!currentConversationId) return; // confirm needs a pinned conversation
        // Disable the card so Allow/Deny can't fire twice (the server is also idempotent — 409 on a
        // double-resolve — but disabling keeps the UI tidy).
        if (card) {
            card.querySelectorAll('sl-button').forEach(function (b) { b.disabled = true; });
            var note = document.createElement('div');
            note.style.cssText = 'font-size:0.78rem;color:var(--text-secondary);margin-top:6px;';
            note.textContent = allow ? 'Allowed — applying…' : 'Denied — telling the AI…';
            card.appendChild(note);
        }
        // Remembered so the confirm_resolved handler can append an "open article" link to the
        // SAME card once the server reports the result (see handleFrame).
        turn._activeConfirmCard = card;
        runTurn(turn, '/api-proxy/chat/' + currentConversationId + '/confirm', {
            toolCallId: toolCallId,
            allow: allow,
            systemPrompt: SYSTEM_PROMPT
        });
    }

    // When the AI creates/edits an article (save/update/append/replace — never delete, which
    // leaves nothing to open), append a direct link to it that opens in a new tab. Appended into
    // the confirm card if there was one (human-approved path), otherwise directly into the bubble
    // body (auto-approve path, where there is no card).
    function appendArticleLink(container, articleId, toolName) {
        if (!container || !articleId) return;
        var verbs = {
            bee_save_article: 'Article created',
            bee_update_article: 'Article updated',
            bee_append_to_article: 'Article updated',
            bee_replace_in_article: 'Article updated'
        };
        var link = document.createElement('a');
        link.href = '/Article/View?id=' + encodeURIComponent(articleId);
        link.target = '_blank';
        link.rel = 'noopener';
        link.style.cssText = 'display:inline-flex;align-items:center;gap:4px;margin-top:6px;font-size:0.82rem;';
        link.innerHTML = '<sl-icon name="box-arrow-up-right" style="font-size:0.85rem;"></sl-icon> '
            + (verbs[toolName] || 'Open article') + ' — view';
        container.appendChild(link);
    }

    // Reads the SSE response body chunk by chunk, splits on blank-line frame boundaries, and
    // dispatches each `event:`/`data:` frame to handleFrame. The reader is always released so an
    // abort/error doesn't leak the underlying stream.
    function readStream(resp, turn) {
        var reader = resp.body.getReader();
        var decoder = new TextDecoder();
        var buffer = '';

        function pump() {
            return reader.read().then(function (chunk) {
                if (chunk.done) return;
                buffer += decoder.decode(chunk.value, { stream: true });
                var idx;
                while ((idx = buffer.indexOf('\n\n')) >= 0) {
                    var frame = buffer.slice(0, idx);
                    buffer = buffer.slice(idx + 2);
                    handleFrame(frame, turn);
                }
                return pump();
            });
        }
        return pump().finally(function () {
            try { reader.cancel(); } catch (e) {}
        });
    }

    function handleFrame(frame, turn) {
        var eventType = 'message';
        var dataLines = [];
        frame.split('\n').forEach(function (line) {
            if (line.indexOf('event:') === 0) eventType = line.slice(6).trim();
            else if (line.indexOf('data:') === 0) dataLines.push(line.slice(5).replace(/^ /, ''));
        });
        var dataStr = dataLines.join('\n');
        var obj = null;
        if (dataStr) {
            try { obj = JSON.parse(dataStr); } catch (e) { obj = null; }
        }

        if (eventType === 'conversation' && obj && obj.conversationId) {
            // Pin the (possibly brand-new) conversation and refresh the sidebar.
            currentConversationId = obj.conversationId;
            loadHistory();
        } else if (eventType === 'delta' && obj) {
            // Append text to a DEDICATED child element, never to streamEl's own textContent —
            // streamEl may already hold an <figure> image (generated-image event, above), and
            // `el.textContent += x` on an element with element children wipes ALL of them (it reads
            // the current text, then rebuilds the whole subtree as a single text node), silently
            // deleting the image. Markdown is rendered once on done to avoid partial-markdown flicker.
            if (!turn.streamEl._streamingStarted) {
                turn.streamEl._streamingStarted = true;
                turn.streamEl.className = '';
                turn.streamEl.textContent = '';
            }
            if (!turn.textEl) {
                turn.textEl = document.createElement('div');
                turn.textEl.style.marginTop = '4px';
                turn.streamEl.appendChild(turn.textEl);
            }
            turn.textEl.textContent += (obj.text || '');
        } else if (eventType === 'tool_call_start' && obj) {
            turn.statusEl.textContent = obj.label || ('Running ' + (obj.tool || 'tool') + '…');
        } else if (eventType === 'tool_call_result' && obj) {
            turn.statusEl.textContent = '';
        } else if (eventType === 'image' && obj) {
            // Phase 5: an image-gen model returned an image. Render it inline in the live bubble
            // (clearing the "Thinking…" placeholder first); finalizeTurn appends any caption on done.
            if (!turn.streamEl._streamingStarted) {
                turn.streamEl._streamingStarted = true;
                turn.streamEl.className = '';
                turn.streamEl.textContent = '';
            }
            if (obj.dataUrl) {
                renderImageFigure(turn.streamEl, obj.dataUrl, obj.attachmentId, 'generated-image');
                turn.pendingImage = true;
            }
        } else if (eventType === 'confirm_required' && obj) {
            // Phase 3: a write tool wants to run. Show Allow/Deny; the stream ends right after this
            // frame (the server pauses the turn). resumeConfirm re-arms a continuation stream.
            turn.pending = obj;
            turn.statusEl.textContent = '';
            renderConfirmCard(turn, obj);
        } else if (eventType === 'confirm_resolved' && obj) {
            // Server executed (allow) or denied the write; the continuation follows as deltas.
            // Fires with NO preceding confirm_required when auto-approve is on (Admin -> AI/Chat) —
            // in that case there is no card, so the link is appended straight into the bubble body.
            turn.statusEl.textContent = '';
            if (obj.ok && obj.articleId) {
                appendArticleLink(turn._activeConfirmCard || turn.body, obj.articleId, obj.toolName);
            }
        } else if (eventType === 'done' && obj) {
            if (typeof obj.content === 'string') turn.assistantRaw = obj.content;
            if (obj.conversationId) currentConversationId = obj.conversationId;
            turn.pending = null;
            turn.done = true;
        } else if (eventType === 'error' && obj) {
            throw new Error(obj.error || 'Server error');
        }
    }

    // ── Phase 5: "Save to Bee" ────────────────────────────────────────────────
    // Uploads a chat image (uploaded or generated) to the vault media store via the EXISTING,
    // ACL-checked /api-proxy/media/upload path, attached to a user-picked note found through the
    // existing /api-proxy/search. This NEVER happens automatically — only on the explicit click.
    // It writes only to whatever note the user picks, through the same ACL the editor uses.

    var saveDialog = document.getElementById('chat-save-to-bee');
    var savePreview = document.getElementById('chat-save-preview');
    var saveSearch = document.getElementById('chat-save-search');
    var saveResults = document.getElementById('chat-save-results');
    var saveSelected = document.getElementById('chat-save-selected');
    var saveConfirm = document.getElementById('chat-save-confirm');
    var saveSourceBlob = null;     // Blob to upload (built from the image src).
    var saveFileName = 'chat-image.png';
    var saveKind = null;           // 'generated-image' or 'attached' — picks the markdown alt text.
    var saveArticleId = null;
    var saveSearchTimer = null;

    function openSaveToBee(src, attachmentId, kind) {
        saveArticleId = null;
        if (saveConfirm) saveConfirm.disabled = true;
        if (saveSelected) saveSelected.textContent = '';
        if (saveResults) saveResults.innerHTML = '';
        if (saveSearch) saveSearch.value = '';
        if (savePreview) {
            savePreview.innerHTML = '';
            var img = document.createElement('img');
            img.src = src;
            img.style.cssText = 'max-height:160px;max-width:100%;border-radius:6px;border:1px solid var(--sl-panel-border-color);';
            savePreview.appendChild(img);
        }
        // Resolve the source into a Blob for the upload. A data: URL → fetch(blob); a persisted
        // attachment proxy URL → fetch the bytes directly (still goes through the ownership check).
        saveSourceBlob = null;
        fetch(src).then(function (r) { return r.blob(); }).then(function (b) {
            saveSourceBlob = b;
        }).catch(function () { saveSourceBlob = null; });
        saveKind = kind;
        saveFileName = (kind === 'generated-image' ? 'chat-generated' : 'chat-upload')
            + '-' + (attachmentId ? attachmentId.toString().slice(0, 8) : Date.now()) + '.png';
        if (saveDialog) saveDialog.show();
    }

    function runSaveSearch(q) {
        if (!saveResults) return;
        saveResults.innerHTML = '';
        if (!q) return;
        fetch('/api-proxy/search?q=' + encodeURIComponent(q), { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                var articles = (data && data.articles) || [];
                if (!articles.length) {
                    var empty = document.createElement('div');
                    empty.className = 'text-muted';
                    empty.style.cssText = 'padding:8px;font-size:0.8rem;';
                    empty.textContent = 'No notes match.';
                    saveResults.appendChild(empty);
                    return;
                }
                // Server returns title-sorted results (underscore-prefixed system notes first);
                // keep that order, just cap the dropdown at 20.
                articles.slice(0, 20).forEach(function (a) {
                    var row = document.createElement('div');
                    row.style.cssText = 'padding:6px 8px;cursor:pointer;font-size:0.86rem;';
                    row.addEventListener('mouseenter', function () { row.style.background = 'var(--sl-color-neutral-100)'; });
                    row.addEventListener('mouseleave', function () { if (!row._picked) row.style.background = 'transparent'; });
                    row.innerHTML = '<sl-icon name="file-earmark" style="vertical-align:-2px;margin-right:4px;color:var(--text-secondary);"></sl-icon>'
                        + escapeHtml(a.title || '(untitled)')
                        + ' <span class="text-muted" style="font-size:0.74rem;">' + escapeHtml(a.treePath || '') + '</span>';
                    row.addEventListener('click', function () {
                        saveArticleId = a.id;
                        // Visually mark the picked row.
                        Array.prototype.forEach.call(saveResults.children, function (c) {
                            c._picked = false; c.style.background = 'transparent';
                        });
                        row._picked = true;
                        row.style.background = 'var(--sl-color-primary-100)';
                        if (saveSelected) saveSelected.textContent = 'Selected: ' + (a.title || '(untitled)') + ' (' + (a.treePath || '') + ')';
                        if (saveConfirm) saveConfirm.disabled = false;
                    });
                    saveResults.appendChild(row);
                });
            })
            .catch(function () {});
    }

    function showSaveError(msg) {
        if (saveSelected) {
            saveSelected.textContent = 'Save failed: ' + msg;
            saveSelected.style.color = 'var(--sl-color-danger-600)';
        }
    }

    function doSaveToBee() {
        if (!saveArticleId || !saveSourceBlob) return;
        if (saveConfirm) { saveConfirm.loading = true; saveConfirm.disabled = true; }
        // Three steps, all through existing ACL-checked proxies:
        //   1. GET the article (refuse protected targets BEFORE uploading anything);
        //   2. upload the image bytes (media row, FK'd to the article);
        //   3. PUT the content back with a markdown image reference appended, so the image
        //      actually appears in the note (View/Preview already rewrite /api/media/ src).
        // The GET happens here, inside the click, so the read-modify-write window is one
        // network hop — same order of magnitude as the server-side MCP append tool.
        var articleContent = null;
        fetch('/api-proxy/article/' + saveArticleId, { headers: { 'Accept': 'application/json' } })
            .then(function (r) {
                if (!r.ok) throw new Error('could not load the selected note.');
                return r.json();
            })
            .then(function (data) {
                if (data && data.article && data.article.protected) {
                    throw new Error('this note is password-protected. Open it in the editor, unlock it, and paste the image there instead.');
                }
                if (!data || typeof data.content !== 'string') {
                    throw new Error('could not read the note content (is the vault unlocked?).');
                }
                articleContent = data.content;
                var fd = new FormData();
                // The existing /api-proxy/media/upload expects a "file" part + an "articleId" field.
                // The API MediaEndpoints ACL-checks the article's folder path before storing — a
                // read-only or access-denied folder is rejected server-side (never bypassed here).
                fd.append('file', saveSourceBlob, saveFileName);
                fd.append('articleId', saveArticleId);
                return fetch('/api-proxy/media/upload', { method: 'POST', body: fd });
            })
            .then(function (r) {
                if (!r.ok) return r.text().then(function (t) { throw new Error(t || 'the server rejected the upload (check folder access).'); });
                return r.json();
            })
            .then(function (media) {
                var alt = saveKind === 'generated-image' ? 'Generated image' : 'Image from chat';
                var newContent = articleContent.replace(/\s+$/, '')
                    + '\n\n![' + alt + '](/api/media/' + media.id + ')\n';
                return fetch('/api-proxy/article/' + saveArticleId, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ content: newContent })
                });
            })
            .then(function (r) {
                if (!r.ok) throw new Error('the image was uploaded but could not be added to the note text.');
                if (saveDialog) saveDialog.hide();
            })
            .catch(function (err) {
                showSaveError((err && err.message) || 'the server rejected it (check folder access).');
            })
            .finally(function () {
                if (saveConfirm) { saveConfirm.loading = false; saveConfirm.disabled = !saveArticleId; }
            });
    }

    // ── wiring ───────────────────────────────────────────────────────────────

    // Stop button: while streaming, send() acts as Stop (aborts the in-flight fetch).
    if (stopBtn) stopBtn.addEventListener('click', send);

    // Sidebar collapse toggle. Starts collapsed (class present in markup); no persistence
    // by design — every fresh load is collapsed. (Optional localStorage persistence was
    // considered and deliberately skipped to keep this simple.)
    if (sidebarToggle) sidebarToggle.addEventListener('click', function () {
        if (sidebarEl) sidebarEl.classList.toggle('collapsed');
    });
    if (inputEl) {
        inputEl.addEventListener('keydown', function (e) {
            // Enter sends. Shift+Enter or Ctrl/Cmd+Enter inserts a newline instead.
            if (e.key === 'Enter' && !e.shiftKey && !e.ctrlKey && !e.metaKey) {
                e.preventDefault();
                send();
            }
        });
    }
    if (newBtn) newBtn.addEventListener('click', newConversation);

    // "+" menu (Shoelace sl-dropdown, same pattern as the _Layout theme picker). One
    // item only: "Add image" → the same hidden-file-input flow as before.
    if (plusMenu) plusMenu.addEventListener('sl-select', function (e) {
        var item = e.detail && e.detail.item;
        if (item && item.value === 'add-image' && attachInput) attachInput.click();
    });
    if (attachInput) attachInput.addEventListener('change', function () {
        if (attachInput.files && attachInput.files[0]) processImageFile(attachInput.files[0]);
    });
    if (inputEl) {
        inputEl.addEventListener('paste', function (e) {
            var items = e.clipboardData && e.clipboardData.items;
            if (!items) return;
            for (var i = 0; i < items.length; i++) {
                if (items[i].type && items[i].type.indexOf('image/') === 0) {
                    var f = items[i].getAsFile();
                    if (f) { e.preventDefault(); processImageFile(f); break; }
                }
            }
        });
    }

    // Drag-and-drop an image anywhere onto the composer pill. A depth counter handles
    // dragenter/dragleave firing on child elements (textarea, buttons) so the highlight
    // doesn't flicker. Drop reuses processImageFile — identical to the "+" menu path.
    if (composerEl) {
        var dragDepth = 0;
        composerEl.addEventListener('dragenter', function (e) {
            e.preventDefault();
            dragDepth++;
            composerEl.classList.add('drag-over');
        });
        composerEl.addEventListener('dragover', function (e) {
            e.preventDefault(); // required, or the browser navigates to the file on drop
        });
        composerEl.addEventListener('dragleave', function () {
            dragDepth = Math.max(0, dragDepth - 1);
            if (dragDepth === 0) composerEl.classList.remove('drag-over');
        });
        composerEl.addEventListener('drop', function (e) {
            e.preventDefault();
            dragDepth = 0;
            composerEl.classList.remove('drag-over');
            var f = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0];
            if (f) processImageFile(f);
        });
    }

    // Save-to-Bee dialog wiring.
    if (saveSearch) {
        saveSearch.addEventListener('sl-input', function () {
            var q = (saveSearch.value || '').trim();
            if (saveSearchTimer) clearTimeout(saveSearchTimer);
            saveSearchTimer = setTimeout(function () { runSaveSearch(q); }, 250);
        });
    }
    if (saveConfirm) saveConfirm.addEventListener('click', doSaveToBee);
    if (saveDialog) {
        saveDialog.addEventListener('sl-request-close', function (e) {
            // Allow the Cancel button (data-save-close) and overlay/Escape to close.
        });
        var cancelBtn = saveDialog.querySelector('[data-save-close]');
        if (cancelBtn) cancelBtn.addEventListener('click', function () { saveDialog.hide(); });
    }

    // Abort the in-flight stream if the user navigates away / closes the tab — propagates through
    // the Web passthrough to cancel the Api-side OpenRouter stream.
    window.addEventListener('beforeunload', function () {
        if (abortCtrl) { try { abortCtrl.abort(); } catch (e) {} }
    });

    // Start on a fresh (new) conversation. No model picker — the server resolves the
    // effective text model internally; the pill shows its label read-only.
    resetAttachHint();
    greet();
    loadHistory();
    loadModelLabel();
})();
