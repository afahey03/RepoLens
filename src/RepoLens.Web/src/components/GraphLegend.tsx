const nodeTypes = [
    { label: 'File', icon: '📄', color: '#3b82f6' },
    { label: 'Module', icon: '📦', color: '#0ea5e9' },
    { label: 'Namespace', icon: '🏷️', color: '#f59e0b' },
    { label: 'Class', icon: '🔷', color: '#8b5cf6' },
    { label: 'Interface', icon: '🔶', color: '#6366f1' },
    { label: 'Function', icon: '⚡', color: '#10b981' },
    { label: 'Folder', icon: '📁', color: '#78716c' },
];

const edgeTypes = [
    { label: 'Imports', color: '#22d3ee', dashed: false },
    { label: 'Contains', color: '#71717a', dashed: true },
    { label: 'Inherits', color: '#f59e0b', dashed: false },
    { label: 'Implements', color: '#8b5cf6', dashed: false },
];

function GraphLegend() {
    return (
        <div className="graph-legend">
            <div className="legend-section">
                <span className="legend-title">Nodes</span>
                <div className="legend-items">
                    {nodeTypes.map((t) => (
                        <span key={t.label} className="legend-item">
                            <span
                                className="legend-dot"
                                style={{ background: t.color }}
                            />
                            {t.icon} {t.label}
                        </span>
                    ))}
                </div>
            </div>
            <div className="legend-section">
                <span className="legend-title">Edges</span>
                <div className="legend-items">
                    {edgeTypes.map((e) => (
                        <span key={e.label} className="legend-item">
                            <span
                                className="legend-line"
                                style={{
                                    background: e.color,
                                    borderStyle: e.dashed ? 'dashed' : 'solid',
                                }}
                            />
                            {e.label}
                        </span>
                    ))}
                </div>
            </div>
        </div>
    );
}

export default GraphLegend;
