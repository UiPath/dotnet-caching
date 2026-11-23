namespace UiPath.Caching.KeyMove;

/// <param name="EverPresent">The node has read a state hash for the group at some time, which makes the group relevant for as long as the node runs.</param>
/// <param name="SinceRead">Age of the last definitive state read; null before the first.</param>
/// <param name="DurZeroFor">How long the node has seen the current identity at <c>dur=0</c>; null when it is durable.</param>
/// <param name="DstReadFailed">An instance group whose latest Dst read failed, so the latest answer is older than the poll that failed.</param>
/// <param name="SampleKnown">A sample read has succeeded at least once, so a null <see cref="Sample"/> means there is no record, not that none could be read.</param>
/// <param name="SampleDurZeroFor">How long the node has seen the sampled identity at <c>dur=0</c>; null when the sample is durable or there is none.</param>
internal sealed record GroupView(
    GroupRegistration Registration,
    StateAnswer Answer,
    StateRecord? State,
    bool RecordFound,
    bool EverPresent,
    ActedState? LastKnown,
    ControlSample? Sample,
    string? Conflict,
    TimeSpan? SinceRead,
    TimeSpan? DurZeroFor,
    TimeSpan SinceStart,
    bool DstReadFailed = false,
    bool SampleKnown = false,
    TimeSpan? SampleDurZeroFor = null);
