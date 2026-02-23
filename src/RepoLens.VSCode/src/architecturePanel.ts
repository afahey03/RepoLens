import * as vscode from 'vscode';
import type { ArchitectureResponse } from './types';
import { getWebviewContent } from './webviewUtil';

/**
 * Interactive architecture graph rendered as an SVG force-directed layout entirely
 * inside a VS Code webview.  Nodes are draggable; edges are drawn with arrowheads.
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
            id: n.id, label: n.name, type: n.type, filePath: n.filePath,
        })));
        const edgesJson = JSON.stringify(data.edges.map((e) => ({
            source: e.source, target: e.target, label: e.relationship,
        })));

        const body = `
        <h1>Architecture Graph</h1>
        <div style="margin-bottom:8px;">
            <span class="muted">${data.nodes.length} nodes &middot; ${data.edges.length} edges</span>
            &nbsp;
            <button id="resetBtn" title="Reset layout">Reset Layout</button>
        </div>
        <svg id="graph" width="100%" height="600" style="border:1px solid var(--border);border-radius:6px;"></svg>
        `;

        const script = `
(function() {
    const vscode = acquireVsCodeApi();
    const nodes = ${nodesJson};
    const edges = ${edgesJson};

    const TYPE_COLORS = {
        Class: '#178600', Interface: '#3178c6', Method: '#f1e05a', Function: '#f1e05a',
        Namespace: '#00ADD8', Module: '#A97BFF', File: '#888', Property: '#dea584',
        Variable: '#701516', Enum: '#c22d40', Import: '#555', Default: '#888'
    };

    const svg = document.getElementById('graph');
    const width = svg.clientWidth || 900;
    const height = 600;
    svg.setAttribute('viewBox', '0 0 ' + width + ' ' + height);

    // Build index
    const nodeMap = {};
    nodes.forEach((n, i) => {
        n.x = width / 2 + (Math.random() - 0.5) * width * 0.6;
        n.y = height / 2 + (Math.random() - 0.5) * height * 0.6;
        n.vx = 0; n.vy = 0;
        nodeMap[n.id] = n;
    });

    // Resolve edges
    const links = edges.filter(e => nodeMap[e.source] && nodeMap[e.target])
                       .map(e => ({ source: nodeMap[e.source], target: nodeMap[e.target], label: e.label }));

    // SVG defs for arrowheads
    const defs = document.createElementNS('http://www.w3.org/2000/svg', 'defs');
    defs.innerHTML = '<marker id="arrow" viewBox="0 0 10 10" refX="22" refY="5" markerWidth="6" markerHeight="6" orient="auto"><path d="M0,0 L10,5 L0,10 Z" fill="#888"/></marker>';
    svg.appendChild(defs);

    // Draw edges
    const edgeGroup = document.createElementNS('http://www.w3.org/2000/svg', 'g');
    svg.appendChild(edgeGroup);
    const lineEls = links.map(l => {
        const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
        line.setAttribute('stroke', '#555');
        line.setAttribute('stroke-width', '1');
        line.setAttribute('marker-end', 'url(#arrow)');
        edgeGroup.appendChild(line);
        return { el: line, link: l };
    });

    // Draw nodes
    const nodeGroup = document.createElementNS('http://www.w3.org/2000/svg', 'g');
    svg.appendChild(nodeGroup);
    const nodeEls = nodes.map(n => {
        const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
        g.style.cursor = 'pointer';

        const circle = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
        circle.setAttribute('r', '12');
        circle.setAttribute('fill', TYPE_COLORS[n.type] || TYPE_COLORS.Default);
        circle.setAttribute('stroke', '#fff');
        circle.setAttribute('stroke-width', '1.5');
        g.appendChild(circle);

        const text = document.createElementNS('http://www.w3.org/2000/svg', 'text');
        text.setAttribute('dx', '16');
        text.setAttribute('dy', '4');
        text.setAttribute('fill', 'var(--vscode-editor-foreground)');
        text.setAttribute('font-size', '11');
        text.textContent = n.label.length > 25 ? n.label.slice(0, 23) + '…' : n.label;
        g.appendChild(text);

        nodeGroup.appendChild(g);

        // Drag handling
        let dragging = false;
        g.addEventListener('mousedown', (e) => { dragging = true; e.preventDefault(); });
        document.addEventListener('mousemove', (e) => {
            if (!dragging) return;
            const rect = svg.getBoundingClientRect();
            n.x = (e.clientX - rect.left) / rect.width * width;
            n.y = (e.clientY - rect.top) / rect.height * height;
            n.vx = 0; n.vy = 0;
        });
        document.addEventListener('mouseup', () => { dragging = false; });

        // Double-click → navigate
        g.addEventListener('dblclick', () => {
            if (n.filePath) {
                vscode.postMessage({ command: 'navigate', filePath: n.filePath, line: n.line || 1 });
            }
        });

        return { el: g, node: n };
    });

    // Simple force simulation
    function tick() {
        // Repulsion
        for (let i = 0; i < nodes.length; i++) {
            for (let j = i + 1; j < nodes.length; j++) {
                let dx = nodes[j].x - nodes[i].x;
                let dy = nodes[j].y - nodes[i].y;
                let d2 = dx * dx + dy * dy || 1;
                let f = 6000 / d2;
                nodes[i].vx -= dx * f;
                nodes[i].vy -= dy * f;
                nodes[j].vx += dx * f;
                nodes[j].vy += dy * f;
            }
        }
        // Attraction along edges
        links.forEach(l => {
            let dx = l.target.x - l.source.x;
            let dy = l.target.y - l.source.y;
            let d = Math.sqrt(dx * dx + dy * dy) || 1;
            let f = (d - 100) * 0.005;
            l.source.vx += dx / d * f;
            l.source.vy += dy / d * f;
            l.target.vx -= dx / d * f;
            l.target.vy -= dy / d * f;
        });
        // Center gravity
        nodes.forEach(n => {
            n.vx += (width / 2 - n.x) * 0.001;
            n.vy += (height / 2 - n.y) * 0.001;
        });
        // Integrate
        nodes.forEach(n => {
            n.vx *= 0.9; n.vy *= 0.9;
            n.x += n.vx; n.y += n.vy;
            n.x = Math.max(20, Math.min(width - 20, n.x));
            n.y = Math.max(20, Math.min(height - 20, n.y));
        });
        // Update SVG
        nodeEls.forEach(ne => ne.el.setAttribute('transform', 'translate(' + ne.node.x + ',' + ne.node.y + ')'));
        lineEls.forEach(le => {
            le.el.setAttribute('x1', le.link.source.x);
            le.el.setAttribute('y1', le.link.source.y);
            le.el.setAttribute('x2', le.link.target.x);
            le.el.setAttribute('y2', le.link.target.y);
        });
        requestAnimationFrame(tick);
    }
    tick();

    // Reset button
    document.getElementById('resetBtn').addEventListener('click', () => {
        nodes.forEach(n => {
            n.x = width / 2 + (Math.random() - 0.5) * width * 0.6;
            n.y = height / 2 + (Math.random() - 0.5) * height * 0.6;
            n.vx = 0; n.vy = 0;
        });
    });

    // Handle messages from webview → extension
    window.addEventListener('message', (e) => {});
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
