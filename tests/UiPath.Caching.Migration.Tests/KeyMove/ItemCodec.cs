using System.Text;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The version text is the value; the prefixes pick a decode class.
internal sealed class ItemCodec : IKeyMoveCodec<Item>
{
    public static ItemCodec Instance { get; } = new();

    public byte[] Encode(Item value) => Encoding.UTF8.GetBytes(value.Name);

    public Decoded<Item> Decode(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (text.StartsWith("junk:", StringComparison.Ordinal))
        {
            return new Decoded<Item>(DecodeClass.Unreadable, null);
        }

        if (text.StartsWith("exp:", StringComparison.Ordinal))
        {
            return new Decoded<Item>(DecodeClass.Expired, null);
        }

        return text.StartsWith("big:", StringComparison.Ordinal) ? new Decoded<Item>(DecodeClass.TooLarge, new Item(text)) : Decoded<Item>.Of(new Item(text));
    }
}
