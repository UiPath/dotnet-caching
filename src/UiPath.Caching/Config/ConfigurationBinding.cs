namespace UiPath.Caching.Config;

internal static class ConfigurationBinding
{
    public static void Bind(IConfiguration section, object options)
    {
        if (options is IBindsFromConfiguration binds)
        {
            binds.Bind(section);
        }
        else
        {
            section.Bind(options);
        }
    }
}
