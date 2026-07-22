using cursor_grading_web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace cursor_grading_web.Pages;

public class standardize_model : PageModel
{
    private readonly spreadsheet_standardize_service _standardize;

    public standardize_model(spreadsheet_standardize_service standardize)
    {
        _standardize = standardize;
    }

    [BindProperty]
    public string? base_format_name { get; set; }

    [BindProperty]
    public IFormFile? input_file { get; set; }

    [BindProperty]
    public string? sheet_name { get; set; }

    [BindProperty]
    public string output_name { get; set; } = "companies_standardized.xlsx";

    [BindProperty]
    public string? temp_source_path { get; set; }

    [BindProperty]
    public List<string> target_headers { get; set; } = new();

    [BindProperty]
    public List<string> source_headers { get; set; } = new();

    /// <summary>Selected source column for each target (same order as target_headers). Empty = leave blank.</summary>
    [BindProperty]
    public List<string> mapped_source { get; set; } = new();

    public List<SelectListItem> base_format_options { get; private set; } = new();
    public bool show_mapping { get; private set; }
    public string? message { get; private set; }
    public bool is_error { get; private set; }
    public string? download_url { get; private set; }
    public string? output_saved_name { get; private set; }
    public int? rows_written { get; private set; }

    public void OnGet()
    {
        load_base_format_options();
    }

    public IActionResult OnPostPrepare()
    {
        load_base_format_options();

        try
        {
            if (string.IsNullOrWhiteSpace(base_format_name))
            {
                set_error("Select a base-format spreadsheet.");
                return Page();
            }

            if (input_file == null || input_file.Length == 0)
            {
                set_error("Upload a source Excel file (.xlsx).");
                return Page();
            }

            if (!input_file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                set_error("Only .xlsx files are supported.");
                return Page();
            }

            var base_path = _standardize.resolve_base_format_path(base_format_name);
            target_headers = _standardize.read_headers(base_path).ToList();
            if (target_headers.Count == 0)
            {
                set_error("The selected base-format file has no header row.");
                return Page();
            }

            temp_source_path = _standardize.save_temp_upload(input_file);
            source_headers = _standardize.read_headers(temp_source_path, sheet_name).ToList();
            if (source_headers.Count == 0)
            {
                set_error("The uploaded spreadsheet has no header row.");
                return Page();
            }

            mapped_source = _standardize.suggest_source_for_targets(target_headers, source_headers).ToList();
            show_mapping = true;
            message = $"Map each base-format column to a source column, then convert. Source has {source_headers.Count} columns; base format has {target_headers.Count}.";
            return Page();
        }
        catch (Exception ex)
        {
            set_error(ex.Message);
            return Page();
        }
    }

    public IActionResult OnPostConvert()
    {
        load_base_format_options();

        try
        {
            if (string.IsNullOrWhiteSpace(temp_source_path) || !System.IO.File.Exists(temp_source_path))
            {
                set_error("Source upload expired. Upload the file again and prepare mapping.");
                return Page();
            }

            if (target_headers.Count == 0)
            {
                set_error("Missing target columns. Start over from Prepare.");
                return Page();
            }

            // Align mapped_source length with targets
            while (mapped_source.Count < target_headers.Count)
                mapped_source.Add("");
            if (mapped_source.Count > target_headers.Count)
                mapped_source = mapped_source.Take(target_headers.Count).ToList();

            var output_path = _standardize.build_output_path(output_name);
            var count = _standardize.write_standardized(
                temp_source_path,
                sheet_name,
                target_headers,
                mapped_source.Cast<string?>().ToList(),
                output_path);

            output_saved_name = Path.GetFileName(output_path);
            download_url = $"/standardized/{Uri.EscapeDataString(output_saved_name)}";
            rows_written = count;
            show_mapping = false;
            message = $"Saved {count} data row(s) to standardized/{output_saved_name}.";
            is_error = false;

            // Clear temp for next run
            try { System.IO.File.Delete(temp_source_path); } catch { /* ignore */ }
            temp_source_path = null;
            target_headers = new();
            source_headers = new();
            mapped_source = new();

            return Page();
        }
        catch (Exception ex)
        {
            // Keep mapping UI if we still have data
            if (target_headers.Count > 0 && source_headers.Count > 0)
                show_mapping = true;
            set_error(ex.Message);
            return Page();
        }
    }

    private void load_base_format_options()
    {
        _standardize.ensure_directories();
        base_format_options = _standardize.list_base_formats()
            .Select(n => new SelectListItem(n, n, string.Equals(n, base_format_name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (string.IsNullOrWhiteSpace(base_format_name) && base_format_options.Count > 0)
            base_format_name = base_format_options[0].Value;
    }

    private void set_error(string msg)
    {
        message = msg;
        is_error = true;
    }
}
