using System.Reflection;
namespace UiPath.Caching.Migration.Tests;

// An ICache<string> that answers every abstract call with a completed default and records which one it was; the sealed members of the interface run their own bodies against it.
public class RecordingCache : DispatchProxy
{
    public List<string> Calls { get; } = [];

    internal static (ICache<string> Cache, RecordingCache Log) Create()
    {
        var cache = Create<ICache<string>, RecordingCache>();
        return (cache, (RecordingCache)(object)cache);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var open = targetMethod.IsGenericMethod ? targetMethod.GetGenericMethodDefinition() : targetMethod;
        var definition = typeof(ICache<>).GetMethods()
            .Concat(typeof(ICache<>).GetProperties().Select(p => p.GetMethod).OfType<MethodInfo>())
            .First(m => m.MetadataToken == open.MetadataToken);
        Calls.Add(definition.IsSpecialName ? definition.Name[4..] : MemberSignature.Of(definition));
        var type = targetMethod.ReturnType;
        if (type == typeof(string))
        {
            return "recorded";
        }

        return type == typeof(void) ? null : Activator.CreateInstance(type);
    }
}
