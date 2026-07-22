using ClosedXML.Excel;

namespace cursor_grading_web.Services;

public class spreadsheet_standardize_service
{
    private readonly IWebHostEnvironment _env;

    public spreadsheet_standardize_service(IWebHostEnvironment env)
    {
        _env = env;
    }

    public string base_formats_dir => Path.Combine(_env.ContentRootPath, "wwwroot", "base-formats");
    public string standardized_dir => Path.Combine(_env.ContentRootPath, "wwwroot", "standardized");
    public string temp_dir => Path.Combine(standardized_dir, "_temp");

    public void ensure_directories()
    {
        Directory.CreateDirectory(base_formats_dir);
        Directory.CreateDirectory(standardized_dir);
        Directory.CreateDirectory(temp_dir);
        ensure_default_base_format();
    }

    public IReadOnlyList<string> list_base_formats()
    {
        ensure_directories();
        return Directory.GetFiles(base_formats_dir, "*.xlsx")
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> read_headers(string file_path, string? sheet_name = null)
    {
        using var wb = new XLWorkbook(file_path);
        var ws = resolve_sheet(wb, sheet_name);
        return read_header_row(ws);
    }

    public IReadOnlyList<string> suggest_source_for_targets(
        IReadOnlyList<string> target_headers,
        IReadOnlyList<string> source_headers)
    {
        var suggestions = new List<string>(target_headers.Count);
        foreach (var target in target_headers)
        {
            var match = source_headers.FirstOrDefault(s =>
                string.Equals(s, target, StringComparison.OrdinalIgnoreCase));
            suggestions.Add(match ?? "");
        }
        return suggestions;
    }

    /// <summary>
    /// Reads up to <paramref name="max_rows"/> data rows (after the header) as dictionaries keyed by header.
    /// </summary>
    public IReadOnlyList<Dictionary<string, string>> read_sample_rows(
        string file_path,
        string? sheet_name = null,
        int max_rows = 10)
    {
        if (max_rows < 1)
            return Array.Empty<Dictionary<string, string>>();

        using var wb = new XLWorkbook(file_path);
        var ws = resolve_sheet(wb, sheet_name);
        var headers = read_header_row(ws);
        if (headers.Count == 0)
            return Array.Empty<Dictionary<string, string>>();

        var header_map = build_header_map(ws);
        var samples = new List<Dictionary<string, string>>();

        foreach (var row in ws.RowsUsed().Skip(1))
        {
            if (samples.Count >= max_rows)
                break;

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var any = false;
            foreach (var header in headers)
            {
                if (!header_map.TryGetValue(header, out var col))
                {
                    dict[header] = "";
                    continue;
                }

                var text = CellDisplayText(row.Cell(col));
                dict[header] = text;
                if (!string.IsNullOrWhiteSpace(text))
                    any = true;
            }

            if (any)
                samples.Add(dict);
        }

        return samples;
    }

    /// <summary>
    /// Writes a workbook whose columns match <paramref name="target_headers"/> (order preserved),
    /// copying cell values from mapped source columns. Empty mapping leaves the target column blank.
    /// </summary>
    public int write_standardized(
        string source_path,
        string? source_sheet,
        IReadOnlyList<string> target_headers,
        IReadOnlyList<string?> source_column_for_target,
        string output_path)
    {
        if (target_headers.Count != source_column_for_target.Count)
            throw new ArgumentException("Target headers and source mappings must have the same length.");

        using var source_wb = new XLWorkbook(source_path);
        var source_ws = resolve_sheet(source_wb, source_sheet);
        var source_header_map = build_header_map(source_ws);

        var source_col_indexes = new int[target_headers.Count];
        for (var i = 0; i < target_headers.Count; i++)
        {
            var mapped = source_column_for_target[i]?.Trim() ?? "";
            if (string.IsNullOrEmpty(mapped))
            {
                source_col_indexes[i] = -1;
                continue;
            }

            if (!source_header_map.TryGetValue(mapped, out var col))
                throw new InvalidOperationException($"Source column '{mapped}' was not found in the uploaded spreadsheet.");

            source_col_indexes[i] = col;
        }

        using var out_wb = new XLWorkbook();
        var out_ws = out_wb.AddWorksheet("Sheet1");

        for (var i = 0; i < target_headers.Count; i++)
            out_ws.Cell(1, i + 1).Value = target_headers[i];

        var data_rows = source_ws.RowsUsed().Skip(1).ToList();
        var out_row = 2;
        foreach (var row in data_rows)
        {
            for (var i = 0; i < target_headers.Count; i++)
            {
                var src_col = source_col_indexes[i];
                if (src_col < 0)
                    continue;

                var cell = row.Cell(src_col);
                CopyCellValue(cell, out_ws.Cell(out_row, i + 1));
            }
            out_row++;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output_path)!);
        out_wb.SaveAs(output_path);
        return data_rows.Count;
    }

    public string save_temp_upload(IFormFile file)
    {
        ensure_directories();
        var safe = Path.GetFileNameWithoutExtension(file.FileName);
        foreach (var c in Path.GetInvalidFileNameChars())
            safe = safe.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(safe))
            safe = "upload";

        var name = $"{safe}_{DateTime.UtcNow:yyyyMMddHHmmssfff}.xlsx";
        var path = Path.Combine(temp_dir, name);
        using var stream = File.Create(path);
        file.CopyTo(stream);
        return path;
    }

    public string resolve_base_format_path(string file_name)
    {
        ensure_directories();
        var name = Path.GetFileName(file_name);
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select a valid base-format .xlsx file.");

        var path = Path.Combine(base_formats_dir, name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Base format '{name}' was not found in base-formats.", path);

        return path;
    }

    public string build_output_path(string output_name)
    {
        ensure_directories();
        var name = Path.GetFileName(output_name.Trim());
        if (string.IsNullOrWhiteSpace(name))
            name = "standardized.xlsx";
        if (!name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            name += ".xlsx";
        return Path.Combine(standardized_dir, name);
    }

    private void ensure_default_base_format()
    {
        var existing = Directory.GetFiles(base_formats_dir, "*.xlsx");
        if (existing.Length > 0)
            return;

        var path = Path.Combine(base_formats_dir, "base-format.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");
        string[] headers =
        [
            "Company Name",
            "Website Link",
            "Employee",
            "First Name",
            "Last Name",
            "Title",
            "Email",
            "email status",
            "Linkedin (if Available)",
            "Company primary products",
            "about Company",
            "GRADE",
            "WEBSITE_GRADE",
            "Comment"
        ];
        for (var i = 0; i < headers.Length; i++)
            ws.Cell(1, i + 1).Value = headers[i];
        wb.SaveAs(path);
    }

    private static IXLWorksheet resolve_sheet(XLWorkbook wb, string? sheet_name)
    {
        if (string.IsNullOrWhiteSpace(sheet_name))
            return wb.Worksheet(1);
        return wb.Worksheet(sheet_name);
    }

    private static List<string> read_header_row(IXLWorksheet ws)
    {
        var headers = new List<string>();
        foreach (var cell in ws.Row(1).CellsUsed())
        {
            var val = cell.GetString().Trim();
            if (!string.IsNullOrEmpty(val))
                headers.Add(val);
        }
        return headers;
    }

    private static Dictionary<string, int> build_header_map(IXLWorksheet ws)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in ws.Row(1).CellsUsed())
        {
            var val = cell.GetString().Trim();
            if (!string.IsNullOrEmpty(val))
                map[val] = cell.WorksheetColumn().ColumnNumber();
        }
        return map;
    }

    private static string CellDisplayText(IXLCell cell)
    {
        if (cell.TryGetValue(out string? s))
            return (s ?? "").Trim();

        if (cell.TryGetValue(out double d))
            return d.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (cell.TryGetValue(out DateTime dt))
            return dt.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

        if (cell.TryGetValue(out bool b))
            return b ? "true" : "false";

        return cell.GetFormattedString().Trim();
    }

    private static void CopyCellValue(IXLCell source, IXLCell dest)
    {
        if (source.TryGetValue(out string? s))
        {
            dest.Value = s ?? "";
            return;
        }

        if (source.TryGetValue(out double d))
        {
            dest.Value = d;
            return;
        }

        if (source.TryGetValue(out DateTime dt))
        {
            dest.Value = dt;
            return;
        }

        if (source.TryGetValue(out bool b))
        {
            dest.Value = b;
            return;
        }

        dest.Value = source.GetFormattedString();
    }
}
