using cursor_grading_web.Models;
using cursor_grading_web.Services;

namespace cursor_grading_web_tests;

public class grade_parser_tests
{
    private readonly grade_parser _parser = new();

    [Fact]
    public void parse_valid_json_good_returns_grade()
    {
        var result = _parser.parse("{\"grade\": \"GOOD\", \"reason\": \"has an R&D lab\"}");

        Assert.Equal("GOOD", result.grade);
        Assert.Equal("has an R&D lab", result.reason);
    }

    [Fact]
    public void parse_valid_json_maybe_returns_grade()
    {
        var result = _parser.parse("{\"grade\": \"MAYBE\", \"reason\": \"unclear engineering presence\"}");

        Assert.Equal("MAYBE", result.grade);
        Assert.Equal("unclear engineering presence", result.reason);
    }

    [Fact]
    public void parse_valid_json_unable_returns_grade()
    {
        var result = _parser.parse("{\"grade\": \"UNABLE\", \"reason\": \"site blocked\"}");

        Assert.Equal("UNABLE", result.grade);
        Assert.Equal("site blocked", result.reason);
    }

    [Fact]
    public void parse_json_with_markdown_fences_strips_them()
    {
        var result = _parser.parse("```json\n{\"grade\": \"GOOD\", \"reason\": \"electronics manufacturing\"}\n```");

        Assert.Equal("GOOD", result.grade);
        Assert.Equal("electronics manufacturing", result.reason);
    }

    [Fact]
    public void parse_json_missing_reason_adds_default()
    {
        var result = _parser.parse("{\"grade\": \"GOOD\"}");

        Assert.Equal("GOOD", result.grade);
        Assert.Equal("(no reason given)", result.reason);
    }

    [Fact]
    public void parse_invalid_json_falls_back_to_regex()
    {
        var result = _parser.parse("I think this company is GOOD because they do engineering work.");

        Assert.Equal("GOOD", result.grade);
    }

    [Fact]
    public void parse_invalid_json_maybe_falls_back_to_regex()
    {
        var result = _parser.parse("Rating: MAYBE - unclear if they have test equipment needs.");

        Assert.Equal("MAYBE", result.grade);
    }

    [Fact]
    public void parse_invalid_json_unable_falls_back_to_regex()
    {
        var result = _parser.parse("Result: UNABLE. The website was blocked by Cloudflare.");

        Assert.Equal("UNABLE", result.grade);
    }

    [Fact]
    public void parse_completely_invalid_input_defaults_to_unable()
    {
        var result = _parser.parse("This response has no grade keyword anywhere in it whatsoever");

        Assert.Equal("UNABLE", result.grade);
        Assert.Contains("Could not parse agent response", result.reason);
    }

    [Fact]
    public void parse_empty_string_defaults_to_unable()
    {
        var result = _parser.parse("");

        Assert.Equal("UNABLE", result.grade);
    }

    [Fact]
    public void parse_json_grade_case_insensitive()
    {
        var result = _parser.parse("{\"grade\": \"good\", \"reason\": \"lowercase test\"}");

        Assert.Equal("GOOD", result.grade);
        Assert.Equal("lowercase test", result.reason);
    }

    [Fact]
    public void parse_reason_truncated_at_200_chars()
    {
        var long_reason = new string('x', 500);
        var result = _parser.parse(long_reason);

        Assert.Equal("UNABLE", result.grade);
        // The reason includes a "Could not parse agent response: " prefix, so total is prefix + 200 truncated chars
        Assert.Contains("Could not parse agent response:", result.reason);
        Assert.True(result.reason.Length <= 235, $"Expected <= 235 but got {result.reason.Length}");
    }
}