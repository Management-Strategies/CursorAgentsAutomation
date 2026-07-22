namespace cursor_grading_web.Models;

public class grading_options
{
    public const string section_name = "grading";

    public int max_workers { get; set; } = 6;
    public int per_row_timeout_seconds { get; set; } = 180;
    public int max_retries { get; set; } = 1;
    public int save_every { get; set; } = 5;

    /// <summary>How many worker permits to unlock per ramp step. Use 1 to feed workers one at a time. &lt;=0 or &gt;= max workers disables ramp.</summary>
    public int ramp_batch_size { get; set; } = 1;

    /// <summary>Milliseconds to wait after unlocking a batch before unlocking the next (gives each worker time to start).</summary>
    public int ramp_interval_ms { get; set; } = 8000;
}

public class column_options
{
    public const string section_name = "columns";

    public string website { get; set; } = "Website Link";
    public string products { get; set; } = "Company primary products";
    public string about { get; set; } = "about Company";
    public string grade_out { get; set; } = "WEBSITE_GRADE";
    public string comment_out { get; set; } = "Comment";
}

public class deep_seek_options
{
    public const string section_name = "deep_seek";

    public string api_key { get; set; } = "";
    public string model { get; set; } = "deepseek-v4-pro";
    public string base_url { get; set; } = "https://api.deepseek.com";

    // USD per 1M tokens (deepseek-v4-pro defaults from DeepSeek pricing)
    public decimal input_cache_hit_per_million { get; set; } = 0.003625m;
    public decimal input_cache_miss_per_million { get; set; } = 0.435m;
    public decimal output_per_million { get; set; } = 0.87m;

    public decimal compute_cost(int cache_hit_tokens, int cache_miss_tokens, int completion_tokens)
    {
        return cache_hit_tokens / 1_000_000m * input_cache_hit_per_million
             + cache_miss_tokens / 1_000_000m * input_cache_miss_per_million
             + completion_tokens / 1_000_000m * output_per_million;
    }
}

public class gemini_options
{
    public const string section_name = "gemini";

    public string api_key { get; set; } = "";
    public string model { get; set; } = "gemini-2.5-flash";
    public string base_url { get; set; } = "https://generativelanguage.googleapis.com/v1beta/openai";

    // USD per 1M tokens (gemini-2.5-flash approximate defaults; adjust in appsettings)
    public decimal input_per_million { get; set; } = 0.30m;
    public decimal output_per_million { get; set; } = 2.50m;

    public decimal compute_cost(int prompt_tokens, int completion_tokens)
    {
        return prompt_tokens / 1_000_000m * input_per_million
             + completion_tokens / 1_000_000m * output_per_million;
    }
}

public class llm_options
{
    public const string section_name = "llm";

    /// <summary>Active provider: "deepseek" or "gemini".</summary>
    public string provider { get; set; } = "deepseek";
}

/// <summary>Resolved settings for the active LLM provider (registered as singleton).</summary>
public class active_llm_settings
{
    public string provider { get; init; } = "deepseek";
    public string display_name { get; init; } = "DeepSeek";
    public string model { get; init; } = "";
    public string base_url { get; init; } = "";
    public string api_key { get; init; } = "";
    public bool supports_balance { get; init; }

    public decimal input_cache_hit_per_million { get; init; }
    public decimal input_cache_miss_per_million { get; init; }
    public decimal output_per_million { get; init; }

    public decimal compute_cost(int cache_hit_tokens, int cache_miss_tokens, int completion_tokens)
    {
        return cache_hit_tokens / 1_000_000m * input_cache_hit_per_million
             + cache_miss_tokens / 1_000_000m * input_cache_miss_per_million
             + completion_tokens / 1_000_000m * output_per_million;
    }
}
