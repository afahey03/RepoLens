import dagre from '@dagrejs/dagre';
import type { Node, Edge } from '@xyflow/react';

const NODE_WIDTH = 200;
const NODE_HEIGHT = 50;

export interface LayoutOptions {
    direction?: 'TB' | 'LR' | 'BT' | 'RL';
    nodeSpacing?: number;
    rankSpacing?: number;
}

/**
 * Applies dagre hierarchical layout to React Flow nodes/edges.
 * Returns repositioned nodes.
 */
export function layoutGraph(
    nodes: Node[],
    edges: Edge[],
    options: LayoutOptions = {},
): Node[] {
    const { direction = 'TB', nodeSpacing = 40, rankSpacing = 80 } = options;

    const g = new dagre.graphlib.Graph({ compound: false })
        .setDefaultEdgeLabel(() => ({}))
        .setGraph({
            rankdir: direction,
            nodesep: nodeSpacing,
            ranksep: rankSpacing,
            marginx: 20,
            marginy: 20,
        });

    for (const node of nodes) {
        g.setNode(node.id, { width: NODE_WIDTH, height: NODE_HEIGHT });
    }

    for (const edge of edges) {
        g.setEdge(edge.source, edge.target);
    }

    dagre.layout(g);

    return nodes.map((node) => {
        const pos = g.node(node.id);
        return {
            ...node,
            position: {
                x: pos.x - NODE_WIDTH / 2,
                y: pos.y - NODE_HEIGHT / 2,
            },
        };
    });
}
