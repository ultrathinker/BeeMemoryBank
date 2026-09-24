// ===== SPA Navigation =====
(function () {
    function isSpaUrl(pathname) {
        return pathname === '/Folder' ||
               pathname === '/Article/View' ||
               pathname === '/Search' ||
               pathname === '/Activity' ||
               pathname === '/Admin';
    }

    var currentPageAbortController = new AbortController();
    window.bmbGetPageSignal = function () {
        return currentPageAbortController.signal;
    };

    function spaCleanup() {
        if (currentPageAbortController) {
            currentPageAbortController.abort();
            currentPageAbortController = new AbortController();
        }
        document.dispatchEvent(new CustomEvent('bmb:page-cleanup'));
    }

    function getSectionScripts(doc) {
        if (!doc || !doc.body) return [];
        var bodyScripts = Array.from(doc.body.querySelectorAll('script'));
        var siteJsIdx = -1;
        for (var i = 0; i < bodyScripts.length; i++) {
            var src = bodyScripts[i].getAttribute('src') || '';
            if (src.includes('site.js')) {
                siteJsIdx = i;
                break;
            }
        }
        return siteJsIdx >= 0 ? bodyScripts.slice(siteJsIdx + 1) : [];
    }

    async function executeScripts(container) {
        var scripts = Array.from(container.querySelectorAll('script'));
        var executable = scripts.filter(function (s) {
            var type = (s.getAttribute('type') || '').toLowerCase();
            return !type || type === 'text/javascript' || type === 'module';
        });

        for (var i = 0; i < executable.length; i++) {
            var oldScript = executable[i];
            await new Promise(function (resolve) {
                var newScript = document.createElement('script');
                Array.from(oldScript.attributes).forEach(function (attr) {
                    newScript.setAttribute(attr.name, attr.value);
                });
                newScript.onload = resolve;
                newScript.onerror = resolve;
                if (!oldScript.src && oldScript.textContent) {
                    newScript.textContent = oldScript.textContent;
                    oldScript.parentNode.replaceChild(newScript, oldScript);
                    resolve();
                } else {
                    oldScript.parentNode.replaceChild(newScript, oldScript);
                }
            });
        }
    }

    async function spaAfterSwap(doc) {
        document.body.classList.remove('graph-page', 'ai-chat-page', 'home-chat-page', 'chat-ui');
        var pageContent = document.getElementById('page-content');
        if (!pageContent) return;

        // Process target page scripts from @section Scripts outside #page-content
        var sectionScripts = getSectionScripts(doc);
        sectionScripts.forEach(function (s) {
            var clone = document.createElement('script');
            Array.from(s.attributes).forEach(function (attr) {
                clone.setAttribute(attr.name, attr.value);
            });
            clone.textContent = s.textContent;
            pageContent.appendChild(clone);
        });

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

                spaCleanup();

                currentContent.innerHTML = newContent.innerHTML;
                var newTitle = doc.querySelector('title');
                if (newTitle) document.title = newTitle.textContent;
                if (pushState !== false) history.pushState({ spa: true }, '', targetUrl.href);
                await spaAfterSwap(doc);
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
