using cursor_grading_web.Hubs;
using cursor_grading_web.Models;
using cursor_grading_web.Services;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration ----
builder.Services.Configure<grading_options>(builder.Configuration.GetSection(grading_options.section_name));
builder.Services.Configure<column_options>(builder.Configuration.GetSection(column_options.section_name));
builder.Services.Configure<deep_seek_options>(builder.Configuration.GetSection(deep_seek_options.section_name));

// ---- HTTP Clients ----
var deep_seek_config = builder.Configuration.GetSection(deep_seek_options.section_name).Get<deep_seek_options>()!;
var deep_seek_api_key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? deep_seek_config.api_key;

// DeepSeek API client
builder.Services.AddHttpClient<deep_seek_service>(client =>
{
    client.BaseAddress = new Uri(deep_seek_config.base_url.TrimEnd('/'));
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {deep_seek_api_key}");
    client.Timeout = TimeSpan.FromMinutes(2);
});

// Web scraper client (with browser-like headers)
builder.Services.AddHttpClient<web_scraper_service>(client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
    client.Timeout = TimeSpan.FromSeconds(30);
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
builder.Services.AddSingleton<prompt_builder>();
builder.Services.AddSingleton<grade_parser>();
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

// Cancel job endpoint
app.MapPost("/api/cancel_job", (grading_background_service svc) =>
{
    var cancelled = svc.cancel_job();
    return cancelled ? Results.Ok(new { cancelled = true }) : Results.Ok(new { cancelled = false, message = "No active job" });
});

app.Run();
