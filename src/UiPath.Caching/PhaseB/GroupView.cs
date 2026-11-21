namespace UiPath.Caching.PhaseB;

/// <summary>Everything the rules of a group are chosen from, at one instant.</summary>
/// <param name="SinceRead">Age of the last definitive state read; null before the first.</param>
/// <param name="DurZeroFor">How long the node has seen the current identity at <c>dur=0</c>; null when it is durable.</param>
internal sealed record GroupView(
    GroupRegistration Registration,
    StateAnswer Answer,
    StateRecord? State,
    bool RecordFound,
    ActedState? LastKnown,
    ControlSample? Sample,
    string? Conflict,
    TimeSpan? SinceRead,
    TimeSpan? DurZeroFor,
    TimeSpan SinceStart);
