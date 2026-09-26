using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace UiPath.Caching.Telemetry;

/// <summary>Times one cache operation and reports it; a struct, so an operation allocates nothing for its telemetry.</summary>
/// <remarks>It lives in the caller's async state machine, so it carries only a shared target and two timestamps.</remarks>
public struct TelemetryScope
{
    public const string DependencyType = "Redis";
    public const string OutcomeTag = "Outcome";
    public const string ProviderTag = "Provider";
    public const string MethodTag = "Method";
    public const string TypeTag = "Type";
    public const string KeysTag = "Keys";
    public const string BatchIdTag = "BatchId";
    private const string Prefix = "Caching.Stats.";
    private const string Hits = Prefix + "Hits.";
    private const string Misses = Prefix + "Misses.";
    private const string HitOutcome = "Hit";
    private const string MissOutcome = "Miss";

    // One target per metric name each provider emits; weak on the provider, and keyed by the type's name so a collectible type is not rooted.
    private static readonly ConditionalWeakTable<ICachingTelemetryProvider, ConcurrentDictionary<(TimeProvider Clock, string Provider, string Method, string TypeName), Target>> Targets = [];

    private readonly Target? _target;
    private readonly long _startTimestamp;

    // 0 while running; the elapsed timestamp ticks plus one once stopped.
    private long _stoppedElapsed;

    /// <summary>Starts timing; with <see cref="NullTelemetryProvider"/> the scope does nothing.</summary>
    public TelemetryScope(ICachingTelemetryProvider provider, TimeProvider clock, string providerName, string callerMethod, Type cacheObjectType)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (provider is null or NullTelemetryProvider)
        {
            return;
        }

        _target = Targets.GetValue(provider, static _ => new())
            .GetOrAdd((clock, providerName, callerMethod, cacheObjectType.Name), static (scope, provider) => new Target(provider, scope.Clock, scope.Provider, scope.Method, scope.TypeName), provider);
        _startTimestamp = clock.GetTimestamp();
    }

    public readonly bool IsEnabled => _target is not null;

    /// <summary>Freezes the duration; later calls keep the first.</summary>
    public void Stop()
    {
        if (_target is null || _stoppedElapsed > 0)
        {
            return;
        }
        _stoppedElapsed = _target.Clock.GetTimestamp() - _startTimestamp + 1;
    }

    public readonly void Track(bool hit, int keyCount = 1)
    {
        if (_target is not { } target)
        {
            return;
        }
        target.Provider.TrackMetric(hit ? target.HitName : target.MissName, Elapsed(target).TotalMilliseconds, [new(KeysTag, keyCount.ToString(CultureInfo.InvariantCulture))]);
    }

    public readonly void TrackKeyReads((string Key, bool Hit)[] reads)
    {
        if (_target is not { } target)
        {
            return;
        }

        var elapsed = Elapsed(target);
        // The start instant, read back from the monotonic time since it rather than a wall-clock read per operation.
        var startTime = target.Clock.GetUtcNow() - target.Clock.GetElapsedTime(_startTimestamp);
        var batchId = Guid.NewGuid().ToString();
        var hitProperties = Properties(target, true, batchId);
        var missProperties = Properties(target, false, batchId);
        foreach (var (key, hit) in reads)
        {
            target.Provider.TrackDependency(
                type: DependencyType,
                target: target.ProviderName,
                name: target.Method,
                data: key,
                startTime: startTime,
                duration: elapsed,
                resultCode: hit ? HitOutcome : MissOutcome,
                success: true,
                properties: hit ? hitProperties : missProperties);
        }
    }

    private static KeyValuePair<string, string>[] Properties(Target target, bool hit, string batchId) =>
    [
        new(OutcomeTag, hit ? HitOutcome : MissOutcome),
        new(ProviderTag, target.ProviderName),
        new(MethodTag, target.Method),
        new(TypeTag, target.TypeName),
        new(BatchIdTag, batchId),
    ];

    private readonly TimeSpan Elapsed(Target target) =>
        target.Clock.GetElapsedTime(0, _stoppedElapsed > 0 ? _stoppedElapsed - 1 : target.Clock.GetTimestamp() - _startTimestamp);

    private sealed class Target(ICachingTelemetryProvider provider, TimeProvider clock, string providerName, string method, string typeName)
    {
        public ICachingTelemetryProvider Provider { get; } = provider;

        public TimeProvider Clock { get; } = clock;

        public string ProviderName { get; } = providerName;

        public string Method { get; } = method;

        public string TypeName { get; } = typeName;

        public string HitName { get; } = $"{Hits}{providerName}.{method}.{typeName}";

        public string MissName { get; } = $"{Misses}{providerName}.{method}.{typeName}";
    }
}
