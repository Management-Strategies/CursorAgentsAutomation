using System.Text;
using System.Text.Json;
using cursor_grading_web.Models;

namespace cursor_grading_web.Services;

/// <summary>
/// OpenAI-compatible chat completions client used for DeepSeek and Gemini.
/// </summary>
public class llm_grading_service
{
    private readonly IHttpClientFactory _http_client_factory;
    private readonly llm_provider_catalog _catalog;
    private readonly ILogger<llm_grading_service> _logger;

    public llm_grading_service(
        IHttpClientFactory http_client_factory,
        llm_provider_catalog catalog,
        ILogger<llm_grading_service> logger)
    {
        _http_client_factory = http_client_factory;
        _catalog = catalog;
        _logger = logger;
    }

    public async Task<llm_grade_response> grade_async(
        string prompt,
        string? provider,
        CancellationToken ct)
    {
        var settings = _catalog.get(provider);
        var http_client = _http_client_factory.CreateClient(settings.provider);

        var request_body = new
        {
            model = settings.model,
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
        cts.CancelAfter(TimeSpan.FromSeconds(120));

        var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = content
        };

        var response = await http_client.SendAsync(request, cts.Token);
        var response_body = await response.Content.ReadAsStringAsync(cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            var snippet = response_body.Length > 400 ? response_body[..400] : response_body;
            throw new HttpRequestException(
                $"{settings.display_name} HTTP {(int)response.StatusCode} {response.ReasonPhrase} " +
                $"(model={settings.model}): {snippet}");
        }

        using var doc = JsonDocument.Parse(response_body);

        var root = doc.RootElement;
        var (cache_hit, cache_miss, completion) = parse_usage(root);
        var cost = settings.compute_cost(cache_hit, cache_miss, completion);

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

        _logger.LogWarning("{Provider} response missing expected structure: {Response}",
            settings.display_name, response_body);
        return new llm_grade_response("", cache_hit, cache_miss, completion, cost);
    }

    public static (int cache_hit, int cache_miss, int completion) parse_usage(JsonElement root)
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

        if (cache_hit == 0 && cache_miss == 0 &&
            usage.TryGetProperty("prompt_tokens", out var prompt_el))
        {
            cache_miss = prompt_el.GetInt32();
        }

        return (cache_hit, cache_miss, completion);
    }

    public async Task<deep_seek_balance?> get_balance_async(
        string? provider = null,
        CancellationToken ct = default)
    {
        var settings = _catalog.try_get(provider);
        if (settings == null || !settings.supports_balance)
            return null;

        try
        {
            var http_client = _http_client_factory.CreateClient(settings.provider);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            var response = await http_client.GetAsync("user/balance", cts.Token);
            response.EnsureSuccessStatusCode();

            var response_body = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(response_body);
            var root = doc.RootElement;

            var is_available = root.TryGetProperty("is_available", out var avail_el) && avail_el.GetBoolean();

            if (!root.TryGetProperty("balance_infos", out var infos) || infos.GetArrayLength() == 0)
            {
                _logger.LogWarning("Balance response missing balance_infos: {Response}", response_body);
                return null;
            }

            JsonElement? chosen = null;
            foreach (var info in infos.EnumerateArray())
            {
                if (info.TryGetProperty("currency", out var cur) &&
                    string.Equals(cur.GetString(), "USD", StringComparison.OrdinalIgnoreCase))
                {
                    chosen = info;
                    break;
                }
            }

            chosen ??= infos[0];
            var bal = chosen.Value;

            return new deep_seek_balance(
                is_available,
                bal.TryGetProperty("currency", out var currency_el) ? currency_el.GetString() ?? "USD" : "USD",
                parse_decimal(bal, "total_balance"),
                parse_decimal(bal, "granted_balance"),
                parse_decimal(bal, "topped_up_balance"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch {Provider} balance", settings.display_name);
            return null;
        }
    }

    private static decimal parse_decimal(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var prop))
            return 0m;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var n))
            return n;

        if (prop.ValueKind == JsonValueKind.String &&
            decimal.TryParse(prop.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var from_string))
            return from_string;

        return 0m;
    }
}
