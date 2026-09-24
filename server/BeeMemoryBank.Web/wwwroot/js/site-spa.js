// ===== SPA Navigation =====
(function () {
    function isSpaUrl(pathname) {
        return pathname === '/Folder' ||
               pathname === '/Article/View' ||
               pathname === '/Search' ||
               pathname === '/Activity' ||
               pathname === '/Admin';
    }

    function executeScripts(container) {
        var scripts = Array.from(container.querySelectorAll('script'));
        var executable = scripts.filter(function (s) {
            var type = (s.getAttribute('type') || '').toLowerCase();
            return !type || type === 'text/javascript' || type === 'module';
        });
        var external = executable.filter(function (s) { return s.src; });

        return Promise.all(external.map(function (oldScript) {
            return new Promise(function (resolve) {
                var newScript = document.createElement('script');
                Array.from(oldScript.attributes).forEach(function (attr) {
                    newScript.setAttribute(attr.name, attr.value);
                });
                newScript.onload = resolve;
                newScript.onerror = resolve;
                oldScript.parentNode.replaceChild(newScript, oldScript);
            });
        }));
    }

    async function spaAfterSwap() {
        document.body.classList.remove('graph-page', 'ai-chat-page', 'home-chat-page', 'chat-ui');
        var pageContent = document.getElementById('page-content');
        if (!pageContent) return;

        if (window.bmbInitSlForms) window.bmbInitSlForms(pageContent);
        await executeScripts(pageContent);
        if (window.bmbHighlightActiveTreeItem) window.bmbHighlightActiveTreeItem(true);
        var mainContent = pageContent.querySelector('.main-content');
        if (mainContent) mainContent.scrollTop = 0;

        document.dispatchEvent(new CustomEvent('bmb:page-swapped'));
        document.dispatchEvent(new CustomEvent('bmb:page-loaded'));
    }

    var spaNavigating = false;

    function spaNavigate(url, pushState) {
        if (spaNavigating) return;
        var targetUrl = (typeof url === 'string') ? new URL(url, window.location.origin) : url;
        if (!isSpaUrl(targetUrl.pathname)) { window.location.href = targetUrl.href; return; }
        if (pushState !== false && targetUrl.href === window.location.href) return;

        spaNavigating = true;
        var spaTimeout = setTimeout(function () { spaNavigating = false; }, 30000);
        var mainContent = document.querySelector('.main-content');
        if (mainContent) { mainContent.style.opacity = '0.5'; mainContent.style.pointerEvents = 'none'; }

        fetch(targetUrl.href)
            .then(function (response) {
                if (!response.ok) throw new Error('HTTP ' + response.status);
                if (response.redirected && response.url.includes('/Login')) {
                    window.location.href = response.url; throw new Error('redirect');
                }
                return response.text();
            })
            .then(async function (html) {
                var parser = new DOMParser();
                var doc = parser.parseFromString(html, 'text/html');
                var newContent = doc.getElementById('page-content');
                var currentContent = document.getElementById('page-content');
                if (!newContent || !currentContent) { window.location.href = targetUrl.href; return; }
                currentContent.innerHTML = newContent.innerHTML;
                var newTitle = doc.querySelector('title');
                if (newTitle) document.title = newTitle.textContent;
                if (pushState !== false) history.pushState({ spa: true }, '', targetUrl.href);
                await spaAfterSwap();
            })
            .catch(function (err) {
                if (err.message === 'redirect') return;
                window.location.href = targetUrl.href;
            })
            .finally(function () {
                clearTimeout(spaTimeout);
                spaNavigating = false;
                if (mainContent) { mainContent.style.opacity = ''; mainContent.style.pointerEvents = ''; }
            });
    }

    document.addEventListener('click', function (e) {
        if (e.ctrlKey || e.metaKey || e.shiftKey || e.altKey || e.button !== 0) return;
        var link = e.target.closest('a[href]');
        var slBtn = !link ? e.target.closest('sl-button[href]') : null;
        var href = link ? link.getAttribute('href') : (slBtn ? slBtn.getAttribute('href') : null);
        if (!href || href.startsWith('#')) return;
        if ((link || slBtn) && (link || slBtn).getAttribute('target')) return;
        var url;
        try { url = new URL(href, window.location.origin); } catch (err) { return; }
        if (url.origin !== window.location.origin) return;
        if (!isSpaUrl(url.pathname)) return;
        e.preventDefault();
        if (slBtn) e.stopPropagation();
        spaNavigate(url, true);
    });

    window.addEventListener('popstate', function () {
        var url = new URL(window.location.href);
        if (isSpaUrl(url.pathname)) spaNavigate(url, false);
        else window.location.reload();
    });

    if (isSpaUrl(window.location.pathname)) {
        history.replaceState({ spa: true }, '', window.location.href);
    }

    window.bmbSpaNavigate = spaNavigate;
    window.bmbIsSpaUrl = isSpaUrl;
})();
