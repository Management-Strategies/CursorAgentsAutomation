using System.Net.Http;
using System.Security.Authentication;
using AngleSharp;
using cursor_grading_web.Models;

namespace cursor_grading_web.Services;

public class web_scraper_service
{
    private readonly HttpClient _http_client;
    private const int max_text_length = 8000;
    private const int timeout_seconds = 45;

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

            var html = await fetch_html_async(url, cts.Token);
            return await parse_html_async(html, cts.Token);
        }
        catch (TaskCanceledException)
        {
            if (ct.IsCancellationRequested)
                return new scrape_result(false, "", "Cancelled by user");
            return new scrape_result(false, "", $"Request timed out after {timeout_seconds}s");
        }
        catch (HttpRequestException ex)
        {
            return new scrape_result(false, "", $"Network error: {short_error(ex)}");
        }
        catch (Exception ex)
        {
            return new scrape_result(false, "", $"Scraping error: {short_error(ex)}");
        }
    }

    private async Task<string> fetch_html_async(string url, CancellationToken ct)
    {
        try
        {
            return await http_get_html_async(url, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (is_certificate_name_mismatch(ex))
            {
                var alt = try_alternate_www_url(url);
                if (alt != null)
                {
                    try
                    {
                        return await http_get_html_async(alt, ct);
                    }
                    catch (Exception www_ex) when (
                        is_schannel_handshake_failure(www_ex) && !ct.IsCancellationRequested)
                    {
                        return await bouncy_https_fetcher.get_html_async(alt, ct);
                    }
                    catch (Exception www_ex) when (
                        is_certificate_name_mismatch(www_ex) && !ct.IsCancellationRequested)
                    {
                        // fall through to BouncyCastle on original URL below
                    }
                }
            }

            if (is_schannel_handshake_failure(ex) || is_certificate_name_mismatch(ex))
                return await bouncy_https_fetcher.get_html_async(
                    try_alternate_www_url(url) ?? url, ct);

            throw;
        }
    }

    private async Task<string> http_get_html_async(string url, CancellationToken ct)
    {
        var response = await _http_client.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        return await response.Content.ReadAsStringAsync(ct);
    }

    public static string? try_alternate_www_url(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return null;

        string new_host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            new_host = host[4..];
        else
            new_host = "www." + host;

        if (string.IsNullOrWhiteSpace(new_host) ||
            new_host.Equals(host, StringComparison.OrdinalIgnoreCase))
            return null;

        var builder = new UriBuilder(uri) { Host = new_host };
        return builder.Uri.ToString();
    }

    public static bool is_certificate_name_mismatch(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException!)
        {
            var msg = e.Message ?? "";
            if (msg.Contains("RemoteCertificateNameMismatch", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("SEC_E_WRONG_PRINCIPAL", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("target principal name is incorrect", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("certificate is not valid for", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Hostname mismatch", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static bool is_schannel_handshake_failure(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException!)
        {
            var msg = e.Message ?? "";
            if (msg.Contains("SEC_E_ILLEGAL_MESSAGE", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("SSL connection could not be established", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("secure channel", StringComparison.OrdinalIgnoreCase) ||
                (msg.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) &&
                 !is_certificate_name_mismatch(ex)))
            {
                return true;
            }

            // AuthenticationException without a name-mismatch message => treat as handshake
            if (e is AuthenticationException && !is_certificate_name_mismatch(ex))
                return true;
        }
        return false;
    }

    /// <summary>True for either cert-name mismatch or Schannel handshake failure.</summary>
    public static bool is_ssl_or_channel_failure(Exception ex) =>
        is_certificate_name_mismatch(ex) || is_schannel_handshake_failure(ex);

    private static async Task<scrape_result> parse_html_async(string html, CancellationToken ct)
    {
        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var document = await context.OpenAsync(req => req.Content(html), ct);

        var unwanted = new[] { "script", "style", "nav", "footer", "header", "noscript", "iframe" };
        foreach (var tag in unwanted)
        {
            foreach (var el in document.QuerySelectorAll(tag))
                el.Remove();
        }

        var body = document.Body;
        if (body == null)
            return new scrape_result(false, "", "No body content found on page");

        var text = body.TextContent;
        text = string.Join(" ", text.Split(
            new[] { ' ', '\t', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (text.Length > max_text_length)
            text = text[..max_text_length];

        if (string.IsNullOrWhiteSpace(text))
            return new scrape_result(false, "", "Page body was empty after parsing");

        return new scrape_result(true, text, "");
    }

    private static string short_error(Exception ex)
    {
        // Prefer a one-line message; never dump stack traces into the grade reason.
        var msg = ex.Message ?? "";
        var nl = msg.IndexOfAny(['\r', '\n']);
        if (nl >= 0)
            msg = msg[..nl].Trim();

        if (ex.InnerException != null)
        {
            var inner = ex.InnerException.Message ?? "";
            var inl = inner.IndexOfAny(['\r', '\n']);
            if (inl >= 0) inner = inner[..inl].Trim();
            if (!string.IsNullOrWhiteSpace(inner) &&
                !msg.Contains(inner, StringComparison.OrdinalIgnoreCase))
            {
                msg = string.IsNullOrWhiteSpace(msg) ? inner : $"{msg} ({inner})";
            }
        }

        if (msg.Length > 300)
            msg = msg[..300] + "…";

        return msg;
    }
}
