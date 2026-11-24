using System.Text;
using System.Text.Json;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The value text is the field's bytes and an empty field is a null; a value that starts with "junk:" cannot be read. The native side keeps system fields, as the library does:
// the metadata field holds the user metadata as JSON, or is empty when it stands for an empty hash, and the version field counts the writes.
internal sealed class ItemHashCodec(bool cachesNulls = false, bool systemFields = false, int maxFields = 4096, int maxBytes = 4 * 1024 * 1024) : IKeyMoveHashCodec<Item>
{
    public bool CachesNulls => cachesNulls;

    public bool CarriesMetadata => systemFields;

    public IReadOnlyList<KeyValuePair<string, byte[]>> EncodeFields(IDictionary<string, Item?> fields, IDictionary<string, string?>? metadata)
    {
        var stored = fields.Select(f => new KeyValuePair<string, byte[]>(f.Key, f.Value is null ? [] : Encoding.UTF8.GetBytes(f.Value.Name))).ToList();
        if (systemFields && metadata is not null)
        {
            stored.Add(new("_metadata_", JsonSerializer.SerializeToUtf8Bytes(metadata)));
        }
        else if (systemFields && stored.Count == 0 && cachesNulls)
        {
            stored.Add(new("_metadata_", []));
        }

        return stored;
    }

    public DecodedHash<Item> DecodeFields(IReadOnlyCollection<KeyValuePair<string, byte[]>> stored, bool capped = true)
    {
        var length = stored.Count;
        if (capped && KeyMoveHashSize.Of(length, maxFields) == DecodeClass.TooLarge)
        {
            return DecodedHash<Item>.Of(DecodeClass.TooLarge, length);
        }

        var bytes = stored.Sum(e => (long)Encoding.UTF8.GetByteCount(e.Key) + e.Value.Length);
        var fields = new Dictionary<string, Item?>(StringComparer.Ordinal);
        IDictionary<string, string?>? metadata = null;
        long version = 0;
        foreach (var (name, value) in stored)
        {
            if (name == "_metadata_")
            {
                metadata = value.Length == 0 ? null : JsonSerializer.Deserialize<Dictionary<string, string?>>(value);
                continue;
            }

            if (name == "_version_")
            {
                version = long.Parse(Encoding.UTF8.GetString(value), System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }

            var text = Encoding.UTF8.GetString(value);
            if (text.StartsWith("junk:", StringComparison.Ordinal))
            {
                return DecodedHash<Item>.Of(DecodeClass.Unreadable, length, bytes);
            }

            fields[name] = value.Length == 0 ? null : new Item(text);
        }

        if (fields.Count == 0)
        {
            return DecodedHash<Item>.Of(DecodeClass.Null, length, bytes);
        }

        return new DecodedHash<Item>(capped && KeyMoveHashSize.ExceedsBytes(bytes, maxBytes) ? DecodeClass.TooLarge : DecodeClass.Ok, fields, metadata, version, length, bytes);
    }
}
