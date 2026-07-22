using cursor_grading_web.Models;
using cursor_grading_web.Services;

namespace cursor_grading_web_tests;

public class prompt_builder_tests
{
    private readonly prompt_builder _builder = new();

    [Fact]
    public void build_contains_website_and_products()
    {
        var prompt = _builder.build(
            "https://example.com",
            "Test Equipment",
            "Engineers",
            "Scraped text about manufacturing and calibration.");

        Assert.Contains("https://example.com", prompt);
        Assert.Contains("Test Equipment", prompt);
        Assert.Contains("Engineers", prompt);
        Assert.Contains("Scraped text about manufacturing and calibration.", prompt);
    }

    [Fact]
    public void build_contains_who_we_are_section()
    {
        var prompt = _builder.build("https://example.com", "Products", "About", "Scraped");

        Assert.Contains("Alliance Test Equipment", prompt);
        Assert.Contains("alliancetesteq.com", prompt);
        Assert.Contains("oscilloscopes", prompt);
    }

    [Fact]
    public void build_contains_grading_instructions()
    {
        var prompt = _builder.build("https://example.com", "Products", "About", "Scraped");

        Assert.Contains("GOOD", prompt);
        Assert.Contains("MAYBE", prompt);
        Assert.Contains("UNABLE", prompt);
        Assert.Contains("in-house engineering", prompt);
    }

    [Fact]
    public void build_contains_json_shape_instruction()
    {
        var prompt = _builder.build("https://example.com", "Products", "About", "Scraped");

        Assert.Contains("\"grade\"", prompt);
        Assert.Contains("\"reason\"", prompt);
    }

    [Fact]
    public void build_with_examples_includes_them()
    {
        var examples = new List<company_row>
        {
            new(1, "Company A", "site-a.com", "Product A", "About A"),
            new(2, "Company B", "site-b.com", "Product B", "About B")
        };

        var prompt = _builder.build("https://example.com", "Products", "About", "Scraped", examples);

        Assert.Contains("for calibration", prompt);
        Assert.Contains("site-a.com", prompt);
        Assert.Contains("site-b.com", prompt);
    }

    [Fact]
    public void build_with_null_examples_does_not_include_example_section()
    {
        var prompt = _builder.build("https://example.com", "Products", "About", "Scraped", null);

        Assert.DoesNotContain("for calibration", prompt);
    }

    [Fact]
    public void build_with_empty_examples_does_not_include_example_section()
    {
        var prompt = _builder.build("https://example.com", "Products", "About", "Scraped", new List<company_row>());

        Assert.DoesNotContain("for calibration", prompt);
    }
}