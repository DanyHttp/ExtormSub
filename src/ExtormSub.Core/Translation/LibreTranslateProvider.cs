using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExtormSub.Core.Translation;

/// <summary>
/// Self-hosted LibreTranslate (POST /translate). No model, no context or glossary: plain machine translation.
/// The API key is optional and only needed when the server runs with --api-keys.
/// </summary>
public sealed class LibreTranslateProvider(HttpClient http, string baseUrl, TimeSpan timeout, string? apiKey) : ITranslationProvider
{
    private readonly Uri _endpoint = new(baseUrl.TrimEnd('/') + "/translate");
    private readonly string? _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    public string Name => "LibreTranslate";
    public string CacheScope => $"{baseUrl}|libretranslate";

    public async Task<string> TranslateAsync(TranslationRequest request, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var body = new JsonObject
        {
            ["q"] = request.Text,
            ["source"] = LanguageCode(request.SourceLanguage) ?? "auto",
            ["target"] = LanguageCode(request.TargetLanguage) ?? request.TargetLanguage.Trim().ToLowerInvariant(),
            ["format"] = "text",
        };
        if (_apiKey is not null) body["api_key"] = _apiKey;

        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var response = await http.SendAsync(msg, cts.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw OpenAiCompatibleProvider.MapError(response, text, _endpoint.Host);

            string? translated;
            try { translated = JsonNode.Parse(text)?["translatedText"]?.GetValue<string>(); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                throw new TranslationException(TranslationErrorKind.Fatal, "Response was not valid LibreTranslate JSON", inner: ex);
            }
            return translated?.Trim() ?? throw new TranslationException(TranslationErrorKind.Fatal, "Response had no translatedText");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TranslationException(TranslationErrorKind.Transient, $"Timed out after {timeout.TotalSeconds:0}s");
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationException(TranslationErrorKind.Transient, $"Network error: {ex.InnerException?.Message ?? ex.Message}", inner: ex);
        }
    }

    /// <summary>"Persian" → "fa". Codes pass through ("fa", "zh-Hans"). Null when unrecognised.</summary>
    public static string? LanguageCode(string language)
    {
        var s = language.Trim();
        if (s.Equals("auto", StringComparison.OrdinalIgnoreCase)) return "auto";
        if (s.Length is 2 or 3 || s.Contains('-')) return s;
        return CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .FirstOrDefault(c => c.EnglishName.Equals(s, StringComparison.OrdinalIgnoreCase)
                              || c.NativeName.Equals(s, StringComparison.OrdinalIgnoreCase))
            ?.TwoLetterISOLanguageName;
    }
}
