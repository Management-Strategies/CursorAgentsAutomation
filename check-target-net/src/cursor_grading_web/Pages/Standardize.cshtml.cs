using cursor_grading_web.Models;
using cursor_grading_web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace cursor_grading_web.Pages;

public class standardize_model : PageModel
{
    private readonly spreadsheet_standardize_service _standardize;
    private readonly llm_grading_service _llm;
    private readonly last_standardize_form_state_store _last_form_store;

    public standardize_model(
        spreadsheet_standardize_service standardize,
        llm_grading_service llm,
        last_standardize_form_state_store last_form_store)
    {
        _standardize = standardize;
        _llm = llm;
        _last_form_store = last_form_store;
    }

    [BindProperty]
    public string? base_format_name { get; set; }

    [BindProperty]
    public IFormFile? input_file { get; set; }

    [BindProperty]
    public bool use_last_file { get; set; }

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
    public bool has_last_file { get; private set; }
    public string? last_file_display_name { get; private set; }

    public void OnGet()
    {
        restore_last_form_state();
        load_base_format_options();
    }

    public async Task<IActionResult> OnPostPrepareAsync(CancellationToken ct)
    {
        load_base_format_options();

        try
        {
            if (string.IsNullOrWhiteSpace(base_format_name))
            {
                set_error("Select a base-format spreadsheet.");
                restore_last_file_flags();
                return Page();
            }

            string source_path;
            string original_file_name;

            if (use_last_file)
            {
                var last = _last_form_store.load();
                var last_path = last == null ? null : _last_form_store.resolve_saved_input_path(last);
                if (last_path == null)
                {
                    set_error("The last chosen file is no longer available. Please select a file.");
                    restore_last_file_flags();
                    return Page();
                }

                source_path = last_path;
                original_file_name = last!.original_file_name ?? Path.GetFileName(last_path);
            }
            else
            {
                if (input_file == null || input_file.Length == 0)
                {
                    set_error("Upload a source Excel file (.xlsx).");
                    restore_last_file_flags();
                    return Page();
                }

                if (!input_file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    set_error("Only .xlsx files are supported.");
                    restore_last_file_flags();
                    return Page();
                }

                // Persistent copy for "use last file", plus working temp for mapping/convert.
                var persistent = _last_form_store.save_persistent_upload(input_file);
                original_file_name = input_file.FileName;
                source_path = persistent;
            }

            var base_path = _standardize.resolve_base_format_path(base_format_name);
            target_headers = _standardize.read_headers(base_path).ToList();
            if (target_headers.Count == 0)
            {
                set_error("The selected base-format file has no header row.");
                restore_last_file_flags();
                return Page();
            }

            // Working copy under standardized/_temp for convert step.
            temp_source_path = copy_to_temp(source_path, original_file_name);
            source_headers = _standardize.read_headers(temp_source_path, sheet_name).ToList();
            if (source_headers.Count == 0)
            {
                set_error("The uploaded spreadsheet has no header row.");
                restore_last_file_flags();
                return Page();
            }

            _last_form_store.save(new last_standardize_form_state
            {
                base_format_name = base_format_name,
                original_file_name = original_file_name,
                saved_input_file_name = Path.GetFileName(source_path),
                sheet_name = sheet_name,
                output_name = string.IsNullOrWhiteSpace(output_name)
                    ? "companies_standardized.xlsx"
                    : output_name
            });

            var ai_note = "AI suggested mappings; review before convert.";
            try
            {
                var samples = _standardize.read_sample_rows(temp_source_path, sheet_name, max_rows: 10);
                mapped_source = (await _llm.suggest_column_mapping_async(
                    target_headers,
                    source_headers,
                    samples,
                    provider: null,
                    ct)).ToList();

                // Fill any blanks the model left with exact-name matches (never reuse a source).
                var used = new HashSet<string>(
                    mapped_source.Where(s => !string.IsNullOrWhiteSpace(s)),
                    StringComparer.OrdinalIgnoreCase);
                var exact = _standardize.suggest_source_for_targets(target_headers, source_headers);
                for (var i = 0; i < mapped_source.Count && i < exact.Count; i++)
                {
                    if (!string.IsNullOrWhiteSpace(mapped_source[i]))
                        continue;
                    if (string.IsNullOrWhiteSpace(exact[i]))
                        continue;
                    if (!used.Add(exact[i]))
                        continue;
                    mapped_source[i] = exact[i];
                }
            }
            catch (Exception ai_ex)
            {
                mapped_source = _standardize.suggest_source_for_targets(target_headers, source_headers).ToList();
                ai_note = $"AI suggestion failed ({ai_ex.Message}); using exact-name matches instead. Review before convert.";
            }

            show_mapping = true;
            message =
                $"{ai_note} Source has {source_headers.Count} columns; base format has {target_headers.Count}.";
            is_error = false;
            return Page();
        }
        catch (Exception ex)
        {
            set_error(ex.Message);
            restore_last_file_flags();
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
                restore_last_file_flags();
                return Page();
            }

            if (target_headers.Count == 0)
            {
                set_error("Missing target columns. Start over from Prepare.");
                restore_last_file_flags();
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

            // Remember Step 1 choices after successful convert as well.
            var last = _last_form_store.load() ?? new last_standardize_form_state();
            last.base_format_name = base_format_name;
            last.sheet_name = sheet_name;
            last.output_name = Path.GetFileName(output_path);
            _last_form_store.save(last);

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

            restore_last_form_state();
            load_base_format_options();
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

    private string copy_to_temp(string source_path, string original_file_name)
    {
        _standardize.ensure_directories();
        var safe = Path.GetFileNameWithoutExtension(original_file_name);
        foreach (var c in Path.GetInvalidFileNameChars())
            safe = safe.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(safe))
            safe = "upload";

        var name = $"{safe}_{DateTime.UtcNow:yyyyMMddHHmmssfff}.xlsx";
        var dest = Path.Combine(_standardize.temp_dir, name);
        System.IO.File.Copy(source_path, dest, overwrite: true);
        return dest;
    }

    private void restore_last_form_state()
    {
        var last = _last_form_store.load();
        if (last == null)
            return;

        if (!string.IsNullOrWhiteSpace(last.base_format_name))
            base_format_name = last.base_format_name;
        if (!string.IsNullOrWhiteSpace(last.output_name))
            output_name = last.output_name;
        sheet_name = last.sheet_name;
        restore_last_file_flags(last);
    }

    private void restore_last_file_flags(last_standardize_form_state? last = null)
    {
        last ??= _last_form_store.load();
        has_last_file = false;
        last_file_display_name = null;
        if (last == null)
            return;

        var last_path = _last_form_store.resolve_saved_input_path(last);
        if (last_path == null)
            return;

        has_last_file = true;
        last_file_display_name = last.original_file_name ?? Path.GetFileName(last_path);
        use_last_file = true;
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
