namespace UiPath.Caching.Tests.KeyMove.Harness;

internal enum ScriptCallKind
{
    PutString,
    PutHash,
    Delete,
    ExpireIn,
    Cap,
    Pub,
    PubHash,
    Bump,
    Fill,
    FillHash,
    SrcDel,
    SrcSet,
    SrcExp,
    CmpDel,
    CmpDelHash,
    CtlSync,
    CtlCommit,
    Gate,
    HSetC,
    HDelC,
    HExpC,
}
