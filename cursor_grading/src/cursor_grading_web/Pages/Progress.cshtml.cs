using Microsoft.AspNetCore.Mvc.RazorPages;

namespace cursor_grading_web.Pages;

public class progress_model : PageModel
{
    public string? file_name { get; set; }

    public void OnGet(string? file)
    {
        file_name = file ?? "companies_graded.xlsx";
    }
}