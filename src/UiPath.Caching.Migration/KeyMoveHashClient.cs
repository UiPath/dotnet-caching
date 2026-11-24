using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>The library path of a managed hash cache while its group is in key move: the forms for the whole hash and for its fields, one admitted unit at a time, with the plain path used for S0 and for a cache the state does not manage.</summary>
internal sealed partial class KeyMoveHashClient<T> : IHashPathClient<T>, IDisposable
    where T : class
{
    private const CommandFlags Master = CommandFlags.DemandMaster;

    private readonly KeyMoveHashClientParts<T> _p;
    private readonly KeyMoveOptions _options;
    private readonly NegativeHints _hints;
    private readonly MoveRetrySet _retries;
    private readonly MarkerNode _node;
    private readonly SupersessionHelper _helper;
    private readonly HashReads<T> _reads;
    private readonly HashPromotion<T> _promotion;
    private readonly HashPromotion<T> _reversePromotion;

    public KeyMoveHashClient(KeyMoveHashClientParts<T> parts)
    {
        _p = parts ?? throw new ArgumentNullException(nameof(parts));
        _options = parts.Options;
        _hints = new NegativeHints(parts.Clock, _options.HintTtl, _options.MaxHints);
        _retries = new MoveRetrySet(parts.Clock, _options, ExhaustedStep, parts.RunRetriesInBackground, _ => Count("retry_superseded"));
        _node = MarkerNode.For(parts.Plane, parts.Clock, _options, parts.RunRetriesInBackground);
        _helper = new SupersessionHelper(() => _p.Plane.Data, parts.InPlaceKey, parts.NativeKey, parts.Group, parts.CacheId);
        _reads = new HashReads<T>(parts);
        _promotion = new HashPromotion<T>(parts, _reads, _hints, _helper);
        _reversePromotion = new HashPromotion<T>(parts, _reads, _hints, _helper, reverse: true);
    }

    public TimeSpan? SettleWithin => _p.SettleWithin();

    public bool NoFallback => Moving;

    public bool KeepsStoredOnFill => true;

    public IReadOnlyCollection<string> InFlightKeys => _retries.OutstandingKeys;

    public bool Moving => PlanFor(write: false).Mode != KeyMoveMode.Plain || PlanFor(write: true).Mode != KeyMoveMode.Plain;

    internal KeyMoveHashClientParts<T> Parts => _p;

    internal MoveRetrySet Retries => _retries;

    internal UnmarkQueue Unmarks => _node.Unmarks;

    internal WritePermits Permits => _node.Permits;

    /// <summary>The helper that answers a live marker for this cache; the mover takes it as its supersession handler.</summary>
    internal ISupersessionHandler Supersession => _helper;

    /// <summary>The one promotion of a hash from L to N, which a mover calls too.</summary>
    internal HashPromotion<T> Promotion => _promotion;

    internal NegativeHints Hints => _hints;

    private KeyMoveData? Data => _p.Plane.Data;

    private long FenceTtlMs => (long)_options.FenceTtl.TotalMilliseconds;

    public Task SettledAsync(string key) => _retries.SettledAsync(key) ?? PathClientTasks.NoPendingRetry;

    public Task StopRetriesAsync() => _retries.StopAsync();

    public void KeyChanged(string key) => _hints.Drop(key);

    public void Gap() => _hints.Clear();

    public void Dispose()
    {
        _retries.Dispose();
    }

    internal KeyMovePlan PlanFor(bool write) => KeyMovePlanning.PlanFor(_p.Plane, _p.Group, _p.CacheId, write);

    private static RedisKey Key(string text) => text;

    private string InPlace(string key) => _p.InPlaceKey(key);

    private string Native(string key) => _p.NativeKey(key);

    private RedisKey ControlOf(KeyMoveData data, string dataKey) => data.Names.ControlFor(_p.Group, dataKey);

    private FencedKeys Fenced(KeyMoveData data, string n) => new(data.Names.ForwardFence(n), Key(n), ControlOf(data, n));

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _p.Clock.TimestampFrequency);

    private ValueTask At(SchedulePointStep step) => _p.Schedule.AtAsync(step);

    private void Count(string counter) => RouterTelemetry.CountMove(counter, _p.CacheId, _p.Group);

    private void ExhaustedStep(string key)
    {
        Count("retry_exhausted");
        _p.Plane.Alert(_p.Group, "retry_exhausted", $"{_p.CacheId}:{key}");
    }

    private CacheUnavailableException Unavailable() => new($"Cache '{_p.CacheId}' is closed for writes while its group moves.");

    private WriteResult Typed(WriteOutcome failed) => failed == WriteOutcome.Unknown ? WriteResult.UnknownWithin(_p.SettleWithin()) : failed;

    /// <returns>The unit, or null when none can be admitted; Replan when the gate holds another identity than the rules the caller planned under, in which case the whole operation is built again.</returns>
    private (AdmittedUnit? Unit, bool Replan) Admit(bool promotion, string? expectedId)
    {
        var unit = _p.Plane.Enter(_p.Group, promotion);
        if (unit is null || expectedId is null || unit.Id == expectedId)
        {
            return (unit, false);
        }

        // A plan made under an older state is never run with a newer identity.
        unit.Dispose();
        return (null, true);
    }

    // A write that waits for a new snapshot does so up to the time the rules left, then it is closed.
    private async ValueTask<KeyMovePlan> SettleWaitAsync(KeyMovePlan plan, CancellationToken cancellationToken)
    {
        if (plan.Mode != KeyMoveMode.Wait)
        {
            return plan;
        }

        var until = _p.Clock.GetTimestamp() + Ticks(plan.Rules.WriteWait ?? TimeSpan.Zero);
        while (plan.Mode == KeyMoveMode.Wait && _p.Clock.GetTimestamp() < until)
        {
            await Task.Delay(_options.AdmissionPoll, _p.Clock, cancellationToken).ConfigureAwait(false);
            plan = PlanFor(write: true);
        }

        return plan.Mode == KeyMoveMode.Wait ? plan with { Mode = KeyMoveMode.Closed } : plan;
    }
}
