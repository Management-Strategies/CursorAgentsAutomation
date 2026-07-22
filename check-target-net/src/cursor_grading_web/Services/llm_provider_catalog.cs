using cursor_grading_web.Models;

namespace cursor_grading_web.Services;

/// <summary>
/// Holds configured LLM providers (whichever API keys are available).
/// </summary>
public class llm_provider_catalog
{
    private readonly Dictionary<string, active_llm_settings> _by_provider;

    public llm_provider_catalog(
        IEnumerable<active_llm_settings> providers,
        string default_provider)
    {
        _by_provider = providers.ToDictionary(
            p => p.provider,
            p => p,
            StringComparer.OrdinalIgnoreCase);

        DefaultProvider = Normalize(default_provider);
        if (!_by_provider.ContainsKey(DefaultProvider) && _by_provider.Count > 0)
            DefaultProvider = _by_provider.Keys.First();
    }

    public string DefaultProvider { get; }

    public IReadOnlyCollection<active_llm_settings> Available => _by_provider.Values;

    public bool is_available(string? provider) =>
        !string.IsNullOrWhiteSpace(provider) && _by_provider.ContainsKey(Normalize(provider));

    public active_llm_settings get(string? provider)
    {
        var key = Normalize(string.IsNullOrWhiteSpace(provider) ? DefaultProvider : provider);
        if (_by_provider.TryGetValue(key, out var settings))
            return settings;

        throw new InvalidOperationException(
            $"LLM provider '{key}' is not configured. Set its API key or choose another provider.");
    }

    public active_llm_settings? try_get(string? provider)
    {
        var key = Normalize(string.IsNullOrWhiteSpace(provider) ? DefaultProvider : provider!);
        return _by_provider.TryGetValue(key, out var settings) ? settings : null;
    }

    public static string Normalize(string provider) =>
        provider.Trim().ToLowerInvariant() switch
        {
            "gemini" => "gemini",
            _ => "deepseek"
        };
}
