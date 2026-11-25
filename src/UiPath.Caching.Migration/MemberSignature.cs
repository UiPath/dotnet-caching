using System.Reflection;

namespace UiPath.Caching.Migration;

/// <summary>A member of <see cref="ICache{T}"/> or <see cref="IHashCache{T}"/> as one line of text, so the rule table can name it and a test can look it up.</summary>
internal static class MemberSignature
{
    public static string Of(MemberInfo member) => member switch
    {
        PropertyInfo property => property.Name,
        MethodInfo method => $"{method.Name}{(method.IsGenericMethodDefinition ? $"<{string.Join(',', method.GetGenericArguments().Select(a => a.Name))}>" : string.Empty)}({string.Join(", ", method.GetParameters().Select(p => Friendly(p.ParameterType)))})",
        _ => member.Name,
    };

    /// <summary>The abstract members of the interface, which is what a class must implement; a sealed member forwards to them.</summary>
    public static IEnumerable<MemberInfo> Required() => Required(typeof(ICache<>));

    /// <summary>The abstract members of the generic interface <paramref name="type"/>.</summary>
    public static IEnumerable<MemberInfo> Required(Type type)
    {
        foreach (var property in type.GetProperties().Where(p => p.GetMethod?.IsAbstract == true))
        {
            yield return property;
        }

        foreach (var method in type.GetMethods().Where(m => m.IsAbstract && !m.IsSpecialName))
        {
            yield return method;
        }
    }

    private static string Friendly(Type type)
    {
        if (type.IsByRef)
        {
            return "ref " + Friendly(type.GetElementType()!);
        }

        if (type.IsArray)
        {
            return Friendly(type.GetElementType()!) + "[]";
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        return $"{name}<{string.Join(',', type.GetGenericArguments().Select(Friendly))}>";
    }
}
