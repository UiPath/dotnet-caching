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
}
