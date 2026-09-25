using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ExtormSub.Core.Translation;

namespace ExtormSub.Tests;

/// <summary>Scriptable provider: per-text delay and failure, records calls.</summary>
internal sealed class FakeTranslator : ITranslationProvider
{
    public ConcurrentQueue<string> Calls { get; } = new();
    public Func<string, int> DelayMs { get; set; } = _ => 0;
    public Func<string, int, Exception?> Fail { get; set; } = (_, _) => null;
    private readonly ConcurrentDictionary<string, int> _attempts = new();
    private int _concurrent, _maxConcurrent;

    public string Name => "Fake";
    public string CacheScope => "fake";
    public int MaxConcurrentSeen => _maxConcurrent;

    public async Task<string> TranslateAsync(TranslationRequest request, CancellationToken ct)
    {
        Calls.Enqueue(request.Text);
        int now = Interlocked.Increment(ref _concurrent);
        InterlockedMax(ref _maxConcurrent, now);
        try
        {
            int attempt = _attempts.AddOrUpdate(request.Text, 1, (_, a) => a + 1);
            await Task.Delay(DelayMs(request.Text), ct);
            if (Fail(request.Text, attempt) is { } ex) throw ex;
            return "FA:" + request.Text;
        }
        finally { Interlocked.Decrement(ref _concurrent); }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}

internal sealed class OutcomeSink
{
    public ConcurrentQueue<TranslationOutcome> Items { get; } = new();
    public void Add(TranslationOutcome o) => Items.Enqueue(o);

    public async Task<List<TranslationOutcome>> WaitForAsync(int count, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (Items.Count < count && DateTime.UtcNow < deadline) await Task.Delay(5);
        return Items.ToList();
    }
}

public class TranslationCacheTests
{
    [Fact]
    public void Normalized_text_hits_the_same_entry()
    {
        var cache = new TranslationCache(10);
        cache.Set(TranslationCache.Key("m", "fa", "Hello there."), "سلام");
        Assert.True(cache.TryGet(TranslationCache.Key("m", "fa", "  hello   there"), out var v));
        Assert.Equal("سلام", v);
        Assert.False(cache.TryGet(TranslationCache.Key("other-model", "fa", "hello there"), out _));
        Assert.Equal(1, cache.Hits);
        Assert.Equal(1, cache.Misses);
    }

    [Fact]
    public void Evicts_least_recently_used()
    {
        var cache = new TranslationCache(2);
        cache.Set("a", "1");
        cache.Set("b", "2");
        cache.TryGet("a", out _); // a is now most recent
        cache.Set("c", "3");      // evicts b
        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.Equal(2, cache.Count);
    }
}

public class TranslationContextTests
{
    [Fact]
    public void Keeps_only_the_last_n_lines()
    {
        var ctx = new TranslationContextManager(3);
        for (int i = 1; i <= 5; i++) ctx.Add($"s{i}", $"t{i}");
        Assert.Equal(["s3", "s4", "s5"], ctx.Snapshot().Select(c => c.Source));
    }

    [Fact]
    public void Prompt_separates_context_from_the_text_to_translate()
    {
        var req = new TranslationRequest("That's sick.",
            [new ContextLine("Did you see that jump?", "اون پرش رو دیدی؟")],
            [new ExtormSub.Core.Text.GlossaryEntry { Term = "GitHub" }], "English", "Persian");
        var user = SubtitlePrompt.User(req);
        Assert.Contains("<context>", user);
        Assert.EndsWith("<translate>That's sick.</translate>", user);
        Assert.Contains("GitHub => GitHub", user);
        Assert.Contains("ONLY the text inside <translate>", SubtitlePrompt.System("English", "Persian"));
    }

    [Theory]
    [InlineData("\"سلام\"", "سلام")]
    [InlineData("<translate>سلام</translate>", "سلام")]
    [InlineData("Translation: سلام", "سلام")]
    [InlineData("```\nسلام\n```", "سلام")]
    [InlineData("خط اول\nخط دوم", "خط اول خط دوم")]
    public void Response_cleanup(string raw, string expected) => Assert.Equal(expected, SubtitlePrompt.CleanResponse(raw));
}

public class TranslationQueueTests
{
    private static TranslationJob Job(long seq, string text, bool final = true) => new(seq, text, final, [], []);

    private static (TranslationQueue Queue, FakeTranslator Fake, OutcomeSink Sink) Create(TranslationQueueOptions? o = null)
    {
        var fake = new FakeTranslator();
        var q = new TranslationQueue(fake, new TranslationCache(100),
            o ?? new TranslationQueueOptions { BaseBackoff = TimeSpan.FromMilliseconds(1), MaxRequestsPerMinute = 0 });
        var sink = new OutcomeSink();
        q.Completed += sink.Add;
        return (q, fake, sink);
    }

    [Fact]
    public async Task Every_job_completes_even_when_finishing_out_of_order()
    {
        var (q, fake, sink) = Create();
        fake.DelayMs = t => t == "slow" ? 150 : 5;
        q.Submit(Job(1, "slow"));
        q.Submit(Job(2, "fast"));
        var outcomes = await sink.WaitForAsync(2);
        Assert.Equal([2L, 1L], outcomes.Select(o => o.Seq)); // raw completion order is not seq order...
        Assert.All(outcomes, o => Assert.Equal("FA:" + o.SourceText, o.Translation)); // ...the sequencer fixes that
    }

    [Fact]
    public async Task Newer_text_for_same_seq_cancels_the_older_request()
    {
        var (q, fake, sink) = Create();
        fake.DelayMs = _ => 100;
        q.Submit(Job(1, "I think", final: false));
        await Task.Delay(20);
        q.Submit(Job(1, "I think we should leave"));
        var outcomes = await sink.WaitForAsync(1);
        await Task.Delay(200);
        var single = Assert.Single(sink.Items);
        Assert.Equal("I think we should leave", single.SourceText);
        Assert.True(single.IsFinal);
    }

    [Fact]
    public async Task Same_text_promoted_to_final_reuses_in_flight_request()
    {
        var (q, fake, sink) = Create();
        fake.DelayMs = _ => 80;
        q.Submit(Job(7, "That's sick.", final: false));
        q.Submit(Job(7, "that's sick", final: true));
        var outcome = Assert.Single(await sink.WaitForAsync(1));
        await Task.Delay(100);
        Assert.Single(fake.Calls);
        Assert.True(outcome.IsFinal);
    }

    [Fact]
    public async Task Cache_prevents_repeated_requests()
    {
        var (q, fake, sink) = Create();
        q.Submit(Job(1, "Hello."));
        await sink.WaitForAsync(1);
        q.Submit(Job(2, "hello"));
        var outcomes = await sink.WaitForAsync(2);
        Assert.Single(fake.Calls);
        Assert.True(outcomes[1].FromCache);
    }

    [Fact]
    public async Task Transient_errors_retry_with_backoff_then_succeed()
    {
        var (q, fake, sink) = Create();
        fake.Fail = (_, attempt) => attempt < 3 ? new TranslationException(TranslationErrorKind.Transient, "boom") : null;
        q.Submit(Job(1, "retry me"));
        var outcome = Assert.Single(await sink.WaitForAsync(1));
        Assert.Equal("FA:retry me", outcome.Translation);
        Assert.Equal(3, fake.Calls.Count);
    }

    [Fact]
    public async Task Auth_errors_do_not_retry_and_report_failure()
    {
        var (q, fake, sink) = Create();
        fake.Fail = (_, _) => new TranslationException(TranslationErrorKind.Auth, "bad key");
        q.Submit(Job(1, "x"));
        var outcome = Assert.Single(await sink.WaitForAsync(1));
        Assert.Null(outcome.Translation);
        Assert.Equal(TranslationErrorKind.Auth, outcome.Error);
        Assert.Single(fake.Calls);
        Assert.Equal(TranslationHealth.AuthError, q.Health);
    }

    [Fact]
    public async Task Concurrency_is_bounded()
    {
        var (q, fake, sink) = Create(new TranslationQueueOptions { MaxConcurrent = 2, MaxRequestsPerMinute = 0 });
        fake.DelayMs = _ => 40;
        for (int i = 1; i <= 6; i++) q.Submit(Job(i, $"t{i}"));
        await sink.WaitForAsync(6);
        Assert.Equal(6, sink.Items.Count);
        Assert.True(fake.MaxConcurrentSeen <= 2);
    }

    [Fact]
    public async Task Cancel_suppresses_the_outcome()
    {
        var (q, fake, sink) = Create();
        fake.DelayMs = _ => 100;
        q.Submit(Job(1, "obsolete"));
        q.Cancel(1);
        await Task.Delay(200);
        Assert.Empty(sink.Items);
    }
}

public class OpenAiCompatibleProviderTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static readonly TranslationRequest Req = new("Hello", [], [], "English", "Persian");

    [Fact]
    public async Task Sends_chat_completion_with_bearer_key_and_parses_content()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":" «سلام» "}}]}"""));
        var p = new OpenAiCompatibleProvider(new HttpClient(handler),
            new OpenAiCompatibleConfig { BaseUrl = "https://api.example.com/v1/", Model = "m1" }, "sk-test-123456789");

        Assert.Equal("سلام", await p.TranslateAsync(Req, CancellationToken.None));
        var (request, body) = handler.Seen.Single();
        Assert.Equal("https://api.example.com/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.NotNull(request.Content!.Headers.ContentLength); // sized, not chunked: simple local servers need this
        var json = JsonNode.Parse(body)!;
        Assert.Equal("m1", (string)json["model"]!);
        Assert.Equal("system", (string)json["messages"]![0]!["role"]!);
        Assert.Contains("<translate>Hello</translate>", (string)json["messages"]![1]!["content"]!);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TranslationErrorKind.Auth)]
    [InlineData(HttpStatusCode.TooManyRequests, TranslationErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.BadGateway, TranslationErrorKind.Transient)]
    [InlineData(HttpStatusCode.NotFound, TranslationErrorKind.Fatal)]
    public async Task Maps_http_errors(HttpStatusCode code, TranslationErrorKind kind)
    {
        var p = new OpenAiCompatibleProvider(new HttpClient(new StubHandler((_, _) => Json(code, "{\"error\":\"x\"}"))),
            new OpenAiCompatibleConfig(), "k");
        var ex = await Assert.ThrowsAsync<TranslationException>(() => p.TranslateAsync(Req, CancellationToken.None));
        Assert.Equal(kind, ex.Kind);
    }

    [Fact]
    public async Task Timeout_is_transient()
    {
        var handler = new StubHandler((_, _) => { Thread.Sleep(300); return Json(HttpStatusCode.OK, "{}"); });
        var slow = new DelayingHandler(TimeSpan.FromSeconds(5));
        var p = new OpenAiCompatibleProvider(new HttpClient(slow), new OpenAiCompatibleConfig { Timeout = TimeSpan.FromMilliseconds(100) }, null);
        var ex = await Assert.ThrowsAsync<TranslationException>(() => p.TranslateAsync(Req, CancellationToken.None));
        Assert.Equal(TranslationErrorKind.Transient, ex.Kind);
    }

    [Fact]
    public async Task Retries_once_without_temperature_when_model_rejects_it()
    {
        var handler = new StubHandler((_, body) => body.Contains("temperature")
            ? Json(HttpStatusCode.BadRequest, """{"error":{"message":"Unsupported value: 'temperature'"}}""")
            : Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"}}]}"""));
        var p = new OpenAiCompatibleProvider(new HttpClient(handler), new OpenAiCompatibleConfig(), "k");
        Assert.Equal("ok", await p.TranslateAsync(Req, CancellationToken.None));
        Assert.Equal(2, handler.Seen.Count);
    }

    [Fact]
    public void Scrub_removes_keys_and_truncates()
    {
        var s = OpenAiCompatibleProvider.Scrub("Incorrect API key provided: sk-abcdefghijklmnop " + new string('x', 400));
        Assert.DoesNotContain("sk-abcdefghijklmnop", s);
        Assert.True(s.Length <= 301);
    }

    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

public class LibreTranslateProviderTests
{
    private sealed class StubHandler(HttpStatusCode code, string json) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, await request.Content!.ReadAsStringAsync(ct)));
            return new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Posts_language_codes_and_parses_translatedText()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"translatedText":" سلام "}""");
        var p = new LibreTranslateProvider(new HttpClient(handler), "http://localhost:5000/", TimeSpan.FromSeconds(5), "key1");

        Assert.Equal("سلام", await p.TranslateAsync(new("Hello", [], [], "English", "Persian"), CancellationToken.None));
        var (request, body) = handler.Seen.Single();
        Assert.Equal("http://localhost:5000/translate", request.RequestUri!.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("Hello", (string)json["q"]!);
        Assert.Equal("en", (string)json["source"]!);
        Assert.Equal("fa", (string)json["target"]!);
        Assert.Equal("key1", (string)json["api_key"]!);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, TranslationErrorKind.Auth)]
    [InlineData(HttpStatusCode.TooManyRequests, TranslationErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.BadRequest, TranslationErrorKind.Fatal)]
    public async Task Maps_http_errors(HttpStatusCode code, TranslationErrorKind kind)
    {
        var p = new LibreTranslateProvider(new HttpClient(new StubHandler(code, """{"error":"x"}""")), "http://x", TimeSpan.FromSeconds(5), null);
        var ex = await Assert.ThrowsAsync<TranslationException>(() => p.TranslateAsync(new("Hi", [], [], "en", "fa"), CancellationToken.None));
        Assert.Equal(kind, ex.Kind);
    }

    [Theory]
    [InlineData("English", "en")]
    [InlineData("persian", "fa")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("auto", "auto")]
    [InlineData("Klingonese", null)]
    public void Maps_language_names_to_codes(string name, string? code) =>
        Assert.Equal(code, LibreTranslateProvider.LanguageCode(name));

    /// <summary>Real server check. Set LIBRETRANSLATE_URL (e.g. http://localhost:5000) to run it.</summary>
    [Fact]
    public async Task Live_server_translates_when_configured()
    {
        var url = Environment.GetEnvironmentVariable("LIBRETRANSLATE_URL");
        if (string.IsNullOrEmpty(url)) return;
        var p = new LibreTranslateProvider(new HttpClient(), url, TimeSpan.FromSeconds(60), null);
        var result = await p.TranslateAsync(new("Good morning, my friend.", [], [], "English", "Persian"), CancellationToken.None);
        Assert.Matches(@"\p{IsArabic}", result);
    }
}
