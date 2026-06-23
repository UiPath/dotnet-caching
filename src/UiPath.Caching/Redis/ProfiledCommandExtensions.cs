using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using StackExchange.Redis.Profiling;

namespace UiPath.Caching.Redis;

[SuppressMessage("SonarLint.Rule", "S3011: Reflection should not be used to increase accessibility of classes, methods, or fields")]
[ExcludeFromCodeCoverage]
public static class ProfiledCommandExtensions
{
    public static string GetCommandName(this IProfiledCommand profiledCommand)
    {
        var name = GetCommand(profiledCommand);
        if (profiledCommand.RetransmissionOf == null)
        {
            return name;
        }

        var retransmissionName = GetCommand(profiledCommand.RetransmissionOf);
        return $"{name} (Retransmission of {retransmissionName}: {profiledCommand.RetransmissionReason})";
    }

    public static string GetStatement(this IProfiledCommand profiledCommand) =>
        profiledCommand.GetCommandAndKey() ?? profiledCommand.GetCommandName();

    public static string? GetTarget(this IProfiledCommand profiledCommand) =>
        profiledCommand.EndPoint switch
        {
            IPEndPoint ipEndPoint => $"{ipEndPoint.Address}:{ipEndPoint.Port}",
            DnsEndPoint dnsEndPoint => $"{dnsEndPoint.Host}:{dnsEndPoint.Port}",
            _ => null,
        };

    internal static Lazy<RedisProfileFetcher> FetcherLazy { get; set; } = new(FetcherFactory);

    private static RedisProfileFetcher FetcherFactory()
    {
        var messageType = Type.GetType("StackExchange.Redis.Message,StackExchange.Redis", false);
        var profiledCommandType = Type.GetType("StackExchange.Redis.Profiling.ProfiledCommand,StackExchange.Redis", false);
        if (messageType != null && profiledCommandType != null)
        {
            var commandAndKey = messageType.GetProperty("CommandAndKey", BindingFlags.Public | BindingFlags.Instance);
            var messageProperty = profiledCommandType.GetField("Message", BindingFlags.NonPublic | BindingFlags.Instance);
            if (commandAndKey != null && messageProperty != null)
            {
                var _messageFetcher = BuildFieldGetter(profiledCommandType, messageProperty);
                var _commandAndKeyFetcher = BuildPropertyGetter(messageType, commandAndKey);
                return new RedisProfileFetcher
                {
                    Message = _messageFetcher,
                    CommandAndKey = _commandAndKeyFetcher,
                    ProfiledCommandType = profiledCommandType
                };
            }
        }

        return new RedisProfileFetcher();
    }

    /// <summary>
    /// Builds a delegate to get a property from an object. <paramref name="type"/> is cast to <see cref="Object"/>,
    /// with the returned property cast to <see cref="Object"/>.
    /// </summary>
    private static Func<object, object> BuildFieldGetter(Type type, FieldInfo fieldInfo)
    {
        var parameterExpression = Expression.Parameter(typeof(object), "value");
        var parameterCastExpression = Expression.Convert(parameterExpression, type);
        var memberExpression = Expression.Field(parameterCastExpression, fieldInfo);
        var returnCastExpression = Expression.Convert(memberExpression, typeof(object));
        return Expression.Lambda<Func<object, object>>(returnCastExpression, parameterExpression).Compile();
    }

    /// <summary>
    /// Builds a delegate to get a property from an object. <paramref name="type"/> is cast to <see cref="Object"/>,
    /// with the returned property cast to <see cref="Object"/>.
    /// </summary>
    private static Func<object, object> BuildPropertyGetter(Type type, PropertyInfo propertyInfo)
    {
        var parameterExpression = Expression.Parameter(typeof(object), "value");
        var parameterCastExpression = Expression.Convert(parameterExpression, type);
        var memberExpression = Expression.Property(parameterCastExpression, propertyInfo);
        var returnCastExpression = Expression.Convert(memberExpression, typeof(object));
        return Expression.Lambda<Func<object, object>>(returnCastExpression, parameterExpression).Compile();
    }

    private static string GetCommand(this IProfiledCommand profiledCommand) =>
    !string.IsNullOrEmpty(profiledCommand.Command)
        ? profiledCommand.Command
        : ProfiledCommandProcessor.UnknownCommand;

    private static string? GetCommandAndKey(this IProfiledCommand profiledCommand)
    {
        var fetcher = FetcherLazy.Value;
        if (profiledCommand.GetType() != fetcher.ProfiledCommandType || fetcher.Message == null)
            return null;

        var message = fetcher.Message.Invoke(profiledCommand);
        return fetcher.CommandAndKey?.Invoke(message) as string;
    }

    internal struct RedisProfileFetcher
    {
        public Func<object, object>? Message { get; set; }
        public Func<object, object>? CommandAndKey { get; set; }
        public Type? ProfiledCommandType { get; set; }
    }
}
