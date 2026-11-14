using UiPath.Caching.Negotiation;

namespace UiPath.Caching.Tests.Negotiation;

public class TickScheduleTests
{
    [Fact]
    public void The_node_list_is_read_at_its_own_cadence_when_the_advertisement_is_much_faster()
    {
        var sut = new TickSchedule(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), () => 1.0);
        var now = TimeSpan.Zero;
        int advertisements = 0, reads = 0;

        while (now < TimeSpan.FromMinutes(5))
        {
            now += sut.Wait(now);
            var (advertise, refresh) = sut.Take(now);
            advertisements += advertise ? 1 : 0;
            reads += refresh ? 1 : 0;
            if (refresh)
            {
                advertise.Should().BeTrue("a read is always preceded by an advertisement");
            }
        }

        reads.Should().Be(5);
        advertisements.Should().BeInRange(299, 301);
    }

    [Fact]
    public void A_slow_advertisement_does_not_slow_the_node_list_read()
    {
        var sut = new TickSchedule(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(15), () => 1.0);
        var now = TimeSpan.Zero;
        var reads = 0;

        while (now < TimeSpan.FromMinutes(1))
        {
            now += sut.Wait(now);
            reads += sut.Take(now).Refresh ? 1 : 0;
        }

        reads.Should().Be(4);
    }
}
