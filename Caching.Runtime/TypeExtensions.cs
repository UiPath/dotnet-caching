using System.Collections.Frozen;
using System.Reflection;
using System.Text;

namespace UiPath.Platform.Caching;

public static class TypeExtensions
{
    private static readonly FrozenDictionary<Type, string> TypesToFriendlyNames = new Dictionary<Type, string>
    {
        {typeof(bool), "bool"},
        {typeof(byte), "byte"},
        {typeof(sbyte), "sbyte"},
        {typeof(char), "char"},
        {typeof(decimal), "decimal"},
        {typeof(double), "double"},
        {typeof(float), "float"},
        {typeof(int), "int"},
        {typeof(uint), "uint"},
        {typeof(long), "long"},
        {typeof(ulong), "ulong"},
        {typeof(short), "short"},
        {typeof(ushort), "ushort"},
        {typeof(string), "string"}
    }.ToFrozenDictionary();

    public static List<T> GetAllPublicConstantValues<T>(this Type type) =>
        type
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(fi => fi.IsLiteral && !fi.IsInitOnly && fi.FieldType == typeof(T))
            .Select(x => x.GetRawConstantValue())
            .OfType<T>()
            .ToList();

    internal static string GetCacheFriendlyTypeName(this Type type, char separator)
    {
        if (type.IsGenericParameter || !type.IsGenericType)
        {
            return TypesToFriendlyNames.TryGetValue(type, out var friendlyName) ? friendlyName : type.Name;
        }

        var builder = new StringBuilder();
        var name = type.Name;
        builder.Append(name, 0, name.IndexOf('`')).Append(separator);
        var first = true;
        foreach (var arg in type.GetGenericArguments())
        {
            if (!first)
            {
                builder.Append(',');
            }
            builder.Append(GetCacheFriendlyTypeName(arg, separator));
            first = false;
        }
        return builder.ToString();
    }
}
