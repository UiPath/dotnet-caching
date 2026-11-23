using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

public sealed class KeyMoveStateOptionsTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(-1)]
    public void A_wait_for_more_than_one_replica_is_rejected_with_the_reason(int replicas)
    {
        var build = () => new KeyMoveStateOptions { StateWaitReplicas = replicas };

        build.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*waits for one replica*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void No_wait_and_a_wait_for_one_replica_are_accepted(int replicas) =>
        new KeyMoveStateOptions { StateWaitReplicas = replicas }.StateWaitReplicas.Should().Be(replicas);

    [Fact]
    public void One_replica_is_the_default() => new KeyMoveStateOptions().StateWaitReplicas.Should().Be(1);
}
