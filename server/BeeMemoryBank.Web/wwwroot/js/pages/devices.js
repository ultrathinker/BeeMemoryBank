// Admin > Devices. The address /Admin/Devices#connect-device opens the "Connect a device" dialog: that is where the old page /Admin/ConnectDevice and /Connect lead,
// and where a link from another page can point. The dialog opens nothing on the network by itself; the button inside it does (a POST).
(function () {
    'use strict';

    function openConnectDialog() {
        var dialog = document.getElementById('dlg-connect-device');
        if (dialog && typeof dialog.show === 'function' && !dialog.open) dialog.show();
    }

    var wanted = '';
    try { wanted = decodeURIComponent((location.hash || '').slice(1)); } catch (err) { wanted = ''; }
    if (wanted !== 'connect-device') return;

    // Shoelace defines the element a moment after the page is parsed: wait for it, then open.
    if (window.customElements && customElements.whenDefined) {
        customElements.whenDefined('sl-dialog').then(openConnectDialog);
    } else {
        openConnectDialog();
    }
})();
