namespace cursor_grading_web.Models;

public class last_form_state
{
    public string? original_file_name { get; set; }
    public string? saved_input_file_name { get; set; }
    public string output_name { get; set; } = "companies_graded.xlsx";
    public string? sheet_name { get; set; }
    public int max_workers { get; set; } = 6;
    public string llm_provider { get; set; } = "deepseek";
}
