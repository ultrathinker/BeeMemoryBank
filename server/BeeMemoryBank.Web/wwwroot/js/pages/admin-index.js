// The Admin index. The old single page had a card per block, and people bookmarked /Admin#card-snapshots. A URL fragment never reaches the server, so it is read
// here: a known fragment is moved to the page of its block (the table comes from AdminPageCatalog.LegacyAnchors, rendered into #admin-legacy-anchors).
(function () {
    'use strict';

    var holder = document.getElementById('admin-legacy-anchors');
    if (!holder) return;

    var id = '';
    try { id = decodeURIComponent((location.hash || '').slice(1)); } catch (err) { return; }
    if (!id) return;

    var map = {};
    try { map = JSON.parse(holder.getAttribute('data-map') || '{}'); } catch (err) { return; }

    // Only a page of the Admin area of this site: the table is the server's, and still nothing but /Admin/<Name> is followed.
    var target = Object.prototype.hasOwnProperty.call(map, id) ? map[id] : null;
    if (typeof target === 'string' && /^\/Admin\/[A-Za-z]+$/.test(target)) location.replace(target);
})();
