using cursor_grading_web.Models;
using cursor_grading_web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace cursor_grading_web.Pages;

public class progress_model : PageModel
{
    private readonly llm_provider_catalog _llm_catalog;

    public string? file_name { get; set; }
    public string llm_provider { get; set; } = "deepseek";
    public string llm_display_name { get; set; } = "DeepSeek";
    public bool llm_supports_balance { get; set; } = true;
    public string llm_model { get; set; } = "";

    public progress_model(llm_provider_catalog llm_catalog)
    {
        _llm_catalog = llm_catalog;
    }

    public void OnGet(string? file, string? provider)
    {
        file_name = file ?? "companies_graded.xlsx";
        llm_provider = llm_provider_catalog.Normalize(provider ?? _llm_catalog.DefaultProvider);
        if (!_llm_catalog.is_available(llm_provider))
            llm_provider = _llm_catalog.DefaultProvider;

        var settings = _llm_catalog.get(llm_provider);
        llm_display_name = settings.display_name;
        llm_supports_balance = settings.supports_balance;
        llm_model = settings.model;
    }
}
