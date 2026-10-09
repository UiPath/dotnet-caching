using NSubstitute.Core;

namespace UiPath.Caching.Tests.Fakes;

/// <summary>Stubs a ValueTask-returning call by passing it as an argument, which is how it is meant to be consumed.</summary>
internal static class ValueTaskStubs
{
    public static void Returns<T>(ValueTask<T> call, T value) => call.Returns(new ValueTask<T>(value));

    public static void Returns<T>(ValueTask<T> call, Func<CallInfo, ValueTask<T>> value) => call.Returns(value);

    public static void Returns(ValueTask call, Func<CallInfo, ValueTask> value) => call.Returns(value);
}
