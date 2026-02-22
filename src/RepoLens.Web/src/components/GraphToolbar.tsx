interface Props {
    nodeTypes: string[];
    edgeTypes: string[];
    hiddenNodeTypes: Set<string>;
    hiddenEdgeTypes: Set<string>;
    onToggleNodeType: (type: string) => void;
    onToggleEdgeType: (type: string) => void;
    direction: 'TB' | 'LR';
    onDirectionChange: (dir: 'TB' | 'LR') => void;
    nodeCount: number;
    edgeCount: number;
    onFitView: () => void;
}

function GraphToolbar({
    nodeTypes,
    edgeTypes,
    hiddenNodeTypes,
    hiddenEdgeTypes,
    onToggleNodeType,
    onToggleEdgeType,
    direction,
    onDirectionChange,
    nodeCount,
    edgeCount,
    onFitView,
}: Props) {
    return (
        <div className="graph-toolbar">
            <div className="toolbar-group">
                <span className="toolbar-label">Layout</span>
                <button
                    className={`toolbar-btn ${direction === 'TB' ? 'active' : ''}`}
                    onClick={() => onDirectionChange('TB')}
                    title="Top to Bottom"
                >
                    ↓ Vertical
                </button>
                <button
                    className={`toolbar-btn ${direction === 'LR' ? 'active' : ''}`}
                    onClick={() => onDirectionChange('LR')}
                    title="Left to Right"
                >
                    → Horizontal
                </button>
                <button className="toolbar-btn" onClick={onFitView} title="Fit to viewport">
                    ⊞ Fit
                </button>
            </div>

            <div className="toolbar-group">
                <span className="toolbar-label">Nodes</span>
                {nodeTypes.map((type) => (
                    <button
                        key={type}
                        className={`toolbar-filter ${hiddenNodeTypes.has(type) ? 'off' : 'on'}`}
                        onClick={() => onToggleNodeType(type)}
                    >
                        {type}
                    </button>
                ))}
            </div>

            <div className="toolbar-group">
                <span className="toolbar-label">Edges</span>
                {edgeTypes.map((type) => (
                    <button
                        key={type}
                        className={`toolbar-filter ${hiddenEdgeTypes.has(type) ? 'off' : 'on'}`}
                        onClick={() => onToggleEdgeType(type)}
                    >
                        {type}
                    </button>
                ))}
            </div>

            <div className="toolbar-stats">
                {nodeCount} nodes · {edgeCount} edges
            </div>
        </div>
    );
}

export default GraphToolbar;
