import {
    ReactFlow,
    Background,
    Controls,
    type Node,
    type Edge,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import type { ArchitectureResponse } from '../types';

interface Props {
    architecture: ArchitectureResponse;
}

function ArchitectureGraph({ architecture }: Props) {
    // Convert backend nodes/edges to React Flow format
    const nodes: Node[] = architecture.nodes.map((n, i) => ({
        id: n.id,
        data: { label: n.name },
        position: { x: (i % 5) * 220, y: Math.floor(i / 5) * 100 },
        style: getNodeStyle(n.type),
    }));

    const edges: Edge[] = architecture.edges.map((e, i) => ({
        id: `e-${i}`,
        source: e.source,
        target: e.target,
        label: e.relationship,
        animated: e.relationship === 'Imports',
        style: { stroke: '#6366f1' },
    }));

    if (nodes.length === 0) {
        return (
            <div className="empty-state">
                <p>No architecture data available yet. Symbol extraction will populate this in Phase 3.</p>
            </div>
        );
    }

    return (
        <div className="architecture-graph">
            <ReactFlow nodes={nodes} edges={edges} fitView>
                <Background />
                <Controls />
            </ReactFlow>
        </div>
    );
}

function getNodeStyle(type: string): React.CSSProperties {
    const base: React.CSSProperties = {
        padding: '8px 16px',
        borderRadius: '8px',
        fontSize: '12px',
        color: '#e4e4e7',
    };

    switch (type) {
        case 'File':
            return { ...base, background: '#1e3a5f', border: '1px solid #3b82f6' };
        case 'Class':
            return { ...base, background: '#3b1f5e', border: '1px solid #8b5cf6' };
        case 'Function':
            return { ...base, background: '#1a3c34', border: '1px solid #10b981' };
        case 'Namespace':
            return { ...base, background: '#3b3120', border: '1px solid #f59e0b' };
        default:
            return { ...base, background: '#27272a', border: '1px solid #52525b' };
    }
}

export default ArchitectureGraph;
