using System.Collections.Immutable;

namespace UiPath.Caching.Tests.KeyMove.Harness;

// E (a tick of the virtual clock), V (an eviction) and F (a failover): one step with one alternative per option.
internal sealed record EnvActor(string Name, string Kind, int Key, ImmutableArray<string> Options) : Actor(Name, -1)
{
    public const string Tick = "tick";
    public const string Evict = "evict";
    public const string Failover = "failover";

    public override int Choices(World w) => Done ? 0 : Options.Length;

    public override Actor Step(World w, int choice)
    {
        var option = Options[choice];
        var spec = w.Config.Keys[Key];
        switch (Kind)
        {
            case Tick:
                Advance(w, long.Parse(option, System.Globalization.CultureInfo.InvariantCulture));
                break;
            case Evict:
                EvictKey(w, spec, option);
                break;
            default:
                Fail(w, option);
                break;
        }

        Note(w, $"{Kind} {option}");
        return this with { Done = true };
    }

    private static void Advance(World w, long by)
    {
        w.Now += by;
        var before = DataKeys(w);
        w.L.Expire(w.Now);
        w.N.Expire(w.Now);
        var after = DataKeys(w);
        for (var i = 0; i < w.Config.Keys.Length; i++)
        {
            if (before[i] != after[i])
            {
                w.Oracle.Disturb(i);
            }
        }
    }

    private static string[] DataKeys(World w) =>
        [.. w.Config.Keys.Select(k => $"{w.L.Get(k.L)?.Encode()}|{w.N.Get(k.N)?.Encode()}")];

    private void EvictKey(World w, KeySpec spec, string option)
    {
        var (shard, key) = option switch
        {
            "L" => (w.L, spec.L),
            "N" => (w.N, spec.N),
            "G" => (w.N, spec.FenceForward),
            "GL" => (w.L, spec.FenceReverse),
            "ctlL" => (w.L, ShardStore.ControlKey),
            _ => (w.N, ShardStore.ControlKey),
        };
        if (shard.Scripted(() => shard.Remove(key)) && option is "L" or "N")
        {
            // The evicted copy may be the newer one of a write in flight, and the older copy on the other side then shows.
            w.Oracle.Lose(Key);
        }
    }

    private static void Fail(World w, string option)
    {
        var parts = option.Split(':');
        var shard = parts[0] == "L" ? w.L : w.N;
        if (parts[0] == "L")
        {
            w.LFailovers++;
        }
        else
        {
            w.NFailovers++;
        }

        var before = DataKeys(w);
        shard.Failover(int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), w.Now);
        var after = DataKeys(w);

        // Only a key whose copies changed lost anything; a lost fence or staging write loses no value.
        for (var i = 0; i < w.Config.Keys.Length; i++)
        {
            if (before[i] != after[i])
            {
                w.Oracle.Lose(i);
            }
        }
    }
}
