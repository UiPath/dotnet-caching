using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// What the inventory is told about a stored value: a null the other side does not cache, or a value too large for a cache with an origin, is dropped; everything else is moved.
public class KeyMoveValueClassesTests
{
    private const long DefaultCap = 4 * 1024 * 1024;

    // The inventory judges by the stored length alone, which is what the codec measures: empty is a cached null, and one byte over the cap is too large.
    [Theory]
    [InlineData(0, false, false, true)]
    [InlineData(0, true, false, false)]
    [InlineData(5, false, false, false)]
    [InlineData(DefaultCap, false, false, false)]
    [InlineData(DefaultCap + 1, false, false, true)]
    [InlineData(DefaultCap + 1, false, true, false)]
    public void A_value_is_droppable_by_its_stored_length_the_other_sides_nulls_and_the_origin(long length, bool otherCachesNulls, bool noOrigin, bool expected)
    {
        var classes = new KeyMoveValueClasses();
        classes.Add("a", new KeyMoveBinding<Item>(ItemCodec.Instance, otherCachesNulls ? new ItemCodec([]) : new ItemCodec(), k => k, k => k, noOrigin));

        classes.IsDroppable("a", MoveSide.Legacy, length).Should().Be(expected);
    }

    [Fact]
    public void The_native_side_is_judged_by_the_in_place_codecs_nulls_and_an_unbound_cache_is_never_droppable()
    {
        var classes = new KeyMoveValueClasses();
        classes.Add("a", new KeyMoveBinding<Item>(new ItemCodec([]), ItemCodec.Instance, k => k, k => k, false));

        classes.IsDroppable("a", MoveSide.Native, 0).Should().BeFalse("L caches nulls, so a null in N moves back");
        classes.IsDroppable("other", MoveSide.Legacy, 0).Should().BeFalse();
    }
}
