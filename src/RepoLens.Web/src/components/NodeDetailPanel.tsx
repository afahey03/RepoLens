import type { GraphNodeDto, GraphEdgeDto } from '../types';

interface Props {
    node: GraphNodeDto;
    allEdges: GraphEdgeDto[];
    allNodes: GraphNodeDto[];
    onClose: () => void;
}

function NodeDetailPanel({ node, allEdges, allNodes, onClose }: Props) {
    const nodeMap = new Map(allNodes.map((n) => [n.id, n]));

    const incoming = allEdges
        .filter((e) => e.target === node.id)
        .map((e) => ({ ...e, nodeName: nodeMap.get(e.source)?.name ?? e.source }));

    const outgoing = allEdges
        .filter((e) => e.source === node.id)
        .map((e) => ({ ...e, nodeName: nodeMap.get(e.target)?.name ?? e.target }));

    return (
        <div className="node-detail-panel">
            <div className="node-detail-header">
                <h3>{node.name}</h3>
                <button className="close-btn" onClick={onClose}>
                    ✕
                </button>
            </div>

            <div className="node-detail-field">
                <span className="field-label">Type</span>
                <span className="node-type-badge">{node.type}</span>
            </div>

            {node.metadata?.filePath && (
                <div className="node-detail-field">
                    <span className="field-label">File</span>
                    <span className="field-value mono">{node.metadata.filePath}</span>
                </div>
            )}

            {Object.entries(node.metadata ?? {})
                .filter(([k]) => k !== 'filePath')
                .map(([key, value]) => (
                    <div key={key} className="node-detail-field">
                        <span className="field-label">{key}</span>
                        <span className="field-value">{value}</span>
                    </div>
                ))}

            <div className="node-detail-section">
                <h4>Incoming ({incoming.length})</h4>
                {incoming.length > 0 ? (
                    <ul className="edge-list">
                        {incoming.map((e, i) => (
                            <li key={i}>
                                <span className="edge-rel">{e.relationship}</span>
                                <span className="edge-name">{e.nodeName}</span>
                            </li>
                        ))}
                    </ul>
                ) : (
                    <p className="no-edges">None</p>
                )}
            </div>

            <div className="node-detail-section">
                <h4>Outgoing ({outgoing.length})</h4>
                {outgoing.length > 0 ? (
                    <ul className="edge-list">
                        {outgoing.map((e, i) => (
                            <li key={i}>
                                <span className="edge-rel">{e.relationship}</span>
                                <span className="edge-name">{e.nodeName}</span>
                            </li>
                        ))}
                    </ul>
                ) : (
                    <p className="no-edges">None</p>
                )}
            </div>
        </div>
    );
}

export default NodeDetailPanel;
