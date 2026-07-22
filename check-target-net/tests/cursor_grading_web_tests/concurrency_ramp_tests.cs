using cursor_grading_web.Services;

namespace cursor_grading_web_tests;

public class concurrency_ramp_tests
{
    [Fact]
    public async Task run_async_releases_all_immediately_when_batch_covers_workers()
    {
        using var semaphore = new SemaphoreSlim(0, 10);
        var steps = new List<int>();

        var released = await concurrency_ramp.run_async(
            semaphore, workers: 10, ramp_batch_size: 10, ramp_interval_ms: 50_000,
            CancellationToken.None,
            (unlocked, _) => steps.Add(unlocked));

        Assert.Equal(10, released);
        Assert.Equal(new[] { 10 }, steps);
        Assert.Equal(10, semaphore.CurrentCount);
    }

    [Fact]
    public async Task run_async_releases_all_immediately_when_batch_size_non_positive()
    {
        using var semaphore = new SemaphoreSlim(0, 7);
        var steps = new List<int>();

        var released = await concurrency_ramp.run_async(
            semaphore, workers: 7, ramp_batch_size: 0, ramp_interval_ms: 1000,
            CancellationToken.None,
            (unlocked, _) => steps.Add(unlocked));

        Assert.Equal(7, released);
        Assert.Equal(new[] { 7 }, steps);
        Assert.Equal(7, semaphore.CurrentCount);
    }

    [Fact]
    public async Task run_async_unlocks_in_batches_without_exceeding_workers()
    {
        using var semaphore = new SemaphoreSlim(0, 5);
        var steps = new List<int>();

        var released = await concurrency_ramp.run_async(
            semaphore, workers: 5, ramp_batch_size: 2, ramp_interval_ms: 1,
            CancellationToken.None,
            (unlocked, max) =>
            {
                Assert.True(unlocked <= max);
                steps.Add(unlocked);
            });

        Assert.Equal(5, released);
        Assert.Equal(new[] { 2, 4, 5 }, steps);
        Assert.Equal(5, semaphore.CurrentCount);
    }

    [Fact]
    public async Task run_async_feeds_one_worker_at_a_time()
    {
        using var semaphore = new SemaphoreSlim(0, 4);
        var steps = new List<int>();

        var released = await concurrency_ramp.run_async(
            semaphore, workers: 4, ramp_batch_size: 1, ramp_interval_ms: 1,
            CancellationToken.None,
            (unlocked, _) => steps.Add(unlocked));

        Assert.Equal(4, released);
        Assert.Equal(new[] { 1, 2, 3, 4 }, steps);
        Assert.Equal(4, semaphore.CurrentCount);
    }

    [Fact]
    public async Task run_async_respects_cancellation_between_steps()
    {
        using var semaphore = new SemaphoreSlim(0, 10);
        using var cts = new CancellationTokenSource();
        var steps = new List<int>();

        var ramp = concurrency_ramp.run_async(
            semaphore, workers: 10, ramp_batch_size: 2, ramp_interval_ms: 200,
            cts.Token,
            (unlocked, _) =>
            {
                steps.Add(unlocked);
                if (unlocked >= 2)
                    cts.Cancel();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ramp);
        Assert.Equal(new[] { 2 }, steps);
        Assert.Equal(2, semaphore.CurrentCount);
    }
}
