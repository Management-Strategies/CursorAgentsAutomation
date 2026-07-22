namespace cursor_grading_web.Models;

public record grade_result(
    int row_index,
    string grade,
    string reason,
    int cache_hit_tokens = 0,
    int cache_miss_tokens = 0,
    int completion_tokens = 0,
    decimal cost_usd = 0m
);
