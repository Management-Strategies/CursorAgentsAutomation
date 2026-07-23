using System.Threading.Channels;
using cursor_grading_web.Hubs;
using cursor_grading_web.Models;
using Microsoft.AspNetCore.SignalR;

namespace cursor_grading_web.Services;

public class grading_background_service : BackgroundService
{
    private readonly IServiceScopeFactory _scope_factory;
    private readonly IHubContext<grading_hub> _hub_context;
    private readonly ILogger<grading_background_service> _logger;

    // Channel used to signal a new job request from the UI
    private readonly Channel<grading_job_request> _job_channel =
        Channel.CreateUnbounded<grading_job_request>();

    // Track active job so UI can query status
    private volatile grading_job_status? _active_job;

    public grading_background_service(
        IServiceScopeFactory scope_factory,
        IHubContext<grading_hub> hub_context,
        ILogger<grading_background_service> logger)
    {
        _scope_factory = scope_factory;
        _hub_context = hub_context;
        _logger = logger;
    }

    public grading_job_status? get_active_job() => _active_job;

    public bool submit_job(grading_job_request request)
    {
        return _job_channel.Writer.TryWrite(request);
    }

    public bool cancel_job()
    {
        if (_active_job?.cts is { IsCancellationRequested: false } cts)
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stopping_token)
    {
        await foreach (var job in _job_channel.Reader.ReadAllAsync(stopping_token))
        {
            _active_job = new grading_job_status
            {
                request = job,
                status = "running",
                cts = CancellationTokenSource.CreateLinkedTokenSource(stopping_token)
            };

            try
            {
                await run_grading_job(job, _active_job.cts.Token);
                _active_job.status = "completed";
                await _hub_context.Clients.All.SendAsync("job_complete", job.output_path, stopping_token);
            }
            catch (Exception ex) when (is_cancellation(ex))
            {
                _active_job.status = "cancelled";
                _logger.LogInformation("Grading job cancelled");
                await _hub_context.Clients.All.SendAsync("job_cancelled", cancellationToken: stopping_token);
            }
            catch (Exception ex)
            {
                _active_job.status = "error";
                _active_job.error = ex.Message;
                _logger.LogError(ex, "Grading job failed");
                await _hub_context.Clients.All.SendAsync("job_error", ex.Message, cancellationToken: stopping_token);
            }
            finally
            {
                // Job is finished — drop the cancel token so nothing keeps listening
                _active_job.cts?.Dispose();
                _active_job.cts = null;
            }
        }
    }

    private static bool is_cancellation(Exception ex) =>
        ex is OperationCanceledException
        || (ex is AggregateException ae && ae.Flatten().InnerExceptions.All(static e => e is OperationCanceledException));

    private async Task run_grading_job(grading_job_request job, CancellationToken ct)
    {
        using var scope = _scope_factory.CreateScope();
        var excel = scope.ServiceProvider.GetRequiredService<excel_service>();
        var scraper = scope.ServiceProvider.GetRequiredService<web_scraper_service>();
        var llm = scope.ServiceProvider.GetRequiredService<llm_grading_service>();
        var builder = scope.ServiceProvider.GetRequiredService<prompt_builder>();
        var parser = scope.ServiceProvider.GetRequiredService<grade_parser>();
        var options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<grading_options>>().Value;
        var columns = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<column_options>>().Value;

        // Load workbook
        var (pending, grade_examples) = excel.load_workbook(job.input_path, job.sheet_name, columns, 5);

        if (pending.Count == 0)
        {
            _logger.LogInformation("No pending companies to grade");
            return;
        }

        var workers = job.max_workers is >= 1 and <= 100
            ? job.max_workers
            : Math.Clamp(options.max_workers, 1, 100);

        _logger.LogInformation(
            "Starting grading: {Count} companies, {Workers} workers, provider {Provider} (feed {Batch} every {Interval}ms)",
            pending.Count, workers, job.llm_provider, options.ramp_batch_size, options.ramp_interval_ms);

        // Start with 0 permits; ramp unlocks one-by-one (by default) so each can start before the next.
        // Keep the output workbook open for the whole job so the final grade is not lost to open/save races.
        using var writer = excel.open_writer(job.output_path, job.sheet_name, columns, options.save_every);
        using var semaphore = new SemaphoreSlim(0, workers);
        using var ramp_cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var completed_count = 0;
        var scrape_fail_count = 0;
        var executing_count = 0;
        var total = pending.Count;
        // Micro-dollars (1e-6 USD) for thread-safe Interlocked accumulation
        long total_cost_micros = 0;
        var job_sw = System.Diagnostics.Stopwatch.StartNew();

        // serialize workbook writes (ClosedXML is not thread-safe)
        var file_lock = new object();

        var ramp_task = concurrency_ramp.run_async(
            semaphore,
            workers,
            options.ramp_batch_size,
            options.ramp_interval_ms,
            ramp_cts.Token,
            (unlocked, max) => _logger.LogInformation("Ramp: unlocked {Unlocked}/{Workers}", unlocked, max));

        // Start all row tasks; SemaphoreSlim enforces max concurrent workers.
        var tasks = pending.Select(async row =>
        {
            await semaphore.WaitAsync(ct);
            var executing = Interlocked.Increment(ref executing_count);
            await _hub_context.Clients.All.SendAsync(
                "grading_executing",
                executing,
                CancellationToken.None);

            var row_sw = System.Diagnostics.Stopwatch.StartNew();
            grade_result result;
            try
            {
                result = await grade_one_row(row, scraper, llm, builder, parser, options, job.llm_provider, ct);
            }
            finally
            {
                try { semaphore.Release(); }
                catch (ObjectDisposedException) { /* job ending */ }

                var still_executing = Interlocked.Decrement(ref executing_count);
                await _hub_context.Clients.All.SendAsync(
                    "grading_executing",
                    Math.Max(0, still_executing),
                    CancellationToken.None);
            }

            lock (file_lock)
            {
                writer.write_grade(result);
            }
            row_sw.Stop();

            var done = Interlocked.Increment(ref completed_count);
            var scrape_fails = result.reason.StartsWith("Scrape failed:", StringComparison.Ordinal)
                ? Interlocked.Increment(ref scrape_fail_count)
                : Volatile.Read(ref scrape_fail_count);

            var row_micros = (long)Math.Round(result.cost_usd * 1_000_000m, MidpointRounding.AwayFromZero);
            var job_micros = Interlocked.Add(ref total_cost_micros, row_micros);
            var row_cost = result.cost_usd;
            var job_cost = job_micros / 1_000_000m;
            var row_elapsed_sec = row_sw.Elapsed.TotalSeconds;
            var job_elapsed_sec = job_sw.Elapsed.TotalSeconds;
            var executing_now = Math.Max(0, Volatile.Read(ref executing_count));

            // Progress after the grade is recorded in the workbook (do not cancel mid-notify after write)
            await _hub_context.Clients.All.SendAsync(
                "grading_progress",
                new grading_progress_event(
                    done,
                    total,
                    result.grade,
                    result.reason,
                    (double)row_cost,
                    (double)job_cost,
                    scrape_fails,
                    result.row_index,
                    row.company,
                    row.website,
                    row.products,
                    row.about,
                    row.contact,
                    row.email,
                    row.phone,
                    row_elapsed_sec,
                    job_elapsed_sec,
                    executing_now),
                CancellationToken.None);

            _logger.LogInformation(
                "[{Done}/{Total}] Row {Row} -> {Grade}: {Reason} (row ${RowCost:F6}, total ${JobCost:F6}, scrape_fails {ScrapeFails}, executing {Executing}, row {RowElapsed:F1}s, job {JobElapsed:F1}s)",
                done, total, result.row_index, result.grade, result.reason,
                row_cost, job_cost, scrape_fails, executing_now, row_elapsed_sec, job_elapsed_sec);
        }).ToList();

        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            // All rows done (or cancelled) — stop ramp immediately so we don't keep unlocking
            // unused slots for minutes after the run is over.
            ramp_cts.Cancel();
            try
            {
                await ramp_task;
            }
            catch (OperationCanceledException)
            {
                // Expected: rows finished before ramp reached max workers, or job cancelled
            }

            // Always flush remaining grades to disk before job_complete / download.
            lock (file_lock)
            {
                writer.flush();
            }
        }

        _logger.LogInformation(
            "Grading complete. {Count} rows processed, scrape failures {ScrapeFails}, total spend ${Cost:F6}",
            completed_count, scrape_fail_count, total_cost_micros / 1_000_000m);
    }

    private async Task<grade_result> grade_one_row(
        company_row row,
        web_scraper_service scraper,
        llm_grading_service llm,
        prompt_builder builder,
        grade_parser parser,
        grading_options options,
        string llm_provider,
        CancellationToken ct)
    {
        // Step 1: Scrape website
        var scrape = await scraper.scrape_async(row.website, ct);

        if (!scrape.success)
        {
            return new grade_result(row.row_index, "UNABLE", $"Scrape failed: {scrape.error_reason}");
        }

        // Step 2: Build prompt and call LLM with retries
        var prompt = builder.build(row.website, row.products, row.about, scrape.text_content);

        int total_hit = 0;
        int total_miss = 0;
        int total_completion = 0;
        decimal total_cost = 0m;

        string? last_error = null;
        for (int attempt = 0; attempt <= options.max_retries; attempt++)
        {
            try
            {
                var llm_response = await llm.grade_async(prompt, llm_provider, ct);
                total_hit += llm_response.cache_hit_tokens;
                total_miss += llm_response.cache_miss_tokens;
                total_completion += llm_response.completion_tokens;
                total_cost += llm_response.cost_usd;

                var result = parser.parse(llm_response.content);
                return result with
                {
                    row_index = row.row_index,
                    cache_hit_tokens = total_hit,
                    cache_miss_tokens = total_miss,
                    completion_tokens = total_completion,
                    cost_usd = total_cost
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last_error = $"{ex.GetType().Name}: {ex.Message}";
                if (attempt < options.max_retries)
                    await Task.Delay(2000, ct);
            }
        }

        return new grade_result(
            row.row_index,
            "UNABLE",
            $"Failed after {options.max_retries + 1} attempt(s): {last_error}",
            total_hit,
            total_miss,
            total_completion,
            total_cost);
    }
}

public record grading_job_request(
    string input_path,
    string output_path,
    string? sheet_name,
    int max_workers = 6,
    string llm_provider = "deepseek"
);

public record grading_progress_event(
    int completed,
    int total,
    string grade,
    string reason,
    double row_cost,
    double job_cost,
    int scrape_fails,
    int excel_row,
    string company,
    string website,
    string products,
    string about,
    string contact,
    string email,
    string phone,
    double row_elapsed_sec = 0,
    double job_elapsed_sec = 0,
    int executing = 0
);

public class grading_job_status
{
    public grading_job_request? request { get; set; }
    public string status { get; set; } = "idle";
    public string? error { get; set; }
    public CancellationTokenSource? cts { get; set; }
}
