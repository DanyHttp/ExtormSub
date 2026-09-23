namespace ExtormSub.Core.Audio;

/// <summary>
/// Bounded byte ring between the capture thread (single writer) and the DSP worker (single reader).
/// Writes never block or allocate: when full, the oldest whole frames are overwritten and counted.
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly byte[] _buffer;
    private readonly int _blockAlign;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _dataReady = new(false);
    private int _readPos;
    private int _count;
    private long _droppedBytes;
    private long _totalWritten;

    public AudioRingBuffer(int capacityBytes, int blockAlign)
    {
        if (blockAlign <= 0) throw new ArgumentOutOfRangeException(nameof(blockAlign));
        if (capacityBytes < blockAlign) throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        _blockAlign = blockAlign;
        _buffer = new byte[capacityBytes - capacityBytes % blockAlign];
    }

    public static AudioRingBuffer ForDuration(AudioFormat format, TimeSpan duration) =>
        new((int)Math.Max(format.BlockAlign, format.BytesPerSecond * duration.TotalSeconds), format.BlockAlign);

    public int Capacity => _buffer.Length;
    public int Count { get { lock (_gate) return _count; } }
    public long DroppedBytes => Interlocked.Read(ref _droppedBytes);
    public long TotalWritten => Interlocked.Read(ref _totalWritten);

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        lock (_gate)
        {
            if (data.Length >= _buffer.Length)
            {
                // Larger than the whole ring: keep only the newest capacity bytes.
                _droppedBytes += _count + data.Length - _buffer.Length;
                data = data[^_buffer.Length..];
                _readPos = 0;
                _count = 0;
            }

            int overflow = _count + data.Length - _buffer.Length;
            if (overflow > 0)
            {
                overflow = Math.Min(_count, RoundUp(overflow));
                _readPos = (_readPos + overflow) % _buffer.Length;
                _count -= overflow;
                _droppedBytes += overflow;
            }

            int writePos = (_readPos + _count) % _buffer.Length;
            int first = Math.Min(data.Length, _buffer.Length - writePos);
            data[..first].CopyTo(_buffer.AsSpan(writePos));
            data[first..].CopyTo(_buffer);
            _count += data.Length;
            _totalWritten += data.Length;
        }
        _dataReady.Set();
    }

    /// <summary>Reads whole frames only. Returns bytes copied.</summary>
    public int Read(Span<byte> destination)
    {
        lock (_gate)
        {
            int n = Math.Min(_count, destination.Length);
            n -= n % _blockAlign;
            if (n == 0) return 0;
            int first = Math.Min(n, _buffer.Length - _readPos);
            _buffer.AsSpan(_readPos, first).CopyTo(destination);
            _buffer.AsSpan(0, n - first).CopyTo(destination[first..]);
            _readPos = (_readPos + n) % _buffer.Length;
            _count -= n;
            return n;
        }
    }

    /// <summary>Waits until data may be available. Returns false on timeout.</summary>
    public bool WaitForData(TimeSpan timeout, CancellationToken ct)
    {
        bool signalled = _dataReady.Wait(timeout, ct);
        // Reset before the caller reads: anything written after this point sets the event again.
        _dataReady.Reset();
        return signalled || Count > 0;
    }

    public void Clear()
    {
        lock (_gate) { _readPos = 0; _count = 0; }
    }

    private int RoundUp(int bytes) => (bytes + _blockAlign - 1) / _blockAlign * _blockAlign;
}
