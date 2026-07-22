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
            catch (OperationCanceledException)
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
        }
    }

    private async Task run_grading_job(grading_job_request job, CancellationToken ct)
    {
        using var scope = _scope_factory.CreateScope();
        var excel = scope.ServiceProvider.GetRequiredService<excel_service>();
        var scraper = scope.ServiceProvider.GetRequiredService<web_scraper_service>();
        var deep_seek = scope.ServiceProvider.GetRequiredService<deep_seek_service>();
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

        _logger.LogInformation("Starting grading: {Count} companies, {Workers} workers",
            pending.Count, options.max_workers);

        var semaphore = new SemaphoreSlim(options.max_workers);
        var completed_count = 0;
        var total = pending.Count;
        // Micro-dollars (1e-6 USD) for thread-safe Interlocked accumulation
        long total_cost_micros = 0;

        // Use a lock for thread-safe Excel writes (ClosedXML is not thread-safe)
        var file_lock = new object();

        var tasks = pending.Select(async row =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                return await grade_one_row(row, scraper, deep_seek, builder, parser, options, ct);
            }
            finally
            {
                semaphore.Release();
            }
        });

        // Process as they complete
        var chunked = tasks.Chunk(options.save_every);
        foreach (var chunk in chunked)
        {
            var results = await Task.WhenAll(chunk);
            foreach (var result in results)
            {
                // Write to Excel (thread-safe via lock)
                lock (file_lock)
                {
                    excel.write_grade(job.output_path, result, columns);
                }

                Interlocked.Increment(ref completed_count);
                var row_micros = (long)Math.Round(result.cost_usd * 1_000_000m, MidpointRounding.AwayFromZero);
                var job_micros = Interlocked.Add(ref total_cost_micros, row_micros);
                var row_cost = result.cost_usd;
                var job_cost = job_micros / 1_000_000m;

                // Push progress via SignalR (including spend after every company)
                await _hub_context.Clients.All.SendAsync("grading_progress",
                    completed_count, total,
                    result.grade, result.reason,
                    (double)row_cost, (double)job_cost,
                    cancellationToken: ct);

                _logger.LogInformation(
                    "[{Done}/{Total}] Row {Row} -> {Grade}: {Reason} (row ${RowCost:F6}, total ${JobCost:F6})",
                    completed_count, total, result.row_index, result.grade, result.reason,
                    row_cost, job_cost);
            }
        }

        _logger.LogInformation("Grading complete. {Count} rows processed, total spend ${Cost:F6}",
            completed_count, total_cost_micros / 1_000_000m);
    }

    private async Task<grade_result> grade_one_row(
        company_row row,
        web_scraper_service scraper,
        deep_seek_service deep_seek,
        prompt_builder builder,
        grade_parser parser,
        grading_options options,
        CancellationToken ct)
    {
        // Step 1: Scrape website
        var scrape = await scraper.scrape_async(row.website, ct);

        if (!scrape.success)
        {
            return new grade_result(row.row_index, "UNABLE", $"Scrape failed: {scrape.error_reason}");
        }

        // Step 2: Build prompt and call DeepSeek with retries
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
                var llm_response = await deep_seek.grade_async(prompt, ct);
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
    string? sheet_name
);

public class grading_job_status
{
    public grading_job_request? request { get; set; }
    public string status { get; set; } = "idle";
    public string? error { get; set; }
    public CancellationTokenSource? cts { get; set; }
}
