namespace UiPath.Caching.Config;

/// <summary>Options that bind themselves, to tell a key the section left out from one it set.</summary>
internal interface IBindsFromConfiguration
{
    void Bind(IConfiguration section);
}
