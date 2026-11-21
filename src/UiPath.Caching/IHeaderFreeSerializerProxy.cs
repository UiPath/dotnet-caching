namespace UiPath.Caching;

/// <summary>Marks a byte serializer proxy whose output never starts with a format header byte, so its values and headered ones share a key space.</summary>
/// <remarks>
/// A header byte is 0x01 to 0x1F except tab, line feed and carriage return. With this marker a read tells the proxy's bytes from a registered format by the
/// first byte, the node advertises that it reads both, and a rewrite compares the proxy's bytes exactly, so a value converts only when the proxy writes it back
/// unchanged. The default JSON proxy needs no marker.
/// </remarks>
public interface IHeaderFreeSerializerProxy
{
}
