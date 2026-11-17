using System.Buffers;
using System.IO.Compression;
using System.Text.Json;

namespace UiPath.Caching.PluginContract.Tests.Acme;

/// <summary>JSON on its first id, gzipped JSON on its second once the payload is large; the non-deterministic variant prefixes a random nonce.</summary>
public sealed class AcmeValueSerializer(bool deterministic = true, IReadOnlyList<int>? ids = null) : ICacheValueSerializer, IDisposable
{
    public const int GzipThreshold = 256;
    private const int NonceBytes = 8;

    private int _disposals;
    private int _serialized;

    public int Disposals => _disposals;

    public int Serialized => _serialized;

    public IReadOnlyList<int> FormatIds { get; } = ids ?? AcmeFormats.ValueIds;

    public int Serialize<T>(T value, IBufferWriter<byte> target)
    {
        Interlocked.Increment(ref _serialized);
        var json = JsonSerializer.SerializeToUtf8Bytes(value);
        var gzip = json.Length >= GzipThreshold && FormatIds.Count > 1;
        if (!deterministic)
        {
            Random.Shared.NextBytes(target.GetSpan(NonceBytes)[..NonceBytes]);
            target.Advance(NonceBytes);
        }

        if (!gzip)
        {
            target.Write(json);
            return FormatIds[0];
        }

        using var compressed = new MemoryStream();
        using (var zip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zip.Write(json);
        }

        target.Write(compressed.ToArray());
        return FormatIds[1];
    }

    public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source)
    {
        if (!FormatIds.Contains(formatId))
        {
            throw new InvalidOperationException($"acme does not own format id {formatId}.");
        }

        var bytes = source.ToArray();
        if (!deterministic)
        {
            bytes = bytes[NonceBytes..];
        }

        if (formatId == FormatIds[^1] && FormatIds.Count > 1)
        {
            using var zip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var plain = new MemoryStream();
            zip.CopyTo(plain);
            bytes = plain.ToArray();
        }

        return JsonSerializer.Deserialize<T>(bytes);
    }

    public void Dispose() => Interlocked.Increment(ref _disposals);
}
