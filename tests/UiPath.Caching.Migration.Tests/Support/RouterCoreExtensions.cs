using System.Collections.Concurrent;
using System.Reflection;

namespace UiPath.Caching.Migration.Tests.Support;

internal static class RouterCoreExtensions
{
    private static readonly FieldInfo Background = typeof(RouterCore).GetField("_background", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Adds a task with no removal continuation, as a continuation that has not run yet leaves one.</summary>
    public static void AddWithoutRemoval(this RouterCore core, Task task) => ((ConcurrentDictionary<Task, byte>)Background.GetValue(core)!).TryAdd(task, 0);
}
