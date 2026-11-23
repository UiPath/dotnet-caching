namespace UiPath.Caching.KeyMove;

/// <param name="EverPresent">The node has read a state hash for the group at some time, which makes the group relevant for as long as the node runs.</param>
/// <param name="SinceRead">Age of the last definitive state read; null before the first.</param>
/// <param name="DurZeroFor">How long the node has seen the current identity at <c>dur=0</c>; null when it is durable.</param>
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
    TimeSpan SinceStart);
