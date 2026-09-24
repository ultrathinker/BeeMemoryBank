(function () {
    // Tag the body so the CSS rules that clip horizontal overflow only apply here.
    document.body.classList.add('graph-page');

    var graphDataEl = document.getElementById('graph-data');
    var graphData = graphDataEl ? JSON.parse(graphDataEl.textContent || '{}') : {};
    const initialFocus = graphData.focusConcept || null;

    document.getElementById('btn-graph-refresh')?.addEventListener('click', function () {
        window.location.reload();
    });

    // Register the fcose layout once; fcose is orders of magnitude faster than
    // the built-in cose for large graphs (seconds vs. a blocking half-minute)
    // and produces a visibly cleaner layout.
    if (window.cytoscape && window.cytoscapeFcose) {
        cytoscape.use(cytoscapeFcose);
    } else {
        console.error('Graph page: cytoscape or cytoscape-fcose failed to load.');
    }

    let nodes = [];
    let links = [];
    let focusedNodeId = null;
    let singleClickTimer = null;

    let hiddenMatches = [];        // match node data not yet shown
    const GREEN_MORE_ID = '__more_matches__';
    const NEIGH_MORE_PREFIX = '__more_neigh__';   // aggregator id = prefix + parentNodeId
    const expandedNeighborData = new Map();        // parentName -> full neighbor list [{name, weight}] once fetched
    let allSearchLinks = [];

    const filterInput = document.getElementById('concept-filter');
    let allConcepts = [];
    let currentTablePage = 1;
    const tablePageSize = 200;

    // Folder-scope for graph search (null or '/' = no restriction). Set by the
    // "Select folder" picker above the filter bar; threaded into the search fetch URL.
    let searchFolderScope = null;

    function escTableHtml(s) {
        return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function renderConceptTable() {
        const q = (filterInput.value || '').toLowerCase();
        let filtered = allConcepts.filter(d => d.name.toLowerCase().includes(q));
        filtered.sort((a, b) => b.count - a.count);
        const rows = document.getElementById('concept-table-rows');
        const pager = document.getElementById('concept-table-pager');
        const pagerInfo = document.getElementById('concept-table-pager-info');

        if (filtered.length === 0) {
            rows.innerHTML = '<p style="color:var(--sl-color-neutral-400);text-align:center;padding:32px 0;">No tags found.</p>';
            pager.style.display = 'none';
            return;
        }

        const totalPages = Math.max(1, Math.ceil(filtered.length / tablePageSize));
        if (currentTablePage > totalPages) currentTablePage = totalPages;
        if (currentTablePage < 1) currentTablePage = 1;
        const pageItems = filtered.slice((currentTablePage - 1) * tablePageSize, currentTablePage * tablePageSize);

        rows.innerHTML = pageItems.map(d => {
            const escaped = escTableHtml(d.name);
            const variant = d.count > 5 ? 'primary' : 'neutral';
            return '<div class="concept-row" style="display:flex;align-items:center;justify-content:space-between;padding:10px 0;border-bottom:1px solid var(--sl-color-neutral-200);">' +
                '<a href="#" role="button" class="concept-table-link" data-name="' + escaped + '" style="font-weight:500;">' + escaped + '</a>' +
                '<sl-badge variant="' + variant + '" pill>' + d.count + '</sl-badge>' +
                '</div>';
        }).join('');

        if (totalPages > 1) {
            pagerInfo.textContent = 'Page ' + currentTablePage + ' of ' + totalPages + ' \u00B7 ' + filtered.length + ' tags';
            pager.style.display = 'flex';
        } else {
            pager.style.display = 'none';
        }
    }

    document.getElementById('concept-table-pager-prev')?.addEventListener('click', () => {
        if (currentTablePage > 1) { currentTablePage--; renderConceptTable(); }
    });
    document.getElementById('concept-table-pager-next')?.addEventListener('click', () => {
        const q = (filterInput.value || '').toLowerCase();
        const filtered = allConcepts.filter(d => d.name.toLowerCase().includes(q));
        const totalPages = Math.max(1, Math.ceil(filtered.length / tablePageSize));
        if (currentTablePage < totalPages) { currentTablePage++; renderConceptTable(); }
    });

    filterInput?.addEventListener('sl-input', e => {
        const q = (e.target.value || '').toLowerCase();
        currentTablePage = 1;
        renderConceptTable();
        if (cy) {
            cy.batch(() => {
                cy.nodes().forEach(n => {
                    const match = !q || n.data('name').toLowerCase().includes(q);
                    n.style('opacity', match ? 1 : 0.1);
                    n.scratch('_filtered', !match);
                });
                cy.edges().forEach(edge => {
                    const srcHidden = edge.source().scratch('_filtered');
                    const tgtHidden = edge.target().scratch('_filtered');
                    edge.style('opacity', (srcHidden || tgtHidden) ? 0.05 : 0.6);
                });
            });
            return;
        }
        if (node && link && labels) {
            node.style('opacity', d => d.name.toLowerCase().includes(q) ? 1 : 0.1);
            link.style('opacity', d => d.source.id.toLowerCase().includes(q) || d.target.id.toLowerCase().includes(q) ? 0.6 : 0.05);
            labels.style('opacity', d => d.name.toLowerCase().includes(q) ? 1 : 0.1);
        }
    });

    document.getElementById('concept-table-rows')?.addEventListener('click', function(e) {
        var link = e.target.closest('.concept-table-link');
        if (!link) return;
        e.preventDefault();
        switchToGraphAndCenter(link.dataset.name);
    });

    renderConceptTable();

    // ─── View toggle ──────────────────────────────────────────────────────────
    const graphContainer = document.getElementById('graph-container');
    const tableContainer = document.getElementById('table-container');
    const btnGraph = document.getElementById('btn-graph-view');
    const btnTable = document.getElementById('btn-table-view');

    function setView(isGraph) {
        if (!graphContainer || !tableContainer) return;
        graphContainer.style.display = isGraph ? 'block' : 'none';
        tableContainer.style.display = isGraph ? 'none' : 'block';
        btnGraph?.setAttribute('variant', isGraph ? 'primary' : 'default');
        btnTable?.setAttribute('variant', isGraph ? 'default' : 'primary');
        if (isGraph) {
            if (cy) {
                setTimeout(() => { cy.resize(); cy.fit(); }, 10);
            } else if (simulation) {
                setTimeout(() => {
                    width = graphContainer.clientWidth;
                    height = graphContainer.clientHeight;
                    simulation.force("center", d3.forceCenter(width / 2, height / 2));
                    simulation.alpha(0.3).restart();
                }, 10);
            }
        }
    }

    btnGraph?.addEventListener('click', () => setView(true));
    btnTable?.addEventListener('click', () => setView(false));

    window.switchToGraphAndCenter = (conceptName) => {
        setView(true);
        centerOnNode(conceptName);
    };

    let simulation, node, link, labels;
    let svg, g, zoom;
    let cy = null;
    let width, height;
    let currentMode = 'home';
    const expandedTags = new Set();

    const btnModeHome = document.getElementById('btn-mode-home');
    const btnModeFull = document.getElementById('btn-mode-full');
    const graphPlaceholder = document.getElementById('graph-placeholder');
    const legendEl = document.getElementById('graph-legend');

    const LEGEND_BY_MODE = {
        home: [
            { color: '#3b82f6', label: 'Base — popular tags' },
            { color: '#f97316', label: 'Pulse — recently active' },
            { color: 'transparent', border: 'var(--sl-color-warning-500)', label: 'Dashed — has hidden neighbors (double-click to expand)' },
        ],
        search: [
            { color: '#f97316', label: 'Match — your query' },
            { color: '#3b82f6', label: 'Neighbor — linked to matches' },
            { color: '#86efac', label: 'More matches — click to reveal 10 more' },
            { color: '#cbd5e1', label: 'More neighbors — click to reveal 10 more' },
            { color: 'transparent', border: 'var(--sl-color-warning-500)', label: 'Dashed — has deeper tags (double-click to expand)' },
        ],
        full: [
            { color: '#a78bfa', label: 'All tags — unfiltered' },
        ],
    };

    function updateLegend(mode) {
        if (!legendEl) return;
        const items = LEGEND_BY_MODE[mode] || [];
        legendEl.innerHTML = items.map(item => {
            const border = item.border ? ('2px dashed ' + item.border) : '1px solid rgba(0,0,0,0.1)';
            return '<span style="display:inline-flex;align-items:center;gap:6px;">'
                 + '<span style="width:12px;height:12px;border-radius:50%;background:' + item.color + ';border:' + border + ';display:inline-block;"></span>'
                 + item.label
                 + '</span>';
        }).join('');
    }

    function nodeColor(d) {
        if (focusedNodeId && d.id === focusedNodeId) return '#ef4444';
        if (d.group === 'more-match') return '#86efac';
        if (d.group === 'more-neigh') return '#cbd5e1';
        if (d.group === 'base') return '#3b82f6';
        if (d.group === 'pulse') return '#f97316';
        if (d.group === 'match') return '#f97316';
        if (d.group === 'neighbor') return '#3b82f6';
        return '#a78bfa';
    }

    function visibleNeighborCount(nodeId) {
        let cnt = 0;
        for (const l of links) {
            const sid = typeof l.source === 'object' ? l.source.id : l.source;
            const tid = typeof l.target === 'object' ? l.target.id : l.target;
            if (sid === nodeId || tid === nodeId) cnt++;
        }
        return cnt;
    }

    function hiddenNeighbors(d) {
        if (!d.totalNeighbors) return 0;
        if (expandedTags.has(d.id)) return 0;
        return Math.max(0, d.totalNeighbors - visibleNeighborCount(d.id));
    }

    function isTruncated(d) {
        if (d.synthetic) return false;
        if (expandedTags.has(d.id)) return false;
        if (d.totalNeighbors === -1) return true;   // revealed node, unknown count — allow drilling
        return (d.totalNeighbors || 0) - visibleNeighborCount(d.id) > 0;
    }

    function applyTruncationClass() {
        if (!node) return;
        node.classed('truncated', d => isTruncated(d));
    }

    function setPlaceholder(html) {
        if (!graphPlaceholder) return;
        graphPlaceholder.style.display = 'flex';
        graphPlaceholder.innerHTML = html;
    }
    function placeholderLoading() {
        setPlaceholder('<div style="text-align:center;"><sl-spinner style="font-size:3rem;display:block;margin-bottom:8px;"></sl-spinner><p style="color:var(--sl-color-neutral-400);">Loading graph...</p></div>');
    }
    function placeholderEmpty() {
        setPlaceholder('<div style="text-align:center;"><sl-icon name="diagram-3" style="font-size:3rem;display:block;margin-bottom:8px;"></sl-icon><p>No connections found.</p><p style="font-size:0.85rem;">Add tags to multiple articles to see links.</p></div>');
    }
    function placeholderError(msg) {
        setPlaceholder('<div style="text-align:center;color:var(--sl-color-danger-600);"><sl-icon name="exclamation-triangle" style="font-size:3rem;display:block;margin-bottom:8px;"></sl-icon><p>Failed to load graph data.</p><p style="font-size:0.85rem;">' + (msg || '') + '</p></div>');
    }
    function hidePlaceholder() { if (graphPlaceholder) graphPlaceholder.style.display = 'none'; }

    function destroyScene() {
        if (simulation) { simulation.stop(); simulation = null; }
        if (cy) { cy.destroy(); cy = null; }
        d3.select('#graph-svg').selectAll('*').remove();
        var svgEl = document.getElementById('graph-svg');
        var cyEl = document.getElementById('graph-cy');
        if (svgEl) svgEl.style.display = 'block';
        if (cyEl) cyEl.style.display = 'none';
        svg = null; g = null; zoom = null;
        node = null; link = null; labels = null;
        nodes = []; links = [];
        expandedTags.clear();
        if (conceptPanel) conceptPanel.hidden = true;
    }

    function updateModeButtons() {
        btnModeHome?.setAttribute('variant', currentMode === 'home' ? 'primary' : 'default');
        btnModeFull?.setAttribute('variant', currentMode === 'full' ? 'primary' : 'default');
        updateLegend(currentMode);
    }

    async function loadMode(mode, queryOverride) {
        destroyScene();
        currentMode = mode;
        focusedNodeId = null;
        updateModeButtons();
        placeholderLoading();
        try {
            let payload;
            if (mode === 'home') {
                const r = await fetch('/api-proxy/concept-tags/graph/home');
                if (!r.ok) throw new Error('HTTP ' + r.status);
                payload = await r.json();
            } else if (mode === 'search') {
                const q = (queryOverride != null ? queryOverride : (filterInput.value || '')).trim();
                if (!q) {
                    placeholderEmpty();
                    filterInput?.focus();
                    return;
                }
                if (filterInput) filterInput.value = q;
                const depth = 1;
                const r = await fetch('/api-proxy/concept-tags/graph/search?q=' + encodeURIComponent(q) + '&depth=' + depth + '&maxNodes=200'
                    + (searchFolderScope && searchFolderScope !== '/' ? '&treePath=' + encodeURIComponent(searchFolderScope) : ''));
                if (!r.ok) throw new Error('HTTP ' + r.status);
                payload = await r.json();
            } else {
                const [tagsData, graphData] = await Promise.all([
                    fetch('/api-proxy/concept-tags?limit=500').then(r => { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); }),
                    fetch('/api-proxy/concept-tags/graph').then(r => { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
                ]);
                payload = {
                    nodes: tagsData.map(t => ({ name: t.name, articleCount: t.articleCount, group: 'neutral' })),
                    edges: graphData.map(e => ({ source: e.source, target: e.target, weight: e.weight }))
                };
            }
            nodes = payload.nodes.map(n => ({
                id: n.name,
                name: n.name,
                count: n.articleCount || 0,
                group: n.group || 'neutral',
                totalNeighbors: n.totalNeighbors || 0
            }));
            links = payload.edges.map(e => ({ source: e.source, target: e.target, weight: e.weight }));
            if (mode === 'search') {
                allSearchLinks = links.map(e => ({ source: e.source, target: e.target, weight: e.weight }));
                hiddenMatches = [];
                expandedTags.clear();

                const matchNodes = nodes.filter(n => n.group === 'match');
                const neighborNodes = nodes.filter(n => n.group === 'neighbor');
                matchNodes.sort((a, b) => (b.count - a.count) || (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
                const visibleMatches = matchNodes.slice(0, 10);
                hiddenMatches = matchNodes.slice(10);
                const matchIdSet = new Set(visibleMatches.map(m => m.id));

                // Build each visible match's neighbor list (id + weight) from the payload edges.
                const neighborById = new Map(neighborNodes.map(n => [n.id, n]));
                const perMatchNeighbors = new Map();
                for (const l of links) {
                    const sid = typeof l.source === 'object' ? l.source.id : l.source;
                    const tid = typeof l.target === 'object' ? l.target.id : l.target;
                    let mId = null, nId = null;
                    if (matchIdSet.has(sid) && neighborById.has(tid)) { mId = sid; nId = tid; }
                    else if (matchIdSet.has(tid) && neighborById.has(sid)) { mId = tid; nId = sid; }
                    if (mId == null) continue;
                    if (!perMatchNeighbors.has(mId)) perMatchNeighbors.set(mId, []);
                    perMatchNeighbors.get(mId).push({ id: nId, weight: l.weight || 1 });
                }
                // Show at most 10 neighbors per match (highest co-occurrence weight first).
                const shownNeighborIds = new Set();
                for (const m of visibleMatches) {
                    const list = (perMatchNeighbors.get(m.id) || []).sort((a, b) => b.weight - a.weight);
                    for (const nb of list.slice(0, 10)) shownNeighborIds.add(nb.id);
                }
                const visibleNodeIds = new Set([...matchIdSet, ...shownNeighborIds]);
                const finalNeighbors = neighborNodes.filter(n => shownNeighborIds.has(n.id));
                nodes = visibleMatches.concat(finalNeighbors);
                links = links.filter(l => {
                    const sid = typeof l.source === 'object' ? l.source.id : l.source;
                    const tid = typeof l.target === 'object' ? l.target.id : l.target;
                    return visibleNodeIds.has(sid) && visibleNodeIds.has(tid);
                });
                // Matches are auto-expanded: their direct neighbors are on screen, and any
                // overflow beyond the shown 10 becomes their light-gray "+N" bucket (via sync).
                for (const m of visibleMatches) expandedTags.add(m.id);
                if (hiddenMatches.length > 0) {
                    nodes.push({ id: GREEN_MORE_ID, name: '+' + hiddenMatches.length, count: 1, group: 'more-match', synthetic: true, totalNeighbors: 0 });
                }
            }
            if (mode === 'full' || mode === 'home' || mode === 'search') {
                allConcepts = nodes.filter(n => !n.synthetic).map(n => ({ name: n.name, count: n.count }));
                renderConceptTable();
            }
            if (nodes.length === 0) {
                placeholderEmpty();
            } else {
                hidePlaceholder();
                if (mode === 'full') {
                    initFullGraph(payload);
                } else {
                    initGraph();
                }
            }
        } catch (err) {
            placeholderError(err.message || 'Load failed');
            console.error('Graph load error:', err);
        }
    }

    function nodeClickHandler(event, d) {
        event.stopPropagation();
        // Aggregators do nothing on a single click — they expand on double-click only.
        if (d.synthetic) return;
        if (singleClickTimer) clearTimeout(singleClickTimer);
        singleClickTimer = setTimeout(() => {
            singleClickTimer = null;
            focusedNodeId = d.id;
            if (node) node.selectAll('circle').attr('fill', nodeColor);
            showConceptDetails(d);
        }, 250);
    }
    function nodeDblclickHandler(event, d) {
        event.stopPropagation();
        if (singleClickTimer) { clearTimeout(singleClickTimer); singleClickTimer = null; }
        if (d.synthetic) {
            if (d.group === 'more-match') revealMoreMatches();
            else if (d.group === 'more-neigh') revealMoreNeighbors(d.parentId);
            return;
        }
        expandNeighbors(d.id);
    }

    function clearConceptFocus() {
        if (focusedNodeId == null) return;
        focusedNodeId = null;
        if (node) node.selectAll('circle').attr('fill', nodeColor);
    }
    function nodeMouseoverHandler(event, d) {
        const connected = new Set([d.id]);
        link.classed('highlight', l => {
            if (l.source.id === d.id || l.target.id === d.id) {
                connected.add(l.source.id); connected.add(l.target.id);
                return true;
            }
            return false;
        });
        node.style('opacity', n => connected.has(n.id) ? 1 : 0.1);
        labels.style('opacity', n => connected.has(n.id) ? 1 : 0.1);
    }
    function nodeMouseoutHandler() {
        link.classed('highlight', false);
        node.style('opacity', 1);
        labels.style('opacity', 1);
    }

    function dragstarted(event, d) {
        d.fx = d.x; d.fy = d.y;
        d._dragging = false;
    }
    function dragged(event, d) {
        if (!d._dragging) {
            d._dragging = true;
            simulation.alphaTarget(0.3).restart();
        }
        d.fx = event.x; d.fy = event.y;
    }
    function dragended(event, d) {
        if (d._dragging) simulation.alphaTarget(0);
        d.fx = null; d.fy = null;
        d._dragging = false;
    }

    async function expandNeighbors(tagName) {
        if (expandedTags.has(tagName)) return;
        expandedTags.add(tagName);
        await revealMoreNeighbors(tagName);
    }

    function refreshGraphData() {
        if (!simulation || !svg) return;
        syncNeighborAggregators();
        link = g.select('.links').selectAll('line').data(links, d => {
            const sid = typeof d.source === 'object' ? d.source.id : d.source;
            const tid = typeof d.target === 'object' ? d.target.id : d.target;
            return sid < tid ? sid + '|' + tid : tid + '|' + sid;
        }).join(
            enter => enter.append('line').attr('class', 'link').attr('stroke-width', d => Math.sqrt(d.weight) + 1),
            update => update,
            exit => exit.remove()
        );

        node = g.select('.nodes').selectAll('g.node').data(nodes, d => d.id).join(
            enter => {
                const nEnter = enter.append('g').attr('class', 'node')
                    .call(d3.drag().on('start', dragstarted).on('drag', dragged).on('end', dragended))
                    .on('click', nodeClickHandler)
                    .on('dblclick', nodeDblclickHandler)
                    .on('mouseover', nodeMouseoverHandler)
                    .on('mouseout', nodeMouseoutHandler);
                nEnter.append('circle')
                    .attr('r', d => d.synthetic ? 16 : 8 + Math.sqrt(d.count) * 8)
                    .attr('fill', nodeColor);
                nEnter.append('text')
                    .attr('dy', '.35em')
                    .attr('x', d => d.synthetic ? 0 : 12 + Math.sqrt(d.count) * 8)
                    .attr('text-anchor', d => d.synthetic ? 'middle' : 'start')
                    .style('font-size', d => d.synthetic ? '11px' : Math.min(14, 10 + d.count) + 'px')
                    .text(d => d.name);
                return nEnter;
            },
            update => update,
            exit => exit.remove()
        );
        labels = node.select('text');

        simulation.nodes(nodes);
        simulation.force('link').links(links);
        simulation.alpha(0.5).restart();
        applyTruncationClass();
    }

    function linkExists(a, b) {
        for (const l of links) {
            const sid = typeof l.source === 'object' ? l.source.id : l.source;
            const tid = typeof l.target === 'object' ? l.target.id : l.target;
            if ((sid === a && tid === b) || (sid === b && tid === a)) return true;
        }
        return false;
    }

    function revealMoreMatches() {
        if (hiddenMatches.length === 0) return;
        const batch = hiddenMatches.splice(0, 10);
        nodes = nodes.filter(n => n.id !== GREEN_MORE_ID);
        for (const m of batch) { nodes.push(m); expandedTags.add(m.id); }
        const visibleIds = new Set(nodes.map(n => n.id));
        for (const l of allSearchLinks) {
            const sid = typeof l.source === 'object' ? l.source.id : l.source;
            const tid = typeof l.target === 'object' ? l.target.id : l.target;
            if (visibleIds.has(sid) && visibleIds.has(tid) && !linkExists(sid, tid)) {
                links.push({ source: sid, target: tid, weight: l.weight });
            }
        }
        if (hiddenMatches.length > 0) {
            nodes.push({ id: GREEN_MORE_ID, name: '+' + hiddenMatches.length, count: 1, group: 'more-match', synthetic: true, totalNeighbors: 0 });
        }
        refreshGraphData();
    }

    function syncNeighborAggregators() {
        if (currentMode !== 'search') return;
        const aggIds = new Set(nodes.filter(n => n.group === 'more-neigh').map(n => n.id));
        if (aggIds.size) {
            nodes = nodes.filter(n => !aggIds.has(n.id));
            links = links.filter(l => {
                const sid = typeof l.source === 'object' ? l.source.id : l.source;
                const tid = typeof l.target === 'object' ? l.target.id : l.target;
                return !aggIds.has(sid) && !aggIds.has(tid);
            });
        }
        const ids = new Set(nodes.map(x => x.id));
        for (const n of nodes.filter(x => !x.synthetic && expandedTags.has(x.id))) {
            let hidden;
            const full = expandedNeighborData.get(n.name);
            if (full) {
                let vis = 0;
                for (const nb of full) if (ids.has(nb.name)) vis++;
                hidden = full.length - vis;
            } else {
                hidden = Math.max(0, (n.totalNeighbors || 0) - visibleNeighborCount(n.id));
            }
            if (hidden > 0) {
                const aggId = NEIGH_MORE_PREFIX + n.id;
                nodes.push({ id: aggId, name: '+' + hidden, count: 0.5, group: 'more-neigh', synthetic: true, totalNeighbors: 0, parentId: n.id });
                links.push({ source: n.id, target: aggId, weight: 1 });
            }
        }
    }

    async function revealMoreNeighbors(parentId) {
        const parent = nodes.find(n => n.id === parentId);
        if (!parent) return;
        let full = expandedNeighborData.get(parent.name);
        if (!full) {
            try {
                const r = await fetch('/api-proxy/concept-tags/graph/neighbors?tag=' + encodeURIComponent(parent.name));
                if (!r.ok) return;
                const data = await r.json();
                const wByName = new Map();
                for (const e of (data.edges || [])) {
                    const other = e.source === parent.name ? e.target : (e.target === parent.name ? e.source : null);
                    if (!other) continue;
                    wByName.set(other, Math.max(wByName.get(other) || 0, e.weight || 1));
                }
                full = [...wByName.entries()].map(([name, weight]) => ({ name, weight })).sort((a, b) => b.weight - a.weight);
                expandedNeighborData.set(parent.name, full);
            } catch (err) { console.error('neighbor fetch failed', err); return; }
        }
        const existingIds = new Set(nodes.map(n => n.id));
        let added = 0;
        for (const nb of full) {
            if (added >= 10) break;
            if (existingIds.has(nb.name)) continue;
            nodes.push({ id: nb.name, name: nb.name, count: 1, group: 'neighbor', totalNeighbors: -1 });
            existingIds.add(nb.name);
            added++;
        }
        for (const nb of full) {
            if (existingIds.has(nb.name) && !linkExists(parent.name, nb.name)) {
                links.push({ source: parent.name, target: nb.name, weight: nb.weight });
            }
        }
        refreshGraphData();
    }

    function initFullGraph(payload) {
        var svgEl = document.getElementById('graph-svg');
        var cyEl = document.getElementById('graph-cy');
        if (svgEl) svgEl.style.display = 'none';
        if (cyEl) cyEl.style.display = 'block';

        const cs = getComputedStyle(document.documentElement);
        const neutral300 = cs.getPropertyValue('--sl-color-neutral-300').trim() || '#d4d4d8';
        const textPrimary = cs.getPropertyValue('--text-primary').trim() || cs.getPropertyValue('--sl-color-neutral-900').trim() || '#111';
        const bgPrimary = cs.getPropertyValue('--bg-primary').trim() || '#fff';

        const cyNodes = nodes.map(n => ({
            data: { id: n.id, name: n.name, count: n.count }
        }));
        const cyEdges = links.map(e => ({
            data: { source: typeof e.source === 'object' ? e.source.id : e.source,
                    target: typeof e.target === 'object' ? e.target.id : e.target,
                    weight: e.weight || 1 }
        }));

        cy = cytoscape({
            container: document.getElementById('graph-cy'),
            elements: { nodes: cyNodes, edges: cyEdges },
            wheelSensitivity: 0.2,
            style: [
                {
                    selector: 'node',
                    style: {
                        'background-color': ele => ele.data('id') === initialFocus ? '#ef4444' : '#a78bfa',
                        'width': ele => 16 + Math.sqrt(ele.data('count')) * 16,
                        'height': ele => 16 + Math.sqrt(ele.data('count')) * 16,
                        'border-color': '#ffffff',
                        'border-width': 2,
                        'border-opacity': 0.9,
                        'label': ele => (ele.data('count') > 5 || ele.scratch('_hover')) ? ele.data('name') : '',
                        'font-size': 12,
                        'color': textPrimary,
                        'text-outline-width': 2,
                        'text-outline-color': bgPrimary,
                        'text-valign': 'center',
                        'text-halign': 'center',
                        'min-zoomed-font-size': 4
                    }
                },
                {
                    selector: 'edge',
                    style: {
                        'width': ele => Math.sqrt(ele.data('weight')) + 1,
                        'line-color': neutral300,
                        'curve-style': 'bezier',
                        'opacity': 0.6
                    }
                }
            ],
            layout: {
                name: 'fcose',
                quality: 'default',
                randomize: true,
                animate: 'end',
                animationDuration: 600,
                fit: true,
                padding: 30,
                nodeRepulsion: 15000,
                idealEdgeLength: 120,
                edgeElasticity: 0.45,
                nestingFactor: 0.1,
                numIter: 2500
            }
        });

        cy.on('tap', 'node', function(evt) {
            const n = evt.target;
            const d = { id: n.id(), name: n.data('name'), count: n.data('count') };
            showConceptDetails(d);
        });

        cy.on('tap', function(evt) {
            if (evt.target === cy && conceptPanel) conceptPanel.hidden = true;
        });

        cy.on('mouseover', 'node', evt => {
            evt.target.scratch('_hover', true);
            evt.target.style('label', evt.target.data('name'));
        });
        cy.on('mouseout', 'node', evt => {
            evt.target.scratch('_hover', false);
            evt.target.style('label', evt.target.data('count') > 5 ? evt.target.data('name') : '');
        });

        if (initialFocus) {
            setTimeout(() => {
                const n = cy.getElementById(initialFocus);
                if (n && n.length) cy.animate({ center: { eles: n }, zoom: 1.5, duration: 500 });
            }, 150);
        }
    }

    function initGraph() {
        svg = d3.select('#graph-svg');
        width = graphContainer.clientWidth || 800;
        height = graphContainer.clientHeight || 500;

        zoom = d3.zoom()
            .scaleExtent([0.1, 5])
            .on('zoom', (event) => {
                g.attr('transform', event.transform);
                if (labels) labels.style('display', d => (event.transform.k > 0.8 || d.count > 5) ? 'block' : 'none');
            });
        svg.call(zoom);

        g = svg.append('g');
        g.append('g').attr('class', 'links');
        g.append('g').attr('class', 'nodes');

        syncNeighborAggregators();

        simulation = d3.forceSimulation(nodes)
            .force('link', d3.forceLink(links).id(d => d.id).distance(100))
            .force('charge', d3.forceManyBody().strength(-300))
            .force('center', d3.forceCenter(width / 2, height / 2))
            .force('collision', d3.forceCollide().radius(d => 10 + Math.sqrt(d.count) * 8 + 5));

        link = g.select('.links').selectAll('line').data(links).join('line')
            .attr('class', 'link')
            .attr('stroke-width', d => Math.sqrt(d.weight) + 1);

        node = g.select('.nodes').selectAll('g.node').data(nodes, d => d.id).join('g')
            .attr('class', 'node')
            .call(d3.drag().on('start', dragstarted).on('drag', dragged).on('end', dragended))
            .on('click', nodeClickHandler)
            .on('dblclick', nodeDblclickHandler)
            .on('mouseover', nodeMouseoverHandler)
            .on('mouseout', nodeMouseoutHandler);

        node.append('circle')
            .attr('r', d => d.synthetic ? 16 : 8 + Math.sqrt(d.count) * 8)
            .attr('fill', nodeColor);

        labels = node.append('text')
            .attr('dy', '.35em')
            .attr('x', d => d.synthetic ? 0 : 12 + Math.sqrt(d.count) * 8)
            .attr('text-anchor', d => d.synthetic ? 'middle' : 'start')
            .style('font-size', d => d.synthetic ? '11px' : Math.min(14, 10 + d.count) + 'px')
            .text(d => d.name);

        simulation.on('tick', () => {
            link.attr('x1', d => d.source.x).attr('y1', d => d.source.y)
                .attr('x2', d => d.target.x).attr('y2', d => d.target.y);
            node.attr('transform', d => 'translate(' + d.x + ',' + d.y + ')');
        });

        applyTruncationClass();

        if (initialFocus) {
            setTimeout(() => centerOnNode(initialFocus), 100);
        } else {
            setTimeout(() => {
                try {
                    const bounds = g.node().getBBox();
                    const parent = svg.node().parentElement;
                    const fullWidth = parent.clientWidth, fullHeight = parent.clientHeight;
                    const bw = bounds.width, bh = bounds.height;
                    const midX = bounds.x + bw / 2, midY = bounds.y + bh / 2;
                    if (bw == 0 || bh == 0) return;
                    const sc = 0.85 / Math.max(bw / fullWidth, bh / fullHeight);
                    svg.transition().duration(750).call(
                        zoom.transform,
                        d3.zoomIdentity.translate(fullWidth/2, fullHeight/2).scale(sc).translate(-midX, -midY)
                    );
                } catch(e) { console.warn('Initial fit failed:', e); }
            }, 100);
        }

        svg.on('click', function(event) {
            if (event.target.closest('.node')) return;
            if (conceptPanel) conceptPanel.hidden = true;
        });
    }

    function centerOnNode(nodeId, scale = 1.5) {
        if (cy) {
            const n = cy.getElementById(nodeId);
            if (n.length) {
                cy.animate({
                    center: { eles: n },
                    zoom: scale,
                    duration: 750
                });
            }
            return;
        }
        if (!svg || !zoom || !node) return;
        const d = nodes.find(n => n.id === nodeId);
        if (!d) return;
        const w = graphContainer.clientWidth;
        const h = graphContainer.clientHeight;
        svg.transition().duration(750).call(
            zoom.transform,
            d3.zoomIdentity.translate(w / 2, h / 2).scale(scale).translate(-d.x, -d.y)
        );
    }

    // ─── Global controls (persistent across scene rebuilds) ──────────────────
    document.getElementById('btn-zoom-in')?.addEventListener('click', () => {
        if (cy) cy.zoom(cy.zoom() * 1.3);
        else if (svg && zoom) svg.transition().call(zoom.scaleBy, 1.3);
    });
    document.getElementById('btn-zoom-out')?.addEventListener('click', () => {
        if (cy) cy.zoom(cy.zoom() * 0.7);
        else if (svg && zoom) svg.transition().call(zoom.scaleBy, 0.7);
    });
    document.getElementById('btn-zoom-reset')?.addEventListener('click', () => {
        if (cy) cy.fit();
        else if (svg && zoom) {
            svg.transition().duration(750).call(zoom.transform, d3.zoomIdentity);
            if (node) node.selectAll('circle').attr('fill', nodeColor);
        }
    });

    document.getElementById('btn-fullscreen')?.addEventListener('click', () => {
        const el = document.getElementById('graph-fullscreen-wrap');
        if (document.fullscreenElement) document.exitFullscreen();
        else el?.requestFullscreen().catch(err => alert('Fullscreen error: ' + err.message));
    });
    document.addEventListener('fullscreenchange', () => {
        const btn = document.getElementById('btn-fullscreen');
        if (btn) {
            btn.innerHTML = document.fullscreenElement
                ? '<sl-icon slot="prefix" name="fullscreen-exit"></sl-icon> Exit Fullscreen'
                : '<sl-icon slot="prefix" name="fullscreen"></sl-icon> Fullscreen';
        }
        setTimeout(() => {
            if (cy) { cy.resize(); cy.fit(); }
            if (!simulation) return;
            width = graphContainer.clientWidth;
            height = graphContainer.clientHeight;
            simulation.force('center', d3.forceCenter(width / 2, height / 2));
            simulation.alpha(0.3).restart();
        }, 100);
    });

    window.addEventListener('resize', () => {
        if (cy) { cy.resize(); cy.fit(); }
        if (!simulation) return;
        width = graphContainer.clientWidth;
        height = graphContainer.clientHeight;
        simulation.force('center', d3.forceCenter(width / 2, height / 2));
        simulation.alpha(0.3).restart();
    });

    btnModeHome?.addEventListener('click', () => loadMode('home'));
    btnModeFull?.addEventListener('click', () => {
        const dlg = document.getElementById('dlg-full-confirm');
        dlg?.show();
    });

    document.getElementById('btn-full-confirm')?.addEventListener('click', () => {
        document.getElementById('dlg-full-confirm')?.hide();
        loadMode('full');
    });

    filterInput?.addEventListener('keydown', e => {
        if (e.key === 'Enter') {
            e.preventDefault();
            if ((filterInput.value || '').trim()) loadMode('search');
        }
    });

    // ─── Folder-scope picker (restricts the graph search to a folder subtree) ──
    const folderScopeLink = document.getElementById('folder-scope-link');
    const folderScopePickerEl = document.getElementById('folder-scope-picker');
    const folderScopeClearBtn = document.getElementById('folder-scope-clear');
    const folderScopeApplyTip = document.getElementById('folder-scope-apply-tip');
    const folderScopeApplyBtn = document.getElementById('folder-scope-apply');
    let folderPickerApi = null;

    function setFolderScope(path) {
        searchFolderScope = (path && path !== '/') ? path : null;
    }

    function openFolderPicker() {
        if (typeof window.bmbFolderPicker !== 'function') return;
        if (!folderPickerApi) {
            folderPickerApi = window.bmbFolderPicker({
                rootEl: folderScopePickerEl,
                initialPath: searchFolderScope || '/',
                onChange: function(p) {
                    setFolderScope(p);
                }
            });
        } else {
            folderPickerApi.setPath(searchFolderScope || '/');
        }
        if (folderScopeLink) folderScopeLink.style.display = 'none';
        if (folderScopePickerEl) folderScopePickerEl.hidden = false;
        if (folderScopeClearBtn) folderScopeClearBtn.style.display = 'inline-flex';
        if (folderScopeApplyTip) folderScopeApplyTip.style.display = 'inline-flex';
        folderPickerApi.focus();
    }

    function closeFolderPicker() {
        if (folderScopePickerEl) folderScopePickerEl.hidden = true;
        if (folderScopeClearBtn) folderScopeClearBtn.style.display = 'none';
        if (folderScopeApplyTip) folderScopeApplyTip.style.display = 'none';
        if (folderScopeLink) folderScopeLink.style.display = 'inline-flex';
    }

    folderScopeApplyBtn?.addEventListener('click', function() {
        if ((filterInput.value || '').trim()) loadMode('search');
    });

    folderScopeLink?.addEventListener('click', function(e) {
        e.preventDefault();
        openFolderPicker();
    });

    folderScopeClearBtn?.addEventListener('click', function() {
        if (searchFolderScope != null) {
            setFolderScope(null);
            if (folderPickerApi) folderPickerApi.setPath('/');
        } else {
            closeFolderPicker();
        }
    });

    // ─── Detail Panel ───────────────────────────────────────────────────────
    const conceptPanel = document.getElementById('concept-panel');
    if (conceptPanel) {
        new MutationObserver(() => { if (conceptPanel.hidden) clearConceptFocus(); })
            .observe(conceptPanel, { attributes: true, attributeFilter: ['hidden'] });
    }
    const panelName = document.getElementById('panel-concept-name');
    const panelMeta = document.getElementById('panel-concept-meta');
    const panelList = document.getElementById('panel-articles-list');
    const previewPanel = document.getElementById('article-preview-panel');
    const previewFrame = document.getElementById('article-preview-frame');
    const fullWrap = document.getElementById('graph-fullscreen-wrap');

    function showConceptDetails(d) {
        if (!d) return;
        if (panelName) panelName.textContent = d.name;
        const hidden = hiddenNeighbors(d);
        const articlesMsg = `${d.count} article${d.count !== 1 ? 's' : ''} with this tag.`;
        const neighborsMsg = hidden > 0
            ? ` Has ${hidden} more neighbor${hidden !== 1 ? 's' : ''} not shown — double-click to expand.`
            : '';
        if (panelMeta) panelMeta.textContent = articlesMsg + neighborsMsg;
        if (panelList) panelList.innerHTML = '<div style="display:flex;justify-content:center;padding:20px;"><sl-spinner style="font-size:2rem;"></sl-spinner></div>';
        if (conceptPanel) conceptPanel.hidden = false;

        fetch(`/api-proxy/concept-tags/${encodeURIComponent(d.name)}/articles`)
            .then(r => {
                if (!r.ok) throw new Error(`HTTP ${r.status}`);
                return r.json();
            })
            .then(data => {
                const articles = data.articles || [];
                if (articles.length === 0) {
                    if (panelList) panelList.innerHTML = '<p class="text-muted" style="text-align:center;padding:20px;">No articles found for this tag.</p>';
                    return;
                }
                if (panelList) {
                    panelList.innerHTML = articles.map(a => `
                        <div class="concept-article-row" style="display:flex;align-items:flex-start;gap:6px;padding:10px 0;border-bottom:1px solid var(--sl-color-neutral-100);">
                            <sl-icon-button name="arrow-left-circle" label="Preview" class="btn-preview-article" data-id="${a.id}" title="Preview on the left" style="flex-shrink:0;"></sl-icon-button>
                            <div style="flex:1;min-width:0;">
                                <a href="/Article/View?id=${a.id}" style="font-weight:500;display:block;margin-bottom:4px;">${escapeHtml(a.title)}</a>
                                <small class="text-muted" style="font-family:monospace;word-break:break-all;">${escapeHtml(a.treePath)}</small>
                            </div>
                        </div>
                    `).join('');
                }
            })
            .catch(err => {
                console.error('Fetch error:', err);
                if (panelList) panelList.innerHTML = `<p class="text-danger" style="text-align:center;padding:20px;">Failed to load articles: ${err.message}</p>`;
            });
    }

    panelList?.addEventListener('click', function(e) {
        var btn = e.target.closest('.btn-preview-article');
        if (!btn) return;
        e.preventDefault();
        var id = btn.dataset.id;
        if (previewFrame) previewFrame.src = '/Article/Preview?id=' + id;
        if (previewPanel) previewPanel.hidden = false;
    });

    fullWrap?.addEventListener('click', function(e) {
        var closer = e.target.closest('.bee-panel-close');
        if (!closer) return;
        var target = document.getElementById(closer.dataset.target);
        if (target) target.hidden = true;
        if (closer.dataset.target === 'article-preview-panel' && previewFrame) previewFrame.src = 'about:blank';
    });

    document.addEventListener('keydown', function(e) {
        if (e.key !== 'Escape') return;
        if (previewPanel && !previewPanel.hidden) {
            e.preventDefault(); e.stopPropagation();
            previewPanel.hidden = true;
            if (previewFrame) previewFrame.src = 'about:blank';
            return;
        }
        if (conceptPanel && !conceptPanel.hidden) {
            e.preventDefault(); e.stopPropagation();
            conceptPanel.hidden = true;
            return;
        }
    }, true);

    function escapeHtml(text) {
        return String(text == null ? '' : text)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    if (initialFocus) loadMode('search', initialFocus);
    else loadMode('home');
})();
