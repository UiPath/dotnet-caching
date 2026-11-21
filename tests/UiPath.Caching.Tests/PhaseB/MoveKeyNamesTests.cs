using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

public class MoveKeyNamesTests
{
    private const string Group = "g1";

    public static TheoryData<string> Prefixes => ["", "app:", "{shard}:"];

    [Theory]
    [MemberData(nameof(Prefixes))]
    public void Reverse_names_and_the_control_record_are_in_the_slot_of_the_in_place_key(string prefix)
    {
        var names = new MoveKeyNames(prefix, cluster: true);

        foreach (var key in InPlaceKeys())
        {
            var slot = names.SlotOf(key);
            names.SlotOf(names.ReverseFence(key).ToString()).Should().Be(slot, key);
            names.SlotOf(names.ReverseStaging(key, "abc123").ToString()).Should().Be(slot, key);
            names.SlotOf(names.ControlFor(Group, key).ToString()).Should().Be(slot, key);
        }
    }

    [Theory]
    [MemberData(nameof(Prefixes))]
    public void Forward_names_share_the_slot_of_a_tagged_native_key(string prefix)
    {
        var names = new MoveKeyNames(prefix, cluster: true);
        for (var i = 0; i < 100; i++)
        {
            var native = $"app:s:{{cache:k{i}}}";

            names.SlotOf(names.ForwardFence(native).ToString()).Should().Be(names.SlotOf(native));
            names.SlotOf(names.ForwardStaging(native, "n0nce").ToString()).Should().Be(names.SlotOf(native));
        }
    }

    [Fact]
    public void Forward_fence_of_an_untagged_native_key_throws_under_an_untagged_prefix()
    {
        var names = new MoveKeyNames("app:", cluster: true);

        var fence = () => names.ForwardFence("s:plain");
        var staging = () => names.ForwardStaging("s:plain", "n0nce");

        fence.Should().Throw<ArgumentException>();
        staging.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Forward_fence_of_an_untagged_native_key_is_fine_under_a_tagged_prefix()
    {
        var names = new MoveKeyNames("{shard}:", cluster: true);

        var act = () => names.ForwardFence("s:plain");

        act.Should().NotThrow();
        names.SlotOf(names.ForwardFence("s:plain").ToString()).Should().Be(names.SlotOf("s:plain"));
    }

    [Theory]
    [InlineData("", 16384)]
    [InlineData("app:", 16384)]
    public void Slot_set_covers_every_slot_under_an_untagged_prefix(string prefix, int count)
    {
        var names = new MoveKeyNames(prefix, cluster: true);

        names.SlotSet().Should().HaveCount(count).And.OnlyHaveUniqueItems();
        names.ControlRecords(Group).Should().HaveCount(count);
        names.ControlRecords(Group).Select(r => r.Key.ToString()).Distinct().Should().HaveCount(count);
    }

    [Fact]
    public void Slot_set_is_the_prefix_slot_under_a_tagged_prefix()
    {
        var names = new MoveKeyNames("{shard}:", cluster: true);

        names.SlotSet().Should().Equal(HashSlot.Of("{shard}:"));
        names.ControlRecords(Group).Should().ContainSingle();
        foreach (var key in InPlaceKeys())
        {
            names.ControlFor(Group, key).ToString().Should().Be(names.ControlRecords(Group)[0].Key.ToString());
        }
    }

    [Fact]
    public void Slot_set_is_one_record_without_a_cluster()
    {
        var names = new MoveKeyNames("app:", cluster: false);

        names.SlotSet().Should().HaveCount(1);
        names.ControlRecords(Group).Should().ContainSingle();
    }

    [Fact]
    public void Names_without_a_cluster_carry_no_braces()
    {
        var names = new MoveKeyNames("app:", cluster: false);

        names.ControlFor(Group, "n:c,k").ToString().Should().Be("mg:ctl:g1");
        var fence = names.ReverseFence("n:c,k").ToString();
        fence.Should().StartWith("mg:l:");
        fence["mg:l:".Length..].Should().HaveLength(11);
        names.ReverseStaging("n:c,k", "abc").ToString().Should().Be("mg:s:abc");
        names.ReverseFence("n:c,k").ToString().Should().Be(fence);
        foreach (var name in new[] { fence, names.ControlFor(Group, "x").ToString(), names.ReverseStaging("x", "abc").ToString() })
        {
            name.Should().NotContain("{").And.NotContain("}");
        }
    }

    [Fact]
    public void Reverse_fence_hash_is_eleven_characters_and_differs_per_key()
    {
        var names = new MoveKeyNames("", cluster: true);
        var fences = InPlaceKeys().Select(k => names.ReverseFence(k).ToString()).ToList();

        foreach (var fence in fences)
        {
            fence[(fence.IndexOf(":l:", StringComparison.Ordinal) + 3)..].Should().HaveLength(11);
        }

        fences.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("a{b")]
    [InlineData("{}x")]
    public void A_prefix_with_an_empty_or_unmatched_brace_throws_on_a_cluster(string prefix)
    {
        var act = () => new MoveKeyNames(prefix, cluster: true);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("a+b")]
    [InlineData("a@b")]
    [InlineData("a=b")]
    [InlineData("a{b")]
    [InlineData("a}b")]
    [InlineData("")]
    public void Validate_name_rejects_the_characters_the_names_are_built_with(string name)
    {
        var act = () => MoveKeyNames.ValidateName(name, "p");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_name_accepts_an_ordinary_name()
    {
        var act = () => MoveKeyNames.ValidateName("cache-1_x", "p");

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("{}", true, false)]
    [InlineData("a{}b", false, false)]
    [InlineData("a{b", true, false)]
    [InlineData("a}b{", true, false)]
    [InlineData("a{b}c", true, true)]
    [InlineData("plain", true, true)]
    [InlineData("a{b", false, true)]
    [InlineData("a}b{", false, true)]
    public void Key_text_validity_depends_on_braces_and_the_cluster(string key, bool cluster, bool valid)
    {
        MoveKeyNames.IsValidKeyText(key, cluster).Should().Be(valid);
    }

    private static IEnumerable<string> InPlaceKeys() =>
        Enumerable.Range(0, 200).Select(i => $"n:cache{i % 7},c:key{i}");
}
