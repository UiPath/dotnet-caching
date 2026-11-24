using System.Runtime.CompilerServices;

namespace UiPath.Caching.Migration;

/// <summary>What a node shares between its caches for supersession markers: the cleanup queue and the write permits, whose limits are the node's.</summary>
internal sealed class MarkerNode
{
    private static readonly ConditionalWeakTable<IKeyMovePlane, MarkerNode> Nodes = [];

    private MarkerNode(IKeyMovePlane plane, TimeProvider clock, KeyMoveOptions options, bool background)
    {
        Unmarks = new UnmarkQueue(clock, options, () => plane.Data, background);
        Permits = new WritePermits(clock, options.NoOriginWriteInflightMax);
    }

    public UnmarkQueue Unmarks { get; }

    public WritePermits Permits { get; }

    /// <summary>The node's markers part for a plane, made by the first cache that asks.</summary>
    public static MarkerNode For(IKeyMovePlane plane, TimeProvider clock, KeyMoveOptions options, bool background) =>
        Nodes.GetValue(plane, p => new MarkerNode(p, clock, options, background));
}
