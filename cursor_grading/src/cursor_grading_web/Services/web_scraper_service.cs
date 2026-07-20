using System.Net;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using cursor_grading_web.Models;

namespace cursor_grading_web.Services;

public class web_scraper_service
{
    private readonly HttpClient _http_client;
    private const int max_text_length = 8000;
    private const int timeout_seconds = 15;

    public web_scraper_service(HttpClient http_client)
    {
        _http_client = http_client;
    }

    public async Task<scrape_result> scrape_async(string url, CancellationToken ct)
    {
        // Normalize URL: ensure it has a scheme
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeout_seconds));

            var response = await _http_client.GetAsync(url, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return new scrape_result(false, "", $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var html = await response.Content.ReadAsStringAsync(cts.Token);

            // Parse HTML with AngleSharp
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var document = await context.OpenAsync(req => req.Content(html), cts.Token);

            // Remove script, style, nav, footer, header elements
            var unwanted = new[] { "script", "style", "nav", "footer", "header", "noscript", "iframe" };
            foreach (var tag in unwanted)
            {
                foreach (var el in document.QuerySelectorAll(tag))
                {
                    el.Remove();
                }
            }

            // Extract body text
            var body = document.Body;
            if (body == null)
            {
                return new scrape_result(false, "", "No body content found on page");
            }

            var text = body.TextContent;

            // Clean up whitespace
            text = string.Join(" ", text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            // Truncate
            if (text.Length > max_text_length)
                text = text[..max_text_length];

            if (string.IsNullOrWhiteSpace(text))
                return new scrape_result(false, "", "Page body was empty after parsing");

            return new scrape_result(true, text, "");
        }
        catch (TaskCanceledException)
        {
            if (ct.IsCancellationRequested)
                return new scrape_result(false, "", "Cancelled by user");
            return new scrape_result(false, "", $"Request timed out after {timeout_seconds}s");
        }
        catch (HttpRequestException ex)
        {
            return new scrape_result(false, "", $"Network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new scrape_result(false, "", $"Scraping error: {ex.Message}");
        }
    }
}