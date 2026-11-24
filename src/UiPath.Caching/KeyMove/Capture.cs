using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>The outcome of CAP: <see cref="Code"/> 0 with the token in force, -1 for another identity, -2 for a closed or absent gate, -4 for a live supersession marker.</summary>
/// <param name="Marker">The marker value of a -4 reply, which has the shape <c>{-4, marker}</c>; empty for every other code.</param>
internal readonly record struct Capture(long Code, string? Token, bool DestinationExists, string? Gate, long Tcap, RedisValue Marker = default)
{
    public bool Captured => Code == 0;
}
