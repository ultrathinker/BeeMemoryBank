// Login page polling logic when DEK rotation or restore is in progress
(function () {
    if (!document.getElementById('login-form-active')) {
        var pageData = document.getElementById('page-data');
        var interval = 3000;
        if (pageData) {
            try {
                var data = JSON.parse(pageData.textContent);
                if (data && data.reloadInterval) interval = data.reloadInterval;
            } catch (e) {}
        }
        setTimeout(function () { location.reload(); }, interval);
    }
})();
