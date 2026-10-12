#if !NET9_0_OR_GREATER
namespace System.Threading;

#pragma warning disable CS9216 // A Monitor on the instance is how this stand-in locks.

/// <summary>The .NET 9 lock type on earlier targets: C# 13 compiles <c>lock</c> on it to <see cref="EnterScope"/>, which takes a <see cref="Monitor"/> on the instance, so lock fields are declared one way on every target.</summary>
[ExcludeFromCodeCoverage]
internal sealed class Lock
{
    public Scope EnterScope()
    {
        Monitor.Enter(this);
        return new Scope(this);
    }

    public ref struct Scope(Lock owner)
    {
        public readonly void Dispose() => Monitor.Exit(owner);
    }
}
#endif
