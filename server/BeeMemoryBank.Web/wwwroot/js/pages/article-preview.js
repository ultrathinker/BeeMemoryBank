(function () {
    // Markdown preservation and rendering
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

    // Theme sync for body class
    var currentTheme = document.documentElement.getAttribute('data-theme') || 'crimson';
    var isLight = currentTheme === 'light';
    if (isLight) {
        document.documentElement.classList.remove('sl-theme-dark');
        document.documentElement.classList.add('sl-theme-light');
        if (document.body) {
            document.body.classList.remove('sl-theme-dark');
            document.body.classList.add('sl-theme-light');
        }
    } else {
        document.documentElement.classList.remove('sl-theme-light');
        document.documentElement.classList.add('sl-theme-dark');
        if (document.body) {
            document.body.classList.remove('sl-theme-light');
            document.body.classList.add('sl-theme-dark');
        }
    }

    // Copy to clipboard handler
    document.addEventListener('click', function (e) {
        var el = e.target.closest('[data-bmb-copy]');
        if (!el) return;
        var text = el.getAttribute('data-bmb-copy');
        function flash() {
            el.style.transition = 'outline-color 0.2s';
            el.style.outline = '2px solid var(--sl-color-primary-500)';
            el.style.outlineOffset = '2px';
            setTimeout(function () { el.style.outline = ''; }, 500);
        }
        if (navigator.clipboard && window.isSecureContext) {
            navigator.clipboard.writeText(text).then(flash, function () {});
        } else {
            var ta = document.createElement('textarea');
            ta.value = text;
            ta.style.position = 'fixed';
            ta.style.opacity = '0';
            document.body.appendChild(ta);
            ta.select();
            try { document.execCommand('copy'); } catch (e) {}
            document.body.removeChild(ta);
            flash();
        }
    });

    // Render article markdown body
    var dataEl = document.getElementById('article-preview-data');
    if (dataEl) {
        var data = JSON.parse(dataEl.textContent || '{}');
        var content = data.content || '';
        if (content) {
            var body = document.getElementById('article-body');
            if (body && typeof marked !== 'undefined') {
                var renderer = new marked.Renderer();
                var origImage = renderer.image.bind(renderer);
                renderer.image = function (token) {
                    if (token && token.href && token.href.startsWith('/api/media/')) {
                        token.href = '/api-proxy/media/' + token.href.substring('/api/media/'.length);
                    }
                    return origImage(token);
                };
                body.innerHTML = window.bmbRenderMarkdown(content, { renderer: renderer });
            }
        }
    }
})();
