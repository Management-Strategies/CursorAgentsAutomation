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
    private readonly last_form_state_store _last_form_store;
    private readonly llm_provider_catalog _llm_catalog;
    private readonly IWebHostEnvironment _env;

    [BindProperty]
    public IFormFile? input_file { get; set; }

    [BindProperty]
    public bool use_last_file { get; set; }

    [BindProperty]
    public string output_name { get; set; } = "companies_graded.xlsx";

    [BindProperty]
    public string? sheet_name { get; set; }

    [BindProperty]
    public int max_workers { get; set; } = 6;

    [BindProperty]
    public string llm_provider { get; set; } = "deepseek";

    public string? message { get; set; }
    public bool is_error { get; set; }

    public string? last_file_display_name { get; set; }
    public bool has_last_file { get; set; }

    public bool deepseek_available { get; set; }
    public bool gemini_available { get; set; }
    public string? deepseek_model { get; set; }
    public string? gemini_model { get; set; }

    public string llm_display_name { get; set; } = "DeepSeek";
    public bool llm_supports_balance { get; set; }
    public string llm_model { get; set; } = "";

    public index_model(
        grading_background_service grading_service,
        IOptions<grading_options> grading_options,
        last_form_state_store last_form_store,
        llm_provider_catalog llm_catalog,
        IWebHostEnvironment env)
    {
        _grading_service = grading_service;
        _grading_options = grading_options.Value;
        _last_form_store = last_form_store;
        _llm_catalog = llm_catalog;
        _env = env;
        refresh_provider_flags();
    }

    public void OnGet()
    {
        restore_last_form_state();
        apply_selected_provider_display();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        refresh_provider_flags();
        llm_provider = llm_provider_catalog.Normalize(llm_provider);

        if (!_llm_catalog.is_available(llm_provider))
        {
            message = $"Provider '{llm_provider}' is not configured (missing API key).";
            is_error = true;
            restore_last_form_state();
            apply_selected_provider_display();
            return Page();
        }

        var uploads_dir = Path.Combine(_env.ContentRootPath, "wwwroot", "uploads");
        Directory.CreateDirectory(uploads_dir);

        string input_path;
        string original_file_name;

        if (use_last_file)
        {
            var last = _last_form_store.load();
            var last_path = last == null ? null : _last_form_store.resolve_saved_input_path(last);
            if (last_path == null)
            {
                message = "The last chosen file is no longer available. Please select a file.";
                is_error = true;
                restore_last_form_state();
                apply_selected_provider_display();
                return Page();
            }

            input_path = last_path;
            original_file_name = last!.original_file_name ?? Path.GetFileName(last_path);
        }
        else
        {
            if (input_file == null || input_file.Length == 0)
            {
                message = "Please select an Excel file.";
                is_error = true;
                restore_last_form_state();
                apply_selected_provider_display();
                return Page();
            }

            if (!input_file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                message = "Only .xlsx files are supported.";
                is_error = true;
                restore_last_form_state();
                apply_selected_provider_display();
                return Page();
            }

            input_path = Path.Combine(uploads_dir, $"upload_{DateTime.Now:yyyyMMdd_HHmmss}_{input_file.FileName}");
            await using (var stream = new FileStream(input_path, FileMode.Create))
            {
                await input_file.CopyToAsync(stream);
            }

            original_file_name = input_file.FileName;
        }

        var output_name_safe = string.IsNullOrWhiteSpace(output_name) ? "companies_graded.xlsx" : output_name;
        if (!output_name_safe.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            output_name_safe += ".xlsx";
        var output_path = Path.Combine(uploads_dir, output_name_safe);

        System.IO.File.Copy(input_path, output_path, overwrite: true);

        var workers = Math.Clamp(max_workers, 1, 100);
        max_workers = workers;

        _last_form_store.save(new last_form_state
        {
            original_file_name = original_file_name,
            saved_input_file_name = Path.GetFileName(input_path),
            output_name = output_name_safe,
            sheet_name = sheet_name,
            max_workers = workers,
            llm_provider = llm_provider
        });

        var request = new grading_job_request(input_path, output_path, sheet_name, workers, llm_provider);
        var submitted = _grading_service.submit_job(request);

        if (submitted)
        {
            return RedirectToPage("progress", new { file = output_name_safe, provider = llm_provider });
        }

        message = "Failed to start grading job. Another job may be running.";
        is_error = true;
        restore_last_form_state();
        apply_selected_provider_display();
        return Page();
    }

    private void refresh_provider_flags()
    {
        deepseek_available = _llm_catalog.is_available("deepseek");
        gemini_available = _llm_catalog.is_available("gemini");
        deepseek_model = _llm_catalog.try_get("deepseek")?.model;
        gemini_model = _llm_catalog.try_get("gemini")?.model;
    }

    private void apply_selected_provider_display()
    {
        llm_provider = llm_provider_catalog.Normalize(llm_provider);
        if (!_llm_catalog.is_available(llm_provider))
            llm_provider = _llm_catalog.DefaultProvider;

        var settings = _llm_catalog.get(llm_provider);
        llm_display_name = settings.display_name;
        llm_supports_balance = settings.supports_balance;
        llm_model = settings.model;
    }

    private void restore_last_form_state()
    {
        max_workers = _grading_options.max_workers;
        llm_provider = _llm_catalog.DefaultProvider;

        var last = _last_form_store.load();
        if (last == null)
        {
            apply_selected_provider_display();
            return;
        }

        if (!string.IsNullOrWhiteSpace(last.output_name))
            output_name = last.output_name;
        sheet_name = last.sheet_name;
        if (last.max_workers is >= 1 and <= 100)
            max_workers = last.max_workers;
        if (!string.IsNullOrWhiteSpace(last.llm_provider))
            llm_provider = last.llm_provider;

        var last_path = _last_form_store.resolve_saved_input_path(last);
        if (last_path != null)
        {
            has_last_file = true;
            last_file_display_name = last.original_file_name ?? Path.GetFileName(last_path);
        }

        apply_selected_provider_display();
    }
}
