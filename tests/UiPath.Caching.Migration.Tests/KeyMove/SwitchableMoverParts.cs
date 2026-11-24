using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration.Tests.KeyMove;

/// <summary>The rate the router's movers take, which a test points at the rate it wants before it starts them.</summary>
internal sealed class SwitchableMoveRate : IMoveRate
{
    private IMoveRate? _inner;

    public IMoveRate Inner
    {
        get => Volatile.Read(ref _inner) ?? throw new InvalidOperationException("The test started the mover without a rate.");
        set => Volatile.Write(ref _inner, value);
    }

    public ValueTask<string?> PauseReasonAsync(string unit, CancellationToken cancellationToken) => Inner.PauseReasonAsync(unit, cancellationToken);

    public TimeSpan Reserve(string unit, int keys, long bytes) => Inner.Reserve(unit, keys, bytes);

    public void Owe(string unit, long bytes) => Inner.Owe(unit, bytes);

    public long TakeDebt(string unit) => Inner.TakeDebt(unit);

    public TimeSpan? Remaining(string unit) => Inner.Remaining(unit);

    public void Observed(string unit, MoveResult result, TimeSpan elapsed) => Inner.Observed(unit, result, elapsed);
}

/// <summary>The schedule point of the router's mover promotions, which a test points at its own hold before it starts the movers.</summary>
internal sealed class SwitchableSchedulePoint : ISchedulePoint
{
    private ISchedulePoint _inner = NoSchedulePoint.Instance;

    public ISchedulePoint Inner
    {
        get => Volatile.Read(ref _inner);
        set => Volatile.Write(ref _inner, value);
    }

    public ValueTask AtAsync(SchedulePointStep step) => Inner.AtAsync(step);
}
