window.PagedConfig = {
    after: function (flow) {
        var pages = (flow && flow.total) ? flow.total : document.querySelectorAll('.pagedjs_page').length;
        parent.postMessage({ type: 'bmb-print-rendered', pages: pages }, '*');
    }
};
window.addEventListener('beforeprint', function () {
    document.documentElement.classList.add('bmb-printing');
    if (!document.getElementById('bmb-print-page')) {
        var s = document.createElement('style');
        s.id = 'bmb-print-page';
        s.textContent = '@page { size: A4; margin: 0; }';
        document.head.appendChild(s);
    }
});
window.addEventListener('afterprint', function () {
    document.documentElement.classList.remove('bmb-printing');
    var s = document.getElementById('bmb-print-page');
    if (s) s.remove();
});
