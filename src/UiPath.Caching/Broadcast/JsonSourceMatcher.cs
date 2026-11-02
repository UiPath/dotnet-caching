using System.Text;
using System.Text.Json;

namespace UiPath.Caching.Broadcast;

/// <summary>
/// Finds a JSON event's top-level source property and compares its text with one source's, allocating nothing.
/// Linked into UiPath.Caching.CloudEvents.
/// </summary>
internal sealed class JsonSourceMatcher : IEventSourceMatcher
{
    private readonly byte[] _property;
    private readonly byte[][] _texts;

    private JsonSourceMatcher(byte[] property, byte[][] texts)
    {
        _property = property;
        _texts = texts;
    }

    /// <summary>
    /// A matcher for the texts of <paramref name="source"/> a node writes, each kept only when <paramref name="decodeProbe"/>,
    /// the formatter's own decode of a payload carrying it, gives an event SameSource counts as <paramref name="source"/>'s.
    /// Null when no text passes.
    /// </summary>
    public static JsonSourceMatcher? Create(ReadOnlySpan<byte> property, Uri source, Func<string, IEvent?> decodeProbe)
    {
        var normalized = Normalized(source);
        string[] candidates = normalized == source.OriginalString ? [normalized] : [source.OriginalString, normalized];
        var texts = new List<byte[]>(candidates.Length);
        foreach (var text in candidates)
        {
            if (text.Length > 0 && IsSame(decodeProbe, text, source))
            {
                texts.Add(Encoding.UTF8.GetBytes(text));
            }
        }

        return texts.Count == 0 ? null : new JsonSourceMatcher(property.ToArray(), [.. texts]);
    }

    /// <summary>
    /// True when the payload is one JSON object whose source property, present once, is a string equal to one of the texts.
    /// Any other shape, a second source property and malformed JSON are not this source's.
    /// </summary>
    public bool IsFromSource(ReadOnlyMemory<byte> payload)
    {
        try
        {
            var reader = new Utf8JsonReader(payload.Span);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            var matched = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isSource = reader.ValueTextEquals(_property);
                reader.Read();
                if (!isSource)
                {
                    reader.Skip();
                }
                else if (matched || reader.TokenType != JsonTokenType.String || !Matches(ref reader))
                {
                    // A second source property would win the decode, so it is read past only to say no.
                    return false;
                }
                else
                {
                    matched = true;
                }
            }

            // The rest is read so that a duplicate or trailing content counts against the match, as it does in a decode.
            return matched && reader.TokenType == JsonTokenType.EndObject && !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Normalized(Uri source) =>
        source.IsAbsoluteUri ? source.GetComponents(UriComponents.AbsoluteUri, UriFormat.SafeUnescaped) : source.OriginalString;

    private static bool IsSame(Func<string, IEvent?> decodeProbe, string text, Uri source)
    {
        try
        {
            return decodeProbe(text) is { Source: not null } probe && probe.SameSource(source);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private bool Matches(ref Utf8JsonReader reader)
    {
        foreach (var text in _texts)
        {
            if (reader.ValueTextEquals(text))
            {
                return true;
            }
        }

        return false;
    }
}
