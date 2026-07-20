namespace cursor_grading_web.Models;

public record scrape_result(bool success, string text_content, string error_reason);