using System.Globalization;

namespace UiPath.Caching.Tests.PhaseB.Harness;

// Runs a conformance step against the model on one shard whose keys are named by role.
internal sealed class ModelExecutor(long now)
{
    private const string Fence = "fence";
    private const string Dst = "dst";
    private const string Stg = "stg";
    private const string Ctl = "ctl";
    private const string Src = "src";

    private readonly ModelFaults _f = ModelFaults.None;

    public ShardStore Shard { get; } = new();

    public long Now { get; } = now;

    public static IEnumerable<KeyValuePair<string, string>> Pairs(string fields) =>
        fields.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => { var i = p.IndexOf('=', StringComparison.Ordinal); return new KeyValuePair<string, string>(p[..i], p[(i + 1)..]); });

    public string Run(ScriptCall c)
    {
        try
        {
            return Execute(c);
        }
        catch (WrongTypeException)
        {
            return "WRONGTYPE";
        }
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private long At(ScriptCall c) => c.AtOffset is { } o ? Now + o : -1;

    private string Execute(ScriptCall c)
    {
        var s = Shard;
        switch (c.Kind)
        {
            case ScriptCallKind.PutString:
                s.Put(c.Key, Entry.Str(c.Value));
                return string.Empty;
            case ScriptCallKind.PutHash:
                s.Put(c.Key, Entry.Hash(Pairs(c.Fields)));
                return string.Empty;
            case ScriptCallKind.Delete:
                s.Remove(c.Key);
                return string.Empty;
            case ScriptCallKind.ExpireIn:
                s.ExpireAt(c.Key, Now + c.Px, Now);
                return string.Empty;
            case ScriptCallKind.Cap:
            {
                var r = ModelScripts.Cap(s, Now, Fence, Dst, Ctl, c.FenceTtl, c.Nonce, c.Id, c.Cid, _f);
                return $"{r.Code}|{r.Token}|{r.DestinationExists}|{r.Gate}";
            }

            case ScriptCallKind.Pub:
                return Text(ModelScripts.Pub(s, Now, Fence, Dst, Ctl, c.Token, c.Value, false, c.Px, c.Mode, c.Gate, c.Id, c.Cid, c.Nonce, c.FenceTtl, c.AtOffset is null ? -1 : At(c), _f));
            case ScriptCallKind.PubHash:
                return Text(ModelScripts.PubHash(s, Now, Fence, Dst, Stg, Ctl, c.Token, c.Px, c.Mode, c.Gate, c.Id, c.Cid, c.Nonce, c.FenceTtl, At(c), _f));
            case ScriptCallKind.Bump:
                return Text(ModelScripts.Bump(s, Now, Fence, Dst, Ctl, c.FenceTtl, c.Op, c.Value, false, c.Px, c.Nonce, c.Id, _f));
            case ScriptCallKind.Fill:
                return Text(ModelScripts.Fill(s, Now, Fence, Dst, Ctl, c.Value, false, c.Px, c.Nonce, c.FenceTtl, c.Id, _f));
            case ScriptCallKind.FillHash:
                return Text(ModelScripts.FillHash(s, Now, Stg, Dst, Fence, Ctl, c.Id, c.Px, c.Nonce, c.FenceTtl, _f));
            case ScriptCallKind.SrcDel:
                return Text(ModelScripts.SrcDel(s, Src, Ctl, c.Id, _f));
            case ScriptCallKind.SrcSet:
                return Text(ModelScripts.SrcSet(s, Now, Src, Ctl, c.Id, c.Value, false, c.Px, c.Only ? "nx" : "set", _f));
            case ScriptCallKind.SrcExp:
                return Text(ModelScripts.SrcExp(s, Now, Src, Ctl, c.Id, c.Px, _f));
            case ScriptCallKind.CmpDel:
                return Text(ModelScripts.CmpDel(s, Src, Ctl, c.Id, c.Value, _f));
            case ScriptCallKind.CmpDelHash:
                return Text(ModelScripts.CmpDelHash(s, Src, Ctl, c.Id, [.. Pairs(c.Fields)], _f));
            case ScriptCallKind.CtlSync:
                return Text(ModelScripts.CtlSync(s, Ctl, c.Ep, c.Id, "S1", "fwd", "n", "S0", "none", "n0", "0", "id0", [(c.Cid, c.Nonce)], _f));
            case ScriptCallKind.CtlCommit:
                return Text(ModelScripts.CtlCommit(s, Ctl, c.Ep, c.Id));
            case ScriptCallKind.Gate:
                return Text(ModelScripts.Gate(s, Ctl, c.Id, c.Cid, c.Value, c.Expect));
            case ScriptCallKind.HSetC:
                return Text(ModelScripts.HSetC(s, Now, Src, Ctl, c.Id, c.ReplaceKey, At(c), c.DropField, c.VersionField, c.VersionSeed, c.WithFence ? Fence : null, c.WithFence ? c.Nonce : null, c.FenceTtl, [.. Pairs(c.Fields)], _f));
            case ScriptCallKind.HDelC:
                return Text(ModelScripts.HDelC(s, Now, Src, Ctl, c.Id, c.VersionField, c.VersionSeed, c.WithFence ? Fence : null, c.WithFence ? c.Nonce : null, c.FenceTtl, [.. Pairs(c.Fields).Select(p => p.Key)], _f));
            default:
                return Text(ModelScripts.HExpC(s, Now, Src, Ctl, c.Id, At(c), c.WithFence ? Fence : null, c.WithFence ? c.Nonce : null, c.FenceTtl, _f));
        }
    }
}
