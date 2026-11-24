using Microsoft.Extensions.Options;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The retained options only move forward: a reader that read older options cannot store them over the snapshot of a newer read.
public sealed class LastGoodOptionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reader_that_read_older_options_and_is_slow_to_return_cannot_put_them_over_a_newer_snapshot()
    {
        var inner = new SteppedOptions();
        var good = LastGoodOptions.Over(inner);
        good.CurrentValue.StartKeysPerSecond.Should().Be(1);
        inner.SetVersion(2);
        inner.StallNextRead();

        // The first reader reads version 2 and stalls before it returns; version 3 arrives and a second reader reads it.
        var slow = Task.Run(() => good.CurrentValue.StartKeysPerSecond, Ct);
        inner.Stalled.Wait(TimeSpan.FromSeconds(10), Ct).Should().BeTrue();
        inner.SetVersion(3);
        var fast = Task.Run(() => good.CurrentValue.StartKeysPerSecond, Ct);
        await Task.WhenAny(fast, Task.Delay(TimeSpan.FromMilliseconds(300), TimeProvider.System, Ct));
        inner.Release();
        await Task.WhenAll(slow, fast).WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, Ct);

        inner.Invalidate();
        good.CurrentValue.StartKeysPerSecond.Should().Be(3, "a reload that fails validation keeps the newest options that were read, never an older one");
    }

    private sealed class SteppedOptions : IOptionsMonitor<KeyMoveMoverOptions>
    {
        private readonly ManualResetEventSlim _release = new();
        private int _stall;
        private volatile bool _invalid;
        private volatile int _version = 1;

        public ManualResetEventSlim Stalled { get; } = new();

        public KeyMoveMoverOptions CurrentValue
        {
            get
            {
                if (_invalid)
                {
                    throw new OptionsValidationException(string.Empty, typeof(KeyMoveMoverOptions), ["bad reload"]);
                }

                var value = new KeyMoveMoverOptions { StartKeysPerSecond = _version };
                if (Interlocked.Exchange(ref _stall, 0) == 1)
                {
                    Stalled.Set();
                    _release.Wait(TimeSpan.FromSeconds(10));
                }

                return value;
            }
        }

        public void SetVersion(int version) => _version = version;

        public void Invalidate() => _invalid = true;

        public void StallNextRead() => _stall = 1;

        public void Release() => _release.Set();

        public KeyMoveMoverOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<KeyMoveMoverOptions, string?> listener) => null;
    }
}
