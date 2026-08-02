namespace Psv.Core.Tests;

public class LineIndexTests
{
    [Fact]
    public void ChangedFiresOnAppendCheckpointCompleteAndReset()
    {
        var index = new LineIndex();
        int changedCount = 0;
        index.Changed += () => changedCount++;

        index.AppendCheckpoint(new Checkpoint(4096, 20_000), 20_000, 4096);
        Assert.Equal(1, changedCount);

        index.Complete(4200, 20_500, 20_500);
        Assert.Equal(2, changedCount);

        index.Reset();
        Assert.Equal(3, changedCount);
    }

    [Fact]
    public void ChangedDoesNotFirePerLineOrOnSeedingTheInitialCheckpoint()
    {
        // Changed is checkpoint-granularity, not per-line - firing on every RecordLineEnding call
        // (invoked once per line boundary while scanning) would defeat the whole point of batching
        // notifications to checkpoint frequency instead of line frequency.
        var index = new LineIndex();
        int changedCount = 0;
        index.Changed += () => changedCount++;

        index.SeedInitialCheckpoint(0);
        for (int i = 0; i < 1000; i++)
        {
            index.RecordLineEnding(LineEndingKind.Lf);
        }

        Assert.Equal(0, changedCount);
    }

    [Fact]
    public void TryGetNearestCheckpointByOffsetReturnsFalseWhenEmpty()
    {
        var index = new LineIndex();
        Assert.False(index.TryGetNearestCheckpointByOffset(0, out _));
    }

    [Fact]
    public void TryGetNearestCheckpointByOffsetFindsTheGreatestCheckpointAtOrBeforeTheOffset()
    {
        var index = new LineIndex();
        index.SeedInitialCheckpoint(0);
        index.AppendCheckpoint(new Checkpoint(100, 1_000), 1_000, 100);
        index.AppendCheckpoint(new Checkpoint(200, 2_000), 2_000, 200);

        Assert.True(index.TryGetNearestCheckpointByOffset(500, out var below));
        Assert.Equal(new Checkpoint(0, 0), below);

        Assert.True(index.TryGetNearestCheckpointByOffset(1_000, out var exact));
        Assert.Equal(new Checkpoint(100, 1_000), exact);

        Assert.True(index.TryGetNearestCheckpointByOffset(1_999, out var between));
        Assert.Equal(new Checkpoint(100, 1_000), between);

        Assert.True(index.TryGetNearestCheckpointByOffset(1_000_000, out var beyond));
        Assert.Equal(new Checkpoint(200, 2_000), beyond);
    }
}
