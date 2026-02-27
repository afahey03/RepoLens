import * as vscode from 'vscode';
import type { ArchitectureResponse } from './types';
import { getWebviewContent } from './webviewUtil';

/**
 * Interactive architecture graph with filtering, zoom/pan, search, and
 * smart node limiting so it stays usable even for large repositories.
 */
export class ArchitecturePanel {
    private static current: ArchitecturePanel | undefined;
    private readonly panel: vscode.WebviewPanel;
    private disposed = false;

    private constructor(panel: vscode.WebviewPanel) {
        this.panel = panel;
        panel.onDidDispose(() => {
            this.disposed = true;
            ArchitecturePanel.current = undefined;
        });
    }

    static show(data: ArchitectureResponse): void {
        if (ArchitecturePanel.current && !ArchitecturePanel.current.disposed) {
            ArchitecturePanel.current.panel.reveal();
            ArchitecturePanel.current.render(data);
            return;
        }

        const panel = vscode.window.createWebviewPanel(
            'repolens.architecture',
            'RepoLens: Architecture',
            vscode.ViewColumn.One,
            { enableScripts: true, retainContextWhenHidden: true }
        );

        ArchitecturePanel.current = new ArchitecturePanel(panel);
        ArchitecturePanel.current.render(data);
    }

    private render(data: ArchitectureResponse): void {
        const nodesJson = JSON.stringify(data.nodes.map((n) => ({
            id: n.id, label: n.name, type: n.type, filePath: n.filePath ?? '',
        })));
        const edgesJson = JSON.stringify(data.edges.map((e) => ({
            source: e.source, target: e.target, label: e.relationship,
        })));

        const body = `
        <h1>Architecture Graph</h1>

        <!-- Toolbar -->
        <div id="toolbar" style="display:flex;flex-wrap:wrap;gap:8px;align-items:center;margin-bottom:8px;font-size:0.85em;">
            <span class="muted">${data.nodes.length} nodes &middot; ${data.edges.length} edges</span>
            <input id="searchBox" type="text" placeholder="Search nodes…"
                style="padding:3px 8px;border:1px solid var(--border);border-radius:4px;background:var(--input-bg);color:var(--input-fg);font-size:0.9em;width:180px;" />
            <span class="muted">Nodes:</span>
            <span id="nodeFilters"></span>
            <span class="muted" style="margin-left:4px;">Edges:</span>
            <span id="edgeFilters"></span>
            <span class="muted" style="margin-left:4px;">Limit:</span>
            <select id="limitSelect" style="padding:2px 4px;border:1px solid var(--border);border-radius:4px;background:var(--input-bg);color:var(--input-fg);font-size:0.85em;">
                <option value="50">50</option>
                <option value="100" selected>100</option>
                <option value="200">200</option>
                <option value="500">500</option>
                <option value="0">All</option>
            </select>
            <button id="resetBtn" title="Reset layout">Reset</button>
        </div>
        <div id="info" style="font-size:0.8em;color:var(--vscode-descriptionForeground);margin-bottom:4px;"></div>

        <!-- Graph container with overflow hidden for zoom/pan -->
        <div id="graphContainer" style="position:relative;border:1px solid var(--border);border-radius:6px;overflow:hidden;height:600px;cursor:grab;">
            <svg id="graph" style="position:absolute;top:0;left:0;"></svg>
        </div>

        <!-- Tooltip -->
        <div id="tooltip" style="display:none;position:fixed;padding:6px 10px;background:var(--vscode-editorHoverWidget-background,#252526);border:1px solid var(--vscode-editorHoverWidget-border,#454545);border-radius:4px;font-size:0.8em;z-index:100;pointer-events:none;max-width:300px;white-space:pre-wrap;"></div>
        `;

        const script = `
(function() {
    const vscode = acquireVsCodeApi();
    const ALL_NODES = ${nodesJson};
    const ALL_EDGES = ${edgesJson};

    const TYPE_COLORS = {
        Class: '#178600', Interface: '#3178c6', Method: '#f1e05a', Function: '#f1e05a',
        Namespace: '#00ADD8', Module: '#A97BFF', File: '#888', Property: '#dea584',
        Variable: '#701516', Enum: '#c22d40', Import: '#555', Folder: '#c9510c', Default: '#888'
    };

    // ── State ──
    const hiddenNodeTypes = new Set(['Folder']);
    const hiddenEdgeTypes = new Set(['Contains']);
    let nodeLimit = 100;
    let searchQuery = '';

    // ── Build filters ──
    const nodeTypes = [...new Set(ALL_NODES.map(n => n.type))].sort();
    const edgeTypes = [...new Set(ALL_EDGES.map(e => e.label))].sort();

    function renderFilterChips(types, hiddenSet, containerId, onToggle) {
        const container = document.getElementById(containerId);
        container.innerHTML = '';
        types.forEach(t => {
            const btn = document.createElement('button');
            btn.textContent = t;
            btn.style.cssText = 'padding:1px 6px;border-radius:10px;font-size:0.8em;margin:0 2px;cursor:pointer;border:1px solid var(--border);';
            const update = () => {
                if (hiddenSet.has(t)) {
                    btn.style.background = 'transparent';
                    btn.style.color = 'var(--vscode-disabledForeground,#888)';
                    btn.style.textDecoration = 'line-through';
                } else {
                    btn.style.background = 'var(--badge)';
                    btn.style.color = 'var(--badge-fg)';
                    btn.style.textDecoration = 'none';
                }
            };
            update();
            btn.addEventListener('click', () => {
                if (hiddenSet.has(t)) hiddenSet.delete(t); else hiddenSet.add(t);
                update();
                onToggle();
            });
            container.appendChild(btn);
        });
    }

    // ── Compute visible graph ──
    function computeVisible() {
        // Filter by type
        let nodes = ALL_NODES.filter(n => !hiddenNodeTypes.has(n.type));

        // Filter by search
        if (searchQuery) {
            const q = searchQuery.toLowerCase();
            nodes = nodes.filter(n => n.label.toLowerCase().includes(q) || (n.filePath && n.filePath.toLowerCase().includes(q)));
        }

        // Score by connectivity for smart limiting
        const connCount = {};
        ALL_EDGES.forEach(e => {
            connCount[e.source] = (connCount[e.source] || 0) + 1;
            connCount[e.target] = (connCount[e.target] || 0) + 1;
        });
        nodes.sort((a, b) => (connCount[b.id] || 0) - (connCount[a.id] || 0));

        const total = nodes.length;
        if (nodeLimit > 0 && nodes.length > nodeLimit) {
            nodes = nodes.slice(0, nodeLimit);
        }

        const visibleIds = new Set(nodes.map(n => n.id));
        const edges = ALL_EDGES.filter(e =>
            !hiddenEdgeTypes.has(e.label) &&
            visibleIds.has(e.source) &&
            visibleIds.has(e.target)
        );

        // Info text
        const info = document.getElementById('info');
        if (nodeLimit > 0 && total > nodeLimit) {
            info.textContent = 'Showing top ' + nodes.length + ' of ' + total + ' nodes (by connectivity). Increase limit or search to find specific nodes.';
        } else {
            info.textContent = 'Showing ' + nodes.length + ' nodes, ' + edges.length + ' edges.';
        }

        return { nodes, edges };
    }

    // ── SVG setup ──
    const container = document.getElementById('graphContainer');
    const svg = document.getElementById('graph');
    const tooltip = document.getElementById('tooltip');
    let cw = container.clientWidth || 900;
    let ch = container.clientHeight || 600;

    // Zoom/pan state
    let zoom = 1;
    let panX = 0, panY = 0;
    let isPanning = false, panStartX = 0, panStartY = 0, panStartPX = 0, panStartPY = 0;

    function updateSvgTransform() {
        svg.setAttribute('width', cw);
        svg.setAttribute('height', ch);
        svg.setAttribute('viewBox', (- panX / zoom) + ' ' + (- panY / zoom) + ' ' + (cw / zoom) + ' ' + (ch / zoom));
    }
    updateSvgTransform();

    // Zoom with wheel
    container.addEventListener('wheel', (e) => {
        e.preventDefault();
        const rect = container.getBoundingClientRect();
        const mx = e.clientX - rect.left;
        const my = e.clientY - rect.top;
        const oldZoom = zoom;
        zoom *= e.deltaY < 0 ? 1.15 : 0.87;
        zoom = Math.max(0.1, Math.min(5, zoom));
        // Adjust pan so zoom centers on mouse
        panX = mx - (mx - panX) * (zoom / oldZoom);
        panY = my - (my - panY) * (zoom / oldZoom);
        updateSvgTransform();
    }, { passive: false });

    // Pan with middle-click or background drag
    container.addEventListener('mousedown', (e) => {
        if (e.target === svg || e.target === container) {
            isPanning = true;
            panStartX = e.clientX; panStartY = e.clientY;
            panStartPX = panX; panStartPY = panY;
            container.style.cursor = 'grabbing';
            e.preventDefault();
        }
    });
    document.addEventListener('mousemove', (e) => {
        if (isPanning) {
            panX = panStartPX + (e.clientX - panStartX);
            panY = panStartPY + (e.clientY - panStartY);
            updateSvgTransform();
        }
    });
    document.addEventListener('mouseup', () => {
        if (isPanning) { isPanning = false; container.style.cursor = 'grab'; }
    });

    // ── Rendering ──
    let simNodes = [];
    let simLinks = [];
    let lineEls = [];
    let nodeEls = [];
    let simRunning = false;
    let simFrames = 0;

    function rebuildGraph() {
        const { nodes, edges } = computeVisible();

        // Clear SVG
        svg.innerHTML = '';

        // Defs
        const defs = document.createElementNS('http://www.w3.org/2000/svg', 'defs');
        defs.innerHTML = '<marker id="arrow" viewBox="0 0 10 10" refX="22" refY="5" markerWidth="5" markerHeight="5" orient="auto"><path d="M0,0 L10,5 L0,10 Z" fill="#666"/></marker>';
        svg.appendChild(defs);

        // Map and init positions
        const nodeMap = {};
        const vw = cw / zoom;
        const vh = ch / zoom;
        simNodes = nodes.map(n => {
            const sn = {
                ...n,
                x: vw / 2 + (Math.random() - 0.5) * vw * 0.7,
                y: vh / 2 + (Math.random() - 0.5) * vh * 0.7,
                vx: 0, vy: 0
            };
            nodeMap[n.id] = sn;
            return sn;
        });

        simLinks = edges.filter(e => nodeMap[e.source] && nodeMap[e.target])
            .map(e => ({ source: nodeMap[e.source], target: nodeMap[e.target], label: e.label }));

        // Edge elements
        const edgeGroup = document.createElementNS('http://www.w3.org/2000/svg', 'g');
        svg.appendChild(edgeGroup);
        lineEls = simLinks.map(l => {
            const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
            line.setAttribute('stroke', '#555');
            line.setAttribute('stroke-width', '0.8');
            line.setAttribute('marker-end', 'url(#arrow)');
            edgeGroup.appendChild(line);
            return { el: line, link: l };
        });

        // Node elements
        const nodeGroup = document.createElementNS('http://www.w3.org/2000/svg', 'g');
        svg.appendChild(nodeGroup);
        nodeEls = simNodes.map(n => {
            const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
            g.style.cursor = 'pointer';

            const r = Math.min(14, Math.max(6, 4 + (simLinks.filter(l => l.source === n || l.target === n).length) * 1.5));
            const circle = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
            circle.setAttribute('r', String(r));
            circle.setAttribute('fill', TYPE_COLORS[n.type] || TYPE_COLORS.Default);
            circle.setAttribute('stroke', '#fff');
            circle.setAttribute('stroke-width', '1');
            circle.setAttribute('opacity', '0.9');
            g.appendChild(circle);

            const text = document.createElementNS('http://www.w3.org/2000/svg', 'text');
            text.setAttribute('dx', String(r + 3));
            text.setAttribute('dy', '3');
            text.setAttribute('fill', 'var(--vscode-editor-foreground)');
            text.setAttribute('font-size', simNodes.length > 150 ? '8' : simNodes.length > 50 ? '10' : '11');
            text.textContent = n.label.length > 30 ? n.label.slice(0, 28) + '…' : n.label;
            // Hide labels when there are many nodes to reduce clutter
            if (simNodes.length > 200) text.setAttribute('display', 'none');
            g.appendChild(text);
            n._textEl = text;
            n._r = r;

            nodeGroup.appendChild(g);

            // Drag
            let dragging = false;
            g.addEventListener('mousedown', (e) => { dragging = true; e.stopPropagation(); e.preventDefault(); });
            document.addEventListener('mousemove', (e) => {
                if (!dragging) return;
                const rect = container.getBoundingClientRect();
                n.x = (e.clientX - rect.left - panX) / zoom;
                n.y = (e.clientY - rect.top - panY) / zoom;
                n.vx = 0; n.vy = 0;
            });
            document.addEventListener('mouseup', () => { dragging = false; });

            // Tooltip on hover
            g.addEventListener('mouseenter', (e) => {
                const conns = simLinks.filter(l => l.source === n || l.target === n);
                const incoming = conns.filter(l => l.target === n).map(l => l.source.label);
                const outgoing = conns.filter(l => l.source === n).map(l => l.target.label);
                let tip = n.label + ' (' + n.type + ')';
                if (n.filePath) tip += '\\n' + n.filePath;
                if (incoming.length) tip += '\\n← ' + incoming.slice(0, 5).join(', ') + (incoming.length > 5 ? ' +' + (incoming.length - 5) + ' more' : '');
                if (outgoing.length) tip += '\\n→ ' + outgoing.slice(0, 5).join(', ') + (outgoing.length > 5 ? ' +' + (outgoing.length - 5) + ' more' : '');
                tooltip.textContent = tip;
                tooltip.style.display = 'block';
                tooltip.style.left = e.clientX + 12 + 'px';
                tooltip.style.top = e.clientY + 12 + 'px';
                // Highlight connected nodes
                nodeEls.forEach(ne => ne.el.setAttribute('opacity', '0.25'));
                g.setAttribute('opacity', '1');
                conns.forEach(l => {
                    const other = l.source === n ? l.target : l.source;
                    const otherEl = nodeEls.find(ne => ne.node === other);
                    if (otherEl) otherEl.el.setAttribute('opacity', '1');
                });
                // Show labels on hover for dense graphs
                if (simNodes.length > 200) {
                    n._textEl.setAttribute('display', '');
                    conns.forEach(l => {
                        const other = l.source === n ? l.target : l.source;
                        if (other._textEl) other._textEl.setAttribute('display', '');
                    });
                }
            });
            g.addEventListener('mouseleave', () => {
                tooltip.style.display = 'none';
                nodeEls.forEach(ne => ne.el.setAttribute('opacity', '1'));
                if (simNodes.length > 200) {
                    simNodes.forEach(sn => { if (sn._textEl) sn._textEl.setAttribute('display', 'none'); });
                }
            });
            g.addEventListener('mousemove', (e) => {
                tooltip.style.left = e.clientX + 12 + 'px';
                tooltip.style.top = e.clientY + 12 + 'px';
            });

            // Double-click → navigate
            g.addEventListener('dblclick', () => {
                if (n.filePath) {
                    vscode.postMessage({ command: 'navigate', filePath: n.filePath, line: n.line || 1 });
                }
            });

            return { el: g, node: n };
        });

        // Start simulation
        simFrames = 0;
        if (!simRunning) { simRunning = true; tick(); }
    }

    // ── Force simulation (capped iterations) ──
    const MAX_SIM_FRAMES = 300;

    function tick() {
        if (simFrames > MAX_SIM_FRAMES) { simRunning = false; return; }
        simFrames++;

        const N = simNodes.length;
        // Adapt forces based on graph size
        const repulse = N > 200 ? 3000 : N > 80 ? 5000 : 6000;
        const idealDist = N > 200 ? 60 : N > 80 ? 80 : 100;
        const vw = cw / zoom, vh = ch / zoom;

        // Repulsion (use grid for large graphs to avoid O(n²))
        if (N <= 300) {
            for (let i = 0; i < N; i++) {
                for (let j = i + 1; j < N; j++) {
                    let dx = simNodes[j].x - simNodes[i].x;
                    let dy = simNodes[j].y - simNodes[i].y;
                    let d2 = dx * dx + dy * dy || 1;
                    if (d2 > 250000) continue; // skip very far nodes
                    let f = repulse / d2;
                    simNodes[i].vx -= dx * f;
                    simNodes[i].vy -= dy * f;
                    simNodes[j].vx += dx * f;
                    simNodes[j].vy += dy * f;
                }
            }
        } else {
            // Barnes-Hut-lite: just use random sampling for huge graphs
            for (let i = 0; i < N; i++) {
                const samples = Math.min(40, N - 1);
                for (let s = 0; s < samples; s++) {
                    const j = Math.floor(Math.random() * N);
                    if (j === i) continue;
                    let dx = simNodes[j].x - simNodes[i].x;
                    let dy = simNodes[j].y - simNodes[i].y;
                    let d2 = dx * dx + dy * dy || 1;
                    let f = repulse * (N / samples) / d2;
                    simNodes[i].vx -= dx * f * 0.5;
                    simNodes[i].vy -= dy * f * 0.5;
                }
            }
        }

        // Attraction along edges
        simLinks.forEach(l => {
            let dx = l.target.x - l.source.x;
            let dy = l.target.y - l.source.y;
            let d = Math.sqrt(dx * dx + dy * dy) || 1;
            let f = (d - idealDist) * 0.004;
            l.source.vx += dx / d * f;
            l.source.vy += dy / d * f;
            l.target.vx -= dx / d * f;
            l.target.vy -= dy / d * f;
        });

        // Center gravity
        simNodes.forEach(n => {
            n.vx += (vw / 2 - n.x) * 0.0008;
            n.vy += (vh / 2 - n.y) * 0.0008;
        });

        // Integrate with damping
        const damping = simFrames < 50 ? 0.85 : 0.75;
        simNodes.forEach(n => {
            n.vx *= damping; n.vy *= damping;
            n.x += n.vx; n.y += n.vy;
        });

        // Update SVG positions
        nodeEls.forEach(ne => ne.el.setAttribute('transform', 'translate(' + ne.node.x.toFixed(1) + ',' + ne.node.y.toFixed(1) + ')'));
        lineEls.forEach(le => {
            le.el.setAttribute('x1', le.link.source.x.toFixed(1));
            le.el.setAttribute('y1', le.link.source.y.toFixed(1));
            le.el.setAttribute('x2', le.link.target.x.toFixed(1));
            le.el.setAttribute('y2', le.link.target.y.toFixed(1));
        });

        requestAnimationFrame(tick);
    }

    // ── Toolbar events ──
    renderFilterChips(nodeTypes, hiddenNodeTypes, 'nodeFilters', rebuildGraph);
    renderFilterChips(edgeTypes, hiddenEdgeTypes, 'edgeFilters', rebuildGraph);

    document.getElementById('searchBox').addEventListener('input', (e) => {
        searchQuery = e.target.value.trim();
        rebuildGraph();
    });

    document.getElementById('limitSelect').addEventListener('change', (e) => {
        nodeLimit = parseInt(e.target.value, 10);
        rebuildGraph();
    });

    document.getElementById('resetBtn').addEventListener('click', () => {
        zoom = 1; panX = 0; panY = 0;
        updateSvgTransform();
        rebuildGraph();
    });

    // ResizeObserver
    if (typeof ResizeObserver !== 'undefined') {
        new ResizeObserver(() => {
            cw = container.clientWidth || 900;
            ch = container.clientHeight || 600;
            updateSvgTransform();
        }).observe(container);
    }

    // Initial render
    rebuildGraph();

    window.addEventListener('message', () => {});
})();
        `;

        this.panel.webview.html = getWebviewContent(this.panel.webview, 'Architecture', body, script);

        // Handle navigate messages from webview
        this.panel.webview.onDidReceiveMessage((msg) => {
            if (msg.command === 'navigate' && msg.filePath) {
                vscode.commands.executeCommand('repolens.navigateToSymbol', msg.filePath, msg.line || 1, '');
            }
        });
    }
}
