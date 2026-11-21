namespace UiPath.Caching.PhaseB;

/// <summary>Names the Redis command that runs next, so a schedule point can reorder it.</summary>
internal enum SchedulePointStep : byte
{
    ReadDestination,
    ReadDestinationOnMaster,
    ProbeSource,
    Capture,
    ReadSource,
    Publish,
    ConfirmDestination,
    CompareDelete,
    SourceDelete,
    SourceSet,
    Bump,
    Fill,
    StageHash,
    GateClose,
    GateVerify,
    GateReopen,
    Sweep,
    SourceExpire,
    HashWrite,
    HashDelete,
    HashExpire,
    ControlSync,
    ControlCommit,
}
