namespace UiPath.Caching.Tests.KeyMove.Harness;

// What a node acts under: the state, its direction, whether promotion has begun (nPop) and the identity pair; Lswept is the S5 flag of the state record.
internal sealed record Identity(long Ep, string Id, string St, string Dir, bool NPop, bool Lswept = false)
{
    public bool Reverse => Dir == "rev";

    public static Identity Of(long ep, string st, string dir, bool nPop) => new(ep, "i" + ep, st, dir, nPop);
}
