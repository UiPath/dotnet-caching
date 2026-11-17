using UiPath.Caching.Config;

namespace UiPath.Caching.MessagePack;

public static class MessagePackCachingBuilderExtensions
{
    public const string DefaultName = "messagepack";

    /// <summary>Registers the MessagePack serializer as <paramref name="name"/>, selectable per policy through <c>Serializer.Write</c>.</summary>
    public static ICachingBuilder AddMessagePackSerializer(
        this ICachingBuilder builder,
        string name = DefaultName,
        Action<MessagePackValueSerializerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new MessagePackValueSerializerOptions();
        configure?.Invoke(options);
        return builder.AddValueSerializer(name, MessagePackFormats.ValueIds, _ => new MessagePackValueSerializer(options));
    }

    /// <summary>Reads MessagePack and JSON events; <paramref name="write"/> true writes MessagePack once every live node can read it, false always writes JSON.</summary>
    /// <remarks>
    /// Registers the event format through <see cref="EventFormatBuilderExtensions.AddEventFormat"/>, which keeps the JSON decoder and wraps the JSON formatter whether
    /// <c>AddCloudEvents</c> is called before or after it. A second call throws; so does a call beside another event format that writes.
    /// </remarks>
    public static ICachingBuilder AddMessagePackEvents(this ICachingBuilder builder, bool write)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEventFormat(DefaultName, MessagePackFormats.Event, _ => new MessagePackEventFormatter(), write);
    }
}
