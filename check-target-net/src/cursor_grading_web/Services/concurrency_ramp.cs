namespace cursor_grading_web.Services;

/// <summary>
/// Gradually unlocks SemaphoreSlim permits so jobs do not open all workers at t=0.
/// Default mode: one permit at a time with a delay so each worker can start work first.
/// </summary>
public static class concurrency_ramp
{
    /// <summary>
    /// Releases permits in batches until <paramref name="workers"/> are unlocked.
    /// If batch size is &lt;= 0 or &gt;= workers, releases all permits immediately.
    /// </summary>
    /// <returns>Total permits released (always equals workers when not cancelled early).</returns>
    public static async Task<int> run_async(
        SemaphoreSlim semaphore,
        int workers,
        int ramp_batch_size,
        int ramp_interval_ms,
        CancellationToken ct,
        Action<int, int>? on_step = null)
    {
        if (workers <= 0)
            return 0;

        if (ramp_batch_size <= 0 || ramp_batch_size >= workers)
        {
            semaphore.Release(workers);
            on_step?.Invoke(workers, workers);
            return workers;
        }

        var interval = Math.Max(0, ramp_interval_ms);
        var released = 0;

        while (released < workers)
        {
            ct.ThrowIfCancellationRequested();

            var add = Math.Min(ramp_batch_size, workers - released);
            semaphore.Release(add);
            released += add;
            on_step?.Invoke(released, workers);

            if (released >= workers)
                break;

            // Wait after each unlock so the newly admitted worker can begin
            // (scrape DNS/TLS/HTTP) before the next worker is fed in.
            if (interval > 0)
                await Task.Delay(interval, ct);
        }

        return released;
    }
}
