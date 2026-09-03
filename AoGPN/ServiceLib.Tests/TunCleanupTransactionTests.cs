namespace ServiceLib.Tests;

public class TunCleanupTransactionTests
{
    [Fact]
    public async Task RollbackRunsActionsInReverseOrderOnlyOnce()
    {
        var calls = new List<int>();
        await using var transaction = new TunCleanupTransaction();
        transaction.AddRollback(() => { calls.Add(1); return Task.CompletedTask; });
        transaction.AddRollback(() => { calls.Add(2); return Task.CompletedTask; });

        await transaction.RollbackAsync();
        await transaction.RollbackAsync();

        Assert.Equal([2, 1], calls);
        Assert.True(transaction.IsRolledBack);
    }

    [Fact]
    public async Task CompletePreventsRollback()
    {
        var called = false;
        await using var transaction = new TunCleanupTransaction();
        transaction.AddRollback(() => { called = true; return Task.CompletedTask; });

        await transaction.CompleteAsync();
        await transaction.RollbackAsync();

        Assert.False(called);
        Assert.True(transaction.IsCompleted);
    }
}
