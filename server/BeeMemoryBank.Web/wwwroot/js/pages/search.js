// Search page interaction: mode toggle and auto-submit on mode change
(function () {
    var pageRoot = document.querySelector('#page-content .main-content') || document.getElementById('page-content');
    if (pageRoot) {
        if (pageRoot.dataset.searchInit) return;
        pageRoot.dataset.searchInit = '1';
    }

    var toggle = document.getElementById('searchModeToggle');
    var dropdown = document.getElementById('searchModeDropdown');
    var select = document.getElementById('searchModeSelect');
    var form = document.getElementById('searchForm');

    if (toggle && dropdown) {
        toggle.addEventListener('click', function () {
            var isOpen = dropdown.style.display !== 'none';
            dropdown.style.display = isOpen ? 'none' : 'flex';
            toggle.setAttribute('aria-expanded', isOpen ? 'false' : 'true');
        });
    }

    if (select && form) {
        select.addEventListener('sl-change', function () {
            form.submit();
        });
    }
})();
