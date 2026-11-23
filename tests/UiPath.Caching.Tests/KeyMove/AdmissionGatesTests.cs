using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>The generation gates of a pod on the Dst and of a pod on the Src, with the evidence rows of the model.</summary>
public sealed class AdmissionGatesTests
{
    private const string App = "app";
    private const string Token = "0123456789abcdef0123456789abcdef";

    private readonly FakeGenerations _fake = new();
    private readonly List<(string App, string Code)> _alerts = [];
    private ClaimRecord _mirror = ClaimRecord.From([]);
    private bool _registered = true;

    [Fact]
    public async Task The_dst_gate_admits_in_the_baseline_generation_and_with_no_baseline()
    {
        (await Dst().CheckAsync(App, TestContext.Current.CancellationToken)).Admit.Should().BeTrue("no move acquired a mirror here");

        _fake.Dst = BaselineOf(_fake.Current(RecordSide.Dst)!.Value);

        (await Dst().CheckAsync(App, TestContext.Current.CancellationToken)).Admit.Should().BeTrue();
        _alerts.Should().BeEmpty();
    }

    [Fact]
    public async Task The_dst_gate_refuses_after_a_generation_change_until_a_confirmed_repair_writes_the_new_generation()
    {
        _fake.Dst = BaselineOf(_fake.Current(RecordSide.Dst)!.Value);
        _fake.Set(RecordSide.Dst, new ServerGeneration("dst-run", "promoted"));

        var refused = await Dst().CheckAsync(App, TestContext.Current.CancellationToken);

        refused.Admit.Should().BeFalse();
        refused.Alert.Should().Be("mirror_repair_pending");
        _alerts.Should().Contain((App, "mirror_repair_pending"));

        _fake.Dst = BaselineOf(_fake.Current(RecordSide.Dst)!.Value);

        (await Dst().CheckAsync(App, TestContext.Current.CancellationToken)).Admit.Should().BeTrue();
    }

    [Fact]
    public async Task The_dst_gate_counts_an_unknown_generation_and_an_unreadable_baseline_as_a_change()
    {
        _fake.Dst = BaselineOf(_fake.Current(RecordSide.Dst)!.Value);
        _fake.Set(RecordSide.Dst, null);
        (await Dst().CheckAsync(App, TestContext.Current.CancellationToken)).Admit.Should().BeFalse();

        _fake.Set(RecordSide.Dst, new ServerGeneration("dst-run", "dst-repl"));
        _fake.BaselinesFail = true;
        (await Dst().CheckAsync(App, TestContext.Current.CancellationToken)).Admit.Should().BeFalse();
    }

    [Fact]
    public async Task The_src_gate_leaves_an_app_without_a_move_destination_alone()
    {
        _registered = false;
        _fake.Src = BaselineOf(new ServerGeneration("old", "old"));
        _mirror = Record($"open|{Token}|inst|");

        (await Src().CheckAsync(App, TestContext.Current.CancellationToken)).Admit.Should().BeTrue();
    }

    [Theory]
    [InlineData("pend", "", false)]
    [InlineData("open", "", false)]
    [InlineData("idle", "5:S0", true)]
    [InlineData("closed", "5:S5", true)]
    public async Task A_registered_app_with_no_baseline_reads_the_mirror_and_is_refused_while_it_shows_a_move(string status, string cert, bool admitted)
    {
        _fake.Src = null;
        _mirror = Record($"{status}|{Token}|inst|{cert}");

        var decision = await Src().CheckAsync(App, TestContext.Current.CancellationToken);

        decision.Admit.Should().Be(admitted);
        _fake.BaselineWrites.Should().Be(0, "the gate never creates a baseline from an absent first read");
    }

    [Fact]
    public async Task A_registered_app_with_no_baseline_is_refused_for_a_mirror_that_is_unrecognized_or_unreadable_or_not_pinned_to_one_generation_and_admitted_for_none()
    {
        _fake.Src = null;
        _mirror = ClaimRecord.From([new KeyValuePair<string, string>("imd:g", "bogus")]);
        (await Src().CheckAsync(App, TestContext.Current.CancellationToken)).Admit.Should().BeFalse("unrecognized");

        _mirror = ClaimRecord.From([]);
        (await Src().CheckAsync(App, TestContext.Current.CancellationToken)).Admit.Should().BeTrue("no live field: no move began");

        _fake.Set(RecordSide.Dst, null);
        (await Src().CheckAsync(App, TestContext.Current.CancellationToken)).Admit.Should().BeFalse("an unknown generation");

        _fake.Set(RecordSide.Dst, new ServerGeneration("dst-run", "dst-repl"));
        var unreadable = new SrcAdmissionGate(() => true, _fake, _fake, _ => Task.FromException<ClaimRecord>(new TimeoutException("the Dst is down")));
        (await unreadable.CheckAsync(App, TestContext.Current.CancellationToken)).Admit.Should().BeFalse("an unreadable mirror");
    }

    [Fact]
    public async Task The_src_gate_applies_the_existing_rule_while_the_src_generation_is_the_baseline()
    {
        _fake.Src = BaselineOf(_fake.Current(RecordSide.Src)!.Value);

        (await Src().CheckAsync(App, TestContext.Current.CancellationToken)).Admit.Should().BeTrue();
    }

    [Theory]
    [InlineData("pend", "", false)]
    [InlineData("open", "", false)]
    [InlineData("open", "5:S0", false)]
    [InlineData("closed", "5:S5", false)]
    [InlineData("idle", "5:S0", true)]
    public async Task After_a_src_generation_change_only_a_fresh_idle_s0_mirror_of_this_token_is_clear(string status, string cert, bool admitted)
    {
        _fake.Src = BaselineOf(new ServerGeneration("src-run", "before"));
        _mirror = Record($"{status}|{Token}|inst|{cert}");

        var decision = await Src().CheckAsync(App, TestContext.Current.CancellationToken);

        decision.Admit.Should().Be(admitted);
        if (!admitted)
        {
            _alerts.Should().Contain((App, "src_admission_pending"));
        }
    }

    [Fact]
    public async Task A_mirror_of_another_token_that_is_idle_is_not_clear_and_an_absent_one_is_unknown()
    {
        _fake.Src = BaselineOf(new ServerGeneration("src-run", "before"));
        _mirror = Record("idle|fedcba9876543210fedcba9876543210|inst|5:S0");
        (await Src().CheckAsync(App, TestContext.Current.CancellationToken)).Admit.Should().BeFalse();

        _mirror = ClaimRecord.From([]);
        (await Src().CheckAsync(App, TestContext.Current.CancellationToken)).Admit.Should().BeFalse("fresh absence is unknown, not proof that the move ended");
        _fake.BaselineWrites.Should().Be(0, "an absent read never refreshes the baseline");
    }

    [Fact]
    public async Task An_unrecognized_mirror_is_unknown()
    {
        _fake.Src = BaselineOf(new ServerGeneration("src-run", "before"));
        _mirror = ClaimRecord.From([new KeyValuePair<string, string>("imd:g", "bogus")]);

        (await Src().CheckAsync(App, TestContext.Current.CancellationToken)).Admit.Should().BeFalse();
    }

    [Fact]
    public async Task Evidence_is_stale_when_either_generation_changed_during_the_mirror_read_or_is_unknown()
    {
        _fake.Src = BaselineOf(new ServerGeneration("src-run", "before"));
        _mirror = Record($"idle|{Token}|inst|5:S0");

        // The first read is the gate's own Src read; the next two bracket the mirror read.
        _fake.BeforeRead = n =>
        {
            if (n == 4)
            {
                _fake.Set(RecordSide.Dst, new ServerGeneration("dst-run", "failed-over"));
            }
        };
        (await Src().CheckAsync(App, TestContext.Current.CancellationToken)).Admit.Should().BeFalse("the Dst changed generation inside the bracket");

        _fake.BeforeRead = null;
        _fake.Set(RecordSide.Dst, null);
        (await Src().CheckAsync(App, TestContext.Current.CancellationToken)).Admit.Should().BeFalse("an unknown generation counts as a change");
    }

    [Fact]
    public async Task An_unreadable_src_baseline_refuses()
    {
        _fake.BaselinesFail = true;

        (await Src().CheckAsync(App, TestContext.Current.CancellationToken)).Admit.Should().BeFalse();
    }

    private static ClaimRecord Record(string value) => ClaimRecord.From([new KeyValuePair<string, string>("imd:g", value)]);

    private static AdmissionBaseline BaselineOf(ServerGeneration generation) => new(generation, Token);

    private DstAdmissionGate Dst() => new(_fake, _fake, (a, c, _) => _alerts.Add((a, c)));

    private SrcAdmissionGate Src() => new(() => _registered, _fake, _fake, _ => Task.FromResult(_mirror), (a, c, _) => _alerts.Add((a, c)));
}
