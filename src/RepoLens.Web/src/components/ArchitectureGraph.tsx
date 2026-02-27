import { useCallback, useMemo, useState } from 'react';
import {
    ReactFlow,
    Background,
    Controls,
    MiniMap,
    useReactFlow,
    ReactFlowProvider,
    type Node,
    type Edge,
    type NodeMouseHandler,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import type { ArchitectureResponse, GraphNodeDto, GraphStatsResponse } from '../types';
import { layoutGraph } from '../utils/layoutGraph';
import GraphNodeComponent, { type GraphNodeData } from './GraphNode';
import GraphToolbar from './GraphToolbar';
import GraphLegend from './GraphLegend';
import NodeDetailPanel from './NodeDetailPanel';

interface Props {
    architecture: ArchitectureResponse;
    stats?: GraphStatsResponse | null;
}

/* ── Edge style map ── */
const edgeStyles: Record<string, { stroke: string; strokeDasharray?: string; animated: boolean }> = {
    Imports: { stroke: '#22d3ee', animated: true },
    Contains: { stroke: '#52525b', strokeDasharray: '5 3', animated: false },
    Inherits: { stroke: '#f59e0b', animated: false },
    Implements: { stroke: '#8b5cf6', animated: false },
    Calls: { stroke: '#f87171', animated: true },
};

const defaultEdgeStyle = { stroke: '#6366f1', animated: false };

/* ── Node type registration ── */
const nodeTypes = { graphNode: GraphNodeComponent };

/* ── MiniMap color helper ── */
const miniMapColors: Record<string, string> = {
    File: '#3b82f6',
    Module: '#0ea5e9',
    Namespace: '#f59e0b',
    Class: '#8b5cf6',
    Interface: '#6366f1',
    Function: '#10b981',
    Folder: '#78716c',
};

function miniMapNodeColor(node: Node): string {
    const data = node.data as unknown as GraphNodeData;
    return miniMapColors[data.nodeType] ?? '#52525b';
}

/* ── Inner graph (needs ReactFlowProvider) ── */
function ArchitectureGraphInner({ architecture, stats }: Props) {
    const [direction, setDirection] = useState<'TB' | 'LR'>('TB');
    const [hiddenNodeTypes, setHiddenNodeTypes] = useState<Set<string>>(new Set(['Folder']));
    const [hiddenEdgeTypes, setHiddenEdgeTypes] = useState<Set<string>>(new Set());
    const [selectedNode, setSelectedNode] = useState<GraphNodeDto | null>(null);
    const { fitView } = useReactFlow();

    // Unique types in the data
    const availableNodeTypes = useMemo(
        () => [...new Set(architecture.nodes.map((n) => n.type))].sort(),
        [architecture.nodes],
    );
    const availableEdgeTypes = useMemo(
        () => [...new Set(architecture.edges.map((e) => e.relationship))].sort(),
        [architecture.edges],
    );

    // Filter nodes and edges
    const filteredBackendNodes = useMemo(
        () => architecture.nodes.filter((n) => !hiddenNodeTypes.has(n.type)),
        [architecture.nodes, hiddenNodeTypes],
    );
    const visibleNodeIds = useMemo(
        () => new Set(filteredBackendNodes.map((n) => n.id)),
        [filteredBackendNodes],
    );
    const filteredBackendEdges = useMemo(
        () =>
            architecture.edges.filter(
                (e) =>
                    !hiddenEdgeTypes.has(e.relationship) &&
                    visibleNodeIds.has(e.source) &&
                    visibleNodeIds.has(e.target),
            ),
        [architecture.edges, hiddenEdgeTypes, visibleNodeIds],
    );

    // Convert to React Flow nodes/edges, then layout
    const { nodes, edges } = useMemo(() => {
        const rfNodes: Node[] = filteredBackendNodes.map((n) => ({
            id: n.id,
            type: 'graphNode',
            data: {
                label: n.name,
                nodeType: n.type,
                filePath: n.metadata?.filePath,
                metadata: n.metadata,
            } satisfies GraphNodeData,
            position: { x: 0, y: 0 },
        }));

        const rfEdges: Edge[] = filteredBackendEdges.map((e, i) => {
            const style = edgeStyles[e.relationship] ?? defaultEdgeStyle;
            return {
                id: `e-${i}`,
                source: e.source,
                target: e.target,
                label: e.relationship,
                animated: style.animated,
                style: { stroke: style.stroke, strokeDasharray: style.strokeDasharray },
                labelStyle: { fill: '#a1a1aa', fontSize: 10 },
                labelBgStyle: { fill: '#18181b', fillOpacity: 0.85 },
                labelBgPadding: [4, 2] as [number, number],
            };
        });

        const laidOut = layoutGraph(rfNodes, rfEdges, { direction });
        return { nodes: laidOut, edges: rfEdges };
    }, [filteredBackendNodes, filteredBackendEdges, direction]);

    // Handlers
    const handleToggleNodeType = useCallback((type: string) => {
        setHiddenNodeTypes((prev) => {
            const next = new Set(prev);
            if (next.has(type)) next.delete(type);
            else next.add(type);
            return next;
        });
    }, []);

    const handleToggleEdgeType = useCallback((type: string) => {
        setHiddenEdgeTypes((prev) => {
            const next = new Set(prev);
            if (next.has(type)) next.delete(type);
            else next.add(type);
            return next;
        });
    }, []);

    const handleNodeClick: NodeMouseHandler = useCallback(
        (_event, node) => {
            const backendNode = architecture.nodes.find((n) => n.id === node.id);
            if (backendNode) setSelectedNode(backendNode);
        },
        [architecture.nodes],
    );

    const handleFitView = useCallback(() => {
        fitView({ padding: 0.15, duration: 300 });
    }, [fitView]);

    if (architecture.nodes.length === 0) {
        return (
            <div className="empty-state">
                <p>No architecture data available.</p>
            </div>
        );
    }

    return (
        <div className="architecture-container">
            <GraphToolbar
                nodeTypes={availableNodeTypes}
                edgeTypes={availableEdgeTypes}
                hiddenNodeTypes={hiddenNodeTypes}
                hiddenEdgeTypes={hiddenEdgeTypes}
                onToggleNodeType={handleToggleNodeType}
                onToggleEdgeType={handleToggleEdgeType}
                direction={direction}
                onDirectionChange={setDirection}
                nodeCount={nodes.length}
                edgeCount={edges.length}
                onFitView={handleFitView}
            />

            <div className="architecture-body">
                <div className="architecture-graph">
                    <ReactFlow
                        nodes={nodes}
                        edges={edges}
                        nodeTypes={nodeTypes}
                        onNodeClick={handleNodeClick}
                        fitView
                        fitViewOptions={{ padding: 0.15 }}
                        minZoom={0.05}
                        maxZoom={2}
                        proOptions={{ hideAttribution: true }}
                    >
                        <Background gap={20} size={1} color="#27272a" />
                        <Controls showInteractive={false} />
                        <MiniMap
                            nodeColor={miniMapNodeColor}
                            nodeStrokeWidth={0}
                            maskColor="rgba(0, 0, 0, 0.65)"
                            style={{ background: '#18181b', border: '1px solid #27272a' }}
                        />
                    </ReactFlow>
                </div>

                {selectedNode && (
                    <NodeDetailPanel
                        node={selectedNode}
                        allEdges={architecture.edges}
                        allNodes={architecture.nodes}
                        onClose={() => setSelectedNode(null)}
                    />
                )}
            </div>

            <GraphLegend />

            {stats && (
                <div className="graph-stats-bar">
                    <span>Depth: {stats.maxDepth}</span>
                    <span>·</span>
                    {Object.entries(stats.nodeTypeCounts)
                        .sort(([, a], [, b]) => b - a)
                        .map(([type, count]) => (
                            <span key={type}>
                                {type}: {count}
                            </span>
                        ))}
                </div>
            )}
        </div>
    );
}

/* ── Outer wrapper with provider ── */
function ArchitectureGraph({ architecture, stats }: Props) {
    return (
        <ReactFlowProvider>
            <ArchitectureGraphInner architecture={architecture} stats={stats} />
        </ReactFlowProvider>
    );
}

export default ArchitectureGraph;
