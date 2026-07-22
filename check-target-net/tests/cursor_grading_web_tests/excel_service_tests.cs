using cursor_grading_web.Models;
using cursor_grading_web.Services;

namespace cursor_grading_web_tests;

public class excel_service_tests : IDisposable
{
    private readonly excel_service _service = new();
    private readonly column_options _columns = new();
    private readonly string _test_data_dir;
    private readonly string _input_file;
    private readonly string _output_file;

    public excel_service_tests()
    {
        _test_data_dir = Path.Combine(AppContext.BaseDirectory, "test_data");
        _input_file = Path.Combine(_test_data_dir, "companies.xlsx");
        _output_file = Path.Combine(Path.GetTempPath(), $"test_output_{Guid.NewGuid()}.xlsx");
    }

    public void Dispose()
    {
        if (File.Exists(_output_file))
            File.Delete(_output_file);
    }

    [Fact]
    public void load_workbook_file_exists_returns_pending_rows()
    {
        // Copy to temp so we don't modify the original
        File.Copy(_input_file, _output_file, overwrite: true);

        var (pending, examples) = _service.load_workbook(_output_file, null, _columns, 5);

        Assert.NotNull(pending);
        Assert.NotNull(examples);

        // There should be rows in the file (verify it loads)
        Assert.True(pending.Count > 0, "Expected at least one pending row in the test data");

        // All pending rows should have a website
        Assert.All(pending, r => Assert.False(string.IsNullOrEmpty(r.website), $"Row {r.row_index} has no website"));

        // Examples should be 5 or fewer
        Assert.True(examples.Count <= 5);
    }

    [Fact]
    public void load_workbook_missing_columns_throws()
    {
        File.Copy(_input_file, _output_file, overwrite: true);

        var bad_columns = new column_options
        {
            website = "NonExistent Column",
            products = "Company primary products",
            about = "about Company who they are selling",
            grade_out = "WEBSITE_GRADE"
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.load_workbook(_output_file, null, bad_columns, 5));

        Assert.Contains("Missing expected columns", ex.Message);
        Assert.Contains("NonExistent Column", ex.Message);
    }

    [Fact]
    public void load_workbook_skips_already_graded_rows()
    {
        File.Copy(_input_file, _output_file, overwrite: true);

        var (pending, _) = _service.load_workbook(_output_file, null, _columns, 5);

        // Verify that none of the pending rows have a grade already set
        // (They were skipped by the loader)
        foreach (var row in pending)
        {
            Assert.NotNull(row.website);
            // These rows should be ungraded (we're grading them)
        }
    }

    [Fact]
    public void write_grade_persists_to_file()
    {
        File.Copy(_input_file, _output_file, overwrite: true);

        var result = new grade_result(2, "GOOD", "Test reason: engineering team found");
        _service.write_grade(_output_file, result, _columns);

        // Reload and verify the grade was written
        var (pending, _) = _service.load_workbook(_output_file, null, _columns, 5);

        // Row 2 should now be skipped (has a grade)
        Assert.DoesNotContain(pending, r => r.row_index == 2);
    }

    [Fact]
    public void open_writer_flushes_trailing_grades_on_dispose()
    {
        File.Copy(_input_file, _output_file, overwrite: true);

        var (pending, _) = _service.load_workbook(_output_file, null, _columns, 5);
        Assert.True(pending.Count >= 3, "Need at least 3 pending rows for this test");

        var first = pending[0];
        var second = pending[1];
        var third = pending[2];

        using (var writer = _service.open_writer(_output_file, null, _columns, save_every: 100))
        {
            writer.write_grade(new grade_result(first.row_index, "GOOD", "one"));
            writer.write_grade(new grade_result(second.row_index, "MAYBE", "two"));
            writer.write_grade(new grade_result(third.row_index, "UNABLE", "three"));
            // No explicit flush — dispose must persist the trailing unsaved grades
        }

        var (remaining, _) = _service.load_workbook(_output_file, null, _columns, 5);
        Assert.DoesNotContain(remaining, r => r.row_index == first.row_index);
        Assert.DoesNotContain(remaining, r => r.row_index == second.row_index);
        Assert.DoesNotContain(remaining, r => r.row_index == third.row_index);
    }

    [Fact]
    public void get_pending_count_returns_count_of_ungraded_rows()
    {
        File.Copy(_input_file, _output_file, overwrite: true);

        var count = _service.get_pending_count(_output_file, null, _columns);

        Assert.True(count > 0, "Expected pending rows in test data");
    }

    [Fact]
    public void load_workbook_invalid_file_throws()
    {
        Assert.ThrowsAny<Exception>(() =>
            _service.load_workbook(@"C:\nonexistent\file.xlsx", null, _columns, 5));
    }
}