namespace UiPath.Caching.Tests.Broadcast;

public class TopicDefaultMembersTests
{
    [Fact]
    public void A_topic_that_keeps_the_defaults_names_no_transport_and_never_reports_a_refusal()
    {
        ITopic<ICacheEvent> sut = new PlainTopic();
        var heard = 0;

        sut.Refused += Listener;
        sut.Refused -= Listener;

        sut.Transport.Should().BeNull();
        heard.Should().Be(0);

        void Listener(Exception _) => heard++;
    }

    [Fact]
    public void The_gap_filtering_wrapper_names_the_transport_of_the_topic_it_wraps()
    {
        ITopic<ICacheEvent> sut = new GapFilteringTopic(new ReportingTopic { Name = "pubsub" });

        sut.Transport.Should().Be("pubsub");
    }

    [Fact]
    public void The_gap_filtering_wrapper_passes_a_listener_to_the_topic_it_wraps_and_takes_it_back()
    {
        var inner = new ReportingTopic();
        ITopic<ICacheEvent> sut = new GapFilteringTopic(inner);
        var heard = new List<Exception>();
        Action<Exception> listener = heard.Add;
        var refusal = new InvalidOperationException("refused");

        sut.Refused += listener;
        inner.Refuse(refusal);
        sut.Refused -= listener;
        inner.Refuse(refusal);

        heard.Should().Equal(refusal);
    }

    private class PlainTopic : ITopic<ICacheEvent>
    {
        public TopicKey TopicKey { get; } = new("plain");

        public EventHandler? OnDisposed { get; set; }

        public IDisposable Subscribe(IObserver<ICacheEvent> observer) => Substitute.For<IDisposable>();

        public ValueTask<bool> PublishAsync(ICacheEvent @event, CancellationToken token = default) => ValueTask.FromResult(true);

        public void Dispose()
        {
        }
    }

    private sealed class ReportingTopic : PlainTopic, ITopic<ICacheEvent>
    {
        private Action<Exception>? _refused;

        event Action<Exception>? ITopic<ICacheEvent>.Refused
        {
            add => _refused += value;
            remove => _refused -= value;
        }

        public string? Name { get; init; }

        string? ITopic<ICacheEvent>.Transport => Name;

        public void Refuse(Exception exception) => _refused?.Invoke(exception);
    }
}
