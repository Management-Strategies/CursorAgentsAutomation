namespace cursor_grading_web.Models;

public class grading_options
{
    public const string section_name = "grading";

    public int max_workers { get; set; } = 6;
    public int per_row_timeout_seconds { get; set; } = 180;
    public int max_retries { get; set; } = 1;
    public int save_every { get; set; } = 5;
}

public class column_options
{
    public const string section_name = "columns";

    public string website { get; set; } = "Website Link";
    public string products { get; set; } = "Company primary products";
    public string about { get; set; } = "about Company who they are selling";
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