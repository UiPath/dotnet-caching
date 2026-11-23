using System.Collections.Immutable;

namespace UiPath.Caching.Tests.KeyMove.Harness;

// Starting worlds: a state of the group, the keys, and the actors that run against them. Node 0 and node 1 are the two application nodes.
internal static class Scenarios
{
    public static readonly Identity S1 = Identity.Of(1, "S1", "fwd", false);
    public static readonly Identity S2 = Identity.Of(2, "S2", "fwd", false);
    public static readonly Identity S3 = Identity.Of(2, "S3", "fwd", true);
    public static readonly Identity S1Rev = Identity.Of(3, "S1", "rev", true);
    public static readonly Identity S3Rev = Identity.Of(4, "S3", "rev", true);
    public static readonly Identity S4 = Identity.Of(3, "S4", "fwd", true);
    public static readonly Identity S4Rev = Identity.Of(5, "S4", "rev", true);
    public static readonly Identity S5 = Identity.Of(4, "S5", "fwd", true);

    public static KeySpec Origin(string initialL = "v0", long ttl = -1) => new("a", "c1", InitialL: initialL, Ttl: ttl);

    public static KeySpec NoOrigin(string initialL = "v0", long ttl = -1) => new("b", "c2", NoOrigin: true, InitialL: initialL, Ttl: ttl);

    public static World Build(ModelFaults? faults, Identity start, Identity[] views, IEnumerable<KeySpec> keys, params Actor[] actors) =>
        Build(new WorldConfig([.. keys], faults ?? ModelFaults.None), start, views, actors);

    public static World Build(WorldConfig config, Identity start, Identity[] views, params Actor[] actors) =>
        World.Create(config, start, views, actors);

    public static Identity[] Both(Identity view) => [view, view];

    public static ImmutableArray<WriterOp> Ops(params WriterOp[] ops) => [.. ops];

    public static WriteActor Write(string name, int node, int key, string needs, params WriterOp[] ops) => new(name, node, key, [.. ops], needs);

    public static EnvActor Evict(int key, params string[] options) => new("V", EnvActor.Evict, key, [.. options]);

    public static EnvActor Fail(params string[] options) => new("F", EnvActor.Failover, 0, [.. options]);

    public static EnvActor Tick(params string[] amounts) => new("E", EnvActor.Tick, 0, [.. amounts]);

    public static TransitionActor Transition(Identity from, Identity to, int[] adopt, bool staleReplay = false, bool evictControl = false) =>
        new("X", to, from, [.. adopt], staleReplay, evictControl);
}
