using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExtormSub.Core.Settings;

namespace ExtormSub.Core.Translation;

public sealed record ProviderPreset(string Name, string BaseUrl, string DefaultModel, bool RequiresKey);

public static class ProviderPresets
{
    public static IReadOnlyList<ProviderPreset> All { get; } =
    [
        new("OpenAI", "https://api.openai.com/v1", "gpt-4.1-mini", true),
        new("DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat", true),
        new("Claude", "https://api.anthropic.com/v1", "claude-haiku-4-5", true),
        new("Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-flash", true),
        new("OpenRouter", "https://openrouter.ai/api/v1", "openai/gpt-4.1-mini", true),
        new("LibreTranslate", "http://localhost:5000", "", false),
        new("Custom", "http://localhost:11434/v1", "", false),
    ];

    public static bool IsLibreTranslate(string name) => name.Equals("LibreTranslate", StringComparison.OrdinalIgnoreCase);

    public static ITranslationProvider Create(HttpClient http, TranslationSettings t, string? apiKey) =>
        IsLibreTranslate(t.Provider)
            ? new LibreTranslateProvider(http, t.BaseUrl, TimeSpan.FromSeconds(t.TimeoutSeconds), apiKey)
            : new OpenAiCompatibleProvider(http, new OpenAiCompatibleConfig
            {
                ProviderName = t.Provider,
                BaseUrl = t.BaseUrl,
                Model = t.Model,
                Temperature = t.Temperature,
                Timeout = TimeSpan.FromSeconds(t.TimeoutSeconds),
            }, apiKey);

    public static ProviderPreset Find(string name) =>
        All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? All[^1];
}

public sealed record OpenAiCompatibleConfig
{
    public string ProviderName { get; init; } = "OpenAI";
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";
    public string Model { get; init; } = "gpt-4.1-mini";
    public double? Temperature { get; init; } = 0.3;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public string SourceLanguage { get; init; } = "English";
    public string TargetLanguage { get; init; } = "Persian";
}

/// <summary>
/// Chat-completions translator. Works with OpenAI, DeepSeek, OpenRouter, Gemini and Anthropic
/// (their OpenAI-compatible endpoints), Ollama, LM Studio and any compatible server.
/// Never logs the API key or request headers.
/// </summary>
public sealed partial class OpenAiCompatibleProvider : ITranslationProvider
{
    private readonly HttpClient _http;
    private readonly OpenAiCompatibleConfig _config;
    private readonly string? _apiKey;
    private readonly Uri _endpoint;
    private volatile bool _omitTemperature;

    public OpenAiCompatibleProvider(HttpClient http, OpenAiCompatibleConfig config, string? apiKey)
    {
        _http = http;
        _config = config;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _endpoint = new Uri(config.BaseUrl.TrimEnd('/') + "/chat/completions");
    }

    public string Name => _config.ProviderName;
    public string CacheScope => $"{_config.BaseUrl}|{_config.Model}";

    public async Task<string> TranslateAsync(TranslationRequest request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_config.Timeout);
        try
        {
            return await SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (TranslationException ex) when (ex.Kind == TranslationErrorKind.Fatal && !_omitTemperature
                                               && ex.Message.Contains("temperature", StringComparison.OrdinalIgnoreCase))
        {
            // Some reasoning models reject a custom temperature. Drop it once and retry.
            _omitTemperature = true;
            return await SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TranslationException(TranslationErrorKind.Transient, $"Timed out after {_config.Timeout.TotalSeconds:0}s");
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationException(TranslationErrorKind.Transient, $"Network error: {ex.InnerException?.Message ?? ex.Message}", inner: ex);
        }
    }

    private async Task<string> SendAsync(TranslationRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _config.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = SubtitlePrompt.System(request.SourceLanguage, request.TargetLanguage) },
                new JsonObject { ["role"] = "user", ["content"] = SubtitlePrompt.User(request) },
            },
            ["stream"] = false,
        };
        if (_config.Temperature is { } t && !_omitTemperature) body["temperature"] = t;

        // Sized body (not chunked): some local/compatible servers reject chunked requests.
        using var msg = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (_apiKey is not null) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _http.SendAsync(msg, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw MapError(response, text, _endpoint.Host);

        string? content;
        try
        {
            content = JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new TranslationException(TranslationErrorKind.Fatal, "Response was not valid chat-completions JSON", inner: ex);
        }
        if (content is null)
            throw new TranslationException(TranslationErrorKind.Fatal, "Response had no choices[0].message.content");

        return SubtitlePrompt.CleanResponse(content);
    }

    internal static TranslationException MapError(HttpResponseMessage response, string body, string host)
    {
        var detail = $"HTTP {(int)response.StatusCode} from {host}: {Scrub(body)}";
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(TranslationErrorKind.Auth, detail),
            HttpStatusCode.TooManyRequests => new(TranslationErrorKind.RateLimited, detail, response.Headers.RetryAfter?.Delta),
            HttpStatusCode.RequestTimeout => new(TranslationErrorKind.Transient, detail),
            >= HttpStatusCode.InternalServerError => new(TranslationErrorKind.Transient, detail),
            _ => new(TranslationErrorKind.Fatal, detail),
        };
    }

    /// <summary>Truncates provider error bodies and removes anything shaped like a key before it reaches logs/UI.</summary>
    internal static string Scrub(string body)
    {
        var s = KeyLike().Replace(body, "***");
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > 300 ? s[..300] + "…" : s;
    }

    [GeneratedRegex(@"(sk-[A-Za-z0-9_\-]{8,}|Bearer\s+\S+|AIza[0-9A-Za-z_\-]{20,})")]
    private static partial Regex KeyLike();
}
