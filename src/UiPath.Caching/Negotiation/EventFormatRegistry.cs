using UiPath.Caching.Serialization;

namespace UiPath.Caching.Negotiation;

/// <summary>The event formats registered on a builder: this node decodes each by its first byte, and writes the one that is set to write.</summary>
internal sealed class EventFormatRegistry
{
    private readonly List<EventFormat> _formats = [];

    public IReadOnlyList<EventFormat> Formats => _formats;

    public EventFormat? Writer => _formats.Find(f => f.Write);

    public IReadOnlyList<string> ReadTokens => [.. _formats.Select(f => NodeAdvertisement.Token(f.Id))];

    public string? WriteToken => Writer is { } writer ? NodeAdvertisement.Token(writer.Id) : null;

    /// <summary>Registers the format and returns it, or throws and registers nothing.</summary>
    public EventFormat Add(string name, int formatId, Func<IServiceProvider, IEventFormatterProxy<ICacheEvent>> factory, bool write)
    {
        var id = formatId;
        if (string.Equals(name, CacheValueFormats.JsonName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{name}' is reserved for the built-in JSON format.", nameof(name));
        }

        if (_formats.Find(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) is not null)
        {
            throw new ArgumentException($"An event format named '{name}' is already registered.", nameof(name));
        }

        if (!FormatIdRules.IsEventId(id))
        {
            throw new ArgumentOutOfRangeException(
                nameof(formatId),
                $"Event format '{name}' declares id {FormatIdRules.Hex(id)}; an event body starts with a single id byte: 0x01-0x0F (except 0x09, 0x0A, 0x0D) or 0x10-0x1E.");
        }

        if (_formats.Find(f => f.Id == id) is { } owner)
        {
            throw new ArgumentException(
                $"Event format id {FormatIdRules.Hex(id)} is already registered as '{owner.Name}'; both would advertise the token '{NodeAdvertisement.Token(id)}'.",
                nameof(formatId));
        }

        if (write && Writer is { } writer)
        {
            throw new InvalidOperationException($"Event format '{name}' cannot write: '{writer.Name}' already does. Register one writer; give the other write: false.");
        }

        var format = new EventFormat(name, formatId, factory, write);
        _formats.Add(format);
        return format;
    }
}
