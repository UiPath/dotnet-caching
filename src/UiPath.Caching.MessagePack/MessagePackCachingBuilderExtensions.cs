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
        return builder.AddLibraryValueSerializer(name, _ => new MessagePackValueSerializer(options));
    }
}
