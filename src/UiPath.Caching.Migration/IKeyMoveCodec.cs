namespace UiPath.Caching.Migration;

/// <summary>Bytes to value and back for one side of a cache: the in-place format for L, the policy's effective format for N. No type name is read from Redis.</summary>
internal interface IKeyMoveCodec<T>
    where T : class
{
    byte[] Encode(T value);

    /// <summary>Encodes a value and hands the caller the format lease to hold through the write.</summary>
    byte[] Encode(T value, out WriteLease lease)
    {
        lease = default;
        return Encode(value);
    }

    Decoded<T> Decode(byte[] bytes);

    /// <summary>The bytes of a cached null on this side, or null when the side does not cache nulls.</summary>
    byte[]? EncodeNull();
}
