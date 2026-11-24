using Microsoft.Extensions.Options;

namespace UiPath.Caching.Migration;

/// <summary>An <see cref="IOptionsMonitor{TOptions}"/> over one value that never changes.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
