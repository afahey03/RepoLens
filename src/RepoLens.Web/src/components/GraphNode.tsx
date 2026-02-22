import { Handle, Position, type NodeProps } from '@xyflow/react';

export interface GraphNodeData extends Record<string, unknown> {
    label: string;
    nodeType: string;
    filePath?: string;
    metadata?: Record<string, string>;
}

const typeConfig: Record<string, { icon: string; bg: string; border: string; accent: string }> = {
    Repository: { icon: '📦', bg: '#1a1a2e', border: '#6366f1', accent: '#818cf8' },
    Folder: { icon: '📁', bg: '#1c1917', border: '#78716c', accent: '#a8a29e' },
    File: { icon: '📄', bg: '#1e3a5f', border: '#3b82f6', accent: '#60a5fa' },
    Namespace: { icon: '🏷️', bg: '#3b3120', border: '#f59e0b', accent: '#fbbf24' },
    Class: { icon: '🔷', bg: '#3b1f5e', border: '#8b5cf6', accent: '#a78bfa' },
    Interface: { icon: '🔶', bg: '#312e81', border: '#6366f1', accent: '#818cf8' },
    Function: { icon: '⚡', bg: '#1a3c34', border: '#10b981', accent: '#34d399' },
    Module: { icon: '📦', bg: '#1e293b', border: '#0ea5e9', accent: '#38bdf8' },
};

const defaultConfig = { icon: '●', bg: '#27272a', border: '#52525b', accent: '#71717a' };

function GraphNodeComponent({ data }: NodeProps) {
    const nodeData = data as unknown as GraphNodeData;
    const cfg = typeConfig[nodeData.nodeType] ?? defaultConfig;

    return (
        <div
            className="graph-node"
            style={{
                background: cfg.bg,
                borderColor: cfg.border,
            }}
        >
            <Handle type="target" position={Position.Top} className="graph-handle" />
            <div className="graph-node-header">
                <span className="graph-node-icon">{cfg.icon}</span>
                <span className="graph-node-type" style={{ color: cfg.accent }}>
                    {nodeData.nodeType}
                </span>
            </div>
            <div className="graph-node-label" title={nodeData.label}>
                {nodeData.label}
            </div>
            {nodeData.filePath && (
                <div className="graph-node-path" title={nodeData.filePath}>
                    {truncatePath(nodeData.filePath)}
                </div>
            )}
            <Handle type="source" position={Position.Bottom} className="graph-handle" />
        </div>
    );
}

function truncatePath(path: string): string {
    if (path.length <= 35) return path;
    const parts = path.split('/');
    if (parts.length <= 2) return path;
    return `…/${parts.slice(-2).join('/')}`;
}

export default GraphNodeComponent;
