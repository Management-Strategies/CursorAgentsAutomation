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

    /// <summary>
    /// Asks the LLM to map each target (base-format) header to a source header using sample rows.
    /// Returns one source header (or empty) per target, in target order. Unknown sources are stripped.
    /// </summary>
    public async Task<IReadOnlyList<string>> suggest_column_mapping_async(
        IReadOnlyList<string> target_headers,
        IReadOnlyList<string> source_headers,
        IReadOnlyList<Dictionary<string, string>> sample_rows,
        string? provider = null,
        CancellationToken ct = default)
    {
        var settings = _catalog.get(provider);
        var http_client = _http_client_factory.CreateClient(settings.provider);

        var user_payload = new
        {
            target_headers,
            source_headers,
            sample_rows
        };

        var user_prompt =
            "Map each target_headers entry to the best matching source_headers column.\n" +
            "Use sample_rows (up to 10) to disambiguate similar names.\n" +
            "Respond ONLY with JSON of the form:\n" +
            "{\"mappings\":[{\"target\":\"...\",\"source\":\"... or null\"}]}\n" +
            "Include every target exactly once. source must be an exact source_headers string, or null/empty if unsure.\n" +
            "CRITICAL: Each source column may be assigned to at most ONE target. Never reuse the same source for two targets.\n" +
            "If several targets could match one source, pick the single best target for that source and leave the others null.\n" +
            "DOMAIN HINT: DESCRIPTION (and similar general description columns) is a more general term. " +
            "It fits better with [about Company] than with [Company primary products]. " +
            "Products is more specific and less general than [about Company]; prefer mapping Description → about Company " +
            "and leave Company primary products for product/catalog/offering columns.\n\n" +
            JsonSerializer.Serialize(user_payload);

        var request_body = new
        {
            model = settings.model,
            messages = new[]
            {
                new
                {
                    role = "system",
                    content =
                        "You map spreadsheet column headers. Respond ONLY with valid JSON: " +
                        "{\"mappings\":[{\"target\":\"string\",\"source\":\"string|null\"}]}. " +
                        "No markdown, no code fences, no extra text. " +
                        "source values must be exact strings from source_headers, or null. " +
                        "Never assign the same source to more than one target; choose the best single match and leave other targets null. " +
                        "Description-like source columns map preferentially to about Company, not Company primary products."
                },
                new { role = "user", content = user_prompt }
            },
            response_format = new { type = "json_object" },
            temperature = 0.1
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
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new InvalidOperationException("LLM mapping response missing choices.");

        var choice = choices[0];
        if (!choice.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var msg_content))
            throw new InvalidOperationException("LLM mapping response missing message content.");

        var content_text = msg_content.GetString() ?? "";
        return sanitize_column_mappings(content_text, target_headers, source_headers);
    }

    /// <summary>
    /// Parses LLM mapping JSON and returns one sanitized source header per target (empty if unknown/missing).
    /// </summary>
    public static IReadOnlyList<string> sanitize_column_mappings(
        string llm_json,
        IReadOnlyList<string> target_headers,
        IReadOnlyList<string> source_headers)
    {
        var result = new string[target_headers.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = "";

        if (string.IsNullOrWhiteSpace(llm_json) || target_headers.Count == 0)
            return result;

        var source_lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var src in source_headers)
        {
            if (!string.IsNullOrWhiteSpace(src))
                source_lookup[src.Trim()] = src;
        }

        var target_index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < target_headers.Count; i++)
        {
            var t = target_headers[i]?.Trim() ?? "";
            if (!string.IsNullOrEmpty(t) && !target_index.ContainsKey(t))
                target_index[t] = i;
        }

        using var doc = JsonDocument.Parse(llm_json);
        var root = doc.RootElement;

        // Accept {"mappings":[...]} or a bare object { "Target": "Source", ... }
        if (root.TryGetProperty("mappings", out var mappings) && mappings.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in mappings.EnumerateArray())
            {
                var target = read_string_prop(item, "target");
                var source = read_string_prop(item, "source");
                apply_mapping(result, target_index, source_lookup, target, source);
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (string.Equals(prop.Name, "mappings", StringComparison.OrdinalIgnoreCase))
                    continue;

                var source = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Null => null,
                    _ => prop.Value.ToString()
                };
                apply_mapping(result, target_index, source_lookup, prop.Name, source);
            }
        }

        enforce_unique_sources(result, target_headers);
        return result;
    }

    /// <summary>
    /// If the same source was mapped to multiple targets, keep only the best-scoring target and clear the rest.
    /// </summary>
    private static void enforce_unique_sources(string[] result, IReadOnlyList<string> target_headers)
    {
        var best_for_source = new Dictionary<string, (int idx, int score)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < result.Length; i++)
        {
            var src = result[i];
            if (string.IsNullOrWhiteSpace(src))
                continue;

            var score = mapping_score(target_headers[i], src);
            if (!best_for_source.TryGetValue(src, out var prev) || score > prev.score)
                best_for_source[src] = (i, score);
        }

        for (var i = 0; i < result.Length; i++)
        {
            var src = result[i];
            if (string.IsNullOrWhiteSpace(src))
                continue;
            if (!best_for_source.TryGetValue(src, out var best) || best.idx != i)
                result[i] = "";
        }
    }

    private static int mapping_score(string? target, string? source)
    {
        var t = (target ?? "").Trim();
        var s = (source ?? "").Trim();
        if (t.Length == 0 || s.Length == 0)
            return 0;

        if (string.Equals(t, s, StringComparison.OrdinalIgnoreCase))
            return 1000;

        // Description is general → prefer about Company over Company primary products.
        var source_is_description = s.Contains("description", StringComparison.OrdinalIgnoreCase);
        if (source_is_description)
        {
            if (t.Contains("about", StringComparison.OrdinalIgnoreCase))
                return 900;
            if (t.Contains("product", StringComparison.OrdinalIgnoreCase))
                return 100;
        }

        if (s.Contains(t, StringComparison.OrdinalIgnoreCase) ||
            t.Contains(s, StringComparison.OrdinalIgnoreCase))
            return 500 + Math.Min(t.Length, s.Length);

        var t_tokens = tokenize(t);
        var s_tokens = tokenize(s);
        if (t_tokens.Count == 0 || s_tokens.Count == 0)
            return 1;

        var overlap = t_tokens.Count(tok => s_tokens.Contains(tok));
        return overlap * 50;
    }

    private static HashSet<string> tokenize(string value)
    {
        var parts = value.Split(
            [' ', '_', '-', '/', '(', ')', '[', ']', '.', ',', ':'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
    }

    private static void apply_mapping(
        string[] result,
        Dictionary<string, int> target_index,
        Dictionary<string, string> source_lookup,
        string? target,
        string? source)
    {
        if (string.IsNullOrWhiteSpace(target))
            return;
        if (!target_index.TryGetValue(target.Trim(), out var idx))
            return;

        if (string.IsNullOrWhiteSpace(source))
        {
            result[idx] = "";
            return;
        }

        if (source_lookup.TryGetValue(source.Trim(), out var canonical))
            result[idx] = canonical;
        else
            result[idx] = "";
    }

    private static string? read_string_prop(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop))
            return null;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Null => null,
            _ => prop.ToString()
        };
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
