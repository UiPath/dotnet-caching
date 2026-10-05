using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Telemetry;

public class TelemetryTagsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(20)]
    public void Tags_read_back_in_order_inline_or_overflowed(int count)
    {
        var source = Enumerable.Range(0, count).Select(i => new KeyValuePair<string, string>($"k{i}", $"v{i}")).ToArray();

        TelemetryTags<string> tags = source.AsSpan();

        tags.Count.Should().Be(count);
        tags.IsEmpty.Should().Be(count == 0);
        Enumerable.Range(0, count).Select(i => tags[i]).Should().Equal(source);
        tags.Should().Equal(source);
    }

    [Fact]
    public void A_collection_expression_an_array_and_a_span_build_the_same_tags()
    {
        KeyValuePair<string, double>[] array = [new("a", 1), new("b", 2)];

        TelemetryTags<double> fromExpression = [new("a", 1), new("b", 2)];
        TelemetryTags<double> fromArray = array;
        TelemetryTags<double> fromSpan = array.AsSpan();
        TelemetryTags<double> fromReadOnlySpan = (ReadOnlySpan<KeyValuePair<string, double>>)array;

        fromExpression.Should().Equal(array);
        fromArray.Should().Equal(array);
        fromSpan.Should().Equal(array);
        fromReadOnlySpan.Should().Equal(array);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(12)]
    public void Tags_are_a_copy_of_their_source(int count)
    {
        var source = Enumerable.Range(0, count).Select(i => new KeyValuePair<string, string>($"k{i}", "before")).ToArray();
        TelemetryTags<string> tags = source;

        source[0] = new("k0", "after");

        tags[0].Value.Should().Be("before");
    }

    [Fact]
    public void Default_and_a_null_array_are_empty()
    {
        TelemetryTags<string> fromNull = (KeyValuePair<string, string>[]?)null;

        default(TelemetryTags<string>).IsEmpty.Should().BeTrue();
        default(TelemetryTags<string>).Should().BeEmpty();
        fromNull.IsEmpty.Should().BeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void An_index_outside_the_tags_throws(int index)
    {
        TelemetryTags<string> tags = [new("a", "1"), new("b", "2")];

        var read = () => tags[index];

        read.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public void Tags_with_the_same_pairs_in_order_are_equal(int count)
    {
        var pairs = Enumerable.Range(0, count).Select(i => new KeyValuePair<string, double>($"k{i}", i)).ToArray();
        TelemetryTags<double> left = pairs;
        TelemetryTags<double> right = pairs.ToArray();
        TelemetryTags<double> reordered = pairs.Reverse().ToArray();
        TelemetryTags<double> shorter = pairs[..^1];

        (left == right).Should().BeTrue();
        left.GetHashCode().Should().Be(right.GetHashCode());
        left.Equals((object)right).Should().BeTrue();
        (left != reordered).Should().BeTrue();
        left.Equals(shorter).Should().BeFalse();
    }

    [Fact]
    public void A_proxied_provider_matches_tags_by_value()
    {
        var provider = Substitute.For<ICachingTelemetryProvider>();

        provider.TrackMetric("metric", 2, [new("key", "value")]);

        provider.Received(1).TrackMetric("metric", 2, [new("key", "value")]);
        provider.DidNotReceive().TrackMetric("metric", 2, [new("key", "other")]);
    }

    [Fact]
    public void A_proxied_provider_receives_the_tags_it_was_sent()
    {
        var provider = Substitute.For<ICachingTelemetryProvider>();

        provider.TrackEvent("evt", [new("key", "value")], [new("m", 1.5)]);
        provider.TrackMetric("metric", 2, [new("key", "value")]);
        provider.TrackException(new InvalidOperationException(), [new("key", "value")]);
        provider.TrackDependency("t", "target", "name", "data", DateTimeOffset.UnixEpoch, TimeSpan.Zero, "200", true, [new("key", "value")]);

        provider.Received(1).TrackEvent("evt", Arg.Is<TelemetryTags<string>>(t => t.Count == 1 && t[0].Value == "value"), Arg.Is<TelemetryTags<double>>(t => t[0].Value == 1.5));
        provider.Received(1).TrackMetric("metric", 2, Arg.Is<TelemetryTags<string>>(t => t[0].Key == "key"));
        provider.Received(1).TrackException(Arg.Any<InvalidOperationException>(), Arg.Is<TelemetryTags<string>>(t => t.Count == 1), Arg.Any<TelemetryTags<double>>());
        provider.Received(1).TrackDependency("t", "target", "name", "data", DateTimeOffset.UnixEpoch, TimeSpan.Zero, "200", true, Arg.Is<TelemetryTags<string>>(t => t.Count == 1), Arg.Any<TelemetryTags<double>>());
    }

    [Fact]
    public void ToDictionaryOrNull_answers_null_for_no_tags_and_the_last_value_per_key()
    {
        TelemetryTags<string> duplicated = [new("a", "1"), new("a", "2"), new("b", "3")];

        TelemetryTags.ToDictionaryOrNull(default(TelemetryTags<string>)).Should().BeNull();
        TelemetryTags.ToDictionaryOrNull(default(TelemetryTags<double>)).Should().BeNull();
        TelemetryTags.ToDictionaryOrNull(duplicated).Should().Equal(new Dictionary<string, string> { ["a"] = "2", ["b"] = "3" });
    }
}
