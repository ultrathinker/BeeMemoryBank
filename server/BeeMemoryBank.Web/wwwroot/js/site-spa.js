// ===== SPA Navigation =====
(function () {
    function isSpaUrl(pathname) {
        if (!pathname) return false;
        var p = pathname.replace(/\/+$/, '') || '/';
        var lower = p.toLowerCase();
        return lower === '/folder' ||
               lower === '/article/view' ||
               lower === '/search' ||
               lower === '/activity' ||
               lower === '/admin';
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
        var pageContent = document.getElementById('page-content');
        if (pageContent) {
            delete pageContent.dataset.adminInit;
            delete pageContent.dataset.folderInit;
            delete pageContent.dataset.articleViewInit;
            delete pageContent.dataset.searchInit;
            delete pageContent.dataset.treeInit;
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

    function extractPageScripts(doc, newContent) {
        var result = [];
        var seen = new Set();

        function add(scriptEl) {
            var type = (scriptEl.getAttribute('type') || '').toLowerCase();
            // Ignore non-executable data islands such as application/json
            if (type && type !== 'text/javascript' && type !== 'module') return;
            var src = scriptEl.getAttribute('src');
            if (src) {
                var key;
                try { key = new URL(src, window.location.origin).pathname; }
                catch (e) { key = src; }
                if (seen.has(key)) return;
                seen.add(key);
            }
            result.push({
                src: src || null,
                type: type || null,
                textContent: scriptEl.textContent || '',
                attributes: Array.from(scriptEl.attributes).map(function (a) {
                    return { name: a.name, value: a.value };
                })
            });
        }

        // 1. Any executable scripts inside newContent (e.g. /Folder, /Article/View)
        if (newContent) {
            var innerScripts = Array.from(newContent.querySelectorAll('script'));
            innerScripts.forEach(function (s) {
                var type = (s.getAttribute('type') || '').toLowerCase();
                if (!type || type === 'text/javascript' || type === 'module') {
                    add(s);
                    // Remove from newContent so innerHTML doesn't place dead inert script tags in the DOM
                    s.remove();
                }
            });
        }

        // 2. Any section scripts from doc.body (rendered via @section Scripts after site.js)
        var sectionScripts = getSectionScripts(doc);
        sectionScripts.forEach(function (s) {
            add(s);
        });

        return result;
    }

    async function executeScripts(scriptList, container) {
        for (var i = 0; i < scriptList.length; i++) {
            var item = scriptList[i];
            await new Promise(function (resolve) {
                var script = document.createElement('script');
                item.attributes.forEach(function (attr) {
                    script.setAttribute(attr.name, attr.value);
                });
                script.onload = resolve;
                script.onerror = function (err) {
                    console.error('Failed to load SPA script:', script.src, err);
                    resolve();
                };
                if (!item.src && item.textContent) {
                    script.textContent = item.textContent;
                    container.appendChild(script);
                    resolve();
                } else {
                    container.appendChild(script);
                }
            });
        }
    }

    async function spaAfterSwap(pageContent, scriptsToExecute) {
        document.body.classList.remove('graph-page', 'ai-chat-page', 'home-chat-page', 'chat-ui');
        if (!pageContent) return;

        if (window.bmbInitSlForms) window.bmbInitSlForms(pageContent);
        if (scriptsToExecute && scriptsToExecute.length > 0) {
            await executeScripts(scriptsToExecute, pageContent);
        }
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

                // Extract all executable scripts while still inert and detached in doc
                var scriptsToExecute = extractPageScripts(doc, newContent);

                spaCleanup();

                currentContent.innerHTML = newContent.innerHTML;
                var newTitle = doc.querySelector('title');
                if (newTitle) document.title = newTitle.textContent;
                if (pushState !== false) history.pushState({ spa: true }, '', targetUrl.href);
                await spaAfterSwap(currentContent, scriptsToExecute);
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
        if (e.ctrlKey || e.metaKey || e.shiftKey || e.altKey || (e.button !== 0 && e.button !== undefined)) return;
        var target = e.target;
        if (target && target.nodeType === Node.TEXT_NODE) target = target.parentElement;
        var link = (e.composedPath ? e.composedPath().find(function (el) { return el && el.tagName === 'A' && el.hasAttribute && el.hasAttribute('href'); }) : null) ||
                   (target && target.closest ? target.closest('a[href]') : null);
        var slBtn = !link ? ((e.composedPath ? e.composedPath().find(function (el) { return el && el.tagName === 'SL-BUTTON' && el.hasAttribute && el.hasAttribute('href'); }) : null) ||
                   (target && target.closest ? target.closest('sl-button[href]') : null)) : null;
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
