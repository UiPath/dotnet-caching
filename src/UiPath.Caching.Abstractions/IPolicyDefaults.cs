namespace UiPath.Caching;

/// <summary>A cache factory whose policy factory <c>Cache&lt;T&gt;</c> and <c>HashCache&lt;T&gt;</c> use when none is passed.</summary>
public interface IPolicyDefaults
{
    ICachePolicyFactory? PolicyFactory { get; }
}
