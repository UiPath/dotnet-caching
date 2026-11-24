using System.Text;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// Each side's bytes carry its own tag, so a test sees which format a step wrote; the in-place side cannot hold one value.
internal sealed class TaggedItemCodec(string tag) : IKeyMoveCodec<Item>
{
    public const string Unencodable = "too-wide";

    public byte[] Encode(Item value) =>
        tag == "l:" && value.Name == Unencodable ? throw new InvalidOperationException("The in-place format cannot hold this value.") : Encoding.UTF8.GetBytes(tag + value.Name);

    public byte[]? EncodeNull() => null;

    public Decoded<Item> Decode(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text.StartsWith(tag, StringComparison.Ordinal)
            ? Decoded<Item>.Of(new Item(text[tag.Length..]))
            : new Decoded<Item>(DecodeClass.Unreadable, null);
    }
}
