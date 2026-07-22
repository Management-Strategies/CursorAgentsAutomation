using ClosedXML.Excel;
using cursor_grading_web.Models;

namespace cursor_grading_web.Services;

public class excel_service
{
    private static readonly HashSet<string> valid_grades = new(StringComparer.OrdinalIgnoreCase)
    {
        "GOOD", "MAYBE", "UNABLE"
    };

    public (List<company_row> pending, List<grade_result> examples) load_workbook(
        string file_path, string? sheet_name, column_options columns, int max_examples)
    {
        var pending = new List<company_row>();
        var examples = new List<grade_result>();

        using var wb = new XLWorkbook(file_path);
        var ws = string.IsNullOrEmpty(sheet_name) ? wb.Worksheet(1) : wb.Worksheet(sheet_name);

        // Read header row to find column indexes
        var header_row = ws.Row(1);
        var header_map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in header_row.CellsUsed())
        {
            var val = cell.GetString().Trim();
            if (!string.IsNullOrEmpty(val))
                header_map[val] = cell.WorksheetColumn().ColumnNumber();
        }

        int col_company = first_header(header_map, "Company Name", "Company");
        int col_contact = first_header(header_map, "Contact Name", "Contact", "Contact Person", "Full Name");
        int col_email = first_header(header_map, "Email", "Contact Email", "E-mail");
        int col_phone = first_header(header_map, "Phone", "Contact Phone", "Telephone", "Mobile");
        int col_website = header_map.GetValueOrDefault(columns.website, -1);
        int col_products = header_map.GetValueOrDefault(columns.products, -1);
        int col_about = header_map.GetValueOrDefault(columns.about, -1);
        int col_grade = header_map.GetValueOrDefault(columns.grade_out, -1);
        int col_comment = header_map.GetValueOrDefault(columns.comment_out, -1);

        if (col_website == -1 || col_products == -1 || col_about == -1 || col_grade == -1)
        {
            var missing = new List<string>();
            if (col_website == -1) missing.Add(columns.website);
            if (col_products == -1) missing.Add(columns.products);
            if (col_about == -1) missing.Add(columns.about);
            if (col_grade == -1) missing.Add(columns.grade_out);
            throw new InvalidOperationException($"Missing expected columns: {string.Join(", ", missing)}");
        }

        foreach (var row in ws.RowsUsed().Skip(1))
        {
            int row_num = row.RowNumber();
            var grade_val = GetCellValue(row, col_grade);

            // Collect examples from previously graded rows
            if (!string.IsNullOrEmpty(grade_val) && valid_grades.Contains(grade_val.Trim().ToUpper()) && examples.Count < max_examples)
            {
                examples.Add(new grade_result(
                    row_num,
                    grade_val.Trim().ToUpper(),
                    GetCellValue(row, col_comment)
                )
                {
                    // We're just using grade_result as a carrier; ignore reason for examples
                });
            }

            // Skip already-graded rows
            if (!string.IsNullOrEmpty(grade_val))
                continue;

            var website = GetCellValue(row, col_website);
            if (string.IsNullOrEmpty(website))
                continue;

            pending.Add(new company_row(
                row_num,
                col_company > 0 ? GetCellValue(row, col_company) : "",
                website,
                GetCellValue(row, col_products),
                GetCellValue(row, col_about),
                col_contact > 0 ? GetCellValue(row, col_contact) : "",
                col_email > 0 ? GetCellValue(row, col_email) : "",
                col_phone > 0 ? GetCellValue(row, col_phone) : ""
            ));
        }

        return (pending, examples);
    }

    private static int first_header(Dictionary<string, int> header_map, params string[] names)
    {
        foreach (var name in names)
        {
            if (header_map.TryGetValue(name, out var col))
                return col;
        }
        return -1;
    }

    /// <summary>
    /// Opens the output workbook once for the duration of a grading job.
    /// Prefer this over repeated <see cref="write_grade"/> open/save cycles, which can drop the last write under load.
    /// </summary>
    public excel_grade_writer open_writer(string file_path, string? sheet_name, column_options columns, int save_every = 5)
        => new(file_path, sheet_name, columns, save_every);

    public void write_grade(string file_path, grade_result result, column_options columns, string? sheet_name = null)
    {
        using var writer = open_writer(file_path, sheet_name, columns, save_every: 1);
        writer.write_grade(result);
        writer.flush();
    }

    public int get_pending_count(string file_path, string? sheet_name, column_options columns)
    {
        using var wb = new XLWorkbook(file_path);
        var ws = string.IsNullOrEmpty(sheet_name) ? wb.Worksheet(1) : wb.Worksheet(sheet_name);

        var header_row = ws.Row(1);
        var header_map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in header_row.CellsUsed())
        {
            var val = cell.GetString().Trim();
            if (!string.IsNullOrEmpty(val))
                header_map[val] = cell.WorksheetColumn().ColumnNumber();
        }

        int col_grade = header_map.GetValueOrDefault(columns.grade_out, -1);
        int col_website = header_map.GetValueOrDefault(columns.website, -1);
        if (col_grade == -1 || col_website == -1) return 0;

        int count = 0;
        foreach (var row in ws.RowsUsed().Skip(1))
        {
            var grade = GetCellValue(row, col_grade);
            var website = GetCellValue(row, col_website);
            if (string.IsNullOrEmpty(grade) && !string.IsNullOrEmpty(website))
                count++;
        }

        return count;
    }

    private static string GetCellValue(IXLRow row, int column_number)
    {
        if (column_number <= 0) return "";
        var cell = row.Cell(column_number);
        return cell.IsEmpty() ? "" : cell.GetString().Trim();
    }
}

/// <summary>
/// Holds an output workbook open for the job lifetime. Cell updates are applied in memory;
/// disk is flushed periodically and always on <see cref="flush"/> / dispose.
/// </summary>
public sealed class excel_grade_writer : IDisposable
{
    private readonly XLWorkbook _wb;
    private readonly IXLWorksheet _ws;
    private readonly int _col_grade;
    private readonly int _col_comment;
    private readonly int _save_every;
    private readonly object _gate = new();
    private int _writes_since_save;
    private bool _dirty;
    private bool _disposed;

    public excel_grade_writer(string file_path, string? sheet_name, column_options columns, int save_every = 5)
    {
        _wb = new XLWorkbook(file_path);
        _ws = string.IsNullOrEmpty(sheet_name) ? _wb.Worksheet(1) : _wb.Worksheet(sheet_name);
        _save_every = Math.Max(1, save_every);

        var header_map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in _ws.Row(1).CellsUsed())
        {
            var val = cell.GetString().Trim();
            if (!string.IsNullOrEmpty(val))
                header_map[val] = cell.WorksheetColumn().ColumnNumber();
        }

        _col_grade = header_map.GetValueOrDefault(columns.grade_out, -1);
        _col_comment = header_map.GetValueOrDefault(columns.comment_out, -1);
    }

    public void write_grade(grade_result result)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_col_grade > 0)
                _ws.Cell(result.row_index, _col_grade).Value = result.grade;
            if (_col_comment > 0)
                _ws.Cell(result.row_index, _col_comment).Value = result.reason;

            _dirty = true;
            _writes_since_save++;
            if (_writes_since_save >= _save_every)
                save_unlocked();
        }
    }

    /// <summary>Force any pending cell updates to disk. Safe to call multiple times.</summary>
    public void flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_dirty)
                save_unlocked();
        }
    }

    private void save_unlocked()
    {
        _wb.Save();
        _dirty = false;
        _writes_since_save = 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                if (_dirty)
                    save_unlocked();
            }
            finally
            {
                _wb.Dispose();
                _disposed = true;
            }
        }
    }
}
