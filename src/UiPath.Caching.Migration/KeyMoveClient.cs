using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>Handles managed cache operations during key move, one admitted unit at a time. S0 and unmanaged caches use the router's per-group path.</summary>
internal sealed partial class KeyMoveClient<T> : IPathClient<T>, IDisposable
    where T : class
{
    private const CommandFlags Master = CommandFlags.DemandMaster;
    private const string IdentityRerun = "identity_rerun";
    private const string RetrySuperseded = "retry_superseded";
    private const string RetryExhausted = "retry_exhausted";

    private readonly KeyMoveClientParts<T> _p;
    private readonly KeyMoveOptions _options;
    private readonly NegativeHints _hints;
    private readonly MoveRetrySet _retries;
    private readonly IMoveSide<T> _source;
    private readonly MarkerNode _node;
    private readonly SupersessionHelper _helper;
    private readonly KeyPromotion<T> _promotion;

    public KeyMoveClient(KeyMoveClientParts<T> parts)
    {
        _p = parts ?? throw new ArgumentNullException(nameof(parts));
        _options = parts.Options;
        _source = parts.Source ?? new InPlaceMoveSide<T>(parts.Plane, parts.Group, parts.InPlaceKey, parts.InPlace, parts.Mappable, parts.Clock);
        _hints = new NegativeHints(parts.Clock, _options.HintTtl, _options.MaxHints);
        _retries = new MoveRetrySet(parts.Clock, _options, ExhaustedStep, parts.RunRetriesInBackground, _ => Count(RetrySuperseded));
        _node = MarkerNode.For(parts.Plane, parts.Clock, _options, parts.RunRetriesInBackground);
        _helper = new SupersessionHelper(() => _p.Plane.Data, parts.InPlaceKey, parts.NativeKey, parts.Group, parts.CacheId);
        _promotion = new KeyPromotion<T>(parts, _source, _helper);
    }

    public TimeSpan? SettleWithin => _p.SettleWithin();

    public bool NoFallback => PlanFor(write: false).Mode != KeyMoveMode.Plain || PlanFor(write: true).Mode != KeyMoveMode.Plain;

    public bool KeepsStoredOnFill => true;

    public IReadOnlyCollection<string> InFlightKeys => _retries.OutstandingKeys;

    public bool Moving => PlanFor(write: false).Mode != KeyMoveMode.Plain || PlanFor(write: true).Mode != KeyMoveMode.Plain;

    public ILibraryBatch<T>? PlainBatch => Moving ? null : _p.Plain.PlainBatch;

    internal MoveRetrySet Retries => _retries;

    internal UnmarkQueue Unmarks => _node.Unmarks;

    internal WritePermits Permits => _node.Permits;

    /// <summary>The helper that answers a live marker for this cache; the mover takes it as its supersession handler.</summary>
    internal ISupersessionHandler Supersession => _helper;

    internal NegativeHints Hints => _hints;

    private IMoveSide<T> Source => _source;

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

    internal KeyMovePlan PlanFor(bool write)
    {
        var rules = _p.Plane.RulesFor(_p.Group);
        if (!rules.Relevant || !rules.IsManaged(_p.CacheId))
        {
            return new KeyMovePlan(KeyMoveMode.Plain, rules);
        }

        return new KeyMovePlan(write ? WriteMode(rules) : ReadMode(rules), rules);
    }

    private static KeyMoveMode WriteMode(GroupRules rules)
    {
        if (rules.Writes != WriteRule.Follow)
        {
            return rules.Writes == WriteRule.WaitThenFailClosed ? KeyMoveMode.Wait : KeyMoveMode.Closed;
        }

        return rules.Acting switch
        {
            null or { State: "S0" } => KeyMoveMode.Plain,
            { Direction: "rev" } => KeyMoveMode.Closed,
            { State: var state } => ModeOf(state, KeyMoveMode.Closed),
        };
    }

    private static KeyMoveMode ModeOf(string state, KeyMoveMode otherwise) => state switch
    {
        "S1" => KeyMoveMode.S1,
        "S2" => KeyMoveMode.S2,
        "S3" => KeyMoveMode.S3,
        "S4" => KeyMoveMode.S4,
        "S5" => KeyMoveMode.S5,
        _ => otherwise,
    };

    private static RedisKey Key(string text) => text;

    private KeyMoveMode ReadMode(GroupRules rules)
    {
        if (rules.Reads == ReadRule.UnionNativeFirst)
        {
            return KeyMoveMode.Union;
        }

        if (rules.Acting is null or { State: "S0" })
        {
            return KeyMoveMode.Plain;
        }

        var act = rules.Acting;
        if (act.Direction == "rev")
        {
            return KeyMoveMode.Union;
        }

        var mode = ModeOf(act.State, KeyMoveMode.Union);
        if (mode is KeyMoveMode.S1 or KeyMoveMode.S2 && act.NPop != 0)
        {
            return KeyMoveMode.Union;
        }

        if (mode == KeyMoveMode.S4)
        {
            return _p.Plane.KeepsSourceFallback(_p.Group, _p.CacheId) ? KeyMoveMode.Union : KeyMoveMode.S5;
        }

        return mode;
    }

    private string Native(string key) => _p.NativeKey(key);

    private RedisKey ControlOf(KeyMoveData data, string dataKey) => data.Names.ControlFor(_p.Group, dataKey);

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _p.Clock.TimestampFrequency);

    private ValueTask At(SchedulePointStep step) => _p.Schedule.AtAsync(step);

    private bool SourceUnavailable(bool required) => required && !Source.IsAvailable;

    private void Count(string counter) => RouterTelemetry.CountMove(counter, _p.CacheId, _p.Group);

    private void ExhaustedStep(string key)
    {
        Count(RetryExhausted);
        _p.Plane.Alert(_p.Group, RetryExhausted, $"{_p.CacheId}:{key}");
    }

    /// <returns>The unit, or null when none can be admitted; Replan when the gate holds another identity than the rules the caller planned under, in which case the whole operation is built again.</returns>
    private Task<(AdmittedUnit? Unit, bool Replan)> AdmitAsync(bool promotion, string? expectedId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var unit = _p.Plane.Enter(_p.Group, promotion);
        if (unit is null || expectedId is null || unit.Id == expectedId)
        {
            return Task.FromResult<(AdmittedUnit?, bool)>((unit, false));
        }

        // A plan made under an older state is never run with a newer identity.
        unit.Dispose();
        return Task.FromResult<(AdmittedUnit?, bool)>((null, true));
    }

}
