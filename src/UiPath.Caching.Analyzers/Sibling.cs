namespace UiPath.Caching.Analyzers;

/// <summary>A state overload: its state parameter's name, whether that type is its last type parameter, and whether it is a cache interface member.</summary>
internal sealed class Sibling(string parameterName, bool stateTypeIsLast, bool isInterfaceMember)
{
    public string ParameterName { get; } = parameterName;

    public bool StateTypeIsLast { get; } = stateTypeIsLast;

    public bool IsInterfaceMember { get; } = isInterfaceMember;
}
