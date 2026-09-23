using System.Buffers.Binary;

namespace ExtormSub.Core.Audio;

/// <summary>A finished utterance waiting for speech recognition.</summary>
public sealed record SpooledUtterance(long Seq, float[] Audio, TimeSpan Start, TimeSpan End, long EndedTimestamp);

/// <summary>
/// FIFO backlog between VAD and ASR. Up to <c>memoryLimit</c> of audio stays in memory; beyond that,
/// new utterances are written to disk as 16-bit PCM (when a directory is given) so a slow ASR never
/// loses speech. Beyond <c>diskLimit</c> — or when spooling is disabled — the oldest utterances are
/// dropped and returned so the caller can account for them. Nothing touches the disk while ASR keeps up.
/// Thread-safe: the DSP thread enqueues, the ASR thread dequeues.
/// </summary>
public sealed class UtteranceSpool : IDisposable
{
    private const int Rate = AudioFormat.AsrSampleRate;

    private sealed class Entry
    {
        public required long Seq;
        public required TimeSpan Start, End;
        public required long EndedTimestamp;
        public required int Samples;
        public float[]? Audio;
        public string? File;
    }

    private readonly LinkedList<Entry> _queue = new();
    private readonly object _gate = new();
    private readonly string? _directory;
    private readonly long _memoryLimit, _diskLimit;
    private long _memorySamples, _diskSamples;

    public UtteranceSpool(string? directory, TimeSpan memoryLimit, TimeSpan diskLimit)
    {
        _directory = directory;
        _memoryLimit = (long)(memoryLimit.TotalSeconds * Rate);
        _diskLimit = (long)(diskLimit.TotalSeconds * Rate);
        if (_directory is not null)
        {
            Directory.CreateDirectory(_directory);
            DeleteFiles(); // leftovers from a crash are for a session that no longer exists
        }
    }

    public int Count { get { lock (_gate) return _queue.Count; } }
    public double MemorySeconds { get { lock (_gate) return (double)_memorySamples / Rate; } }
    public double DiskSeconds { get { lock (_gate) return (double)_diskSamples / Rate; } }
    public bool IsEmpty => Count == 0;

    /// <summary>Queues an utterance. Returns utterances dropped to stay within limits (oldest first).</summary>
    public IReadOnlyList<long> Enqueue(SpooledUtterance u)
    {
        var entry = new Entry { Seq = u.Seq, Start = u.Start, End = u.End, EndedTimestamp = u.EndedTimestamp, Samples = u.Audio.Length };
        var dropped = new List<long>();
        lock (_gate)
        {
            // An utterance arriving at an empty queue always stays in memory: ASR takes it next anyway.
            if (_memorySamples > 0 && _memorySamples + entry.Samples > _memoryLimit && _directory is not null)
            {
                entry.File = Path.Combine(_directory, $"{u.Seq:D10}.pcm");
                WritePcm16(entry.File, u.Audio);
                _diskSamples += entry.Samples;
            }
            else
            {
                entry.Audio = u.Audio;
                _memorySamples += entry.Samples;
            }
            _queue.AddLast(entry);

            // Over budget: drop the oldest entries that count against it (memory entries when there is no
            // spool, disk entries otherwise), never the newest utterance.
            bool disk = _directory is not null;
            while ((disk ? _diskSamples > _diskLimit : _memorySamples > _memoryLimit) && _queue.Count > 1)
            {
                var node = _queue.First;
                while (node is not null && node != _queue.Last && (node.Value.File is not null) != disk) node = node.Next;
                if (node is null || node == _queue.Last) break;
                _queue.Remove(node);
                Release(node.Value);
                dropped.Add(node.Value.Seq);
            }
        }
        return dropped;
    }

    public bool TryDequeue(out SpooledUtterance utterance)
    {
        Entry e;
        lock (_gate)
        {
            if (_queue.First is null)
            {
                utterance = null!;
                return false;
            }
            e = _queue.First.Value;
            _queue.RemoveFirst();
            if (e.File is null) _memorySamples -= e.Samples;
            else _diskSamples -= e.Samples;
        }
        // Disk reads happen outside the lock so the DSP thread never waits on them.
        var audio = e.Audio ?? ReadPcm16(e.File!, e.Samples);
        if (e.File is not null) TryDelete(e.File);
        utterance = new SpooledUtterance(e.Seq, audio, e.Start, e.End, e.EndedTimestamp);
        return true;
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var e in _queue) Release(e);
            _queue.Clear();
        }
    }

    private void Release(Entry e)
    {
        if (e.File is null) _memorySamples -= e.Samples;
        else
        {
            _diskSamples -= e.Samples;
            TryDelete(e.File);
        }
    }

    private static void WritePcm16(string path, float[] audio)
    {
        var bytes = new byte[audio.Length * 2];
        for (int i = 0; i < audio.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)Math.Clamp(audio[i] * 32767f, -32768f, 32767f));
        File.WriteAllBytes(path, bytes);
    }

    private static float[] ReadPcm16(string path, int samples)
    {
        var bytes = File.ReadAllBytes(path);
        var audio = new float[Math.Min(samples, bytes.Length / 2)];
        for (int i = 0; i < audio.Length; i++)
            audio[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2)) / 32768f;
        return audio;
    }

    private void DeleteFiles()
    {
        foreach (var f in Directory.EnumerateFiles(_directory!, "*.pcm")) TryDelete(f);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        Clear();
        if (_directory is not null) DeleteFiles();
    }
}
