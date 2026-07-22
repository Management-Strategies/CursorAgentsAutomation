using System.Net;
using System.Text;
using Moq;
using Moq.Protected;
using cursor_grading_web.Models;
using cursor_grading_web.Services;

namespace cursor_grading_web_tests;

public class web_scraper_service_tests
{
    private static HttpClient create_mock_http_client(HttpStatusCode status_code, string content, string media_type = "text/html")
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = status_code,
                Content = new StringContent(content, Encoding.UTF8, media_type)
            });

        return new HttpClient(handler.Object) { Timeout = TimeSpan.FromSeconds(30) };
    }

    [Fact]
    public async Task scrape_async_successful_response_returns_text()
    {
        var html = "<html><head><script>ignore me</script><style>.x{}</style></head><body><h1>Engineering Services</h1><p>We do calibration and repair.</p></body></html>";
        var client = create_mock_http_client(HttpStatusCode.OK, html);
        var service = new web_scraper_service(client);

        var result = await service.scrape_async("https://example.com", CancellationToken.None);

        Assert.True(result.success, $"Expected success but got: {result.error_reason}");
        Assert.Contains("Engineering Services", result.text_content);
        Assert.Contains("calibration and repair", result.text_content);
        Assert.DoesNotContain("ignore me", result.text_content); // scripts removed
    }

    [Fact]
    public async Task scrape_async_adds_https_when_missing_scheme()
    {
        var client = create_mock_http_client(HttpStatusCode.OK, "<html><body>Content</body></html>");
        var service = new web_scraper_service(client);

        var result = await service.scrape_async("example.com", CancellationToken.None);

        Assert.True(result.success);
        Assert.Contains("Content", result.text_content);
    }

    [Fact]
    public async Task scrape_async_http_error_returns_failure()
    {
        var client = create_mock_http_client(HttpStatusCode.NotFound, "");
        var service = new web_scraper_service(client);

        var result = await service.scrape_async("https://example.com", CancellationToken.None);

        Assert.False(result.success);
        Assert.Contains("HTTP 404", result.error_reason);
    }

    [Fact]
    public async Task scrape_async_timeout_returns_failure()
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (_, token) =>
            {
                await Task.Delay(30000, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var client = new HttpClient(handler.Object) { Timeout = TimeSpan.FromMilliseconds(50) };
        var service = new web_scraper_service(client);

        var result = await service.scrape_async("https://example.com", CancellationToken.None);

        Assert.False(result.success);
        // Either timeout or task cancelled
        Assert.NotEmpty(result.error_reason);
    }

    [Fact]
    public async Task scrape_async_cancellation_requested_returns_failure()
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (_, token) =>
            {
                await Task.Delay(30000, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var client = new HttpClient(handler.Object);
        var service = new web_scraper_service(client);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await service.scrape_async("https://example.com", cts.Token);

        Assert.False(result.success);
        Assert.Contains("Cancelled by user", result.error_reason);
    }

    [Fact]
    public async Task scrape_async_server_error_returns_failure()
    {
        var client = create_mock_http_client(HttpStatusCode.InternalServerError, "Server Error");
        var service = new web_scraper_service(client);

        var result = await service.scrape_async("https://example.com", CancellationToken.None);

        Assert.False(result.success);
        Assert.Contains("500", result.error_reason);
    }

    [Fact]
    public async Task scrape_async_empty_body_returns_failure()
    {
        var client = create_mock_http_client(HttpStatusCode.OK, "<html><body></body></html>");
        var service = new web_scraper_service(client);

        var result = await service.scrape_async("https://example.com", CancellationToken.None);

        Assert.False(result.success);
        Assert.Contains("empty", result.error_reason, StringComparison.OrdinalIgnoreCase);
    }
}