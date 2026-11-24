namespace UiPath.Caching.Tests.KeyMove.Harness;

using static ScriptCallKind;

// The shared table of the differential test: each scenario is a list of steps run by the model and by MoveScripts, with every reply and the final keys compared.
internal static class ConformanceScenarios
{
    private const string M = "sm:src";

    private static readonly ScriptCall Setup = new(CtlSync) { Nonce = "g1" };

    // The record of a core build at S3: a limit, and the identity as the binding.
    private static readonly ScriptCall Limited = new(CtlSync) { Nonce = "g1", Limit = 100 };

    // A record that names the forward generation as its binding.
    private static readonly ScriptCall Generation = new(CtlSync) { Nonce = "g1", Limit = 100, BindingField = "fg", Fg = "g9" };

    public static IReadOnlyDictionary<string, ScriptCall[]> All { get; } = new Dictionary<string, ScriptCall[]>
    {
        ["CAP creates the fence"] = [Setup, new(Cap) { Nonce = "n1" }],
        ["CAP twice keeps the first token"] = [Setup, new(Cap) { Nonce = "n1" }, new(Cap) { Nonce = "n2" }],
        ["CAP reports an existing destination"] = [Setup, PutS("dst", "x"), new(Cap)],
        ["CAP under another identity"] = [Setup, new(Cap) { Id = "other" }],
        ["CAP with no gate for the cache"] = [Setup, new(Cap) { Cid = "zz" }],
        ["CAP with a closed gate"] = [Setup, new(Gate) { Value = "CLOSED:c" }, new(Cap)],
        ["CAP with no record"] = [new(Cap)],
        ["CAP answers a current marker before it takes a token"] = [Limited, SetMarked("b1"), new(Cap) { Nonce = "n2", MarkerField = M }],
        ["CAP ignores a marker of an older identity"] = [Limited, Mark("old|n0"), new(Cap) { Nonce = "n1", MarkerField = M }],
        ["CAP ignores a marker when asked for none"] = [Limited, Mark("id1|n0"), new(Cap) { Nonce = "n1" }],
        ["CAP matches a marker by the bar, not by a prefix of the identity"] = [Limited, Mark("id10|n0"), new(Cap) { Nonce = "n1", MarkerField = M }],
        ["CAP reads the binding from the field the record names"] = [Generation, Mark("g9|n0"), new(Cap) { Nonce = "n1", MarkerField = M }],
        ["CAP ignores a marker of the identity when the record binds to the generation"] = [Generation, Mark("id1|n0"), new(Cap) { Nonce = "n2", MarkerField = M }],
        ["CAP with a record that names a missing binding field ignores the marker"] = [new(CtlSync) { Nonce = "g1", BindingField = "zz" }, Mark("id1|n0"), new(Cap) { Nonce = "n1", MarkerField = M }],
        ["BUMP setm sets the key, the fence and the marker"] = [Limited, SetMarked("b1", px: 40_000, first: true)],
        ["BUMP setm replaces the marker of an earlier attempt"] = [Limited, SetMarked("b1"), SetMarked("b2")],
        ["BUMP setm keeps the peak of the field count"] = [Limited, SetMarked("b1"), new(Unmark) { MarkerField = M, Value = "id1|b1" }, PutField("ctl", "sm:b", "id1|x"), SetMarked("b2")],
        ["BUMP setm refuses a first attempt at the limit and changes nothing"] = [Limited, PutS("dst", "old"), new(LimSet) { Limit = 14 }, SetMarked("b1", first: true)],
        ["BUMP setm below the limit takes a first attempt"] = [Limited, new(LimSet) { Limit = 15 }, SetMarked("b1", first: true)],
        ["BUMP setm never refuses a rerun"] = [Limited, new(LimSet) { Limit = 14 }, SetMarked("b1")],
        ["BUMP setm does not refuse a first attempt that has its field"] = [Limited, Mark("id1|n0"), new(LimSet) { Limit = 1 }, SetMarked("b1", first: true)],
        ["BUMP setm on a record with no limit refuses a first attempt"] = [Setup, SetMarked("b1", first: true)],
        ["BUMP setm on a record with no limit takes a rerun"] = [Setup, SetMarked("b1")],
        ["BUMP setm refuses a late command and changes nothing"] = [Limited, PutS("dst", "old"), SetMarked("b1", first: true, at: -1_000)],
        ["BUMP setm takes a command whose deadline is ahead"] = [Limited, SetMarked("b1", first: true, at: 60_000)],
        ["BUMP setm refuses a record with no binding value"] = [new(CtlSync) { Nonce = "g1", Limit = 100, BindingField = "zz" }, PutS("dst", "old"), SetMarked("b1")],
        ["BUMP setm under another identity"] = [Limited, new(Bump) { Op = "setm", MarkerField = M, Nonce = "b1", Id = "other", First = true }],
        ["BUMP setm binds the marker to the generation when the record names one"] = [Generation, SetMarked("b1")],
        ["BUMP del removes the marker field"] = [Limited, PutS("dst", "old"), Mark("id1|n0"), new(Bump) { Op = "del", MarkerField = M, Nonce = "b1" }, new(Bump) { Op = "del", MarkerField = M, Nonce = "b2" }],
        ["BUMP del with no marker field leaves the markers"] = [Limited, PutS("dst", "old"), Mark("id1|n0"), new(Bump) { Op = "del", Nonce = "b1" }],
        ["UNMARK removes only the exact value"] = [Setup, Mark("id1|n0"), new(Unmark) { MarkerField = M, Value = "id1|n1" }, new(Unmark) { MarkerField = M, Value = "id1|n0" }, new(Unmark) { MarkerField = M, Value = "id1|n0" }],
        ["UNMARK checks no identity"] = [Setup, Mark("old|n0"), new(Unmark) { MarkerField = M, Value = "old|n0", Id = "other" }],
        ["UNMARK with no record"] = [new(Unmark) { MarkerField = M, Value = "id1|n0" }],
        ["UNMARKN removes the pairs that still match"] = [Setup, Mark("id1|n0"), PutField("ctl", "sm:b", "id1|n1"), PutField("ctl", "sm:c", "id1|n2"), new(UnmarkMany) { Fields = "sm:src=id1|n0;sm:b=id1|x;sm:c=id1|n2;sm:zz=id1|n3" }],
        ["SMGC answers live for the current identity and keeps the marker"] = [Setup, Mark("id1|n0"), new(Smgc) { MarkerField = M, Value = "id1|n0" }],
        ["SMGC removes an obsolete marker"] = [Setup, Mark("old|n0"), new(Smgc) { MarkerField = M, Value = "old|n0" }, new(Smgc) { MarkerField = M, Value = "old|n0" }],
        ["SMGC leaves a marker that changed"] = [Setup, Mark("old|n1"), new(Smgc) { MarkerField = M, Value = "old|n0" }],
        ["SMGC with no marker or no record"] = [new(Smgc) { MarkerField = M, Value = "old|n0" }, Setup, new(Smgc) { MarkerField = M, Value = "old|n0" }],
        ["SMGC matches by the binding, not by a prefix of the identity"] = [Setup, Mark("id10|n0"), new(Smgc) { MarkerField = M, Value = "id10|n0" }],
        ["SMGC keeps a marker of the retiring generation and removes an older one"] = [new(CtlSync) { Nonce = "g1", BindingField = "fg", Fg = "g9", Rg = "g5" }, Mark("g5|n0"), PutField("ctl", "sm:b", "g1|n1"), new(Smgc) { MarkerField = M, Value = "g5|n0" }, new(Smgc) { MarkerField = "sm:b", Value = "g1|n1" }],
        ["SMGC reads the binding from the field the record names"] = [Generation, Mark("id1|n0"), PutField("ctl", "sm:b", "g9|n1"), new(Smgc) { MarkerField = M, Value = "id1|n0" }, new(Smgc) { MarkerField = "sm:b", Value = "g9|n1" }],
        ["SMGCN answers each pair"] = [Setup, Mark("old|n0"), PutField("ctl", "sm:b", "id1|n1"), PutField("ctl", "sm:c", "old|n2"), new(SmgcMany) { Fields = "sm:src=old|n0;sm:b=id1|n1;sm:c=old|x;sm:zz=old|n3" }],
        ["SRCDELG deletes under the current binding"] = [Setup, PutS("src", "x"), new(SrcDelG) { Binding = "id1" }, new(SrcDelG) { Binding = "id1" }],
        ["SRCDELG refuses a binding that ended and deletes nothing"] = [Setup, PutS("src", "x"), new(SrcDelG) { Binding = "id0" }, new(SrcDelG) { Binding = string.Empty }],
        ["SRCDELG under a stale identity"] = [Setup, PutS("src", "x"), new(SrcDelG) { Id = "other", Binding = "id1" }],
        ["SRCDELG accepts the retiring generation"] = [new(CtlSync) { Nonce = "g1", BindingField = "fg", Fg = "g9", Rg = "g5" }, PutS("src", "x"), new(SrcDelG) { Binding = "g4" }, new(SrcDelG) { Binding = "g5" }],
        ["SRCDELG reads the binding from the field the record names"] = [Generation, PutS("src", "x"), new(SrcDelG) { Binding = "id1" }, new(SrcDelG) { Binding = "g9" }],
        ["SRCDELG with no record"] = [PutS("src", "x"), new(SrcDelG) { Binding = "id1" }],
        ["LIMSET sets the limit under the identity"] = [Setup, new(LimSet) { Limit = 50 }, new(LimSet) { Limit = 60, Id = "other" }],
        ["CTLCOMPACT rewrites a record and sets its peak to its length"] = [Limited, SetMarked("b1"), new(Unmark) { MarkerField = M, Value = "id1|b1" }, new(Compact), new(Compact) { Id = "other" }],
        ["CTLCOMPACT refuses a record above 2048 fields"] = [new(PutHash) { Key = "ctl", Fields = "id=id1;" + string.Join(';', Enumerable.Range(0, 2050).Select(i => $"f{i}=v")) }, new(Compact)],
        ["CTLSYNC writes the limit and the binding fields it is given and removes the ones it is not"] = [new(CtlSync) { Nonce = "g1", Limit = 99, BindingField = "fg", Fg = "g9", Rg = "g5" }, new(CtlSync) { Ep = 2, Id = "id2", Cid = "c9", Nonce = "g2" }, new(CtlSync) { Ep = 3, Id = "id3", Cid = "c9", Nonce = "g3", Limit = 77 }],
        ["CTLSYNC drops the gates of the previous list and keeps the markers"] = [Setup, Mark("id1|n0"), new(CtlSync) { Ep = 2, Id = "id2", Cid = "c9", Nonce = "g2" }],
        ["CTLSYNC keeps a gate that the list does not name"] = [Setup, PutField("ctl", "c:zz", "OPEN:g0"), new(CtlSync) { Ep = 2, Id = "id2", Cid = "c9", Nonce = "g2" }],
        ["CTLSYNC scans a record that has no list"] = [new(PutHash) { Key = "ctl", Fields = "ep=1;id=id1;c:c1=OPEN:g1;c:c2=OPEN:g2;sm:src=id1|n0" }, new(CtlSync) { Ep = 2, Id = "id2", Cid = "c9", Nonce = "g2" }, new(CtlSync) { Ep = 3, Id = "id3", Cid = "c8", Nonce = "g3" }],
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
        ["HSETC leaves a counter at its maximum and answers 0"] = [Setup, PutH("src", "a=1;ver=9223372036854775807"), new(HSetC) { Fields = "a=2", VersionField = "ver" }],
        ["HDELC leaves a counter at its maximum"] = [Setup, PutH("src", "a=1;b=2;ver=9223372036854775807"), new(HDelC) { Fields = "a=", VersionField = "ver" }],
        ["HSETC with a past expiry removes the key"] = [Setup, PutH("src", "x=1"), new(HSetC) { Fields = "a=1", AtOffset = -1_000 }],
        ["HEXPC with a past expiry removes the key"] = [Setup, PutH("src", "a=1"), new(HExpC) { AtOffset = -1_000 }],
        ["HDELC with a fence"] = [Setup, PutH("src", "a=1;b=2"), new(HDelC) { Fields = "a=", WithFence = true, Nonce = "h1" }],
        ["HSETC on a string key with a fence"] = [Setup, PutS("src", "x"), new(HSetC) { Fields = "a=1", WithFence = true, Nonce = "h1" }],
        ["HDELC on a string key with a fence"] = [Setup, PutS("src", "x"), new(HDelC) { Fields = "a=", WithFence = true, Nonce = "h1" }],
        ["HSETC with a counter that is not canonical"] = [Setup, PutH("src", "a=1;ver=+5"), new(HSetC) { Fields = "a=2", VersionField = "ver" }, PutH("src", "ver=007"), new(HSetC) { Fields = "a=3", VersionField = "ver" }],
        ["HEXPC sets and clears an expiry"] = [Setup, PutH("src", "a=1"), new(HExpC) { AtOffset = 60_000, WithFence = true, Nonce = "e1" }, new(HExpC), new(Delete) { Key = "src" }, new(HExpC) { AtOffset = 60_000 }],
        ["with no record every script refuses"] = [PutS("src", "x"), new(SrcDel), new(SrcSet), new(CmpDel) { Value = "x" }, new(Bump) { Op = "del" }, new(Fill), new(Gate), new(HExpC), new(Pub) { Token = "t" }],
    };

    private static ScriptCall SetMarked(string nonce, long px = -1, bool first = false, long? at = null) =>
        new(Bump) { Op = "setm", MarkerField = M, Nonce = nonce, Value = "n1", Px = px, First = first, AtOffset = at };

    private static ScriptCall Mark(string value) => PutField("ctl", M, value);

    private static ScriptCall PutField(string key, string field, string value) => new(ScriptCallKind.PutField) { Key = key, MarkerField = field, Value = value };

    private static ScriptCall PutS(string key, string value) => new(PutString) { Key = key, Value = value };

    private static ScriptCall PutH(string key, string fields) => new(PutHash) { Key = key, Fields = fields };
}
