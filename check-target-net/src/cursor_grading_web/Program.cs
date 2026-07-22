using cursor_grading_web.Hubs;
using cursor_grading_web.Models;
using cursor_grading_web.Services;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration ----
builder.Services.Configure<grading_options>(builder.Configuration.GetSection(grading_options.section_name));
builder.Services.Configure<column_options>(builder.Configuration.GetSection(column_options.section_name));
builder.Services.Configure<deep_seek_options>(builder.Configuration.GetSection(deep_seek_options.section_name));
builder.Services.Configure<gemini_options>(builder.Configuration.GetSection(gemini_options.section_name));
builder.Services.Configure<llm_options>(builder.Configuration.GetSection(llm_options.section_name));

var llm_config = builder.Configuration.GetSection(llm_options.section_name).Get<llm_options>() ?? new llm_options();
var deep_seek_config = builder.Configuration.GetSection(deep_seek_options.section_name).Get<deep_seek_options>() ?? new deep_seek_options();
var gemini_config = builder.Configuration.GetSection(gemini_options.section_name).Get<gemini_options>() ?? new gemini_options();

var configured_providers = new List<active_llm_settings>();

var deepseek_key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
if (!string.IsNullOrWhiteSpace(deepseek_key))
{
    configured_providers.Add(new active_llm_settings
    {
        provider = "deepseek",
        display_name = "DeepSeek",
        model = deep_seek_config.model,
        base_url = deep_seek_config.base_url.TrimEnd('/'),
        api_key = deepseek_key,
        supports_balance = true,
        input_cache_hit_per_million = deep_seek_config.input_cache_hit_per_million,
        input_cache_miss_per_million = deep_seek_config.input_cache_miss_per_million,
        output_per_million = deep_seek_config.output_per_million
    });

    builder.Services.AddHttpClient("deepseek", client =>
    {
        client.BaseAddress = new Uri(deep_seek_config.base_url.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {deepseek_key}");
        client.Timeout = TimeSpan.FromMinutes(2);
    });
}

var gemini_key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
if (!string.IsNullOrWhiteSpace(gemini_key))
{
    configured_providers.Add(new active_llm_settings
    {
        provider = "gemini",
        display_name = "Gemini",
        model = gemini_config.model,
        base_url = gemini_config.base_url.TrimEnd('/'),
        api_key = gemini_key,
        supports_balance = false,
        input_cache_hit_per_million = 0m,
        input_cache_miss_per_million = gemini_config.input_per_million,
        output_per_million = gemini_config.output_per_million
    });

    builder.Services.AddHttpClient("gemini", client =>
    {
        client.BaseAddress = new Uri(gemini_config.base_url.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {gemini_key}");
        client.Timeout = TimeSpan.FromMinutes(2);
    });
}

if (configured_providers.Count == 0)
{
    throw new InvalidOperationException(
        "No LLM provider is configured. Set the System environment variables " +
        "DEEPSEEK_API_KEY and/or GEMINI_API_KEY. Do not put API keys in appsettings.json.");
}

var default_provider = llm_provider_catalog.Normalize(llm_config.provider ?? "deepseek");
var catalog = new llm_provider_catalog(configured_providers, default_provider);
builder.Services.AddSingleton(catalog);
builder.Services.AddSingleton(catalog.get(catalog.DefaultProvider)); // default for pages that still inject active_llm_settings
builder.Services.AddSingleton<llm_grading_service>();

// Web scraper client (with browser-like headers)
builder.Services.AddHttpClient<web_scraper_service>(client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate");
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = true,
    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
    MaxAutomaticRedirections = 5
});

// ---- App Services ----
builder.Services.AddSingleton<excel_service>();
builder.Services.AddSingleton<spreadsheet_standardize_service>();
builder.Services.AddSingleton<prompt_builder>();
builder.Services.AddSingleton<grade_parser>();
builder.Services.AddSingleton<last_form_state_store>();
builder.Services.AddSingleton<last_standardize_form_state_store>();
builder.Services.AddSingleton<grading_background_service>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<grading_background_service>());

// ---- Razor Pages + SignalR ----
builder.Services.AddRazorPages();
builder.Services.AddSignalR();

var app = builder.Build();

// ---- Middleware Pipeline ----
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapRazorPages();
app.MapHub<grading_hub>("/grading_hub");

app.MapGet("/api/llm_info", (llm_provider_catalog providers, string? provider) =>
{
    var settings = providers.try_get(provider) ?? providers.get(providers.DefaultProvider);
    return Results.Ok(new
    {
        provider = settings.provider,
        display_name = settings.display_name,
        model = settings.model,
        supports_balance = settings.supports_balance,
        available = providers.Available.Select(p => new
        {
            p.provider,
            p.display_name,
            p.model,
            p.supports_balance
        })
    });
});

app.MapPost("/api/cancel_job", (grading_background_service svc) =>
{
    var cancelled = svc.cancel_job();
    return cancelled ? Results.Ok(new { cancelled = true }) : Results.Ok(new { cancelled = false, message = "No active job" });
});

app.MapGet("/api/deepseek_balance", async (
    llm_grading_service llm,
    llm_provider_catalog providers,
    string? provider,
    CancellationToken ct) =>
{
    var settings = providers.try_get(provider) ?? providers.try_get("deepseek");
    if (settings == null || !settings.supports_balance)
    {
        return Results.Ok(new
        {
            ok = false,
            provider = settings?.provider ?? provider,
            display_name = settings?.display_name ?? "LLM",
            message = $"{settings?.display_name ?? "This provider"} does not expose an account balance API"
        });
    }

    var balance = await llm.get_balance_async(settings.provider, ct);
    if (balance == null)
        return Results.Json(new { ok = false, message = $"Could not fetch {settings.display_name} balance" }, statusCode: 502);

    return Results.Ok(new
    {
        ok = true,
        provider = settings.provider,
        display_name = settings.display_name,
        is_available = balance.is_available,
        currency = balance.currency,
        total_balance = balance.total_balance,
        granted_balance = balance.granted_balance,
        topped_up_balance = balance.topped_up_balance
    });
});

app.Run();
