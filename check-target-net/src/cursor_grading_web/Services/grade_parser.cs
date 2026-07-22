using System.Text.Json;
using System.Text.RegularExpressions;
using cursor_grading_web.Models;

namespace cursor_grading_web.Services;

public class grade_parser
{
    private static readonly HashSet<string> valid_grades = new(StringComparer.OrdinalIgnoreCase)
    {
        "GOOD", "MAYBE", "UNABLE"
    };

    private static readonly Regex code_fence_regex = new(
        @"^```(?:json)?|```$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex grade_keyword_regex = new(
        @"\b(GOOD|MAYBE|UNABLE)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public grade_result parse(string raw_text)
    {
        var text = raw_text.Trim();

        // Try clean JSON first (strip code fences if present)
        var cleaned = code_fence_regex.Replace(text, "").Trim();
        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            var grade = "";
            var reason = "";

            if (root.TryGetProperty("grade", out var grade_el))
                grade = grade_el.GetString()?.Trim().ToUpper() ?? "";
            if (root.TryGetProperty("reason", out var reason_el))
                reason = reason_el.GetString()?.Trim() ?? "";

            if (!string.IsNullOrEmpty(grade) && valid_grades.Contains(grade))
                return new grade_result(0, grade, string.IsNullOrEmpty(reason) ? "(no reason given)" : reason);
        }
        catch (JsonException)
        {
            // Fall through to regex fallback
        }

        // Fallback: look for a bare grade keyword anywhere in the text
        var match = grade_keyword_regex.Match(text);
        if (match.Success)
        {
            var grade = match.Groups[1].Value.ToUpper();
            var reason = text.Length > 200 ? text[..200] : text;
            return new grade_result(0, grade, reason);
        }

        return new grade_result(0, "UNABLE", $"Could not parse agent response: {(text.Length > 200 ? text[..200] : text)}");
    }
}