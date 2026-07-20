using cursor_grading_web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using cursor_grading_web.Models;

namespace cursor_grading_web.Pages;

public class index_model : PageModel
{
    private readonly grading_background_service _grading_service;
    private readonly grading_options _grading_options;
    private readonly IWebHostEnvironment _env;

    [BindProperty]
    public IFormFile? input_file { get; set; }

    [BindProperty]
    public string output_name { get; set; } = "companies_graded.xlsx";

    [BindProperty]
    public string? sheet_name { get; set; }

    [BindProperty]
    public int max_workers { get; set; } = 6;

    public string? message { get; set; }
    public bool is_error { get; set; }

    public index_model(
        grading_background_service grading_service,
        IOptions<grading_options> grading_options,
        IWebHostEnvironment env)
    {
        _grading_service = grading_service;
        _grading_options = grading_options.Value;
        _env = env;
    }

    public void OnGet()
    {
        max_workers = _grading_options.max_workers;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (input_file == null || input_file.Length == 0)
        {
            message = "Please select an Excel file.";
            is_error = true;
            return Page();
        }

        if (!input_file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            message = "Only .xlsx files are supported.";
            is_error = true;
            return Page();
        }

        // Ensure uploads directory exists
        var uploads_dir = Path.Combine(_env.ContentRootPath, "wwwroot", "uploads");
        Directory.CreateDirectory(uploads_dir);

        // Save uploaded file
        var input_path = Path.Combine(uploads_dir, $"upload_{DateTime.Now:yyyyMMdd_HHmmss}_{input_file.FileName}");
        await using (var stream = new FileStream(input_path, FileMode.Create))
        {
            await input_file.CopyToAsync(stream);
        }

        // Output path
        var output_name_safe = string.IsNullOrWhiteSpace(output_name) ? "companies_graded.xlsx" : output_name;
        if (!output_name_safe.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            output_name_safe += ".xlsx";
        var output_path = Path.Combine(uploads_dir, output_name_safe);

        // Copy input to output so we start grading from a copy
        System.IO.File.Copy(input_path, output_path, overwrite: true);

        // Submit job
        var request = new grading_job_request(input_path, output_path, sheet_name);
        var submitted = _grading_service.submit_job(request);

        if (submitted)
        {
            return RedirectToPage("progress", new { file = output_name_safe });
        }
        else
        {
            message = "Failed to start grading job. Another job may be running.";
            is_error = true;
            return Page();
        }
    }
}