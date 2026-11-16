using System.Buffers;
using System.Runtime.ExceptionServices;
using MessagePack;
using MessagePack.Resolvers;

namespace UiPath.Caching.MessagePack;

internal sealed class MessagePackValueSerializer : ICacheValueSerializer
{
    // MessagePack's reserved extension type for an LZ4 block array: the extension holds each block's uncompressed length.
    private const sbyte Lz4BlockArrayTypeCode = 98;

    // LZ4 cannot expand a block by more than this factor, so a larger claim is corrupt input, not data.
    private const long MaxExpansion = 255;

    private static readonly MessagePackSerializerOptions Default =
        ContractlessStandardResolver.Options.WithSecurity(ResilientMessagePackSecurity.Instance);

    private readonly MessagePackSerializerOptions _plain;
    private readonly MessagePackSerializerOptions _compressing;
    private readonly MessagePackSerializerOptions _compressRaw;
    private readonly int _threshold;
    private readonly int _maxDecompressedBytes;

    public MessagePackValueSerializer(MessagePackValueSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.CompressionThresholdBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxDecompressedBytes);

        var user = options.SerializerOptions ?? Default;
        if (ReferenceEquals(user.Security, MessagePackSecurity.TrustedData))
        {
            user = user.WithSecurity(ResilientMessagePackSecurity.Instance);
        }

        _plain = user.WithCompression(MessagePackCompression.None);
        _threshold = Math.Max(options.CompressionThresholdBytes, 1);
        _maxDecompressedBytes = options.MaxDecompressedBytes;
        _compressing = _plain
            .WithCompression(MessagePackCompression.Lz4BlockArray)
            .WithCompressionMinLength(1)
            .WithSecurity(_plain.Security.WithMaximumDecompressedSize(options.MaxDecompressedBytes));
        _compressRaw = _compressing.WithResolver(RawMessagePackResolver.Instance);
    }

    public IReadOnlyList<int> FormatIds { get; } = [CacheValueFormats.MessagePack, CacheValueFormats.MessagePackLz4];

    internal MessagePackSerializerOptions Options => _plain;

    public int Serialize<T>(T value, IBufferWriter<byte> target)
    {
        if (_threshold == int.MaxValue)
        {
            try
            {
                MessagePackSerializer.Serialize(target, value, _plain);
            }
            catch (MessagePackSerializationException e)
            {
                RethrowOutOfMemory(e);
                throw;
            }

            return CacheValueFormats.MessagePack;
        }

        using var plain = new PooledBufferWriter();
        try
        {
            MessagePackSerializer.Serialize(plain, value, _plain);
        }
        catch (MessagePackSerializationException e)
        {
            RethrowOutOfMemory(e);
            throw;
        }

        var plainBytes = plain.WrittenMemory;
        // A value above the read limit would be stored compressed and then never read back, so it stays plain.
        if (plainBytes.Length >= _threshold && plainBytes.Length <= _maxDecompressedBytes)
        {
            // MessagePack keeps a compressed result even when it is larger, so the sizes decide which form is stored.
            using var compressed = new PooledBufferWriter();
            try
            {
                MessagePackSerializer.Serialize(compressed, new RawMessagePack(plainBytes), _compressRaw);
            }
            catch (MessagePackSerializationException e)
            {
                RethrowOutOfMemory(e);
                throw;
            }

            if (compressed.WrittenMemory.Length < plainBytes.Length)
            {
                target.Write(compressed.WrittenMemory.Span);
                return CacheValueFormats.MessagePackLz4;
            }
        }

        target.Write(plainBytes.Span);
        return CacheValueFormats.MessagePack;
    }

    public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source)
    {
        try
        {
            return DeserializeCore<T>(formatId, source);
        }
        catch (MessagePackSerializationException e)
        {
            RethrowOutOfMemory(e);
            throw;
        }
    }

    // MessagePack wraps an allocation failure; a cache must not read it as a miss and run the generator under memory pressure.
    private static void RethrowOutOfMemory(Exception e)
    {
        for (var inner = e.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is OutOfMemoryException)
            {
                ExceptionDispatchInfo.Capture(inner).Throw();
            }
        }
    }

    private static bool TryReadCompressedHeader(ReadOnlySequence<byte> bytes, out long plainLength)
    {
        plainLength = 0;
        try
        {
            var reader = new MessagePackReader(bytes);
            if (reader.NextMessagePackType != MessagePackType.Array)
            {
                return false;
            }

            var blocks = reader.ReadArrayHeader() - 1;
            if (blocks < 1 || reader.NextMessagePackType != MessagePackType.Extension)
            {
                return false;
            }

            var header = reader.ReadExtensionFormatHeader();
            if (header.TypeCode != Lz4BlockArrayTypeCode)
            {
                return false;
            }

            var lengths = new MessagePackReader(reader.ReadRaw(header.Length));
            for (var i = 0; i < blocks; i++)
            {
                var blockLength = lengths.ReadInt64();
                if (blockLength < 0)
                {
                    return false;
                }

                plainLength = checked(plainLength + blockLength);
            }

            return lengths.End && reader.NextMessagePackType == MessagePackType.Binary;
        }
        catch (Exception e) when (e is MessagePackSerializationException or EndOfStreamException or OverflowException or InvalidOperationException)
        {
            return false;
        }
    }

    private T? DeserializeCore<T>(int formatId, ReadOnlySequence<byte> source)
    {
        switch (formatId)
        {
            case CacheValueFormats.MessagePack:
                return MessagePackSerializer.Deserialize<T>(source, _plain);
            case CacheValueFormats.MessagePackLz4:
                if (!TryReadCompressedHeader(source, out var plainLength))
                {
                    throw new InvalidDataException("A value stored as MessagePack with LZ4 does not hold a compressed block array.");
                }

                if (plainLength > source.Length * MaxExpansion || plainLength > _maxDecompressedBytes)
                {
                    throw new InvalidDataException($"A compressed value of {source.Length} bytes claims {plainLength} bytes uncompressed.");
                }

                return MessagePackSerializer.Deserialize<T>(source, _compressing);
            default:
                throw new InvalidOperationException($"The MessagePack serializer does not own format id 0x{formatId:X2}.");
        }
    }
}
