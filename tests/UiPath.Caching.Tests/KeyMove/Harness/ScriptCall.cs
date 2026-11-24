namespace UiPath.Caching.Tests.KeyMove.Harness;

// One step of a conformance scenario, described once and run by the model and by the real scripts. Keys are roles: fence, dst, stg, ctl, src.
internal sealed record ScriptCall(ScriptCallKind Kind)
{
    public string Key { get; init; } = "dst";

    public string Id { get; init; } = "id1";

    public string Cid { get; init; } = "c1";

    public string Nonce { get; init; } = "nonce-new";

    public string Token { get; init; } = "tok";

    public string Value { get; init; } = "v1";

    public string Gate { get; init; } = "OPEN:g1";

    public string Mode { get; init; } = "set";

    public string Op { get; init; } = "set";

    public string Expect { get; init; } = string.Empty;

    // Hash content as "a=1;b=2".
    public string Fields { get; init; } = string.Empty;

    public string? DropField { get; init; }

    public string? VersionField { get; init; }

    public long VersionSeed { get; init; }

    public bool ReplaceKey { get; init; }

    public bool WithFence { get; init; }

    public long Px { get; init; } = -1;

    public long FenceTtl { get; init; } = 30_000;

    public long Ep { get; init; } = 1;

    // An instant relative to the server clock at the start of the scenario (CAP and PUB's tcap, a hash expiry), or null for none (-1).
    public long? AtOffset { get; init; }

    public bool Only { get; init; }

    // The marker field of CAP, BUMP and UNMARK; empty for none. PutField writes it, and the other control fields, into the hash named by Key.
    public string MarkerField { get; init; } = string.Empty;

    public bool First { get; init; }

    // The binding value SRCDELG is given.
    public string Binding { get; init; } = string.Empty;

    // LIMSET's value, and the lim CTLSYNC writes; null leaves the record's own.
    public long? Limit { get; init; }

    // What CTLSYNC copies into the record: the field that names the binding, the forward generation and the retiring one.
    public string BindingField { get; init; } = string.Empty;

    public string Fg { get; init; } = string.Empty;

    public string Rg { get; init; } = string.Empty;
}
