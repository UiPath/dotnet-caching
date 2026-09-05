namespace UiPath.Caching;

/// <summary>
/// Refinement of <see cref="ISerializerProxy{T1}"/> over <c>byte[]</c> for a serializer that can hand back
/// memory without materializing an array — in particular a caller's own <see cref="ReadOnlyMemory{T}"/>,
/// returned as-is. The Redis tier asks for this when the serializer offers it, so a buffer a caller already
/// holds reaches the wire with no array in between; every other consumer keeps using
/// <see cref="ISerializerProxy{T1}.Serialize"/>.
/// </summary>
/// <remarks>
/// <para>The memory returned is borrowed: it may alias the value it was produced from, and it is valid only
/// until the operation that asked for it completes. Consumers therefore never retain it — the Redis tier
/// copies it into the connection's buffer when the command is written, and the write is awaited to
/// completion before the caller is told it is done. That last part is what makes the arrangement safe, and
/// it is a property of the resilience pipeline as much as of the cache: see
/// <see cref="Policies.IResiliencePipeline"/>.</para>
/// <para>The memory-backed tiers keep whatever value they are handed, so nothing there goes through this
/// interface; a caller writing a borrowed buffer to such a tier copies it first.</para>
/// </remarks>
public interface IMemorySerializerProxy : ISerializerProxy<byte[]>
{
    /// <summary>
    /// Serializes <paramref name="value"/> to borrowed memory. Generic rather than taking <see cref="object"/>
    /// so a struct value — <see cref="ReadOnlyMemory{T}"/> itself — is not boxed on the way in.
    /// </summary>
    ReadOnlyMemory<byte> SerializeToMemory<T>(T? value);
}
