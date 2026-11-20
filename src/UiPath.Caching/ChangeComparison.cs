namespace UiPath.Caching;

internal static class ChangeComparison
{
    /// <summary>Two nulls are equal and one null is not, without asking the comparer.</summary>
    public static bool AreEqual<T>(IEqualityComparer<T> comparer, T? current, T? candidate) =>
        current is null || candidate is null ? current is null && candidate is null : comparer.Equals(current, candidate);
}
