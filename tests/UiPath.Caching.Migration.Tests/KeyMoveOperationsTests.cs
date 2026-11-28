using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration.Tests;

// What the operator's status says for a stored state, the confirm texts, and when the operations are registered.
public class KeyMoveOperationsTests
{
    [Theory]
    [InlineData(KeyMoveStates.Off, KeyMoveStage.Off)]
    [InlineData(KeyMoveStates.Preparing, KeyMoveStage.Preparing)]
    [InlineData(KeyMoveStates.Switching, KeyMoveStage.Switching)]
    [InlineData(KeyMoveStates.Copying, KeyMoveStage.Copying)]
    [InlineData(KeyMoveStates.Copied, KeyMoveStage.Copied)]
    [InlineData(KeyMoveStates.Done, KeyMoveStage.Done)]
    [InlineData("", KeyMoveStage.Off)]
    public void Each_stored_state_is_one_stage(string state, KeyMoveStage stage) =>
        KeyMoveOperations.StatusOf("g", State(state, "fwd")).Stage.Should().Be(stage);

    [Fact]
    public void The_status_carries_the_direction_the_holds_the_valid_approvals_the_residue_and_the_wait()
    {
        var bare = State(KeyMoveStates.Copied, "rev", residue: new Dictionary<string, long> { ["a"] = 3 });
        var at = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        var values = new Dictionary<string, string>
        {
            [StateFields.ApprovalPrefix + ApprovalKinds.Release] = new Approval(ApprovalKinds.Release, bare.Identity, Approval.EvidenceOf(ApprovalKinds.Release, bare), "op", "ok").Encode(),
            [StateFields.ApprovalPrefix + ApprovalKinds.Start] = new Approval(ApprovalKinds.Start, "0:old", string.Empty, "op", "ok").Encode(),
            [StateFields.Blocked] = "release_pending:release.consent:no_consent\nhold:operator",
            [StateFields.BlockedAt] = at.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var state = bare with { HoldOp = "maintenance", HoldAuto = "canary", Values = values, FieldNames = [.. values.Keys] };

        var status = KeyMoveOperations.StatusOf("g", state);

        status.Should().BeEquivalentTo(new KeyMoveStatus(
            "g",
            KeyMoveStage.Copied,
            true,
            true,
            "maintenance",
            "canary",
            [ApprovalKinds.Release],
            new Dictionary<string, long> { ["a"] = 3 },
            ["release_pending:release.consent:no_consent", "hold:operator"],
            at));
    }

    [Fact]
    public void A_state_with_no_wait_holds_or_approvals_shows_none()
    {
        var values = new Dictionary<string, string> { [StateFields.BlockedAt] = "123" };
        var status = KeyMoveOperations.StatusOf("g", State(KeyMoveStates.Preparing, "fwd") with { Values = values });

        status.Should().Match<KeyMoveStatus>(s => !s.Reverse && s.OperatorHold == null && s.AutomaticHold == null && s.Approvals.Count == 0 && s.Residue.Count == 0 && s.BlockingGates.Count == 0);
        status.BlockedSince.Should().BeNull("a time without reasons is no wait");
    }

    [Fact]
    public void A_kind_that_goes_past_a_check_names_its_exact_text_and_the_others_take_free_text()
    {
        KeyMoveApprovalKinds.ConfirmTextOf(KeyMoveApprovalKinds.CloseForce).Should().Be("close without the full soak or with canary keys counted");
        KeyMoveApprovalKinds.ConfirmTextOf(KeyMoveApprovalKinds.ForceReverse).Should().Be(ApprovalKinds.ConfirmTexts[ApprovalKinds.ForceReverse]);
        KeyMoveApprovalKinds.ConfirmTextOf(KeyMoveApprovalKinds.SkipBarrier).Should().Be(ApprovalKinds.ConfirmTexts[ApprovalKinds.SkipBarrier]);
        KeyMoveApprovalKinds.ConfirmTextOf(KeyMoveApprovalKinds.Abandon).Should().Be(ApprovalKinds.ConfirmTexts[ApprovalKinds.Abandon]);
        KeyMoveApprovalKinds.ConfirmTextOf(KeyMoveApprovalKinds.StartUnsettled).Should().Be(new StageDriverOptions().UpgradeHoldOverrideText);
        KeyMoveApprovalKinds.ConfirmTextOf(KeyMoveApprovalKinds.Close).Should().BeNull();
        KeyMoveApprovalKinds.ConfirmTextOf("invented").Should().BeNull();
    }

    [Fact]
    public void Every_kind_of_the_public_list_is_a_kind_the_move_takes()
    {
        var kinds = typeof(KeyMoveApprovalKinds).GetFields().Select(f => (string)f.GetRawConstantValue()!).ToArray();

        kinds.Should().BeEquivalentTo(ApprovalKinds.All);
    }

    [Fact]
    public void Without_UseKeyMove_the_operations_are_not_registered()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddCachingRouter(new ConfigurationBuilder().Build(), router => router
            .UseLibraryWhen(_ => false)
            .Add(new RouterCacheRegistration<string> { Name = "items", Group = "g", Source = new NoSource(), RedisKey = key => key }));

        services.Should().NotContain(d => d.ServiceType == typeof(IKeyMoveOperations));
    }

    private static StateRecord State(string state, string direction, IReadOnlyDictionary<string, long>? residue = null) =>
        new(state, direction, 7, "id7", true, 0, string.Empty, null, "a", "cs", string.Empty, string.Empty, string.Empty, string.Empty, Residue: residue);

    private sealed class NoSource : ISourceCache<string>
    {
        public ValueTask<SourceRead<string>> GetAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<WriteResult> SetAsync(string key, string value, CacheExpiry expiry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<AddResult> TryAddAsync(string key, string value, CacheExpiry expiry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask ClearAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
