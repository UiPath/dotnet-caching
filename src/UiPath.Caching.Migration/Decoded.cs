namespace UiPath.Caching.Migration;

internal readonly record struct Decoded<T>(DecodeClass Class, T? Value)
    where T : class
{
    public static Decoded<T> Of(T value) => new(DecodeClass.Ok, value);
}
