using System.Text.Json;
#if !NET9_0_OR_GREATER
using System.Text.Json.Nodes;
#endif

namespace UiPath.Caching.Serialization;

/// <summary>Whether a value read and written again says what was stored.</summary>
internal static class LosslessCheck
{
    /// <summary>JSON compares as documents: property order, layout and the spelling of a number do not matter, anything gained or lost does; text that does not parse, or repeats a property name, is lossy.</summary>
    public static bool Json(ReadOnlySpan<byte> original, ReadOnlySpan<byte> reencoded)
    {
        try
        {
#if NET9_0_OR_GREATER
            using var first = JsonDocument.Parse(original.ToArray());
            using var second = JsonDocument.Parse(reencoded.ToArray());
            return JsonElement.DeepEquals(first.RootElement, second.RootElement);
#else
            return JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(reencoded));
#endif
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // net8 throws ArgumentException for a duplicate property name, which net10 parses; either way the document is not one to compare.
            return false;
        }
    }

    public static bool Headered(ReadOnlySpan<byte> original, ReadOnlySpan<byte> reserialized) =>
        original.SequenceEqual(reserialized);
}
