namespace UiPath.Caching.Tests.PhaseB.Harness;

using static ScriptCallKind;

// The shared table of the differential test: each scenario is a list of steps run by the model and by MoveScripts, with every reply and the final keys compared.
internal static class ConformanceScenarios
{
    private static readonly ScriptCall Setup = new(CtlSync) { Nonce = "g1" };

    public static IReadOnlyDictionary<string, ScriptCall[]> All { get; } = new Dictionary<string, ScriptCall[]>
    {
        ["CAP creates the fence"] = [Setup, new(Cap) { Nonce = "n1" }],
        ["CAP twice keeps the first token"] = [Setup, new(Cap) { Nonce = "n1" }, new(Cap) { Nonce = "n2" }],
        ["CAP reports an existing destination"] = [Setup, PutS("dst", "x"), new(Cap)],
        ["CAP under another identity"] = [Setup, new(Cap) { Id = "other" }],
        ["CAP with no gate for the cache"] = [Setup, new(Cap) { Cid = "zz" }],
        ["CAP with a closed gate"] = [Setup, new(Gate) { Value = "CLOSED:c" }, new(Cap)],
        ["CAP with no record"] = [new(Cap)],
        ["PUB publishes with a deadline"] = [Setup, new(Cap) { Nonce = "n1" }, new(Pub) { Token = "n1", Px = 60_000, AtOffset = 0, Mode = "nx" }],
        ["PUB publishes a persistent value"] = [Setup, new(Cap) { Nonce = "n1" }, new(Pub) { Token = "n1", Mode = "nx" }],
        ["PUB creates with a relative life"] = [Setup, new(Cap) { Nonce = "n1" }, new(Pub) { Token = "n1", Px = 45_000, Mode = "nx" }],
        ["PUB nx finds the destination"] = [Setup, new(Cap) { Nonce = "n1" }, PutS("dst", "old"), new(Pub) { Token = "n1", Mode = "nx" }],
        ["PUB set overwrites"] = [Setup, new(Cap) { Nonce = "n1" }, PutS("dst", "old"), new(Pub) { Token = "n1", Mode = "set" }],
        ["PUB with a moved fence"] = [Setup, new(Cap) { Nonce = "n1" }, new(Pub) { Token = "other" }],
        ["PUB with the fence gone"] = [Setup, new(Pub) { Token = "n1" }],
        ["PUB under another identity"] = [Setup, new(Cap) { Nonce = "n1" }, new(Pub) { Token = "n1", Id = "other" }],
        ["PUB with a changed gate"] = [Setup, new(Cap) { Nonce = "n1" }, new(Gate) { Value = "OPEN:g2" }, new(Pub) { Token = "n1" }],
        ["PUB with a closed gate"] = [Setup, new(Cap) { Nonce = "n1" }, new(Gate) { Value = "CLOSED:g1" }, new(Pub) { Token = "n1", Gate = "CLOSED:g1" }],
        ["PUB past the deadline"] = [Setup, new(Cap) { Nonce = "n1" }, new(Pub) { Token = "n1", Px = 1_000, AtOffset = -3_600_000 }],
        ["PUBH publishes with a deadline"] = [Setup, new(Cap) { Nonce = "n1" }, PutH("stg", "a=1;b=2"), new(PubHash) { Token = "n1", Px = 60_000, AtOffset = 0, Mode = "nx" }],
        ["PUBH publishes a persistent hash"] = [Setup, new(Cap) { Nonce = "n1" }, PutH("stg", "a=1;b=2"), PutS("dst", "old"), new(PubHash) { Token = "n1", Mode = "set" }],
        ["PUBH with the staging key missing"] = [Setup, new(Cap) { Nonce = "n1" }, new(PubHash) { Token = "n1" }],
        ["PUBH with a moved fence"] = [Setup, new(Cap) { Nonce = "n1" }, PutH("stg", "a=1"), new(PubHash) { Token = "other" }],
        ["PUBH nx finds the destination"] = [Setup, new(Cap) { Nonce = "n1" }, PutH("stg", "a=1"), PutS("dst", "old"), new(PubHash) { Token = "n1", Mode = "nx" }],
        ["PUBH with a closed gate"] = [Setup, new(Cap) { Nonce = "n1" }, PutH("stg", "a=1"), new(Gate) { Value = "CLOSED:g1" }, new(PubHash) { Token = "n1", Gate = "CLOSED:g1" }],
        ["PUBH past the deadline"] = [Setup, new(Cap) { Nonce = "n1" }, PutH("stg", "a=1"), new(PubHash) { Token = "n1", Px = 1_000, AtOffset = -3_600_000 }],
        ["PUBH under another identity"] = [Setup, new(Cap) { Nonce = "n1" }, PutH("stg", "a=1"), new(PubHash) { Token = "n1", Id = "other" }],
        ["BUMP sets with a life"] = [Setup, new(Bump) { Op = "set", Px = 40_000, Nonce = "b1" }],
        ["BUMP sets without a life over a key with one"] = [Setup, PutS("dst", "old"), new(ExpireIn) { Key = "dst", Px = 9_000 }, new(Bump) { Op = "set", Nonce = "b1" }],
        ["BUMP deletes and counts"] = [Setup, PutS("dst", "old"), new(Bump) { Op = "del", Nonce = "b1" }, new(Bump) { Op = "del", Nonce = "b2" }],
        ["BUMP none only replaces the fence"] = [Setup, PutS("dst", "old"), new(Bump) { Op = "none", Nonce = "b1" }],
        ["BUMP under another identity"] = [Setup, PutS("dst", "old"), new(Bump) { Op = "del", Id = "other" }],
        ["FILL creates and refuses an existing key"] = [Setup, new(Fill) { Nonce = "f1" }, new(Fill) { Nonce = "f2", Value = "v2" }],
        ["FILL with a life"] = [Setup, new(Fill) { Nonce = "f1", Px = 20_000 }],
        ["FILL under another identity"] = [Setup, new(Fill) { Id = "other" }],
        ["FILLH renames staging with a life"] = [Setup, PutH("stg", "a=1;b=2"), new(FillHash) { Nonce = "f1", Px = 20_000 }],
        ["FILLH persists with no life"] = [Setup, PutH("stg", "a=1;b=2"), new(FillHash) { Nonce = "f1" }],
        ["FILLH with the staging key missing"] = [Setup, new(FillHash) { Nonce = "f1" }],
        ["FILLH finds the destination"] = [Setup, PutH("stg", "a=1"), PutS("dst", "old"), new(FillHash) { Nonce = "f1" }, new(Delete) { Key = "stg" }],
        ["FILLH under another identity"] = [Setup, PutH("stg", "a=1"), new(FillHash) { Id = "other" }],
        ["SRCDEL deletes under the identity"] = [Setup, PutS("src", "x"), new(SrcDel) { Id = "other" }, new(SrcDel), new(SrcDel)],
        ["SRCSET sets, refuses nx over a key and sets a life"] = [Setup, new(SrcSet), new(SrcSet) { Only = true, Value = "v2" }, new(SrcSet) { Px = 30_000, Value = "v3" }],
        ["SRCEXP refreshes and persists"] = [Setup, PutS("src", "x"), new(SrcExp) { Px = 10_000 }, new(SrcExp), new(Delete) { Key = "src" }, new(SrcExp), new(SrcExp) { Px = 5_000 }],
        ["CMPDEL compares bytes"] = [Setup, PutS("src", "a"), new(CmpDel) { Value = "b" }, new(CmpDel) { Value = "a" }, new(CmpDel) { Value = "a" }],
        ["CMPDEL on a hash reads as changed"] = [Setup, PutH("src", "a=1"), new(CmpDel) { Value = "a" }],
        ["CMPDELH compares the whole hash"] = [Setup, PutH("src", "a=1;b=2"), new(CmpDelHash) { Fields = "a=1" }, new(CmpDelHash) { Fields = "a=1;b=3" }, new(CmpDelHash) { Fields = "a=1;b=2" }, PutS("src", "x"), new(CmpDelHash) { Fields = "a=1" }],
        ["CTLSYNC is monotone and replaces the gates"] = [Setup, new(CtlSync) { Ep = 2, Id = "id2", Nonce = "g2" }, new(CtlSync) { Ep = 1, Nonce = "g1" }, new(CtlSync) { Ep = 2, Id = "id2", Nonce = "g9" }, new(CtlSync) { Ep = 2, Id = "other" }, new(CtlSync) { Ep = 3, Id = "id3", Cid = "c9", Nonce = "g3" }],
        ["CTLCOMMIT marks the record"] = [Setup, new(CtlCommit), new(CtlCommit) { Ep = 2 }, new(CtlCommit) { Id = "other" }],
        ["GATE closes and reopens with an expectation"] = [Setup, new(Gate) { Value = "CLOSED:a" }, new(Gate) { Value = "OPEN:b", Expect = "CLOSED:zz" }, new(Gate) { Value = "OPEN:b", Expect = "CLOSED:a" }, new(Gate) { Value = "OPEN:b", Expect = "CLOSED:a" }, new(Gate) { Id = "other", Value = "CLOSED:c" }],
        ["HSETC replaces the key with a fence, a count and a deadline"] = [Setup, PutH("src", "x=1"), new(HSetC) { ReplaceKey = true, Fields = "a=1;b=2", WithFence = true, VersionField = "ver", VersionSeed = 5, AtOffset = 60_000, Nonce = "h1" }],
        ["HSETC merges and drops an expiry"] = [Setup, PutH("src", "x=1"), new(ExpireIn) { Key = "src", Px = 9_000 }, new(HSetC) { Fields = "a=9", DropField = "x" }],
        ["HSETC under another identity"] = [Setup, PutH("src", "x=1"), new(HSetC) { Fields = "a=1", Id = "other" }],
        ["HSETC on a string key"] = [Setup, PutS("src", "x"), new(HSetC) { Fields = "a=1" }],
        ["HDELC deletes fields and drops a lone count"] = [Setup, PutH("src", "a=1;b=2;ver=3"), new(HDelC) { Fields = "a=", VersionField = "ver" }, new(HDelC) { Fields = "b=", VersionField = "ver" }],
        ["HEXPC sets and clears an expiry"] = [Setup, PutH("src", "a=1"), new(HExpC) { AtOffset = 60_000, WithFence = true, Nonce = "e1" }, new(HExpC), new(Delete) { Key = "src" }, new(HExpC) { AtOffset = 60_000 }],
        ["with no record every script refuses"] = [PutS("src", "x"), new(SrcDel), new(SrcSet), new(CmpDel) { Value = "x" }, new(Bump) { Op = "del" }, new(Fill), new(Gate), new(HExpC), new(Pub) { Token = "t" }],
    };

    private static ScriptCall PutS(string key, string value) => new(PutString) { Key = key, Value = value };

    private static ScriptCall PutH(string key, string fields) => new(PutHash) { Key = key, Fields = fields };
}
