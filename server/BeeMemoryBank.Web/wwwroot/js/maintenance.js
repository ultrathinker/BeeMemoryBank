(function () {
    'use strict';

    function checkSite() {
        fetch('/', { method: 'HEAD', cache: 'no-store' })
            .then(function (res) {
                if (res.status !== 502 && res.status !== 503) {
                    location.href = '/';
                }
            })
            .catch(function () {});
    }

    var delay = 5000;
    var titleText = 'Maintenance in progress';
    if (location.search.indexOf('restore') >= 0) {
        titleText = 'Snapshot restore in progress';
        var titleEl = document.getElementById('title-text');
        if (titleEl) {
            titleEl.textContent = titleText;
        }
    }

    setTimeout(function () {
        setInterval(checkSite, 3000);
    }, delay);
})();
