using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <param name="NoOrigin">The cache declares <c>NoOrigin</c>, which <c>AtMostOnce</c> implies.</param>
/// <param name="InPlaceKey">The caller key mapped to L as logical text.</param>
/// <param name="NativeKey">The caller key mapped to N.</param>
/// <param name="Plain">The path of S0 and of an unmanaged cache.</param>
/// <param name="Mappable">False for a key whose maps do not round trip, which is never promoted.</param>
internal sealed record KeyMoveClientParts<T>(
    string Group,
    string CacheId,
    bool NoOrigin,
    Func<string, string> InPlaceKey,
    Func<string, string> NativeKey,
    IKeyMoveCodec<T> InPlace,
    IKeyMoveCodec<T> Native,
    IPathClient<T> Plain,
    IKeyMovePlane Plane,
    TimeProvider Clock,
    Func<TimeSpan> SettleWithin)
    where T : class
{
    public KeyMoveOptions Options { get; init; } = new();

    /// <summary>False leaves the retries to the caller of <c>RunDueAsync</c>.</summary>
    public bool RunRetriesInBackground { get; init; } = true;

    public ISchedulePoint Schedule { get; init; } = NoSchedulePoint.Instance;

    public Func<string, bool>? Mappable { get; init; }
}
