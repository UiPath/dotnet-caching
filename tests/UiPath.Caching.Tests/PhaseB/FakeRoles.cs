using System.Reflection;
using StackExchange.Redis;

namespace UiPath.Caching.Tests.PhaseB;

/// <summary>ROLE replies built through the client's internal constructors, for fake servers.</summary>
internal static class FakeRoles
{
    public static Role Master(int replicas)
    {
        var list = Enumerable.Range(0, replicas).Select(i => (Role.Master.Replica)Create(typeof(Role.Master.Replica), "r", 6380 + i, 0L)).ToList();
        return (Role)Create(typeof(Role.Master), 0L, (ICollection<Role.Master.Replica>)list);
    }

    public static Role Unknown() => (Role)Create(typeof(Role.Unknown), "slave");

    private static object Create(Type type, params object[] args) =>
        Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, args, null)!;
}
