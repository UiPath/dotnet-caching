namespace UiPath.Caching.Tests.KeyMove.Harness;

// The two sides of a key for one direction: Src is where data leaves, Dst where it enters, and the fence lives on the Dst side.
internal readonly record struct Route(ShardStore Src, string SrcKey, ShardStore Dst, string DstKey, string Fence)
{
    public static Route Of(World w, KeySpec k, bool reverse) => reverse
        ? new Route(w.N, k.N, w.L, k.L, k.FenceReverse)
        : new Route(w.L, k.L, w.N, k.N, k.FenceForward);
}
