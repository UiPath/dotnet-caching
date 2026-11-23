using UiPath.Caching.Broadcast.Redis;
using UiPath.Caching.Negotiation;

namespace UiPath.Caching.Tests.Broadcast;

public sealed class DepartedOwnersTests
{
    private static readonly FormatNegotiationOptions Negotiation = new() { AdvertiseInterval = TimeSpan.FromMilliseconds(100), LivenessWindow = TimeSpan.FromMilliseconds(100) };

    [Theory]
    [InlineData(true, "Stale")]
    [InlineData(false, "Unknown")]
    public void An_entry_older_than_the_gone_window_is_stale_only_when_its_age_is_from_the_same_clock(bool sameClock, string expected)
    {
        var owners = new DepartedOwners([Entry("urn:old", left: false, TimeSpan.FromSeconds(5))], Negotiation, TimeSpan.FromMilliseconds(200), sameClock);

        owners.Judge("urn:old").ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_left_entry_counts_as_left_whichever_clock_stamped_it(bool sameClock)
    {
        var owners = new DepartedOwners([Entry("urn:left", left: true, TimeSpan.Zero)], Negotiation, TimeSpan.FromMilliseconds(200), sameClock);

        owners.Judge("urn:left").Should().Be(DepartedOwners.Verdict.Left);
    }

    private static StoredAdvertisement Entry(string node, bool left, TimeSpan age) =>
        new(node, new NodeAdvertisement("test", node, [NodeAdvertisement.Json], new Dictionary<string, IReadOnlyList<string>>(), Left: left), age);
}
