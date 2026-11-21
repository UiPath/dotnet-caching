using StackExchange.Redis;
using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

public partial class MoveScriptsIntegrationTests
{
    private const long DeadlinePx = 60_000;
    private const long DeadlineLeft = 10;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cap_creates_the_fence_with_the_supplied_nonce_and_a_ttl(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);

        var capture = await env.CaptureAsync("nonce-1", fenceTtl: 30_000);

        capture.Captured.Should().BeTrue();
        capture.Code.Should().Be(0);
        capture.Token.Should().Be("nonce-1");
        capture.DestinationExists.Should().BeFalse();
        capture.Gate.Should().Be(env.Gate).And.StartWith("OPEN:");
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("nonce-1");
        var ttl = await env.Db.KeyTimeToLiveAsync(env.Fence);
        ttl.Should().NotBeNull();
        ttl!.Value.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
        Math.Abs(capture.Tcap - await env.ServerNowAsync()).Should().BeLessThan(5_000);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cap_keeps_the_token_in_force_and_refreshes_its_ttl(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await env.Db.StringSetAsync(env.Fence, "in-force");

        var capture = await env.CaptureAsync("nonce-2", fenceTtl: 30_000);

        capture.Token.Should().Be("in-force");
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("in-force");
        (await env.Db.KeyTimeToLiveAsync(env.Fence)).Should().NotBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cap_reports_an_existing_destination(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await env.Db.StringSetAsync(env.Dst, "present");

        var capture = await env.CaptureAsync("nonce-3");

        capture.Captured.Should().BeTrue();
        capture.DestinationExists.Should().BeTrue();
        (await env.Db.StringGetAsync(env.Dst)).Should().Be("present");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cap_refuses_another_identity(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);

        var capture = await env.CaptureAsync("nonce-4", id: "other");

        capture.Code.Should().Be(MoveScripts.IdentityMismatch);
        capture.Captured.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cap_refuses_a_missing_control_record(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts, openControl: false);

        var capture = await env.CaptureAsync("nonce-5");

        capture.Code.Should().Be(MoveScripts.IdentityMismatch);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cap_refuses_a_missing_gate(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);

        var capture = await env.CaptureAsync("nonce-6", cid: "no-such-cache");

        capture.Code.Should().Be(MoveScripts.GateRefused);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cap_refuses_a_closed_gate(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        (await env.Scripts.GateAsync(env.Ctl, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "CLOSED:x")).Should().Be(1);

        var capture = await env.CaptureAsync("nonce-7");

        capture.Code.Should().Be(MoveScripts.GateRefused);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pub_replaces_the_fence_and_writes_a_persistent_value(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var capture = await env.CaptureAsync("n0");

        var result = await env.Scripts.PublishAsync(env.Keys, MoveTestEnvironment.Id, "v", env.PublicationFor(capture, "n1"));

        result.Should().Be(MoveScripts.Published);
        (await env.Db.StringGetAsync(env.Dst)).Should().Be("v");
        (await env.Db.KeyTimeToLiveAsync(env.Dst)).Should().BeNull();
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("n1");
        (await env.Db.KeyTimeToLiveAsync(env.Fence)).Should().NotBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pub_returns_zero_when_the_fence_moved(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var capture = await env.CaptureAsync("n0");
        await env.Db.StringSetAsync(env.Fence, "moved");

        var result = await env.Scripts.PublishAsync(env.Keys, MoveTestEnvironment.Id, "v", env.PublicationFor(capture, "n1"));

        result.Should().Be(MoveScripts.FenceMoved);
        (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("moved");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pub_returns_zero_when_the_fence_is_gone(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var capture = await env.CaptureAsync("n0");
        await env.Db.KeyDeleteAsync(env.Fence);

        var result = await env.Scripts.PublishAsync(env.Keys, MoveTestEnvironment.Id, "v", env.PublicationFor(capture, "n1"));

        result.Should().Be(MoveScripts.FenceMoved);
        (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
        (await env.Db.KeyExistsAsync(env.Fence)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pub_with_only_if_absent_keeps_an_existing_destination(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var capture = await env.CaptureAsync("n0");
        await env.Db.StringSetAsync(env.Dst, "present");

        var result = await env.Scripts.PublishAsync(env.Keys, MoveTestEnvironment.Id, "v", env.PublicationFor(capture, "n1", onlyIfAbsent: true));

        result.Should().Be(MoveScripts.DestinationExists);
        (await env.Db.StringGetAsync(env.Dst)).Should().Be("present");
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("n0");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pub_with_only_if_absent_writes_an_absent_destination(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var capture = await env.CaptureAsync("n0");

        var result = await env.Scripts.PublishAsync(env.Keys, MoveTestEnvironment.Id, "v", env.PublicationFor(capture, "n1", onlyIfAbsent: true));

        result.Should().Be(MoveScripts.Published);
        (await env.Db.StringGetAsync(env.Dst)).Should().Be("v");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pub_refuses_another_identity(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var capture = await env.CaptureAsync("n0");

        var result = await env.Scripts.PublishAsync(env.Keys, "other", "v", env.PublicationFor(capture, "n1"));

        result.Should().Be(MoveScripts.IdentityMismatch);
        (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("n0");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pub_refuses_when_the_gate_changed_after_the_capture(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var capture = await env.CaptureAsync("n0");
        (await env.Scripts.GateAsync(env.Ctl, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "OPEN:another")).Should().Be(1);

        var result = await env.Scripts.PublishAsync(env.Keys, MoveTestEnvironment.Id, "v", env.PublicationFor(capture, "n1"));

        result.Should().Be(MoveScripts.GateRefused);
        (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pub_refuses_a_closed_gate_even_when_it_is_the_one_seen(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var capture = await env.CaptureAsync("n0");
        (await env.Scripts.GateAsync(env.Ctl, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "CLOSED:x")).Should().Be(1);

        var result = await env.Scripts.PublishAsync(env.Keys, MoveTestEnvironment.Id, "v", env.PublicationFor(capture, "n1", gateSeen: "CLOSED:x"));

        result.Should().Be(MoveScripts.GateRefused);
        (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pub_refuses_a_deadline_already_past_and_writes_nothing(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var capture = await env.CaptureAsync("n0");
        var tcap = await env.ServerNowAsync() - 10_000;

        var result = await env.Scripts.PublishAsync(env.Keys, MoveTestEnvironment.Id, "v", env.PublicationFor(capture, "n1", px: 5_000, tcap: tcap));

        result.Should().Be(MoveScripts.PastDeadline);
        (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("n0");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pub_sets_the_expiry_to_tcap_plus_px(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var capture = await env.CaptureAsync("n0");
        var tcap = await env.ServerNowAsync() - (DeadlinePx - DeadlineLeft);

        var result = await env.Scripts.PublishAsync(env.Keys, MoveTestEnvironment.Id, "v", env.PublicationFor(capture, "n1", px: DeadlinePx, tcap: tcap));

        await AssertDeadlineAsync(env, result, tcap);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pub_of_a_create_gives_the_value_a_relative_ttl(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var capture = await env.CaptureAsync("n0");

        var result = await env.Scripts.PublishAsync(env.Keys, MoveTestEnvironment.Id, "v", env.PublicationFor(capture, "n1", px: 30_000, tcap: -1));

        result.Should().Be(MoveScripts.Published);
        var ttl = await env.Db.KeyTimeToLiveAsync(env.Dst);
        ttl.Should().NotBeNull();
        ttl!.Value.Should().BeGreaterThan(TimeSpan.FromSeconds(28)).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pub_with_no_expiry_leaves_the_value_persistent_even_with_a_capture_time(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var capture = await env.CaptureAsync("n0");

        var result = await env.Scripts.PublishAsync(env.Keys, MoveTestEnvironment.Id, "v", env.PublicationFor(capture, "n1", px: -1, tcap: capture.Tcap - 100_000));

        result.Should().Be(MoveScripts.Published);
        (await env.Db.KeyTimeToLiveAsync(env.Dst)).Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pub_rejects_a_zero_life(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var capture = await env.CaptureAsync("n0");

        var act = async () => await env.Scripts.PublishAsync(env.Keys, MoveTestEnvironment.Id, "v", env.PublicationFor(capture, "n1", px: 0));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pubh_renames_the_staging_hash_over_the_destination_with_the_deadline(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var capture = await env.CaptureAsync("n0");
        var staging = await env.StageHashAsync();
        await env.Db.StringSetAsync(env.Dst, "old");
        var tcap = await env.ServerNowAsync() - (DeadlinePx - DeadlineLeft);

        var result = await env.Scripts.PublishHashAsync(env.Keys, staging, MoveTestEnvironment.Id, env.PublicationFor(capture, "n1", px: DeadlinePx, tcap: tcap));

        await AssertDeadlineAsync(env, result, tcap);
        (await env.Db.KeyExistsAsync(staging)).Should().BeFalse();
        if (result == MoveScripts.Published)
        {
            (await env.Db.StringGetAsync(env.Fence)).Should().Be("n1");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pubh_with_no_expiry_persists_the_staging_ttl_away(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var capture = await env.CaptureAsync("n0");
        var staging = await env.StageHashAsync(px: 60_000);
        (await env.Db.KeyTimeToLiveAsync(staging)).Should().NotBeNull();

        var result = await env.Scripts.PublishHashAsync(env.Keys, staging, MoveTestEnvironment.Id, env.PublicationFor(capture, "n1", px: -1));

        result.Should().Be(MoveScripts.Published);
        (await env.Db.HashLengthAsync(env.Dst)).Should().Be(2);
        (await env.Db.KeyTimeToLiveAsync(env.Dst)).Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pubh_returns_zero_for_a_missing_staging_key(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var capture = await env.CaptureAsync("n0");
        var staging = env.Make("absent-staging");

        var result = await env.Scripts.PublishHashAsync(env.Keys, staging, MoveTestEnvironment.Id, env.PublicationFor(capture, "n1"));

        result.Should().Be(0);
        (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("n0");
    }

    [Theory]
    [InlineData(true, "identity", MoveScripts.IdentityMismatch)]
    [InlineData(false, "identity", MoveScripts.IdentityMismatch)]
    [InlineData(true, "gate", MoveScripts.GateRefused)]
    [InlineData(false, "gate", MoveScripts.GateRefused)]
    [InlineData(true, "fence", MoveScripts.FenceMoved)]
    [InlineData(false, "fence", MoveScripts.FenceMoved)]
    [InlineData(true, "exists", MoveScripts.DestinationExists)]
    [InlineData(false, "exists", MoveScripts.DestinationExists)]
    [InlineData(true, "deadline", MoveScripts.PastDeadline)]
    [InlineData(false, "deadline", MoveScripts.PastDeadline)]
    public async Task Pubh_deletes_the_staging_key_on_every_refusal(bool useScripts, string refusal, long expected)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var capture = await env.CaptureAsync("n0");
        var staging = await env.StageHashAsync();
        var id = MoveTestEnvironment.Id;
        long px = -1;
        long? tcap = null;
        var onlyIfAbsent = false;
        switch (refusal)
        {
            case "identity":
                id = "other";
                break;
            case "gate":
                await env.Scripts.GateAsync(env.Ctl, id, MoveTestEnvironment.Cid, "OPEN:another");
                break;
            case "fence":
                await env.Db.StringSetAsync(env.Fence, "moved");
                break;
            case "exists":
                await env.Db.StringSetAsync(env.Dst, "present");
                onlyIfAbsent = true;
                break;
            case "deadline":
                px = 5_000;
                tcap = await env.ServerNowAsync() - 10_000;
                break;
        }

        var result = await env.Scripts.PublishHashAsync(env.Keys, staging, id, env.PublicationFor(capture, "n1", px: px, tcap: tcap, onlyIfAbsent: onlyIfAbsent));

        result.Should().Be(expected);
        (await env.Db.KeyExistsAsync(staging)).Should().BeFalse();
        if (refusal != "exists")
        {
            (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bump_with_no_operation_only_replaces_the_fence(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await env.Db.StringSetAsync(env.Dst, "keep");

        var result = await env.Scripts.BumpAsync(env.Keys, MoveTestEnvironment.Id, "b1", 30_000, BumpOperation.None);

        result.Should().Be(1);
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("b1");
        (await env.Db.KeyTimeToLiveAsync(env.Fence)).Should().NotBeNull();
        (await env.Db.StringGetAsync(env.Dst)).Should().Be("keep");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bump_set_writes_the_value_with_and_without_a_life(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);

        (await env.Scripts.BumpAsync(env.Keys, MoveTestEnvironment.Id, "b1", 30_000, BumpOperation.Set, "v1")).Should().Be(1);
        (await env.Db.StringGetAsync(env.Dst)).Should().Be("v1");
        (await env.Db.KeyTimeToLiveAsync(env.Dst)).Should().BeNull();

        (await env.Scripts.BumpAsync(env.Keys, MoveTestEnvironment.Id, "b2", 30_000, BumpOperation.Set, "v2", px: 20_000)).Should().Be(1);
        (await env.Db.StringGetAsync(env.Dst)).Should().Be("v2");
        (await env.Db.KeyTimeToLiveAsync(env.Dst))!.Value.Should().BeGreaterThan(TimeSpan.FromSeconds(18)).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(20));
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("b2");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bump_delete_returns_the_delete_count(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await env.Db.StringSetAsync(env.Dst, "v");

        (await env.Scripts.BumpAsync(env.Keys, MoveTestEnvironment.Id, "b1", 30_000, BumpOperation.Delete)).Should().Be(1);
        (await env.Scripts.BumpAsync(env.Keys, MoveTestEnvironment.Id, "b2", 30_000, BumpOperation.Delete)).Should().Be(0);

        (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("b2");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bump_with_another_identity_changes_nothing(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await env.Db.StringSetAsync(env.Dst, "keep");
        await env.Db.StringSetAsync(env.Fence, "f0");

        var result = await env.Scripts.BumpAsync(env.Keys, "other", "b1", 30_000, BumpOperation.Delete);

        result.Should().Be(MoveScripts.IdentityMismatch);
        (await env.Db.StringGetAsync(env.Dst)).Should().Be("keep");
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("f0");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bump_rejects_a_zero_life(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);

        var act = async () => await env.Scripts.BumpAsync(env.Keys, MoveTestEnvironment.Id, "b1", 30_000, BumpOperation.Set, "v", px: 0);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fill_writes_an_absent_destination_and_replaces_the_fence(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);

        var result = await env.Scripts.FillAsync(env.Keys, MoveTestEnvironment.Id, "v", 30_000, "f1", 60_000);

        result.Should().Be(1);
        (await env.Db.StringGetAsync(env.Dst)).Should().Be("v");
        (await env.Db.KeyTimeToLiveAsync(env.Dst))!.Value.Should().BeGreaterThan(TimeSpan.FromSeconds(28)).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("f1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fill_with_no_expiry_writes_a_persistent_value(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);

        (await env.Scripts.FillAsync(env.Keys, MoveTestEnvironment.Id, "v", -1, "f1", 60_000)).Should().Be(1);

        (await env.Db.KeyTimeToLiveAsync(env.Dst)).Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fill_leaves_an_existing_destination_and_the_fence(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await env.Db.StringSetAsync(env.Dst, "present");
        await env.Db.StringSetAsync(env.Fence, "f0");

        var result = await env.Scripts.FillAsync(env.Keys, MoveTestEnvironment.Id, "v", 30_000, "f1", 60_000);

        result.Should().Be(MoveScripts.DestinationExists);
        (await env.Db.StringGetAsync(env.Dst)).Should().Be("present");
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("f0");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fill_with_another_identity_changes_nothing(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);

        var result = await env.Scripts.FillAsync(env.Keys, "other", "v", 30_000, "f1", 60_000);

        result.Should().Be(MoveScripts.IdentityMismatch);
        (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
        (await env.Db.KeyExistsAsync(env.Fence)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fillh_renames_the_staging_hash_with_a_relative_ttl_and_replaces_the_fence(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var staging = await env.StageHashAsync();

        var result = await env.Scripts.FillHashAsync(env.Keys, staging, MoveTestEnvironment.Id, 30_000, "f1", 60_000);

        result.Should().Be(1);
        (await env.Db.HashLengthAsync(env.Dst)).Should().Be(2);
        (await env.Db.KeyExistsAsync(staging)).Should().BeFalse();
        (await env.Db.KeyTimeToLiveAsync(env.Dst))!.Value.Should().BeGreaterThan(TimeSpan.FromSeconds(28)).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("f1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fillh_with_no_expiry_persists_the_destination(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var staging = await env.StageHashAsync(px: 60_000);

        var result = await env.Scripts.FillHashAsync(env.Keys, staging, MoveTestEnvironment.Id, -1, "f1", 60_000);

        result.Should().Be(1);
        (await env.Db.KeyTimeToLiveAsync(env.Dst)).Should().BeNull();
        (await env.Db.HashLengthAsync(env.Dst)).Should().Be(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fillh_with_an_existing_destination_returns_2_and_deletes_staging(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var staging = await env.StageHashAsync();
        await env.Db.StringSetAsync(env.Dst, "present");
        await env.Db.StringSetAsync(env.Fence, "f0");

        var result = await env.Scripts.FillHashAsync(env.Keys, staging, MoveTestEnvironment.Id, 30_000, "f1", 60_000);

        result.Should().Be(MoveScripts.DestinationExists);
        (await env.Db.StringGetAsync(env.Dst)).Should().Be("present");
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("f0");
        (await env.Db.KeyExistsAsync(staging)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fillh_returns_3_for_a_missing_staging_key(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);

        var result = await env.Scripts.FillHashAsync(env.Keys, env.Make("absent-staging"), MoveTestEnvironment.Id, 30_000, "f1", 60_000);

        result.Should().Be(MoveScripts.StagingMissing);
        (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
        (await env.Db.KeyExistsAsync(env.Fence)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fillh_with_another_identity_changes_nothing(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var staging = await env.StageHashAsync();

        var result = await env.Scripts.FillHashAsync(env.Keys, staging, "other", 30_000, "f1", 60_000);

        result.Should().Be(MoveScripts.IdentityMismatch);
        (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
        (await env.Db.KeyExistsAsync(env.Fence)).Should().BeFalse();
    }

    // The deadline is 10 ms ahead of the server's clock; a slow round trip may pass it first, which is a refusal that writes nothing.
    private static async Task AssertDeadlineAsync(MoveTestEnvironment env, long result, long tcap)
    {
        result.Should().BeOneOf(MoveScripts.Published, MoveScripts.PastDeadline);
        if (result == MoveScripts.PastDeadline)
        {
            (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
            return;
        }

        var remaining = await env.Db.KeyTimeToLiveAsync(env.Dst);
        if (remaining is null)
        {
            (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse("the value had the 10 ms deadline and lapsed");
            return;
        }

        remaining.Value.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(DeadlineLeft));
        if (env.SupportsPExpireTime)
        {
            var at = await env.ExpireTimeAsync(env.Dst);
            if (at >= 0)
            {
                at.Should().Be(tcap + DeadlinePx);
            }
        }
    }
}
