using Microsoft.Extensions.Options;

namespace UiPath.Caching.Migration;

/// <summary>An options monitor that keeps the last value that validated, so a bad reload changes nothing.</summary>
internal sealed class LastGoodOptions : IOptionsMonitor<KeyMoveMoverOptions>
{
    private readonly IOptionsMonitor<KeyMoveMoverOptions> _inner;
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private KeyMoveMoverOptions? _good;

    private LastGoodOptions(IOptionsMonitor<KeyMoveMoverOptions> inner) => _inner = inner;

    public KeyMoveMoverOptions CurrentValue
    {
        get
        {
            // Read and kept under one lock, so a reader that read older options cannot store them over the snapshot of a newer one.
#if NET9_0_OR_GREATER
            if (!_gate.TryEnter())
#else
            if (!Monitor.TryEnter(_gate))
#endif
            {
                Contended?.Invoke();
#if NET9_0_OR_GREATER
                _gate.Enter();
#else
                Monitor.Enter(_gate);
#endif
            }

            try
            {
                return _good = _inner.CurrentValue;
            }
            catch (OptionsValidationException)
            {
                return _good ??= new KeyMoveMoverOptions();
            }
            finally
            {
#if NET9_0_OR_GREATER
                _gate.Exit();
#else
                Monitor.Exit(_gate);
#endif
            }
        }
    }

    /// <summary>Test seam: signals when a reader has to wait for the current read to finish.</summary>
    internal Action? Contended { get; set; }

    /// <summary>Wraps <paramref name="options"/>; one that is already wrapped is returned as it is.</summary>
    public static LastGoodOptions Over(IOptionsMonitor<KeyMoveMoverOptions> options) =>
        options as LastGoodOptions ?? new LastGoodOptions(options ?? throw new ArgumentNullException(nameof(options)));

    public KeyMoveMoverOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<KeyMoveMoverOptions, string?> listener) => _inner.OnChange(listener);
}
