using cursor_grading_web.Services;

namespace cursor_grading_web_tests;

public class column_mapping_sanitize_tests
{
    private static readonly string[] Targets =
    [
        "Company Name",
        "Website Link",
        "about Company",
        "Email"
    ];

    private static readonly string[] Sources =
    [
        "Company Name",
        "Website Link",
        "about Company who they are selling",
        "Email Address"
    ];

    [Fact]
    public void sanitize_accepts_valid_mappings_array()
    {
        var json = """
            {
              "mappings": [
                { "target": "Company Name", "source": "Company Name" },
                { "target": "Website Link", "source": "Website Link" },
                { "target": "about Company", "source": "about Company who they are selling" },
                { "target": "Email", "source": "Email Address" }
              ]
            }
            """;

        var result = llm_grading_service.sanitize_column_mappings(json, Targets, Sources);

        Assert.Equal(
            new[]
            {
                "Company Name",
                "Website Link",
                "about Company who they are selling",
                "Email Address"
            },
            result);
    }

    [Fact]
    public void sanitize_strips_invented_source_names()
    {
        var json = """
            {
              "mappings": [
                { "target": "Company Name", "source": "Org Title" },
                { "target": "Website Link", "source": "Website Link" },
                { "target": "about Company", "source": null },
                { "target": "Email", "source": "" }
              ]
            }
            """;

        var result = llm_grading_service.sanitize_column_mappings(json, Targets, Sources);

        Assert.Equal(new[] { "", "Website Link", "", "" }, result);
    }

    [Fact]
    public void sanitize_empty_or_missing_map_yields_blanks()
    {
        var empty = llm_grading_service.sanitize_column_mappings("{}", Targets, Sources);
        Assert.Equal(new[] { "", "", "", "" }, empty);

        var blank = llm_grading_service.sanitize_column_mappings("", Targets, Sources);
        Assert.Equal(new[] { "", "", "", "" }, blank);
    }

    [Fact]
    public void sanitize_accepts_flat_object_map()
    {
        var json = """
            {
              "Company Name": "Company Name",
              "Website Link": "Website Link",
              "about Company": "about Company who they are selling",
              "Email": null
            }
            """;

        var result = llm_grading_service.sanitize_column_mappings(json, Targets, Sources);

        Assert.Equal(
            new[]
            {
                "Company Name",
                "Website Link",
                "about Company who they are selling",
                ""
            },
            result);
    }

    [Fact]
    public void sanitize_is_case_insensitive_on_headers()
    {
        var json = """
            {
              "mappings": [
                { "target": "company name", "source": "company name" },
                { "target": "WEBSITE LINK", "source": "website link" }
              ]
            }
            """;

        var result = llm_grading_service.sanitize_column_mappings(json, Targets, Sources);

        Assert.Equal("Company Name", result[0]);
        Assert.Equal("Website Link", result[1]);
        Assert.Equal("", result[2]);
        Assert.Equal("", result[3]);
    }

    [Fact]
    public void sanitize_keeps_best_target_when_source_assigned_twice()
    {
        var targets = new[] { "Description", "about Company", "Comment" };
        var sources = new[] { "Description", "Notes" };
        // Both Description and about Company claim source "Description" — keep best (exact name).
        var json = """
            {
              "mappings": [
                { "target": "Description", "source": "Description" },
                { "target": "about Company", "source": "Description" },
                { "target": "Comment", "source": "Notes" }
              ]
            }
            """;

        var result = llm_grading_service.sanitize_column_mappings(json, targets, sources);

        Assert.Equal("Description", result[0]);
        Assert.Equal("", result[1]);
        Assert.Equal("Notes", result[2]);
    }

    [Fact]
    public void sanitize_prefers_description_for_about_company_over_products()
    {
        var targets = new[] { "Company primary products", "about Company" };
        var sources = new[] { "Description" };
        var json = """
            {
              "mappings": [
                { "target": "Company primary products", "source": "Description" },
                { "target": "about Company", "source": "Description" }
              ]
            }
            """;

        var result = llm_grading_service.sanitize_column_mappings(json, targets, sources);

        Assert.Equal("", result[0]);
        Assert.Equal("Description", result[1]);
    }
}
