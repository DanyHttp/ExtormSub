using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using ExtormSub.Core.ASR;
using ExtormSub.Core.Audio;
using ExtormSub.Core.Diagnostics;
using ExtormSub.Core.History;
using ExtormSub.Core.Pipeline;
using ExtormSub.Core.Translation;
using ExtormSub.Infrastructure;
using ExtormSub.Infrastructure.ASR;

namespace ExtormSub.Tests;

public class UtteranceSpoolTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("extormsub-spool").FullName;

    private static SpooledUtterance U(long seq, double seconds, float value = 0.25f) =>
        new(seq, Enumerable.Repeat(value, (int)(16000 * seconds)).ToArray(), TimeSpan.FromSeconds(seq), TimeSpan.FromSeconds(seq + seconds), seq);

    [Fact]
    public void Stays_in_memory_while_asr_keeps_up()
    {
        using var spool = new UtteranceSpool(_dir, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5));
        spool.Enqueue(U(1, 2));
        spool.Enqueue(U(2, 2));
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.True(spool.TryDequeue(out var first));
        Assert.Equal(1, first.Seq);
        Assert.Equal(2, spool.MemorySeconds, 3);
    }

    [Fact]
    public void Spills_to_disk_beyond_memory_limit_and_reads_back_in_order()
    {
        using var spool = new UtteranceSpool(_dir, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(5));
        var dropped = new List<long>();
        for (int i = 1; i <= 4; i++) dropped.AddRange(spool.Enqueue(U(i, 2, value: i / 10f)));

        Assert.Empty(dropped);
        Assert.Equal(3, Directory.GetFiles(_dir, "*.pcm").Length); // #1 in memory, #2-#4 on disk
        Assert.Equal(6, spool.DiskSeconds, 3);

        for (int i = 1; i <= 4; i++)
        {
            Assert.True(spool.TryDequeue(out var u));
            Assert.Equal(i, u.Seq);
            Assert.Equal(32000, u.Audio.Length);
            Assert.Equal(i / 10f, u.Audio[100], 3); // 16-bit round trip
        }
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Disk_limit_drops_the_oldest_and_reports_them()
    {
        using var spool = new UtteranceSpool(_dir, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        var dropped = new List<long>();
        for (int i = 1; i <= 5; i++) dropped.AddRange(spool.Enqueue(U(i, 2)));
        Assert.Equal([2L, 3L], dropped); // #1 is in memory; disk holds at most 5 s
        Assert.True(spool.TryDequeue(out var u) && u.Seq == 1);
        Assert.True(spool.TryDequeue(out u) && u.Seq == 4);
    }

    [Fact]
    public void Without_a_directory_the_oldest_speech_is_dropped()
    {
        using var spool = new UtteranceSpool(null, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(5));
        var dropped = spool.Enqueue(U(1, 2)).Concat(spool.Enqueue(U(2, 2))).ToList();
        Assert.Equal([1L], dropped);
        Assert.Equal(1, spool.Count);
    }

    [Fact]
    public void Leftover_files_from_a_crash_are_removed()
    {
        File.WriteAllBytes(Path.Combine(_dir, "0000000001.pcm"), new byte[10]);
        using var spool = new UtteranceSpool(_dir, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(5));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    public void Dispose() => Directory.Delete(_dir, true);
}

public class SpoolingPipelineTests
{
    [Fact]
    public async Task Slow_asr_loses_nothing_and_late_segments_go_to_history_only()
    {
        var dir = Directory.CreateTempSubdirectory("extormsub-spool").FullName;
        try
        {
            var source = new FakeAudioSource();
            var metrics = new PipelineMetrics();
            await using var pipeline = new SubtitlePipeline(_ => source, () => new EnergyVad(), metrics);
            var shown = new ConcurrentQueue<SubtitleUpdate>();
            var records = new ConcurrentQueue<SegmentRecord>();
            pipeline.SubtitleReady += shown.Enqueue;
            pipeline.SegmentCompleted += records.Enqueue;

            var options = new PipelineOptions
            {
                Vad = new VadOptions { MinSpeechMs = 96, SilenceTimeoutMs = 300, PreRollMs = 0, PostRollMs = 0, PartialIntervalMs = 5000 },
                RingBuffer = TimeSpan.FromSeconds(30),
                MaxAsrBacklog = TimeSpan.FromSeconds(1), // forces spooling
                SpoolDirectory = dir,
                StaleAfter = TimeSpan.FromSeconds(1.5),
            };
            pipeline.Start(options, null, new FakeAsr { DelayMs = 900 }, null);
            for (int i = 0; i < 5; i++)
            {
                source.Push(FakeAudioSource.Tone(1.0));
                source.Push(FakeAudioSource.Silence(0.5));
            }

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (records.Count < 5 && DateTime.UtcNow < deadline) await Task.Delay(20);
            await pipeline.StopAsync();

            Assert.Equal(5, records.Count);                       // nothing lost
            Assert.Equal(0, metrics.DroppedUtterances);
            Assert.Contains(shown, s => !s.IsStale);             // the first ones were live
            Assert.Contains(shown, s => s.IsStale);              // the backlog was caught up for history
            Assert.Equal([1L, 2L, 3L, 4L, 5L], records.Select(r => r.Seq).Order());
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Translation_can_be_swapped_without_restarting_capture()
    {
        var source = new FakeAudioSource();
        await using var pipeline = new SubtitlePipeline(_ => source, () => new EnergyVad(), new PipelineMetrics());
        var shown = new ConcurrentQueue<SubtitleUpdate>();
        pipeline.SubtitleReady += shown.Enqueue;
        var options = new PipelineOptions
        {
            Vad = new VadOptions { MinSpeechMs = 96, SilenceTimeoutMs = 300, PreRollMs = 0, PostRollMs = 0, PartialIntervalMs = 5000 },
        };
        pipeline.Start(options, null, new FakeAsr(), translation: null);
        var session = pipeline.Session;

        using var queue = new TranslationQueue(new FakeTranslator(), new TranslationCache(10), new TranslationQueueOptions { MaxRequestsPerMinute = 0 });
        pipeline.SetTranslation(queue);
        source.Push(FakeAudioSource.Tone(1.0));
        source.Push(FakeAudioSource.Silence(0.5));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (shown.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.StartsWith("FA:", Assert.Single(shown).Translation);
        Assert.Same(session, pipeline.Session);
    }
}

public class ResumableDownloadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("extormsub-dl").FullName;
    private static readonly byte[] Body = RandomNumberGenerator.GetBytes(2_500_000);
    private static readonly string BodySha = Convert.ToHexString(SHA256.HashData(Body));

    /// <summary>Serves <see cref="Body"/> with Range support; can cut the connection after N bytes.</summary>
    private sealed class RangeServer : HttpMessageHandler
    {
        public int? FailAfter { get; set; }
        public List<RangeHeaderSeen> Requests { get; } = [];
        public sealed record RangeHeaderSeen(long? From);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            long from = request.Headers.Range?.Ranges.First().From ?? 0;
            Requests.Add(new(request.Headers.Range?.Ranges.First().From));
            var slice = Body.AsMemory((int)from).ToArray();
            Stream stream = FailAfter is { } n ? new FailingStream(slice, n) : new MemoryStream(slice);
            var content = new StreamContent(stream);
            content.Headers.ContentLength = slice.Length;
            return Task.FromResult(new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class FailingStream(byte[] data, int failAfter) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= failAfter) throw new IOException("connection reset");
            return base.Read(buffer, offset, (int)Math.Min(count, failAfter - Position));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var tmp = new byte[buffer.Length];
            int n = Read(tmp, 0, tmp.Length);
            tmp.AsSpan(0, n).CopyTo(buffer.Span);
            return ValueTask.FromResult(n);
        }
    }

    [Fact]
    public async Task Interrupted_download_resumes_with_a_range_request_and_verifies()
    {
        var server = new RangeServer { FailAfter = 1_000_000 };
        var target = Path.Combine(_dir, "model.bin");
        var downloader = new FileDownloader(new HttpClient(server));

        await Assert.ThrowsAnyAsync<IOException>(() => downloader.DownloadAsync(new Uri("https://x/model.bin"), target, BodySha, Body.Length, null, CancellationToken.None));
        Assert.False(File.Exists(target));
        Assert.Equal(1_000_000, FileDownloader.PartialBytes(target));

        server.FailAfter = null;
        await downloader.DownloadAsync(new Uri("https://x/model.bin"), target, BodySha, Body.Length, null, CancellationToken.None);

        Assert.Equal(Body, File.ReadAllBytes(target));
        Assert.Equal(1_000_000, server.Requests[^1].From); // only the missing part was fetched
        Assert.False(File.Exists(FileDownloader.PartPath(target)));
    }

    [Fact]
    public async Task Checksum_mismatch_deletes_the_file()
    {
        var target = Path.Combine(_dir, "model.bin");
        var downloader = new FileDownloader(new HttpClient(new RangeServer()));
        await Assert.ThrowsAsync<ChecksumMismatchException>(() =>
            downloader.DownloadAsync(new Uri("https://x/model.bin"), target, new string('0', 64), Body.Length, null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    public void Dispose() => Directory.Delete(_dir, true);
}

public class ModelCatalogTests
{
    [Fact]
    public void Every_model_has_a_pinned_size_and_sha256()
    {
        Assert.All(ModelCatalog.All, m =>
        {
            Assert.True(m.Bytes > 10_000_000);
            Assert.Matches("^[0-9a-f]{64}$", m.Sha256!);
        });
    }

    [Theory]
    [InlineData("base.en-q5_1", "base.en")]
    [InlineData("large-v3-turbo-q5_0", "large-v3-turbo")]
    [InlineData("small.en", "small.en")]
    [InlineData("large-v3-turbo", "large-v3-turbo")]
    public void Maps_to_faster_whisper_model_names(string id, string expected) =>
        Assert.Equal(expected, ModelCatalog.FasterWhisperModel(id));

    [WhisperModelFact]
    public async Task Downloaded_model_matches_its_pinned_sha256()
    {
        var path = Fixtures.WhisperModel()!;
        var model = ModelCatalog.All.First(m => path.EndsWith(m.FileName, StringComparison.OrdinalIgnoreCase));
        Assert.True(await ModelDownloader.VerifyAsync(Path.GetDirectoryName(path)!, model));
    }
}

/// <summary>Runs only when the faster-whisper environment has been set up on this PC.</summary>
public sealed class FasterWhisperFactAttribute : FactAttribute
{
    public FasterWhisperFactAttribute()
    {
        if (!new FasterWhisperEnvironment(new AppPaths()).IsInstalled)
            Skip = "faster-whisper is not set up (Settings → Speech Recognition → Set up faster-whisper).";
    }
}

public class FasterWhisperProviderTests
{
    [FasterWhisperFact]
    public async Task Sidecar_transcribes_speech_and_restarts_after_a_crash()
    {
        await using var asr = new FasterWhisperProvider(new FasterWhisperEnvironment(new AppPaths()));
        await asr.InitializeAsync(new AsrOptions { ModelPath = "base.en", ModelId = "base.en", Backend = AsrBackend.Cpu, Threads = 4 }, CancellationToken.None);
        Assert.StartsWith("faster-whisper · cpu", asr.BackendDescription);

        var audio = Fixtures.ReadWav16(Fixtures.Asset("speech.wav"));
        var result = await asr.TranscribeAsync(audio, audio.Length, CancellationToken.None);
        Assert.Contains("leave", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("en", result.Language);
        Assert.True(result.Confidence > 0.5f);

        // Kill the sidecar behind the provider's back: the next request must restart it transparently.
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("python"))
        {
            try { if (p.MainModule?.FileName.Contains("ExtormSub", StringComparison.OrdinalIgnoreCase) == true) p.Kill(); } catch (Exception) { }
        }
        await Task.Delay(500);
        var again = await asr.TranscribeAsync(audio, audio.Length, CancellationToken.None);
        Assert.Contains("weather", again.Text, StringComparison.OrdinalIgnoreCase);
    }
}
