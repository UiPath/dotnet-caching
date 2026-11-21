using System.Text;
using System.Text.Json;

namespace UiPath.Caching.Tests.Fakes;

/// <summary>A custom envelope marked header-free: <c>env:</c> and the JSON; <see cref="Unstable"/> pads every write differently, so no write repeats an earlier one.</summary>
internal sealed class HeaderFreeProxy : ISerializerProxy<byte[]>, IHeaderFreeSerializerProxy
{
    private int _writes;

    public bool Unstable { get; init; }

    public byte[]? Serialize(object? value) =>
        value is null ? null : Encoding.UTF8.GetBytes("env:" + JsonSerializer.Serialize(value) + (Unstable ? new string(' ', Interlocked.Increment(ref _writes)) : string.Empty));

    public T? Deserialize<T>(byte[]? value) => value is null ? default : JsonSerializer.Deserialize<T>(value.AsSpan(4));

    public bool TryDeserialize<T>(string? value, out T? result)
    {
        result = default;
        return false;
    }

    public bool TryDeserialize<T>(object? value, out T? result)
    {
        result = default;
        return false;
    }
}
