namespace UiPath.Platform.Caching.Redis;

internal static class Guard
{
    public static char NotWhiteSpace(char value, string parameterName)
    {
        if (char.IsWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be whitespace.", parameterName);
        }

        return value;
    }

    public static string NotNullOrWhiteSpace(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", parameterName);
        }

        return value;
    }
}
