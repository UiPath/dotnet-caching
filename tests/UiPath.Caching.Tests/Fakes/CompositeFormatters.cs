using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.MessagePack;
using UiPath.Caching.Negotiation;

namespace UiPath.Caching.Tests.Fakes;

internal static class CompositeFormatters
{
    public static CompositeEventFormatter Of(IEventFormatterProxy<ICacheEvent> json, IEventFormatterProxy<ICacheEvent> binary, bool write) =>
        Of(json, binary, write ? FixedEventWrites.Always : FixedEventWrites.Never, write);

    public static CompositeEventFormatter Of(IEventFormatterProxy<ICacheEvent> json, IEventFormatterProxy<ICacheEvent> binary, IEventWrites writes, bool write = true) =>
        new(json, [(MessagePackFormats.Event, "messagepack", binary, write)], writes, NullLogger.Instance);
}
