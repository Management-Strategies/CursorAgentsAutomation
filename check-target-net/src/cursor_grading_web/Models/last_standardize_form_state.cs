namespace cursor_grading_web.Models;

public class last_standardize_form_state
{
    public string? base_format_name { get; set; }
    public string? original_file_name { get; set; }
    public string? saved_input_file_name { get; set; }
    public string? sheet_name { get; set; }
    public string output_name { get; set; } = "companies_standardized.xlsx";
}
