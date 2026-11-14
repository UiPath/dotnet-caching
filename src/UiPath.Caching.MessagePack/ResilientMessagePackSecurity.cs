using System.Collections;
using MessagePack;

namespace UiPath.Caching.MessagePack;

internal sealed class ResilientMessagePackSecurity : MessagePackSecurity
{
    public static readonly ResilientMessagePackSecurity Instance = new();

    private ResilientMessagePackSecurity()
        : base(UntrustedData)
    {
    }

    private ResilientMessagePackSecurity(ResilientMessagePackSecurity copyFrom)
        : base(copyFrom)
    {
    }

    protected override MessagePackSecurity Clone() => new ResilientMessagePackSecurity(this);

    // MessagePack throws for a key type it has no resistant comparer for, which would turn every read of such a value into a miss.
    protected override IEqualityComparer<T> GetHashCollisionResistantEqualityComparer<T>()
    {
        try
        {
            return base.GetHashCollisionResistantEqualityComparer<T>();
        }
        catch (TypeAccessException)
        {
            return EqualityComparer<T>.Default;
        }
    }

    protected override IEqualityComparer GetHashCollisionResistantEqualityComparer()
    {
        try
        {
            return base.GetHashCollisionResistantEqualityComparer();
        }
        catch (TypeAccessException)
        {
            return EqualityComparer<object>.Default;
        }
    }
}
