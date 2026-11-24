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
            lock (_gate)
            {
                try
                {
                    return _good = _inner.CurrentValue;
                }
                catch (OptionsValidationException)
                {
                    return _good ??= new KeyMoveMoverOptions();
                }
            }
        }
    }

    /// <summary>Wraps <paramref name="options"/>; one that is already wrapped is returned as it is.</summary>
    public static LastGoodOptions Over(IOptionsMonitor<KeyMoveMoverOptions> options) =>
        options as LastGoodOptions ?? new LastGoodOptions(options ?? throw new ArgumentNullException(nameof(options)));

    public KeyMoveMoverOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<KeyMoveMoverOptions, string?> listener) => _inner.OnChange(listener);
}
