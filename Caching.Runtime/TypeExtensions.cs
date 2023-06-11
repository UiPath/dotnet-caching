using System.Reflection;

namespace UiPath.Platform.Caching;

public static class TypeExtensions
{
    public static List<T> GetAllPublicConstantValues<T>(this Type type) =>
        type
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(fi => fi.IsLiteral && !fi.IsInitOnly && fi.FieldType == typeof(T))
            .Select(x => x.GetRawConstantValue())
            .OfType<T>()
            .ToList();
}
