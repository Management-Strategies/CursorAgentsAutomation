namespace cursor_grading_web.Models;

public record llm_grade_response(
    string content,
    int cache_hit_tokens,
    int cache_miss_tokens,
    int completion_tokens,
    decimal cost_usd
);
