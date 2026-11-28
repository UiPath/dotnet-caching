using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <param name="NoOrigin">The cache declares <c>NoOrigin</c>, which <c>AtMostOnce</c> implies.</param>
/// <param name="InPlaceKey">The caller key to L, in logical text.</param>
/// <param name="NativeKey">K1N: the caller key to N.</param>
/// <param name="MaxHashFields">The most fields a hash may have to be promoted.</param>
/// <param name="Plain">The path of off and of an unmanaged cache.</param>
/// <param name="Mappable">False for a key whose maps do not round trip, which is never promoted.</param>
internal sealed record KeyMoveHashClientParts<T>(
    string Group,
    string CacheId,
    bool NoOrigin,
    Func<string, string> InPlaceKey,
    Func<string, string> NativeKey,
    IKeyMoveHashCodec<T> InPlace,
    IKeyMoveHashCodec<T> Native,
    int MaxHashFields,
    IHashPathClient<T> Plain,
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

    /// <summary>The parts a binding makes: its codecs, key maps and caps.</summary>
    public static KeyMoveHashClientParts<T> From(string group, string cacheId, KeyMoveHashBinding<T> binding, IHashPathClient<T> plain, IKeyMovePlane plane, TimeProvider clock, Func<TimeSpan> settleWithin) =>
        new(group, cacheId, binding.NoOrigin, binding.InPlaceKey, binding.NativeKey, binding.InPlace, binding.Native, binding.MaxHashFields, plain, plane, clock, settleWithin) { Options = binding.Options };
}
