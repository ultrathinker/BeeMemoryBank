(function () {
    var u = document.documentElement.getAttribute('data-username') || '';
    var k = 'bee-theme' + (u ? '-' + u : '');
    var t = localStorage.getItem(k) || 'crimson';
    document.documentElement.setAttribute('data-theme', t);
    var html = document.documentElement;
    if (t === 'light') {
        html.classList.remove('sl-theme-dark');
        html.classList.add('sl-theme-light');
    } else {
        html.classList.remove('sl-theme-light');
        html.classList.add('sl-theme-dark');
    }

    function syncBody() {
        if (!document.body) return;
        if (t === 'light') {
            document.body.classList.remove('sl-theme-dark');
            document.body.classList.add('sl-theme-light');
        } else {
            document.body.classList.remove('sl-theme-light');
            document.body.classList.add('sl-theme-dark');
        }
    }
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', syncBody);
    } else {
        syncBody();
    }
})();
