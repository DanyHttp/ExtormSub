using System.Buffers.Binary;
using ExtormSub.Core.Audio;

namespace ExtormSub.Tests;

public class AudioRingBufferTests
{
    [Fact]
    public void Reads_back_what_was_written_across_wraparound()
    {
        var ring = new AudioRingBuffer(capacityBytes: 16, blockAlign: 4);
        var dst = new byte[16];

        ring.Write([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
        Assert.Equal(8, ring.Read(dst.AsSpan(0, 8)));
        ring.Write([13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24]); // wraps

        int n = ring.Read(dst);
        Assert.Equal(16, n);
        Assert.Equal(new byte[] { 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24 }, dst[..n]);
        Assert.Equal(0, ring.DroppedBytes);
    }

    [Fact]
    public void Overflow_drops_oldest_whole_frames_and_counts_them()
    {
        var ring = new AudioRingBuffer(capacityBytes: 8, blockAlign: 4);
        ring.Write([1, 1, 1, 1, 2, 2, 2, 2]);
        ring.Write([3, 3, 3, 3]); // full → frame "1" is overwritten

        var dst = new byte[8];
        Assert.Equal(8, ring.Read(dst));
        Assert.Equal(new byte[] { 2, 2, 2, 2, 3, 3, 3, 3 }, dst);
        Assert.Equal(4, ring.DroppedBytes);
    }

    [Fact]
    public void Write_larger_than_capacity_keeps_newest_tail()
    {
        var ring = new AudioRingBuffer(capacityBytes: 4, blockAlign: 2);
        ring.Write([1, 2, 3, 4, 5, 6, 7, 8]);
        var dst = new byte[4];
        Assert.Equal(4, ring.Read(dst));
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, dst);
        Assert.Equal(4, ring.DroppedBytes);
    }

    [Fact]
    public void Read_returns_only_whole_frames()
    {
        var ring = new AudioRingBuffer(capacityBytes: 16, blockAlign: 4);
        ring.Write([1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Equal(4, ring.Read(new byte[6]));
        Assert.Equal(4, ring.Count);
    }

    [Fact]
    public async Task WaitForData_signals_writer_and_times_out_when_idle()
    {
        var ring = new AudioRingBuffer(64, 4);
        Assert.False(ring.WaitForData(TimeSpan.FromMilliseconds(20), CancellationToken.None));
        var writer = Task.Run(async () => { await Task.Delay(30); ring.Write([1, 2, 3, 4]); });
        Assert.True(ring.WaitForData(TimeSpan.FromSeconds(2), CancellationToken.None));
        await writer;
    }

    [Fact]
    public void Writer_is_never_blocked_by_a_stalled_reader()
    {
        var ring = AudioRingBuffer.ForDuration(new AudioFormat(48000, 2, SampleEncoding.Float32), TimeSpan.FromSeconds(1));
        var packet = new byte[3840]; // 10 ms
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) ring.Write(packet); // 10 s of audio into a 1 s ring, reader never runs
        Assert.True(sw.ElapsedMilliseconds < 1000);
        Assert.Equal(ring.Capacity, ring.Count);
        Assert.True(ring.DroppedBytes > 0);
    }
}

public class PcmConversionTests
{
    [Fact]
    public void Float_stereo_is_averaged_to_mono()
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0), 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), -0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), 0.5f);
        var dst = new float[2];
        int n = new PcmConverter(new AudioFormat(48000, 2, SampleEncoding.Float32)).ConvertToMono(bytes, dst);
        Assert.Equal(2, n);
        Assert.Equal(0f, dst[0], 5);
        Assert.Equal(0.75f, dst[1], 5);
    }

    [Theory]
    [InlineData(SampleEncoding.Pcm16)]
    [InlineData(SampleEncoding.Pcm24)]
    [InlineData(SampleEncoding.Pcm32)]
    public void Integer_pcm_is_scaled_to_unit_range(SampleEncoding encoding)
    {
        var fmt = new AudioFormat(16000, 1, encoding);
        var bytes = new byte[fmt.BlockAlign];
        // Half of full scale.
        switch (encoding)
        {
            case SampleEncoding.Pcm16: BinaryPrimitives.WriteInt16LittleEndian(bytes, 16384); break;
            case SampleEncoding.Pcm24: bytes[0] = 0; bytes[1] = 0; bytes[2] = 0x40; break;
            case SampleEncoding.Pcm32: BinaryPrimitives.WriteInt32LittleEndian(bytes, 1 << 30); break;
        }
        var dst = new float[1];
        new PcmConverter(fmt).ConvertToMono(bytes, dst);
        Assert.Equal(0.5f, dst[0], 4);
    }

    [Fact]
    public void Resampler_48k_to_16k_keeps_duration_and_pitch()
    {
        const int inRate = 48000, seconds = 1;
        var input = new float[inRate * seconds];
        for (int i = 0; i < input.Length; i++) input[i] = MathF.Sin(2 * MathF.PI * 440 * i / inRate);

        var rs = new StreamResampler(inRate, 16000);
        var output = new List<float>();
        var buf = new float[rs.MaxOutput(480)];
        for (int i = 0; i < input.Length; i += 480) // stream in 10 ms chunks
        {
            int n = rs.Process(input.AsSpan(i, 480), buf);
            output.AddRange(buf.Take(n));
        }

        Assert.InRange(output.Count, 15800, 16000);
        // Count zero crossings in the steady middle second → ~880 per second for 440 Hz.
        int crossings = 0;
        for (int i = 1000; i < output.Count - 1; i++)
            if (output[i - 1] < 0 != output[i] < 0) crossings++;
        double hz = crossings / 2.0 / ((output.Count - 1001) / 16000.0);
        Assert.InRange(hz, 430, 450);
    }
}

public class VadSegmenterTests
{
    private const int Frame = 512; // 32 ms

    private static VadOptions Options => new()
    {
        Threshold = 0.5f, MinSpeechMs = 96, SilenceTimeoutMs = 320, PreRollMs = 64, PostRollMs = 64,
        MaxUtteranceMs = 60_000, PartialIntervalMs = 320, MinPartialMs = 320,
    };

    private static List<(VadEvents Events, long Seq)> Feed(VadSegmenter seg, params (float Prob, int Frames)[] script)
    {
        var events = new List<(VadEvents, long)>();
        var frame = new float[Frame];
        foreach (var (prob, frames) in script)
            for (int i = 0; i < frames; i++)
            {
                Array.Fill(frame, prob); // sample value = prob so we can see what was kept
                var e = seg.Process(frame, prob);
                if (e != VadEvents.None) events.Add((e, seg.CurrentSeq));
            }
        return events;
    }

    [Fact]
    public void Speech_then_silence_produces_one_utterance_with_pre_and_post_roll()
    {
        var seg = new VadSegmenter(Options);
        var events = Feed(seg, (0.1f, 10), (0.9f, 20), (0.05f, 20));

        Assert.Contains(events, e => e.Events.HasFlag(VadEvents.Started) && e.Seq == 1);
        Assert.Contains(events, e => e.Events.HasFlag(VadEvents.Ended));
        var u = seg.LastEnded!;
        Assert.Equal(1, u.Seq);
        // 2 frames pre-roll + 20 speech + 2 frames post-roll
        Assert.Equal((2 + 20 + 2) * Frame, u.Audio.Length);
        Assert.Equal(0.1f, u.Audio[0]);            // pre-roll is the quiet audio before speech
        Assert.Equal(0.05f, u.Audio[^1]);          // post-roll is trailing silence
        Assert.Equal(TimeSpan.FromMilliseconds(8 * 32), u.Start);
    }

    [Fact]
    public void Blips_shorter_than_min_speech_are_ignored()
    {
        var seg = new VadSegmenter(Options);
        var events = Feed(seg, (0.9f, 2), (0.05f, 30)); // 64 ms < 96 ms
        Assert.Empty(events);
        Assert.Null(seg.LastEnded);
    }

    [Fact]
    public void Short_pauses_below_silence_timeout_do_not_split()
    {
        var seg = new VadSegmenter(Options);
        var events = Feed(seg, (0.9f, 10), (0.05f, 5), (0.9f, 10), (0.05f, 20));
        Assert.Single(events, e => e.Events.HasFlag(VadEvents.Ended));
    }

    [Fact]
    public void Hysteresis_keeps_speech_open_between_release_and_threshold()
    {
        var seg = new VadSegmenter(Options);
        var events = Feed(seg, (0.9f, 10), (0.4f, 30)); // 0.4 ≥ release (0.35): still speech
        Assert.DoesNotContain(events, e => e.Events.HasFlag(VadEvents.Ended));
    }

    [Fact]
    public void Sequence_numbers_increase_monotonically()
    {
        var seg = new VadSegmenter(Options);
        Feed(seg, (0.9f, 10), (0.05f, 20), (0.9f, 10), (0.05f, 20), (0.9f, 10), (0.05f, 20));
        Assert.Equal(3, seg.LastEnded!.Seq);
    }

    [Fact]
    public void Long_speech_is_split_at_max_length_and_continues_with_next_seq()
    {
        var seg = new VadSegmenter(Options with { MaxUtteranceMs = 1024 }); // 32 frames
        var events = Feed(seg, (0.9f, 40));
        var split = events.Single(e => e.Events.HasFlag(VadEvents.Ended));
        Assert.True(split.Events.HasFlag(VadEvents.Started));
        Assert.Equal(2, split.Seq);
        Assert.Equal(1, seg.LastEnded!.Seq);
        Assert.True(seg.InSpeech);
    }

    [Fact]
    public void Partials_are_requested_at_the_configured_cadence()
    {
        var seg = new VadSegmenter(Options);
        var events = Feed(seg, (0.9f, 40)); // 1280 ms speech, first partial after 320 ms of audio
        int partials = events.Count(e => e.Events.HasFlag(VadEvents.PartialDue));
        Assert.InRange(partials, 3, 4);
    }

    [Fact]
    public void Idle_time_advances_stream_clock()
    {
        var seg = new VadSegmenter(Options);
        seg.AdvanceIdle(16000);
        Feed(seg, (0.9f, 10), (0.05f, 20));
        Assert.True(seg.LastEnded!.Start >= TimeSpan.FromSeconds(0.9));
    }
}
