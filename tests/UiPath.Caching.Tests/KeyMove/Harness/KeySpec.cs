namespace UiPath.Caching.Tests.KeyMove.Harness;

/// <param name="Ttl">The relative life of the initial value, or -1 for none.</param>
/// <param name="Unmanaged">A cache that is not in the state's set: every node uses plain commands on L.</param>
internal sealed record KeySpec(string Name, string Cid, bool NoOrigin = false, bool Hash = false, bool Unmanaged = false, string? InitialL = null, string? InitialN = null, long Ttl = -1)
{
    public string L => "L:" + Name;

    public string N => "N:" + Name;

    public string FenceForward => "mg:G:" + Name;

    public string FenceReverse => "mg:GL:" + Name;

    public string Staging(string nonce) => "mg:stg:" + Name + ":" + nonce;
}
