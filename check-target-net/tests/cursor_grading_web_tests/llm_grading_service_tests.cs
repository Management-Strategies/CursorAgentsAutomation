using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using cursor_grading_web.Models;
using cursor_grading_web.Services;

namespace cursor_grading_web_tests;

public class llm_grading_service_tests
{
    private sealed class fake_http_client_factory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public fake_http_client_factory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private static llm_grading_service create_service(
        HttpStatusCode status_code,
        string response_json,
        active_llm_settings? settings = null)
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
                Content = new StringContent(response_json, Encoding.UTF8, "application/json")
            });

        var client = new HttpClient(handler.Object)
        {
            BaseAddress = new Uri("https://api.example.com/")
        };

        settings ??= new active_llm_settings
        {
            provider = "deepseek",
            display_name = "DeepSeek",
            model = "deepseek-v4-pro",
            base_url = "https://api.deepseek.com",
            api_key = "test-key",
            supports_balance = true,
            input_cache_hit_per_million = 0.003625m,
            input_cache_miss_per_million = 0.435m,
            output_per_million = 0.87m
        };

        var catalog = new llm_provider_catalog(new[] { settings }, settings.provider);
        var logger = new Mock<ILogger<llm_grading_service>>().Object;
        return new llm_grading_service(new fake_http_client_factory(client), catalog, logger);
    }

    [Fact]
    public async Task grade_async_successful_response_returns_content()
    {
        var response = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = "{\"grade\": \"GOOD\", \"reason\": \"has calibration services\"}"
                    }
                }
            },
            usage = new
            {
                prompt_cache_hit_tokens = 100,
                prompt_cache_miss_tokens = 200,
                completion_tokens = 50
            }
        });

        var service = create_service(HttpStatusCode.OK, response);
        var result = await service.grade_async("test prompt", "deepseek", CancellationToken.None);

        Assert.Contains("GOOD", result.content);
        Assert.Equal(100, result.cache_hit_tokens);
        Assert.Equal(200, result.cache_miss_tokens);
        Assert.Equal(50, result.completion_tokens);
        Assert.True(result.cost_usd > 0m);
    }

    [Fact]
    public async Task grade_async_gemini_usage_maps_prompt_tokens_to_miss()
    {
        var gemini = new active_llm_settings
        {
            provider = "gemini",
            display_name = "Gemini",
            model = "gemini-2.5-flash",
            base_url = "https://generativelanguage.googleapis.com/v1beta/openai",
            api_key = "test-key",
            supports_balance = false,
            input_cache_hit_per_million = 0m,
            input_cache_miss_per_million = 0.10m,
            output_per_million = 0.40m
        };

        var response = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = "{\"grade\": \"MAYBE\", \"reason\": \"partial fit\"}"
                    }
                }
            },
            usage = new
            {
                prompt_tokens = 1000,
                completion_tokens = 40
            }
        });

        var service = create_service(HttpStatusCode.OK, response, gemini);
        var result = await service.grade_async("test prompt", "gemini", CancellationToken.None);

        Assert.Contains("MAYBE", result.content);
        Assert.Equal(0, result.cache_hit_tokens);
        Assert.Equal(1000, result.cache_miss_tokens);
        Assert.Equal(40, result.completion_tokens);
        Assert.Equal(0.000116m, result.cost_usd);
    }

    [Fact]
    public async Task grade_async_http_error_throws()
    {
        var service = create_service(HttpStatusCode.Unauthorized, "Unauthorized");
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.grade_async("test prompt", "deepseek", CancellationToken.None));
    }

    [Fact]
    public async Task grade_async_empty_response_returns_empty_string()
    {
        var response = JsonSerializer.Serialize(new { choices = new object[0] });
        var service = create_service(HttpStatusCode.OK, response);
        var result = await service.grade_async("test prompt", "deepseek", CancellationToken.None);
        Assert.Equal("", result.content);
    }

    [Fact]
    public async Task get_balance_async_prefers_usd_total_balance()
    {
        var response = JsonSerializer.Serialize(new
        {
            is_available = true,
            balance_infos = new[]
            {
                new
                {
                    currency = "CNY",
                    total_balance = "10.00",
                    granted_balance = "0.00",
                    topped_up_balance = "10.00"
                },
                new
                {
                    currency = "USD",
                    total_balance = "6.22",
                    granted_balance = "0.00",
                    topped_up_balance = "6.22"
                }
            }
        });

        var service = create_service(HttpStatusCode.OK, response);
        var balance = await service.get_balance_async("deepseek", CancellationToken.None);

        Assert.NotNull(balance);
        Assert.Equal("USD", balance!.currency);
        Assert.Equal(6.22m, balance.total_balance);
    }

    [Fact]
    public async Task get_balance_async_returns_null_when_provider_lacks_balance()
    {
        var gemini = new active_llm_settings
        {
            provider = "gemini",
            display_name = "Gemini",
            model = "gemini-2.5-flash",
            base_url = "https://example.com",
            api_key = "test-key",
            supports_balance = false
        };

        var service = create_service(HttpStatusCode.OK, "{}", gemini);
        Assert.Null(await service.get_balance_async("gemini", CancellationToken.None));
    }

    [Fact]
    public void parse_usage_prefers_cache_breakdown_over_prompt_tokens()
    {
        using var doc = JsonDocument.Parse("""
            {"usage":{"prompt_tokens":999,"prompt_cache_hit_tokens":10,"prompt_cache_miss_tokens":20,"completion_tokens":5}}
            """);
        var (hit, miss, completion) = llm_grading_service.parse_usage(doc.RootElement);
        Assert.Equal(10, hit);
        Assert.Equal(20, miss);
        Assert.Equal(5, completion);
    }
}
