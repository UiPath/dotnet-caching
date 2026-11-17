using UiPath.Caching.Negotiation;

namespace UiPath.Caching.Broadcast;

/// <summary>Reads a body by its first byte, the id of a registered event format or else JSON; writes the one format that writes only while <paramref name="writes"/> says so, asked on every event.</summary>
internal sealed class CompositeEventFormatter : IEventFormatterProxy<ICacheEvent>, IEventSourceMatcherProvider
{
    private const int IdSlots = 0x1F;

    private readonly IEventFormatterProxy<ICacheEvent> _json;
    private readonly IEventFormatterProxy<ICacheEvent>?[] _decoders = new IEventFormatterProxy<ICacheEvent>?[IdSlots];
    private readonly IEventFormatterProxy<ICacheEvent>? _writer;
    private readonly int _writerId;
    private readonly string _writerName = string.Empty;
    private readonly IEventWrites _writes;
    private readonly ILogger _logger;
    private int _mismatchLogged;

    public CompositeEventFormatter(
        IEventFormatterProxy<ICacheEvent> json,
        IReadOnlyList<(int Id, string Name, IEventFormatterProxy<ICacheEvent> Formatter, bool Write)> formats,
        IEventWrites writes,
        ILogger logger)
    {
        _json = json;
        _writes = writes;
        _logger = logger;
        foreach (var (id, name, formatter, write) in formats)
        {
            if (id is < 1 or >= IdSlots)
            {
                throw new ArgumentOutOfRangeException(nameof(formats), $"Event format '{name}' has id {id}, outside 1-{IdSlots - 1}.");
            }

            if (_decoders[id] is not null)
            {
                throw new ArgumentException($"Event format '{name}' repeats id {id}.", nameof(formats));
            }

            _decoders[id] = formatter;
            if (write)
            {
                if (_writer is not null)
                {
                    throw new ArgumentException($"Event formats '{_writerName}' and '{name}' both write.", nameof(formats));
                }

                _writer = formatter;
                _writerId = id;
                _writerName = name;
            }
        }
    }

    public ICacheEvent? Decode(ReadOnlyMemory<byte> body) =>
        (DecoderFor(body) ?? _json).Decode(body);

    public ReadOnlyMemory<byte> Encode(ICacheEvent @event)
    {
        if (_writer is null || !_writes.Writes)
        {
            return _json.Encode(@event);
        }

        ReadOnlyMemory<byte> body;
        try
        {
            body = _writer.Encode(@event);
        }
        // Encode takes no token, so a cancellation from a plugin is a plugin failure too; letting it escape would lose the invalidation.
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _mismatchLogged, 1) == 0)
            {
                _logger.LogError(ex, "The event format '{Name}' (id 0x{Id:X2}) threw while encoding; its events are written as JSON.", _writerName, _writerId);
            }

            return _json.Encode(@event);
        }

        if (!body.IsEmpty && body.Span[0] == _writerId)
        {
            return body;
        }

        // A format that does not start its body with its declared id would be dropped as JSON garbage by every reader; the event goes out as JSON instead.
        if (Interlocked.Exchange(ref _mismatchLogged, 1) == 0)
        {
            _logger.LogError(
                "The event format '{Name}' (id 0x{Id:X2}) wrote a body that starts with {Actual}; its events are written as JSON. Make Encode start every body with the declared id.",
                _writerName,
                _writerId,
                body.IsEmpty ? "nothing" : $"0x{body.Span[0]:X2}");
        }

        return _json.Encode(@event);
    }

    /// <summary>Each format's matcher, picked by the first byte; a format without one leaves its bodies to be decoded and compared.</summary>
    public IEventSourceMatcher? CreateSourceMatcher(Uri source)
    {
        var jsonMatcher = (_json as IEventSourceMatcherProvider)?.CreateSourceMatcher(source);
        var matchers = new IEventSourceMatcher?[IdSlots];
        var any = jsonMatcher is not null;
        for (var id = 0; id < IdSlots; id++)
        {
            if (_decoders[id] is not { } decoder)
            {
                continue;
            }

            var matcher = (decoder as IEventSourceMatcherProvider)?.CreateSourceMatcher(source);
            any |= matcher is not null;
            matchers[id] = matcher ?? NeverMatches.Instance;
        }

        return any ? new MatcherByFormat(jsonMatcher, matchers) : null;
    }

    private IEventFormatterProxy<ICacheEvent>? DecoderFor(ReadOnlyMemory<byte> body) =>
        body.IsEmpty || body.Span[0] >= IdSlots ? null : _decoders[body.Span[0]];

    private sealed class MatcherByFormat(IEventSourceMatcher? json, IEventSourceMatcher?[] byId) : IEventSourceMatcher
    {
        public bool IsFromSource(ReadOnlyMemory<byte> payload) =>
            (payload.IsEmpty || payload.Span[0] >= IdSlots || byId[payload.Span[0]] is not { } matcher ? json : matcher)?.IsFromSource(payload) ?? false;
    }

    private sealed class NeverMatches : IEventSourceMatcher
    {
        public static NeverMatches Instance { get; } = new();

        public bool IsFromSource(ReadOnlyMemory<byte> payload) => false;
    }
}
