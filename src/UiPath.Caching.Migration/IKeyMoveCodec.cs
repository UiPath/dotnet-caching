namespace UiPath.Caching.Migration;

/// <summary>Bytes to value and back for one side of a cache: the in-place format for L, the policy's effective format for N. No type name is read from Redis.</summary>
internal interface IKeyMoveCodec<T>
    where T : class
{
    byte[] Encode(T value);

    Decoded<T> Decode(byte[] bytes);
}
