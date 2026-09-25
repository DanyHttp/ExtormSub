using System.Buffers.Binary;
using ExtormSub.Core.ASR;
using ExtormSub.Core.Audio;
using ExtormSub.Core.History;
using ExtormSub.Infrastructure;
using ExtormSub.Infrastructure.ASR;
using ExtormSub.Infrastructure.Audio;
using ExtormSub.Infrastructure.History;
using ExtormSub.Infrastructure.Security;
using ExtormSub.Infrastructure.Vad;
using NAudio.Wave;

namespace ExtormSub.Tests;

internal static class Fixtures
{
    public static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "assets", name);

    /// <summary>Reads a 16-bit PCM mono WAV into floats.</summary>
    public static float[] ReadWav16(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos + 4));
            if (id == "data")
            {
                var samples = new float[size / 2];
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(pos + 8 + i * 2)) / 32768f;
                return samples;
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException("no data chunk");
    }

    public static string? WhisperModel()
    {
        var env = Environment.GetEnvironmentVariable("EXTORMSUB_TEST_MODEL");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExtormSub", "models");
        return new[] { "ggml-base.en.bin", "ggml-tiny.en.bin", "ggml-base.en-q5_1.bin" }
            .Select(f => Path.Combine(local, f)).FirstOrDefault(File.Exists);
    }
}

/// <summary>Runs only when a whisper model is available locally (xunit v2 has no runtime skip).</summary>
public sealed class WhisperModelFactAttribute : FactAttribute
{
    public WhisperModelFactAttribute()
    {
        if (Fixtures.WhisperModel() is null)
            Skip = "No whisper model found. Set EXTORMSUB_TEST_MODEL or download base.en via the app.";
    }
}

public class SileroVadTests
{
    [Fact]
    public void Detects_speech_in_tts_audio_and_not_in_silence()
    {
        using var vad = new SileroVad(Fixtures.Asset("silero_vad.onnx"));
        var silence = new float[512];
        float silentProb = 0;
        for (int i = 0; i < 20; i++) silentProb = Math.Max(silentProb, vad.Process(silence));
        Assert.True(silentProb < 0.2f, $"silence prob {silentProb}");

        vad.Reset();
        var speech = Fixtures.ReadWav16(Fixtures.Asset("speech.wav"));
        int voiced = 0, frames = 0;
        for (int i = 0; i + 512 <= speech.Length; i += 512, frames++)
            if (vad.Process(speech.AsSpan(i, 512)) > 0.5f) voiced++;
        Assert.True(voiced > frames / 3, $"only {voiced}/{frames} frames voiced");
    }

    [Fact]
    public void Silero_splits_tts_at_the_sentence_pause()
    {
        using var vad = new SileroVad(Fixtures.Asset("silero_vad.onnx"));
        var seg = new VadSegmenter(new VadOptions { SilenceTimeoutMs = 500 });
        var speech = Fixtures.ReadWav16(Fixtures.Asset("speech.wav")).Concat(new float[16000]).ToArray();
        var utterances = new List<Utterance>();
        for (int i = 0; i + 512 <= speech.Length; i += 512)
            if (seg.Process(speech.AsSpan(i, 512), vad.Process(speech.AsSpan(i, 512))).HasFlag(VadEvents.Ended))
                utterances.Add(seg.LastEnded!);

        // "I think we should leave now." <pause> "The weather is getting worse."
        Assert.Equal(2, utterances.Count);
        Assert.All(utterances, u => Assert.InRange(u.Duration.TotalSeconds, 1.0, 3.5));
        Assert.True(utterances[1].Start > utterances[0].End);
    }
}

public class WhisperCppProviderTests
{
    [WhisperModelFact]
    public async Task Transcribes_tts_speech_on_cpu()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "extormsub-t-r"), Path.Combine(Path.GetTempPath(), "extormsub-t-l"));
        await using var asr = new WhisperCppProvider(paths);
        var glossary = new ExtormSub.Core.Text.Glossary(ExtormSub.Core.Text.Glossary.Defaults());
        await asr.InitializeAsync(new AsrOptions
        {
            ModelPath = Fixtures.WhisperModel()!, ModelId = "test", Backend = AsrBackend.Cpu, Threads = 4, InitialPrompt = glossary.AsrPrompt,
        }, CancellationToken.None);
        Assert.True(asr.IsReady);

        var audio = Fixtures.ReadWav16(Fixtures.Asset("speech.wav"));
        var result = await asr.TranscribeAsync(audio, audio.Length, CancellationToken.None);

        var text = result.Text.ToLowerInvariant();
        Assert.Contains("leave", text);
        Assert.Contains("weather", text);
        Assert.Equal("en", result.Language);
        Assert.True(result.NoSpeechProbability < 0.5f);

        // Short clip (< 1 s) is padded instead of returning nothing.
        var shortResult = await asr.TranscribeAsync(audio, 12_000, CancellationToken.None);
        Assert.NotNull(shortResult);
    }

    [Fact]
    public async Task Missing_model_fails_with_clear_error()
    {
        await using var asr = new WhisperCppProvider(new AppPaths(Path.GetTempPath(), Path.GetTempPath()));
        var ex = await Assert.ThrowsAsync<AsrInitializationException>(() =>
            asr.InitializeAsync(new AsrOptions { ModelPath = @"C:\nope\ggml-missing.bin" }, CancellationToken.None));
        Assert.Contains("not found", ex.Message);
    }
}

public class HistoryStoreTests : IAsyncLifetime
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"extormsub-{Guid.NewGuid():N}.db");
    private SqliteHistoryStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new SqliteHistoryStore(_db);
        await _store.InitializeAsync();
    }

    [Fact]
    public async Task Round_trips_sessions_and_segments_with_search_and_delete()
    {
        var start = DateTimeOffset.Now;
        await _store.StartSessionAsync(new SessionRecord("s1", start, null, "Speakers", "base.en", "gpt"));
        await _store.AddSegmentAsync(new SegmentRecord("s1", 1, start, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5),
            "Hello 100% there", "سلام", "en", 0.93f, 120, 480));
        await _store.AddSegmentAsync(new SegmentRecord("s1", 2, start, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4),
            "Second line", null, "en", null, 90, null));
        await _store.EndSessionAsync("s1", start.AddMinutes(1));

        var sessions = await _store.ListSessionsAsync();
        var s = Assert.Single(sessions);
        Assert.Equal(2, s.SegmentCount);
        Assert.Equal("سلام", s.Preview);

        var segs = await _store.GetSegmentsAsync("s1");
        Assert.Equal(TimeSpan.FromSeconds(2.5), segs[0].End);
        Assert.Equal(0.93f, segs[0].Confidence!.Value, 3);
        Assert.Null(segs[1].TranslatedText);

        Assert.Single(await _store.ListSessionsAsync("100%"));     // % is literal, not a wildcard
        Assert.Single(await _store.ListSessionsAsync("سلام"));
        Assert.Empty(await _store.ListSessionsAsync("missing"));

        await _store.DeleteSessionAsync("s1");
        Assert.Empty(await _store.ListSessionsAsync());
        Assert.Empty(await _store.GetSegmentsAsync("s1"));
    }

    [Fact]
    public async Task Empty_sessions_are_discarded_on_end()
    {
        await _store.StartSessionAsync(new SessionRecord("empty", DateTimeOffset.Now, null, null, null, null));
        await _store.EndSessionAsync("empty", DateTimeOffset.Now);
        Assert.Empty(await _store.ListSessionsAsync());
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" }) File.Delete(f);
        return Task.CompletedTask;
    }
}

public class SecretStoreTests
{
    [Fact]
    public void Round_trips_encrypted_and_never_stores_plaintext()
    {
        var dir = Directory.CreateTempSubdirectory("extormsub-secrets").FullName;
        try
        {
            var store = new DpapiSecretStore(dir);
            store.Set(DpapiSecretStore.ApiKeyName("OpenAI"), "sk-very-secret-value");
            Assert.Equal("sk-very-secret-value", store.Get(DpapiSecretStore.ApiKeyName("openai")));

            var raw = File.ReadAllBytes(Directory.GetFiles(dir).Single());
            Assert.DoesNotContain("very-secret", System.Text.Encoding.UTF8.GetString(raw));

            store.Set(DpapiSecretStore.ApiKeyName("OpenAI"), null);
            Assert.Null(store.Get(DpapiSecretStore.ApiKeyName("OpenAI")));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class WasapiFormatTests
{
    [Fact]
    public void Maps_common_mix_formats()
    {
        Assert.Equal(new AudioFormat(48000, 2, SampleEncoding.Float32), WasapiSource.Map(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
        Assert.Equal(new AudioFormat(44100, 2, SampleEncoding.Pcm16), WasapiSource.Map(new WaveFormat(44100, 16, 2)));
        Assert.Equal(new AudioFormat(48000, 6, SampleEncoding.Pcm24), WasapiSource.Map(new WaveFormat(48000, 24, 6)));
        Assert.Equal(SampleEncoding.Float32, WasapiSource.Map(new WaveFormatExtensible(48000, 32, 2)).Encoding);
    }
}

public class ModelDownloaderTests
{
    private sealed class BytesHandler(byte[] body, long? declaredLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new StreamContent(new MemoryStream(body));
            content.Headers.ContentLength = declaredLength;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }

    private static readonly WhisperModel Model = new("unit-test", "Unit test", 3_000_000, true, "");

    [Fact]
    public async Task Downloads_atomically_and_reports_progress()
    {
        var dir = Directory.CreateTempSubdirectory("extormsub-models").FullName;
        try
        {
            var body = new byte[3_000_000];
            Random.Shared.NextBytes(body);
            var reports = new List<DownloadProgress>();
            await new ModelDownloader(new HttpClient(new BytesHandler(body, body.Length)))
                .DownloadAsync(Model, dir, new SyncProgress<DownloadProgress>(reports.Add), CancellationToken.None);

            Assert.True(ModelDownloader.IsDownloaded(dir, Model));
            Assert.Equal(body, File.ReadAllBytes(ModelDownloader.PathFor(dir, Model)));
            Assert.Empty(Directory.GetFiles(dir, "*.part"));
            Assert.Equal(1.0, reports[^1].Fraction);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Truncated_download_is_never_treated_as_a_model_but_kept_for_resume()
    {
        var dir = Directory.CreateTempSubdirectory("extormsub-models").FullName;
        try
        {
            var downloader = new ModelDownloader(new HttpClient(new BytesHandler(new byte[1000], 5000)));
            await Assert.ThrowsAnyAsync<Exception>(() => downloader.DownloadAsync(Model, dir, null, CancellationToken.None));
            Assert.False(ModelDownloader.IsDownloaded(dir, Model));
            Assert.Equal(1000, ModelDownloader.PartialBytes(dir, Model));
            Assert.Single(Directory.GetFiles(dir, "*.part"));
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public class LibreTranslateServerTests
{
    /// <summary>Installs LibreTranslate (~1 GB) into a temp folder, starts it and translates. Set LIBRETRANSLATE_SETUP=1 to run.</summary>
    [Fact]
    public async Task Installs_starts_and_translates_when_enabled()
    {
        if (Environment.GetEnvironmentVariable("LIBRETRANSLATE_SETUP") != "1") return;
        var root = Path.Combine(Path.GetTempPath(), "extormsub-lt-test");
        using var server = new ExtormSub.Infrastructure.Translation.LibreTranslateServer(new AppPaths(root, root));
        var log = new Progress<string>(Console.WriteLine);
        if (!server.IsInstalled) await server.SetupAsync(null, log, CancellationToken.None);
        Assert.True(server.IsInstalled);

        var url = new Uri("http://127.0.0.1:5055/");
        await server.EnsureStartedAsync(url, "en,fa", log, CancellationToken.None);
        Assert.True(server.IsRunning);

        var p = new ExtormSub.Core.Translation.LibreTranslateProvider(new HttpClient(), url.ToString(), TimeSpan.FromSeconds(60), null);
        var result = await p.TranslateAsync(new("Good morning, my friend.", [], [], "English", "Persian"), CancellationToken.None);
        Assert.Matches(@"\p{IsArabic}", result);

        server.Stop();
        Assert.False(server.IsRunning);
    }
}
