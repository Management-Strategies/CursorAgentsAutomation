using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using cursor_grading_web.Models;

namespace cursor_grading_web.Services;

public class deep_seek_service
{
    private readonly HttpClient _http_client;
    private readonly deep_seek_options _options;
    private readonly ILogger<deep_seek_service> _logger;

    public deep_seek_service(HttpClient http_client, IOptions<deep_seek_options> options, ILogger<deep_seek_service> logger)
    {
        _http_client = http_client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<llm_grade_response> grade_async(string prompt, CancellationToken ct)
    {
        var request_body = new
        {
            model = _options.model,
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You are a B2B lead screener. You respond ONLY with valid JSON in the format {\"grade\": \"GOOD|MAYBE|UNABLE\", \"reason\": \"...\"}. No markdown, no code fences, no extra text."
                },
                new { role = "user", content = prompt }
            },
            response_format = new { type = "json_object" },
            temperature = 0.2
        };

        var json = JsonSerializer.Serialize(request_body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120)); // 2 min timeout for LLM

        var request = new HttpRequestMessage(HttpMethod.Post, "/chat/completions")
        {
            Content = content
        };

        var response = await _http_client.SendAsync(request, cts.Token);
        response.EnsureSuccessStatusCode();

        var response_body = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(response_body);

        var root = doc.RootElement;
        var (cache_hit, cache_miss, completion) = parse_usage(root);
        var cost = _options.compute_cost(cache_hit, cache_miss, completion);

        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var msg_content))
            {
                return new llm_grade_response(
                    msg_content.GetString() ?? "",
                    cache_hit,
                    cache_miss,
                    completion,
                    cost);
            }
        }

        _logger.LogWarning("DeepSeek response missing expected structure: {Response}", response_body);
        return new llm_grade_response("", cache_hit, cache_miss, completion, cost);
    }

    private static (int cache_hit, int cache_miss, int completion) parse_usage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage))
            return (0, 0, 0);

        var cache_hit = usage.TryGetProperty("prompt_cache_hit_tokens", out var hit_el)
            ? hit_el.GetInt32()
            : 0;
        var cache_miss = usage.TryGetProperty("prompt_cache_miss_tokens", out var miss_el)
            ? miss_el.GetInt32()
            : 0;
        var completion = usage.TryGetProperty("completion_tokens", out var completion_el)
            ? completion_el.GetInt32()
            : 0;

        // Fallback when cache breakdown is absent: treat all prompt tokens as miss
        if (cache_hit == 0 && cache_miss == 0 &&
            usage.TryGetProperty("prompt_tokens", out var prompt_el))
        {
            cache_miss = prompt_el.GetInt32();
        }

        return (cache_hit, cache_miss, completion);
    }
}
