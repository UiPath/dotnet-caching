using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Tests.Redis;

// The condition makes the server refuse the write if the key changed between the compare and the write; a client-side compare followed by a plain SET cannot.
public class FormatRewriteTransactionTests
{
    private static readonly byte[] Old = [1, 2, 3];
    private static readonly byte[] Rewritten = [4, 5];

    private readonly IDatabase _db = Substitute.For<IDatabase>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();

    public FormatRewriteTransactionTests()
    {
        _db.CreateTransaction(Arg.Any<object?>()).Returns(_transaction);
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);
    }

    [Fact]
    public async Task A_string_is_set_in_a_transaction_that_is_conditional_on_the_key_being_equal_and_keeps_the_ttl()
    {
        var applied = await FormatRewriteScripts.RewriteStringAsync(_db, "k", Old, Rewritten, useScripts: false);

        applied.Should().BeTrue();
        _transaction.Received(1).AddCondition(Arg.Is<Condition>(c => c.ToString() == Condition.StringEqual("k", Old).ToString()));
        _ = _transaction.Received(1).StringSetAsync("k", Arg.Is<RedisValue>(v => ((byte[]?)v)!.SequenceEqual(Rewritten)), null, true, When.Always, CommandFlags.DemandMaster);
        _ = _transaction.Received(1).ExecuteAsync(CommandFlags.DemandMaster);
        _ = _db.DidNotReceiveWithAnyArgs().StringSetAsync(default, default, default, default, default, default);
    }

    [Fact]
    public async Task A_transaction_that_does_not_commit_is_a_skip()
    {
        _transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(false);

        (await FormatRewriteScripts.RewriteStringAsync(_db, "k", Old, Rewritten, useScripts: false)).Should().BeFalse();
    }

    [Fact]
    public async Task Each_hash_field_gets_its_own_conditional_transaction()
    {
        var transactions = new List<ITransaction>();
        _db.CreateTransaction(Arg.Any<object?>()).Returns(_ =>
        {
            var transaction = Substitute.For<ITransaction>();
            transaction.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);
            transactions.Add(transaction);
            return transaction;
        });

        var rewritten = await FormatRewriteScripts.RewriteHashFieldsAsync(_db, "k", [("a", Old, Rewritten), ("b", Old, Rewritten)], useScripts: false);

        rewritten.Should().Be(2);
        transactions.Should().HaveCount(2);
        transactions[0].Received(1).AddCondition(Arg.Is<Condition>(c => c.ToString() == Condition.HashEqual("k", "a", Old).ToString()));
        transactions[1].Received(1).AddCondition(Arg.Is<Condition>(c => c.ToString() == Condition.HashEqual("k", "b", Old).ToString()));
        _ = transactions[0].Received(1).ExecuteAsync(CommandFlags.DemandMaster);
        _ = _db.DidNotReceiveWithAnyArgs().HashSetAsync(default, default, default, default, default);
    }

    [Fact]
    public void The_condition_names_the_key_and_the_field_so_the_comparison_above_can_tell_them_apart()
    {
        Condition.StringEqual("k", Old).ToString().Should().NotBe(Condition.StringEqual("other", Old).ToString());
        Condition.StringEqual("k", Old).ToString().Should().NotBe(Condition.StringNotEqual("k", Old).ToString());
        Condition.HashEqual("k", "a", Old).ToString().Should().NotBe(Condition.HashEqual("k", "b", Old).ToString());
        Condition.HashEqual("k", "a", Old).ToString().Should().NotBe(Condition.HashExists("k", "a").ToString());
    }
}
