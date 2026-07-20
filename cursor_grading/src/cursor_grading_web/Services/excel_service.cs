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

        int col_company = header_map.TryGetValue("Company Name", out var cc) ? cc : -1;
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
                GetCellValue(row, col_about)
            ));
        }

        return (pending, examples);
    }

    public void write_grade(string file_path, grade_result result, column_options columns)
    {
        using var wb = new XLWorkbook(file_path);
        var ws = wb.Worksheet(1);

        // Re-read header to find column indexes (in case sheet structure varies)
        var header_row = ws.Row(1);
        var header_map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in header_row.CellsUsed())
        {
            var val = cell.GetString().Trim();
            if (!string.IsNullOrEmpty(val))
                header_map[val] = cell.WorksheetColumn().ColumnNumber();
        }

        int col_grade = header_map.GetValueOrDefault(columns.grade_out, -1);
        int col_comment = header_map.GetValueOrDefault(columns.comment_out, -1);

        if (col_grade > 0)
            ws.Cell(result.row_index, col_grade).Value = result.grade;
        if (col_comment > 0)
            ws.Cell(result.row_index, col_comment).Value = result.reason;

        wb.Save();
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